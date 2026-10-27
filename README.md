# PulseLink 官方通知插件库

本解决方案统一维护官方通知插件，并通过仓库根目录 `Directory.Packages.props` 锁定 SDK 包版本。当前基线为 `PulseLink.Plugin.Abstractions` 26.10.27.4，对应 PulseLink SDK 发布版本 26.10.27.004，插件 API v2。

## 项目

- `PulseLink.NotificationPlugins.WxPusher`：WxPusher 官方通知插件。
- `PulseLink.NotificationPlugins.Dtu`：DTU 短信/语音网关官方通知插件。

## 构建

```powershell
dotnet restore .\PulseLink.NotificationPlugins.Official.sln --configfile .\NuGet.Config
dotnet build .\PulseLink.NotificationPlugins.Official.sln -c Release --no-restore
```

## 打包

```powershell
.\Pack-Plugins.ps1
```

打包输出位于 `artifacts/`，包内包含插件程序集、清单及 UI 内容。此流程生成可在 PulseLink 插件管理页上传的 ZIP；正式发布仍应使用受控私钥通过 SDK packager 签名。

## SDK 更新

先在 PulseLink 仓库运行 `scripts/Build-NotificationPluginSdk.ps1`，再复制对应版本的 `PulseLink.Plugin.Abstractions.*.nupkg` 到 `packages/`，并同步更新 `Directory.Packages.props` 中唯一的 SDK 版本。之后恢复、构建并打包全解。

许可证与上游源码仍由 PulseLink 主仓库维护；此目录是迁出的官方插件解决方案工作副本。
