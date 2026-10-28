# PulseLink 官方通知插件库

本解决方案统一维护官方通知插件，并通过仓库根目录 `Directory.Packages.props` 锁定 SDK 包版本。当前基线为 `PulseLink.Plugin.Abstractions` 26.10.27.4，对应 PulseLink SDK 发布版本 26.10.27.004，插件 API v2。

## 项目

- `PulseLink.NotificationPlugins.WxPusher`：WxPusher 官方通知插件。
- `PulseLink.NotificationPlugins.Dtu`：DTU 短信/语音网关官方通知插件。

## 许可证与商业授权

本仓库采用 [PulseLink 官方通知插件非商业使用及商业授权协议](LICENSE)，标识为 `LicenseRef-PulseLink-Noncommercial-Commercial`。这是源码可见的受限许可。

个人非商业使用、非商业学习、教学及学术研究可按协议免费使用；**除下述 PulseLink 已购许可权益外，商业使用必须事先取得书面商业授权并支付授权费用**。企业内部生产使用、客户交付、产品集成、收费服务和 SaaS 均属于商业使用，即使未对插件单独收费也需要授权。经营主体可免费进行内部隔离评估，但不得用于生产或实际业务。

**已购买并持有有效 PulseLink 许可授权的用户，通过 PulseLink 官方在线插件商城安装本插件后，可在该 PulseLink 许可授权范围内商业使用，无需另行购买插件授权或支付插件授权费。** 合法安装后的离线使用同样适用。该权益不扩大原许可的主体、期限、实例及部署范围，不包含插件的独立销售、转授权或向未获 PulseLink 许可的第三方分发；自行构建、仓库下载及其他渠道副本不自动享有此权益。

授权价格、期限、部署数量及支持范围由商业授权合同约定。请通过 [Autoluminis 主页](https://github.com/autoluminis) 公布的联系方式咨询。第三方 SDK 和依赖遵循各自许可证；本协议不追溯变更此前版本已授予的许可。构建和打包输出均附带 `LICENSE`。

## 构建

```powershell
dotnet restore .\PulseLink.NotificationPlugins.Official.sln --configfile .\NuGet.Config
dotnet build .\PulseLink.NotificationPlugins.Official.sln -c Release --no-restore
```

## 打包

```powershell
.\Pack-Plugins.ps1
```

打包输出位于 `artifacts/`，文件名包含插件 ID 和版本，例如 `pulselink.wxpusher-1.0.1.zip`。包内包含插件程序集、清单及插件 UI 内容。此流程生成可在 PulseLink 插件管理页上传的 ZIP；正式发布仍应使用受控私钥通过 SDK packager 签名。

部署前，在 PulseLink 主程序配置 `PulseLink:Notifications:Plugins:AllowedPluginIds`，加入需要启用的插件 ID（`pulselink.wxpusher`、`pulselink.dtu`），然后在“通知中心 → 插件”上传对应 ZIP。环境变量形式为 `PULSELINK_NOTIFICATION_PLUGIN_ALLOWLIST`。配置项属于 PulseLink 主程序，不需要添加到本仓库的插件 JSON 清单。

各插件的操作、渠道和控制台配置说明见对应目录中的 README。

## SDK 更新

先在 PulseLink 仓库运行 `scripts/Build-NotificationPluginSdk.ps1`，再复制对应版本的 `PulseLink.Plugin.Abstractions.*.nupkg` 到 `packages/`，并同步更新 `Directory.Packages.props` 中唯一的 SDK 版本。之后恢复、构建并打包全解。

插件源码、打包与操作文档由本仓库维护。PulseLink 主仓库负责宿主程序及其插件 SDK；更新 SDK 时按上述步骤同步包版本。
