# WxPusher notification plugin

This PulseLink notification plugin sends alarm notifications through [WxPusher](https://wxpusher.zjiecode.com/).

## Configuration

Create a `pulselink.wxpusher/wxpusher` channel instance with configuration such as:

```json
{
  "appToken": "AT_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
  "contentType": 1,
  "topicId": null,
  "verifyPay": false,
  "url": "https://wxpusher.zjiecode.com/api/send/message"
}
```

- `appToken` is required and is issued by WxPusher.
- `contentType` is `1` for plain text or `2` for HTML.
- `topicId` is optional. It enables topic delivery when no user UID recipient is resolved.
- `verifyPay` enables WxPusher paid-content validation when required by the application.

The plugin declares a `WxPusherUid` contact type. Add each recipient's WxPusher UID in PulseLink's user center, then select the channel in a notification policy. A delivery is sent to all resolved, distinct UIDs; when `topicId` is supplied, it is delivered to that topic as well.

## Package and install

From the repository root, create the installable package:

```powershell
./samples/PulseLink.NotificationPlugins.WxPusher/Pack-Plugin.ps1
```

The script writes `artifacts/pulselink.wxpusher.zip` and refreshes the assembly SHA-256 in the package manifest. Add `pulselink.wxpusher` to `PulseLink:Notifications:Plugins:AllowedPluginIds` (or `PULSELINK_NOTIFICATION_PLUGIN_ALLOWLIST`) before uploading the ZIP through the notification plugin management API/UI.
