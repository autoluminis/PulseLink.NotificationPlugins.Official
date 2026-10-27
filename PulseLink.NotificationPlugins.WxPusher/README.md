# PulseLink WxPusher 通知插件

本插件通过 WxPusher 标准推送 API 发送 PulseLink 报警和通知。接口字段和线上行为以 [WxPusher 官方开发文档](https://wxpusher.zjiecode.com/docs/) 及[发送消息 API](https://wxpusher.zjiecode.com/docs/api-reference.html) 为准。

插件市场图标为 `WxPusher.png`，并随插件包一起发布。

## 配置

在 PulseLink 中创建 `pulselink.wxpusher/wxpusher` 渠道实例，配置示例：

```json
{
  "appToken": "AT_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
  "contentType": 1,
  "topicId": null,
  "verifyPayType": 0,
  "url": "https://wxpusher.zjiecode.com/api/send/message"
}
```

- `appToken`：必填，在 WxPusher 管理后台创建应用后获取。它是密钥，请勿提交真实值到代码仓库。
- `contentType`：消息类型，`1` 为纯文本，`2` 为 HTML，`3` 为 Markdown。
- `topicId`：可选的主题 ID。配置后，即使没有解析到 UID 收件人，也可以向该主题发送；若同时有 UID 收件人，则 UID 和主题都会收到。
- `verifyPayType`：订阅筛选方式。`0` 不筛选，`1` 仅发送给付费订阅用户，`2` 仅发送给未订阅或订阅已过期的用户。默认为 `0`。
- `url`：发送消息 API 地址，默认值为示例中的 WxPusher 官方接口地址。

插件声明了 `WxPusherUid` 联系方式。请在 PulseLink 用户中心为收件人添加 WxPusher UID，再在通知策略中选择该渠道。插件会向解析到的不同 UID 发送通知；配置 `topicId` 时也会同时向主题发送。

旧渠道配置中的 `verifyPay` 布尔值仍兼容：`true` 按 `verifyPayType: 1` 处理，`false` 按 `verifyPayType: 0` 处理。新配置请使用 `verifyPayType`。

WxPusher 接口成功表示发送任务已受理，实际推送由 WxPusher 异步处理；接口受理成功不代表用户设备已收到通知。

## 打包与安装

在官方插件仓库根目录运行以下脚本，构建并打包所有官方通知插件：

```powershell
.\Pack-Plugins.ps1
```

版本化 ZIP 输出到 `artifacts/`，当前 WxPusher 版本示例为 `pulselink.wxpusher-1.0.1.zip`。打包脚本会计算程序集 SHA-256 并写入包内 `plugin.json`。也可在本插件目录运行 `./Pack-Plugin.ps1`，单独生成 WxPusher 安装包。

上传前，在 PulseLink 宿主配置 `PulseLink:Notifications:Plugins:AllowedPluginIds` 中加入 `pulselink.wxpusher`，或设置环境变量 `PULSELINK_NOTIFICATION_PLUGIN_ALLOWLIST`。然后在 PulseLink“通知中心 → 插件”中上传 ZIP。

安装后创建并启用 `wxpusher` 渠道实例，将该实例关联到通知策略。渠道 JSON 中应保存本实例的 `appToken` 等参数；宿主插件白名单只配置插件 ID，不要放入 AppToken。
