# MSIX 打包资源

`AppxManifest.xml` 声明 Windows 11 x64、`packagedClassicApp` / `mediumIL` 和 `runFullTrust`，由构建脚本注入版本及显式提供的身份。默认 `LiveLingo.Development` 只用于未签名开发构建，不代表已注册的商店身份。

`Assets` 中的 LL 字样 PNG 是仓库自有开发图标，尺寸分别为商店 50、磁贴 150 和应用 44 像素；正式商店视觉资源仍需按提交要求准备。`Tools.ps1` 定位 Windows SDK MakeAppx，并仅清理受限定的本次暂存目录。

使用 [New-LiveLingoMsix.ps1](../../scripts/New-LiveLingoMsix.ps1) 构建、[Test-LiveLingoMsix.ps1](../../scripts/Test-LiveLingoMsix.ps1) 校验，不手工修改生成包。完整参数、产物及签名边界见 [交付说明](../../docs/packaging.md#msix-构建)。
