using System.Text.Json;
using PulseLink.Plugin.Abstractions.Notifications;

namespace PulseLink.NotificationPlugins.Dtu;

internal sealed class DtuChannel(string key, string displayName, DtuKind kind, DtuGateway gateway) : INotificationChannel
{
    public string Key => key;
    public string DisplayName => displayName;
    public bool IsBroadcast => false;
    public IReadOnlyList<NotificationContactTypeDefinition> ContactTypes => [new("phone", "phone", "手机号码", "13800138000", "用于接收 DTU 短信和语音通知的手机号码。")];
    /// <summary>DTU gateway settings are plugin-level and are configured in the plugin console.</summary>
    public string DefaultConfigurationJson => "{}";
    /// <summary>SMS and voice each expose one route into the shared singleton gateway service.</summary>
    public int? MaximumInstances => 1;

    public ValueTask<NotificationChannelValidationResult> ValidateConfigurationAsync(string configurationJson, CancellationToken cancellationToken = default)
    {
        try { using var _ = JsonDocument.Parse(configurationJson); return ValueTask.FromResult(NotificationChannelValidationResult.Valid); }
        catch (JsonException) { return ValueTask.FromResult(new NotificationChannelValidationResult(false, ["Channel configuration must be valid JSON."])); }
    }

    public async ValueTask<NotificationDeliveryResult> SendAsync(NotificationDispatchMessage message, CancellationToken cancellationToken = default)
    {
        var recipients = message.Recipients.Select(item => item.Address.Trim()).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).Take(gateway.MaxRecipientsPerSend).ToArray();
        if (recipients.Length == 0) throw new InvalidOperationException("DTU requires at least one phone recipient.");
        DtuSendResult? last = null;
        var hasUnknownAcknowledgement = false;
        foreach (var phone in recipients)
        {
            last = await gateway.SendAsync(kind, phone, message.Body, message.DeliveryId, cancellationToken);
            if (last.IsUnknown)
            {
                hasUnknownAcknowledgement = true;
                continue;
            }
            if (!last.Success) throw new InvalidOperationException(last.Message);
        }
        if (hasUnknownAcknowledgement) return NotificationDeliveryResult.Accepted(message.DeliveryId, "DtuAckPending", "One or more DTU commands were written and await a late ACK.");
        return NotificationDeliveryResult.Delivered(message.DeliveryId, "DtuAck", last?.Message ?? "DTU confirmed delivery.");
    }
}

public sealed record DtuConfiguration(
    bool EnableTcpServer = true,
    int Port = 8000,
    int HeartbeatTimeoutSeconds = 60,
    string Heartbeat = "Heartbeat",
    string SmsSignature = "",
    int SendTimeoutSeconds = 25,
    int TaskIntervalMilliseconds = 1200,
    int QueueCapacity = 1000,
    int MaxRecipientsPerSend = 20,
    bool StripChinaCountryCodeForVoice = true)
{
    public static DtuConfiguration Parse(string json) => (JsonSerializer.Deserialize<DtuConfiguration>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new DtuConfiguration()).Normalize();
    public DtuConfiguration Normalize() => this with
    {
        Port = Math.Clamp(Port, 1, 65535),
        HeartbeatTimeoutSeconds = Math.Clamp(HeartbeatTimeoutSeconds, 10, 3600),
        Heartbeat = string.IsNullOrWhiteSpace(Heartbeat) ? "Heartbeat" : Heartbeat.Trim(),
        SmsSignature = SmsSignature?.Trim() ?? "",
        SendTimeoutSeconds = Math.Clamp(SendTimeoutSeconds, 5, 180),
        TaskIntervalMilliseconds = Math.Clamp(TaskIntervalMilliseconds, 1000, 60000),
        QueueCapacity = Math.Clamp(QueueCapacity, 1, 100000),
        MaxRecipientsPerSend = Math.Clamp(MaxRecipientsPerSend, 1, 200)
    };
    public NotificationChannelValidationResult Validate() => Port is >= 1 and <= 65535 ? NotificationChannelValidationResult.Valid : new(false, ["Port must be between 1 and 65535."]);
}
