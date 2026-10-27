using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PulseLink.Plugin.Abstractions.Notifications;

namespace PulseLink.NotificationPlugins.Dtu;

/// <summary>
/// Shared DTU service. A task is written to at most one device; after that point an absent ACK is
/// deliberately reported as <see cref="DtuTaskState.Unknown"/> rather than retried, preventing
/// duplicate SMS or calls.
/// </summary>
internal sealed class DtuGateway : IAsyncDisposable
{
    private static readonly TimeSpan LateAckGracePeriod = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultHealthReportInterval = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<Guid, DtuSession> sessions = new();
    private readonly ConcurrentDictionary<string, DtuTask> tasks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DtuTask> detachedTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DtuTask> lateAckTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<DtuTask> queue = new();
    private readonly ConcurrentQueue<DtuGatewayLog> logs = new();
    private readonly SemaphoreSlim queueSignal = new(0);
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly TimeSpan healthReportInterval;
    private TcpListener? listener;
    private CancellationTokenSource? stop;
    private CancellationTokenSource? healthReporterStop;
    private Task? acceptLoop;
    private Task? heartbeatLoop;
    private Task? healthReporterLoop;
    private DtuConfiguration configuration = new();
    private INotificationPluginRuntime? runtime;
    private string healthStatus = "Healthy";
    private string healthSummary = "DTU gateway is not started.";
    private int queued;
    private long sequence;

    internal DtuGateway(TimeSpan? healthReportInterval = null)
    {
        this.healthReportInterval = healthReportInterval ?? DefaultHealthReportInterval;
        if (this.healthReportInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(healthReportInterval));
    }

    public void SetRuntime(INotificationPluginRuntime pluginRuntime) => runtime = pluginRuntime;
    public int MaxRecipientsPerSend => configuration.MaxRecipientsPerSend;

