using System.Text.Json;
using System.Reflection;
using PulseLink.Plugin.Abstractions.Notifications;

namespace PulseLink.NotificationPlugins.Dtu;

/// <summary>DTU gateway with SMS and TTS voice channels, device ACK handling, and a host-managed console.</summary>
public sealed class DtuPlugin : INotificationChannelPlugin, INotificationPluginRuntimeAware, INotificationPluginHealthReporter, INotificationPluginConfigurationAware, INotificationPluginOperations
{
    private static readonly JsonSerializerOptions OperationJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly DtuGateway gateway = new();
    private INotificationPluginRuntime? runtime;

    public string Id => "pulselink.dtu";
    public string DisplayName => "DTU SMS / voice gateway";
    /// <inheritdoc />
    public Version Version => Version.Parse(typeof(DtuPlugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "PulseLinkPluginVersion").Value!);
    public IReadOnlyList<string> OperationNames => ["get-status", "get-tasks", "cancel-task", "clear-finished-tasks", "get-configuration", "apply-configuration", "restart", "stop", "send-test"];
    public IEnumerable<INotificationChannel> CreateChannels() => [new DtuChannel("sms", "DTU SMS", DtuKind.Sms, gateway), new DtuChannel("voice", "DTU voice call", DtuKind.Voice, gateway)];

    public async ValueTask StartAsync(INotificationPluginRuntime pluginRuntime, CancellationToken cancellationToken = default)
    {
        runtime = pluginRuntime;
        gateway.SetRuntime(pluginRuntime);
        await gateway.RestartAsync(await GetConfigurationAsync(cancellationToken), cancellationToken);
    }

    public async ValueTask OnChannelConfigurationsChangedAsync(INotificationPluginRuntime pluginRuntime, CancellationToken cancellationToken = default)
    {
        runtime = pluginRuntime;
        gateway.SetRuntime(pluginRuntime);
        await gateway.RestartAsync(await GetConfigurationAsync(cancellationToken), cancellationToken);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default) => gateway.StopAsync(cancellationToken);

    public async ValueTask<JsonElement> ExecuteOperationAsync(string operation, JsonElement payload, CancellationToken cancellationToken = default)
    {
        if (runtime is null) throw new InvalidOperationException("DTU plugin is not started.");
        switch (operation.ToLowerInvariant())
        {
            case "get-status": return JsonSerializer.SerializeToElement(gateway.GetStatus());
            case "get-tasks":
                var taskQuery = payload.Deserialize<DtuTaskQuery>(OperationJsonOptions) ?? new DtuTaskQuery();
                return JsonSerializer.SerializeToElement(gateway.GetTasks(taskQuery.Limit, taskQuery.State));
            case "cancel-task":
                var taskId = payload.Deserialize<DtuTaskCommand>(OperationJsonOptions)?.TaskId;
                return JsonSerializer.SerializeToElement(new { cancelled = !string.IsNullOrWhiteSpace(taskId) && gateway.CancelTask(taskId) });
            case "clear-finished-tasks": return JsonSerializer.SerializeToElement(new { removed = gateway.ClearFinishedTasks() });
            case "get-configuration": return JsonSerializer.SerializeToElement(await GetConfigurationAsync(cancellationToken));
            case "stop": await gateway.PauseAsync(cancellationToken); return JsonSerializer.SerializeToElement(gateway.GetStatus());
            case "restart": await gateway.RestartAsync(await GetConfigurationAsync(cancellationToken), cancellationToken); return JsonSerializer.SerializeToElement(gateway.GetStatus());
            case "apply-configuration":
                var configuration = payload.Deserialize<DtuConfiguration>(OperationJsonOptions)?.Normalize() ?? throw new ArgumentException("A DTU configuration is required.");
                await runtime.UpdatePluginConfigurationAsync(JsonSerializer.Serialize(configuration), cancellationToken);
                await gateway.RestartAsync(configuration, cancellationToken);
                return JsonSerializer.SerializeToElement(gateway.GetStatus());
            case "send-test":
                var test = payload.Deserialize<DtuTestRequest>(OperationJsonOptions) ?? throw new ArgumentException("A test request is required.");
                await gateway.SendAsync(test.Kind, test.Phone, test.Message, "console-test", cancellationToken);
                return JsonSerializer.SerializeToElement(gateway.GetStatus());
            default: throw new NotSupportedException($"Unsupported DTU operation: {operation}.");
        }
    }

    private async ValueTask<DtuConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        var pluginConfiguration = await runtime!.GetPluginConfigurationAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(pluginConfiguration)) return DtuConfiguration.Parse(pluginConfiguration);
        // Compatibility only: existing deployments may still have the shared gateway settings
        // in a channel instance. The plugin console becomes the source of truth after its first save.
        var configuration = (await runtime.ListChannelConfigurationsAsync(cancellationToken)).FirstOrDefault(item => item.IsEnabled);
        // A newly installed service plugin must not claim a TCP port before an administrator
        // has explicitly saved its gateway settings in the plugin console.
        return configuration is null ? new DtuConfiguration(EnableTcpServer: false).Normalize() : DtuConfiguration.Parse(configuration.ConfigurationJson);
    }
}

public sealed record DtuTestRequest(DtuKind Kind, string Phone, string Message);
public sealed record DtuTaskQuery(int Limit = 100, string? State = null);
public sealed record DtuTaskCommand(string TaskId);
public enum DtuKind { Sms, Voice }
