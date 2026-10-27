using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using PulseLink.Plugin.Abstractions.Notifications;

namespace PulseLink.NotificationPlugins.WxPusher;

/// <summary>注册通过 WxPusher 向用户或主题发送报警通知的示例插件。</summary>
public sealed class WxPusherPlugin : INotificationChannelPlugin
{
    /// <inheritdoc />
    public string Id => "pulselink.wxpusher";

    /// <inheritdoc />
    public string DisplayName => "WxPusher";

    /// <inheritdoc />
    public Version Version => Version.Parse(typeof(WxPusherPlugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "PulseLinkPluginVersion").Value!);

    /// <inheritdoc />
    public IEnumerable<INotificationChannel> CreateChannels() => [new WxPusherChannel()];

    /// <inheritdoc />
    public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
