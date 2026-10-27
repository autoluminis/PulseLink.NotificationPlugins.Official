using System.Net.Http.Json;
using System.Text.Json;
using PulseLink.Plugin.Abstractions.Notifications;

namespace PulseLink.NotificationPlugins.WxPusher;

/// <summary>将通知投递至配置的 WxPusher UID 或主题，服务端失败时保留响应信息供重试诊断。</summary>
public sealed class WxPusherChannel : INotificationChannel
{
    private const string DefaultEndpoint = "https://wxpusher.zjiecode.com/api/send/message";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public string Key => "wxpusher";

    /// <inheritdoc />
    public string DisplayName => "WxPusher";

    /// <inheritdoc />
    public bool IsBroadcast => false;

    /// <summary>声明可接收通知的 WxPusher UID 联系方式。</summary>
    public IReadOnlyList<NotificationContactTypeDefinition> ContactTypes =>
    [
        new("WxPusherUid", "WxPusher UID", "WxPusher UID", "UID_xxxxxxxxxxxxxxxxx", "用于接收通知的 WxPusher 用户标识。")
    ];

    /// <summary>为新渠道提供 AppToken、消息类型、主题及付费订阅筛选等默认配置模板。</summary>
    public string DefaultConfigurationJson => """
        {
          "appToken": "AT_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
          "contentType": 1,
          "topicId": null,
          "verifyPayType": 0,
          "url": "https://wxpusher.zjiecode.com/api/send/message"
        }
        """;

    /// <summary>校验 WxPusher 配置的 JSON 格式、令牌、消息类型、主题、订阅筛选和服务地址。</summary>
    public ValueTask<NotificationChannelValidationResult> ValidateConfigurationAsync(string configurationJson, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var configuration = JsonSerializer.Deserialize<WxPusherConfiguration>(configurationJson, JsonOptions);
            var errors = Validate(configuration);
            return ValueTask.FromResult(errors.Count == 0
                ? NotificationChannelValidationResult.Valid
                : new NotificationChannelValidationResult(false, errors));
        }
        catch (JsonException)
        {
            return ValueTask.FromResult(new NotificationChannelValidationResult(false, ["Configuration must be valid JSON."]));
        }
    }

    /// <summary>向解析后的 UID 或主题发送通知，并保留服务端响应供投递诊断。</summary>
    public async ValueTask<NotificationDeliveryResult> SendAsync(NotificationDispatchMessage message, CancellationToken cancellationToken = default)
    {
        var configuration = JsonSerializer.Deserialize<WxPusherConfiguration>(message.ConfigurationJson, JsonOptions);
        var errors = Validate(configuration);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"WxPusher configuration is invalid: {string.Join("; ", errors)}");
        }
        var validConfiguration = configuration!;

        var uids = message.Recipients
            .Where(recipient => string.Equals(recipient.EndpointType, "WxPusherUid", StringComparison.OrdinalIgnoreCase))
            .Select(recipient => recipient.Address.Trim())
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (uids.Length == 0 && validConfiguration.TopicId is null)
        {
            throw new InvalidOperationException("WxPusher requires at least one WxPusher UID recipient or a configured topicId.");
        }

        var payload = new WxPusherRequest(
            validConfiguration.AppToken.Trim(),
            message.Body,
            message.Subject,
            validConfiguration.ContentType,
            uids.Length == 0 ? null : uids,
            validConfiguration.TopicId is null ? null : [validConfiguration.TopicId.Value],
            validConfiguration.VerifyPayType ?? (validConfiguration.VerifyPay ? 1 : 0));

        using var request = new HttpRequestMessage(HttpMethod.Post, validConfiguration.Url.Trim())
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.TryAddWithoutValidation("X-PulseLink-Delivery-Id", message.DeliveryId);

        using var response = await Client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"WxPusher returned HTTP {(int)response.StatusCode}: {responseBody}", null, response.StatusCode);
        }

        var providerMessageId = ValidateWxPusherResponse(responseBody);
        return NotificationDeliveryResult.Accepted(providerMessageId, $"HTTP {(int)response.StatusCode}", responseBody.Length <= 4096 ? responseBody : responseBody[..4096]);
    }

    private static List<string> Validate(WxPusherConfiguration? configuration)
    {
        var errors = new List<string>();
        if (configuration is null) return ["Configuration cannot be empty."];
        if (string.IsNullOrWhiteSpace(configuration.AppToken)) errors.Add("appToken is required.");
        if (configuration.ContentType is not 1 and not 2 and not 3) errors.Add("contentType must be 1 (text), 2 (HTML), or 3 (Markdown).");
        if (configuration.TopicId is <= 0) errors.Add("topicId must be a positive integer when supplied.");
        if (configuration.VerifyPayType is < 0 or > 2) errors.Add("verifyPayType must be 0 (all users), 1 (paid subscribers), or 2 (unsubscribed or expired users).");
        if (!Uri.TryCreate(configuration.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) errors.Add("url must be an absolute HTTP(S) URL.");
        return errors;
    }

    /// <summary>验证 WxPusher 响应，并提取服务端分配的消息 ID。</summary>
    private static string? ValidateWxPusherResponse(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var successful = !root.TryGetProperty("success", out var success) || success.ValueKind is not JsonValueKind.False;
            var validCode = !root.TryGetProperty("code", out var code) || code.ValueKind is not JsonValueKind.Number || code.GetInt32() == 1000;
            if (successful && validCode) return GetMessageId(root);

            var detail = root.TryGetProperty("msg", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : responseBody;
            throw new InvalidOperationException($"WxPusher rejected the notification: {detail}");
        }
        catch (JsonException)
        {
            // A successful HTTP response is accepted when a proxy or older API does not return JSON.
            return null;
        }
    }

    private static string? GetMessageId(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
        {
            return GetMessageId(data[0]);
        }

        // WxPusher now exposes messageContentId for the accepted message; messageId is deprecated.
        if (root.TryGetProperty("messageContentId", out var messageContentId))
        {
            return GetScalarId(messageContentId);
        }

        if (root.TryGetProperty("messageId", out var legacyMessageId)) return GetScalarId(legacyMessageId);
        return null;
    }

    private static string? GetScalarId(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };

    private sealed class WxPusherConfiguration
    {
        public string AppToken { get; init; } = string.Empty;
        public int ContentType { get; init; } = 1;
        public int? TopicId { get; init; }
        // Legacy setting retained for existing channel configurations; the request uses verifyPayType.
        public bool VerifyPay { get; init; }
        public int? VerifyPayType { get; init; }
        public string Url { get; init; } = DefaultEndpoint;
    }

    private sealed record WxPusherRequest(
        string AppToken,
        string Content,
        string Summary,
        int ContentType,
        IReadOnlyList<string>? Uids,
        IReadOnlyList<int>? TopicIds,
        int VerifyPayType);
}