    public async ValueTask RestartAsync(DtuConfiguration next, CancellationToken cancellationToken = default)
    {
        next = next.Normalize();
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (listener is not null && configuration == next) return;
            await StopCoreAsync("DTU gateway restarted.");
            EnsureHealthReporter();
            configuration = next;
            if (!next.EnableTcpServer)
            {
                await UpdateHealthAsync("Healthy", "DTU TCP listener is disabled by configuration.", cancellationToken);
                return;
            }
            try
            {
                stop = new CancellationTokenSource();
                listener = new TcpListener(IPAddress.Any, next.Port);
                listener.Start(100);
                acceptLoop = AcceptLoopAsync(listener, stop.Token);
                heartbeatLoop = HeartbeatLoopAsync(stop.Token);
                await LogAsync("listener_started", $"DTU TCP listener started on {next.Port}.", cancellationToken);
                await UpdateHealthAsync("Healthy", $"Listening on TCP {next.Port}.", cancellationToken);
            }
            catch (Exception exception)
            {
                await StopCoreAsync("DTU gateway failed to start.");
                await UpdateHealthAsync("Unhealthy", $"Failed to start DTU TCP listener on port {next.Port}: {exception.Message}", CancellationToken.None);
                throw;
            }
        }
        finally { lifecycle.Release(); }
    }

    public async ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync("DTU gateway stopped by administrator.");
            EnsureHealthReporter();
            await UpdateHealthAsync("Healthy", "DTU gateway is stopped by administrator.", cancellationToken);
        }
        finally { lifecycle.Release(); }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync("DTU gateway stopped.");
            await UpdateHealthAsync("Healthy", "DTU gateway stopped.", cancellationToken);
            await StopHealthReporterAsync();
        }
        finally { lifecycle.Release(); }
    }

    public async ValueTask<DtuSendResult> SendAsync(DtuKind kind, string phone, string message, string deliveryId, CancellationToken cancellationToken)
    {
        if (listener is null) await RestartAsync(configuration, cancellationToken);
        if (listener is null) throw new InvalidOperationException("DTU TCP listener is disabled.");
        if (Interlocked.Increment(ref queued) > configuration.QueueCapacity)
        {
            Interlocked.Decrement(ref queued);
            throw new InvalidOperationException($"DTU task queue is full ({configuration.QueueCapacity}).");
        }

        var task = new DtuTask(kind, phone, message, deliveryId, configuration);
        if (!tasks.TryAdd(task.Id, task))
        {
            Interlocked.Decrement(ref queued);
            throw new InvalidOperationException("Unable to allocate DTU task.");
        }
        queue.Enqueue(task);
        queueSignal.Release();
        await LogAsync("task_queued", $"{task.Kind} task queued.", cancellationToken, deliveryId);
        using var registration = cancellationToken.Register(() => task.CancelIfQueued());
        try { return await task.Completion.Task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (task.WasDispatched)
        {
            // The host may stop waiting, but the command was already written and must remain observable.
            await LogAsync("task_wait_cancelled", "Host stopped waiting after command was written; ACK tracking continues.", CancellationToken.None, deliveryId);
            throw;
        }
    }

    public DtuGatewayStatus GetStatus() => new(
        listener is not null,
        configuration.Port,
        sessions.Values.OrderBy(item => item.ConnectedAt).Select(item => item.Status()).ToArray(),
        Volatile.Read(ref queued),
        tasks.Values.Count(item => item.State == DtuTaskState.Completed),
        tasks.Values.Count(item => item.State == DtuTaskState.Failed),
        tasks.Values.Count(item => item.State == DtuTaskState.Unknown),
        logs.OrderByDescending(item => item.Sequence).Take(100).OrderBy(item => item.Sequence).ToArray());

    public IReadOnlyList<DtuTaskStatus> GetTasks(int limit = 100, string? state = null)
    {
        limit = Math.Clamp(limit, 1, 300);
        return tasks.Values
            .Where(item => string.IsNullOrWhiteSpace(state) || string.Equals(item.State.ToString(), state, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CreatedAt).Take(limit).Select(item => item.Status()).ToArray();
    }

    public bool CancelTask(string taskId)
    {
        if (!tasks.TryGetValue(taskId, out var task) || !task.CancelIfQueued()) return false;
        _ = LogAsync("task_cancelled", "Queued DTU task cancelled.", CancellationToken.None, task.DeliveryId);
        return true;
    }

    public int ClearFinishedTasks()
    {
        var removed = 0;
        foreach (var item in tasks.Where(pair => pair.Value.State is DtuTaskState.Completed or DtuTaskState.Failed or DtuTaskState.Cancelled))
            if (tasks.TryRemove(item.Key, out _)) removed++;
        return removed;
    }

    private async Task AcceptLoopAsync(TcpListener tcpListener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await tcpListener.AcceptTcpClientAsync(cancellationToken);
                var session = new DtuSession(client);
                sessions[session.Id] = session;
                RestoreDetachedTask(session);
                await LogAsync("device_connected", $"DTU connected: {session.EndPoint}.", CancellationToken.None);
                _ = ReceiveLoopAsync(session, cancellationToken);
                _ = WorkerLoopAsync(session, cancellationToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception exception) { await LogAsync("listener_error", exception.Message, CancellationToken.None); }
        }
    }

    private async Task ReceiveLoopAsync(DtuSession session, CancellationToken cancellationToken)
    {
        var buffer = new byte[2048];
        var pendingMessage = new StringBuilder();
        try
        {
            while (!cancellationToken.IsCancellationRequested && !session.IsStopped)
            {
                var count = await session.Stream.ReadAsync(buffer, cancellationToken);
                if (count == 0) break;
                session.MarkSeen();
                pendingMessage.Append(GetEncoding().GetString(buffer, 0, count));
                if (await HandleReceivedAsync(session, pendingMessage.ToString()) || pendingMessage.Length >= 4096)
                    pendingMessage.Clear();
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        finally { await RemoveSessionAsync(session); }
    }

    private async Task<bool> HandleReceivedAsync(DtuSession session, string text)
    {
        var message = text.Trim();
        if (message.Length == 0) return true;
        if (string.Equals(message, configuration.Heartbeat, StringComparison.OrdinalIgnoreCase))
        {
            session.MarkHeartbeat();
            return true;
        }

        var pending = session.PendingTask;
        if (pending is not null && DtuAck.TryParse(pending.Kind, message, session.EndPoint, out var result, out var progress))
        {
            if (progress)
            {
                await LogAsync("task_progress", result.Message, CancellationToken.None, pending.DeliveryId);
                return true;
            }
            session.ClearPending(pending);
            CompleteTask(pending, result);
            return true;
        }

        if (lateAckTasks.TryGetValue(session.SourceAddress, out var late) && DtuAck.TryParse(late.Kind, message, session.EndPoint, out var lateResult, out var lateProgress))
        {
            if (lateProgress) { await LogAsync("task_late_progress", lateResult.Message, CancellationToken.None, late.DeliveryId); return true; }
            lateAckTasks.TryRemove(session.SourceAddress, out _);
            detachedTasks.TryRemove(session.SourceAddress, out _);
            late.ResolveLateAck(lateResult);
            await ReportLateReceiptAsync(late, lateResult);
            await LogAsync(lateResult.Success ? "task_late_completed" : "task_late_failed", lateResult.Message, CancellationToken.None, late.DeliveryId);
            return true;
        }

        if (MayBePartialProtocolMessage(message, pending?.Kind ?? late?.Kind)) return false;
        await LogAsync("device_message", message.Length > 512 ? message[..512] : message, CancellationToken.None);
        return true;
    }

    private async ValueTask ReportLateReceiptAsync(DtuTask task, DtuSendResult result)
    {
        if (runtime is null || !Guid.TryParse(task.DeliveryId, out _)) return;
        try
        {
            await runtime.ReportReceiptAsync(new NotificationDeliveryReceipt(
                task.DeliveryId,
                result.Success ? NotificationDeliveryConfirmation.Delivered : NotificationDeliveryConfirmation.Rejected,
                result.Device,
                "LateAck",
                result.Message), CancellationToken.None);
        }
        catch (Exception exception)
        {
            await LogAsync("late_receipt_error", exception.Message, CancellationToken.None, task.DeliveryId);
        }
    }

    private bool MayBePartialProtocolMessage(string message, DtuKind? expectedKind)
    {
        var compact = string.Concat(message.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();
        if (compact.Length == 0) return true;
        List<string> tokens = [configuration.Heartbeat.ToUpperInvariant()];
        if (expectedKind == DtuKind.Sms)
            tokens.AddRange(["SMS_SEND_SUCESS", "SMS_SEND_SUCCESS", "SMS_SND_SUCESS", "SMS_SND_SUCCESS", "SMS_SEND_FAIL", "SMS_SND_FAIL", "SMSREADY=0", "SMSREADY=1", "ERROR"]);
        else if (expectedKind == DtuKind.Voice)
            tokens.AddRange(["TTSSPEACK", "TTSSPEAK", "READY", "BUSY", "NOANSWER", "NOCARRIER", "NODIALTONE", "CALLFAIL", "ERROR"]);
        foreach (var token in tokens)
        {
            var maximumPrefixLength = Math.Min(token.Length - 1, compact.Length);
            for (var length = 1; length <= maximumPrefixLength; length++)
                if (compact.EndsWith(token[..length], StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private async Task WorkerLoopAsync(DtuSession session, CancellationToken serverToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, session.StoppedToken);
        var token = linked.Token;
        while (!token.IsCancellationRequested)
        {
            DtuTask? task = null;
            try
            {
                // The wire protocol carries no task identifier. Until the previous command's
                // late ACK is resolved, dispatching another command could attach that ACK to it.
                await AwaitLateAckResolutionAsync(session.SourceAddress, token);
                await queueSignal.WaitAsync(token);
                if (!queue.TryDequeue(out task)) continue;
                Interlocked.Decrement(ref queued);
                if (!task.TryBeginDispatch()) continue;
                await DispatchAsync(session, task, token);
            }
            catch (OperationCanceledException)
            {
                if (task is not null && !task.IsFinished && !task.WasDispatched) Requeue(task);
                return;
            }
            catch (Exception exception)
            {
                if (task is not null && !task.WasDispatched) { task.ReleaseForRetry(); Requeue(task); }
                else if (task is not null) MarkUnknown(task, session, $"Connection failed after command write: {exception.Message}");
            }
        }
    }

    private async Task AwaitLateAckResolutionAsync(string sourceAddress, CancellationToken cancellationToken)
    {
        while (lateAckTasks.TryGetValue(sourceAddress, out var task))
        {
            if (task.UnknownAt is not { } unknownAt || unknownAt < DateTimeOffset.UtcNow - LateAckGracePeriod)
            {
                lateAckTasks.TryRemove(new KeyValuePair<string, DtuTask>(sourceAddress, task));
                detachedTasks.TryRemove(new KeyValuePair<string, DtuTask>(sourceAddress, task));
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    private async Task DispatchAsync(DtuSession session, DtuTask task, CancellationToken cancellationToken)
    {
        session.SetPending(task);
        try
        {
            var command = BuildCommand(task.Kind, task.Phone, task.Message, task.Options);
            await session.WriteAsync(GetEncoding().GetBytes(command), cancellationToken);
            task.MarkDispatched(session.SourceAddress, session.EndPoint);
            await LogAsync("task_dispatched", $"{task.Kind} command sent to {session.EndPoint}.", CancellationToken.None, task.DeliveryId);
            var result = await task.Acknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(task.Options.SendTimeoutSeconds), cancellationToken);
            CompleteTask(task, result);
            await Task.Delay(task.Options.TaskIntervalMilliseconds, CancellationToken.None);
        }
        catch (TimeoutException) { MarkUnknown(task, session, $"No DTU ACK within {task.Options.SendTimeoutSeconds} seconds."); }
        catch (OperationCanceledException) when (task.WasDispatched) { MarkUnknown(task, session, "Gateway or DTU connection stopped while awaiting ACK."); }
        finally { session.ClearPending(task); }
    }

    private void CompleteTask(DtuTask task, DtuSendResult result)
    {
        if (result.Success)
        {
            task.Complete(result);
            _ = LogAsync("task_completed", result.Message, CancellationToken.None, task.DeliveryId);
        }
        else
        {
            task.Fail(result);
            _ = LogAsync("task_failed", result.Message, CancellationToken.None, task.DeliveryId);
        }
    }

    private void MarkUnknown(DtuTask task, DtuSession session, string reason)
    {
        if (!task.MarkUnknown(reason)) return;
        lateAckTasks[session.SourceAddress] = task;
        detachedTasks[session.SourceAddress] = task;
        _ = LogAsync("task_unknown", reason, CancellationToken.None, task.DeliveryId);
    }

    private void Requeue(DtuTask task)
    {
        if (!task.TryRequeue()) return;
        queue.Enqueue(task);
        Interlocked.Increment(ref queued);
        queueSignal.Release();
        _ = LogAsync("task_requeued", "DTU task returned to queue before command write.", CancellationToken.None, task.DeliveryId);
    }

    private async Task RemoveSessionAsync(DtuSession session)
    {
        if (!sessions.TryRemove(session.Id, out _)) return;
        var pending = session.PendingTask;
        if (pending is not null && pending.WasDispatched) MarkUnknown(pending, session, "DTU disconnected before ACK.");
        session.Dispose();
        await LogAsync("device_disconnected", $"DTU disconnected: {session.EndPoint}.", CancellationToken.None);
    }

    private void RestoreDetachedTask(DtuSession session)
    {
        if (!detachedTasks.TryRemove(session.SourceAddress, out var task) || task.State != DtuTaskState.Unknown) return;
        // A DTU protocol has no task identifier. Keep this task associated with the reconnecting source
        // only for a bounded late-ACK grace period; it never receives a second command.
        lateAckTasks[session.SourceAddress] = task;
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (OperationCanceledException) { return; }
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-configuration.HeartbeatTimeoutSeconds);
            foreach (var session in sessions.Values.Where(item => item.LastSeen < cutoff)) session.Dispose();
            foreach (var pair in lateAckTasks.Where(item => item.Value.UnknownAt < DateTimeOffset.UtcNow - LateAckGracePeriod))
            {
                lateAckTasks.TryRemove(pair);
                detachedTasks.TryRemove(pair);
            }
        }
    }

    private void EnsureHealthReporter()
    {
        if (healthReporterLoop is { IsCompleted: false }) return;
        healthReporterStop?.Dispose();
        healthReporterStop = new CancellationTokenSource();
        healthReporterLoop = HealthReporterLoopAsync(healthReporterStop.Token);
    }

    private async Task HealthReporterLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(healthReportInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try { await ReportHealthAsync(Volatile.Read(ref healthStatus), Volatile.Read(ref healthSummary), cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch { /* The next heartbeat retries host persistence without terminating the plugin. */ }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task StopHealthReporterAsync()
    {
        if (healthReporterStop is null) return;
        healthReporterStop.Cancel();
        if (healthReporterLoop is not null) try { await healthReporterLoop; } catch (OperationCanceledException) { }
        healthReporterStop.Dispose();
        healthReporterStop = null;
        healthReporterLoop = null;
    }

    private async Task StopCoreAsync(string reason)
    {
        stop?.Cancel(); listener?.Stop();
        foreach (var session in sessions.Values) session.Dispose();
        sessions.Clear();
        if (acceptLoop is not null) try { await acceptLoop; } catch { }
        if (heartbeatLoop is not null) try { await heartbeatLoop; } catch { }
        while (queue.TryDequeue(out var task)) { Interlocked.Decrement(ref queued); task.Fail(new DtuSendResult(false, reason, "gateway")); }
        stop?.Dispose(); stop = null; listener = null; acceptLoop = null; heartbeatLoop = null;
    }

    private async ValueTask LogAsync(string eventName, string message, CancellationToken cancellationToken, string? deliveryId = null)
    {
        var entry = new DtuGatewayLog(Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow, eventName, message);
        logs.Enqueue(entry); while (logs.Count > 500 && logs.TryDequeue(out _)) { }
        if (runtime is not null) await runtime.WriteLogAsync(new NotificationPluginLogEntry(eventName is "task_failed" or "task_unknown" ? "Warning" : "Information", eventName, message, deliveryId), cancellationToken);
    }

    private ValueTask UpdateHealthAsync(string status, string summary, CancellationToken cancellationToken)
    {
        Volatile.Write(ref healthStatus, status);
        Volatile.Write(ref healthSummary, summary);
        return ReportHealthAsync(status, summary, cancellationToken);
    }

    private ValueTask ReportHealthAsync(string status, string summary, CancellationToken cancellationToken) => runtime is null ? ValueTask.CompletedTask : runtime.ReportHealthAsync(new NotificationPluginHealth(status, summary, new Dictionary<string, string> { ["clients"] = sessions.Count.ToString(), ["queued"] = Volatile.Read(ref queued).ToString() }), cancellationToken);
    private static string BuildCommand(DtuKind kind, string phone, string message, DtuConfiguration options) => $"{NormalizePhone(kind, phone, options)}:{(kind == DtuKind.Sms ? 0 : 3)}:{(kind == DtuKind.Sms && !string.IsNullOrWhiteSpace(options.SmsSignature) ? $"{options.SmsSignature} {message}" : message).Trim()}";
    private static string NormalizePhone(DtuKind kind, string phone, DtuConfiguration options) => kind == DtuKind.Voice && options.StripChinaCountryCodeForVoice && phone.StartsWith("86", StringComparison.Ordinal) ? phone[2..] : phone;
    private static Encoding GetEncoding() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(936); }
    public async ValueTask DisposeAsync() { await StopAsync(); lifecycle.Dispose(); queueSignal.Dispose(); }
}

internal sealed class DtuSession(TcpClient client) : IDisposable
{
    // 连接关闭后 TcpClient.Client 会被清空；断线结算与迟到 ACK 关联仍需要稳定地址。
    private readonly EndPoint? remoteEndPoint = client.Client.RemoteEndPoint;
    private readonly TcpClient client = client;
    private readonly CancellationTokenSource stopped = new();
    private readonly object gate = new();
    private DtuTask? pending;
    public Guid Id { get; } = Guid.NewGuid();
    public NetworkStream Stream { get; } = client.GetStream();
    public string EndPoint => remoteEndPoint?.ToString() ?? "unknown";
    public string SourceAddress => remoteEndPoint is IPEndPoint endpoint ? endpoint.Address.ToString() : EndPoint;
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeen { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastHeartbeatAt { get; private set; }
    public bool IsStopped => stopped.IsCancellationRequested;
    public CancellationToken StoppedToken => stopped.Token;
    public DtuTask? PendingTask { get { lock (gate) return pending; } }
    public void MarkSeen() => LastSeen = DateTimeOffset.UtcNow;
    public void MarkHeartbeat() { LastSeen = DateTimeOffset.UtcNow; LastHeartbeatAt = LastSeen; }
    public void SetPending(DtuTask task) { lock (gate) pending = task; }
    public void ClearPending(DtuTask task) { lock (gate) { if (ReferenceEquals(pending, task)) pending = null; } }
    public Task WriteAsync(byte[] bytes, CancellationToken cancellationToken) => Stream.WriteAsync(bytes, cancellationToken).AsTask();
    public DtuClientStatus Status() => new(Id.ToString("N"), EndPoint, SourceAddress, ConnectedAt, LastSeen, LastHeartbeatAt, PendingTask is not null, PendingTask?.Id);
    public void Dispose() { try { stopped.Cancel(); } catch { } try { Stream.Dispose(); } catch { } try { client.Dispose(); } catch { } }
}

internal sealed class DtuTask
{
    private int state;
    public DtuTask(DtuKind kind, string phone, string message, string deliveryId, DtuConfiguration options) { Id = Guid.NewGuid().ToString("N"); Kind = kind; Phone = phone; Message = message; DeliveryId = deliveryId; Options = options; CreatedAt = DateTimeOffset.UtcNow; }
    public string Id { get; }
    public DtuKind Kind { get; }
    public string Phone { get; }
    public string Message { get; }
    public string DeliveryId { get; }
    public DtuConfiguration Options { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? DispatchedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? UnknownAt { get; private set; }
    public string? Device { get; private set; }
    public string? Summary { get; private set; }
    public DtuTaskState State => (DtuTaskState)Volatile.Read(ref state);
    public bool WasDispatched => State is DtuTaskState.Dispatched or DtuTaskState.Completed or DtuTaskState.Failed or DtuTaskState.Unknown;
    public bool IsFinished => State is DtuTaskState.Completed or DtuTaskState.Failed or DtuTaskState.Cancelled or DtuTaskState.Unknown;
    public TaskCompletionSource<DtuSendResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<DtuSendResult> Acknowledgement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool TryBeginDispatch() => Interlocked.CompareExchange(ref state, (int)DtuTaskState.Dispatching, (int)DtuTaskState.Queued) == (int)DtuTaskState.Queued;
    public void MarkDispatched(string sourceAddress, string device)
    {
        Device = device;
        DispatchedAt = DateTimeOffset.UtcNow;
        Interlocked.CompareExchange(ref state, (int)DtuTaskState.Dispatched, (int)DtuTaskState.Dispatching);
    }
    public bool ReleaseForRetry() => Interlocked.CompareExchange(ref state, (int)DtuTaskState.Queued, (int)DtuTaskState.Dispatching) == (int)DtuTaskState.Dispatching;
    public bool TryRequeue() => State == DtuTaskState.Queued;
    public bool CancelIfQueued() { if (Interlocked.CompareExchange(ref state, (int)DtuTaskState.Cancelled, (int)DtuTaskState.Queued) != (int)DtuTaskState.Queued) return false; CompletedAt = DateTimeOffset.UtcNow; Summary = "Cancelled before dispatch."; Completion.TrySetCanceled(); return true; }
    public void Complete(DtuSendResult result) { if (IsFinished) return; Summary = result.Message; CompletedAt = DateTimeOffset.UtcNow; Volatile.Write(ref state, (int)(result.Success ? DtuTaskState.Completed : DtuTaskState.Failed)); Acknowledgement.TrySetResult(result); Completion.TrySetResult(result); }
    public void Fail(DtuSendResult result) { if (IsFinished) return; Summary = result.Message; CompletedAt = DateTimeOffset.UtcNow; Volatile.Write(ref state, (int)DtuTaskState.Failed); Acknowledgement.TrySetResult(result); Completion.TrySetResult(result); }
    public bool MarkUnknown(string reason) { if (Interlocked.CompareExchange(ref state, (int)DtuTaskState.Unknown, (int)DtuTaskState.Dispatched) != (int)DtuTaskState.Dispatched) return false; Summary = reason; UnknownAt = DateTimeOffset.UtcNow; Completion.TrySetResult(new DtuSendResult(false, reason, Device ?? "unknown", true)); return true; }
    public void ResolveLateAck(DtuSendResult result) { Summary = $"{result.Message} (late ACK)"; CompletedAt = DateTimeOffset.UtcNow; Volatile.Write(ref state, (int)(result.Success ? DtuTaskState.Completed : DtuTaskState.Failed)); Acknowledgement.TrySetResult(result); }
    public DtuTaskStatus Status() => new(Id, DeliveryId, Kind.ToString(), MaskPhone(Phone), State.ToString(), CreatedAt, DispatchedAt, CompletedAt, UnknownAt, Device, Summary);
    private static string MaskPhone(string phone) => phone.Length < 7 ? "***" : $"{phone[..3]}****{phone[^4..]}";
}

public enum DtuTaskState { Queued, Dispatching, Dispatched, Completed, Failed, Unknown, Cancelled }
public sealed record DtuSendResult(bool Success, string Message, string Device, bool IsUnknown = false);
public sealed record DtuClientStatus(string Id, string EndPoint, string SourceAddress, DateTimeOffset ConnectedAt, DateTimeOffset LastSeen, DateTimeOffset? LastHeartbeatAt, bool IsBusy, string? CurrentTaskId);
public sealed record DtuTaskStatus(string Id, string DeliveryId, string Kind, string Phone, string State, DateTimeOffset CreatedAt, DateTimeOffset? DispatchedAt, DateTimeOffset? CompletedAt, DateTimeOffset? UnknownAt, string? Device, string? Summary);
public sealed record DtuGatewayLog(long Sequence, DateTimeOffset Timestamp, string Event, string Message);
public sealed record DtuGatewayStatus(bool IsRunning, int Port, IReadOnlyList<DtuClientStatus> Clients, int PendingCount, int CompletedCount, int FailedCount, int UnknownCount, IReadOnlyList<DtuGatewayLog> Logs);

internal static class DtuAck
{
    public static bool TryParse(DtuKind kind, string reply, string device, out DtuSendResult result, out bool progress)
    {
        progress = false;
        var value = string.Concat(reply.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();
        if (kind == DtuKind.Sms)
        {
            if (value.Contains("SMS_SEND_SUCESS") || value.Contains("SMS_SEND_SUCCESS") || value.Contains("SMS_SND_SUCESS") || value.Contains("SMS_SND_SUCCESS")) { result = new(true, "DTU confirmed SMS delivery.", device); return true; }
            if (value.Contains("SMS_SEND_FAIL") || value.Contains("SMS_SND_FAIL") || value.Contains("SMSREADY=0") || value.Contains("ERROR")) { result = new(false, $"DTU rejected SMS: {reply}", device); return true; }
            if (value.Contains("SMSREADY=1")) { progress = true; result = new(true, "DTU SMS module ready.", device); return true; }
        }
        else
        {
            // The factory protocol defines "tts busy" as dialing progress, not call rejection.
            if (value.Contains("TTSBUSY") || value.Contains("QWTTS:1")) { progress = true; result = new(true, $"DTU voice progress: {reply}", device); return true; }
            // "TTS speack" means the call was answered and playback started; "ready" means it ended.
            if (value.Contains("TTSSPEACK") || value.Contains("TTSSPEAK")) { result = new(true, "DTU confirmed that the voice call was answered.", device); return true; }
            if (value.Contains("QWTTS:0") || value == "READY" || value.EndsWith(":READY")) { result = new(true, "DTU confirmed voice delivery.", device); return true; }
            if (value.Contains("NOANSWER") || value.Contains("NOCARRIER") || value.Contains("NODIALTONE") || value.Contains("CALLFAIL") || value.Contains("ERROR")) { result = new(false, $"DTU rejected voice call: {reply}", device); return true; }
        }
        result = default!;
        return false;
    }
}
