# 正式打包与安装

发布面向 Windows 11 x64，安装目录统一为 `D:\Soft Ware\LiveLingo\app\desktop`。版本只体现在 ZIP 文件名与包清单，不创建带编号的安装目录。用户指南随包交付，源文件为 [USER-GUIDE](../packaging/USER-GUIDE.md)。

## 从源码打包

要求固定 .NET SDK 10.0.401 和 PowerShell 7。在仓库根运行：

```powershell
./scripts/New-LiveLingoPackage.ps1 -Version 0.1.0
```

默认生成 `D:\Soft Ware\LiveLingo\packages\LiveLingo-0.1.0-win-x64.zip` 和同名 `.zip.sha256`。同版本已存在时拒绝覆盖；更新版本号或明确清理旧压缩包后再构建。`-OutputDirectory` 支持其他非 C 盘目录。暂存位于忽略的 `artifacts/packaging/<唯一ID>`，完成后清除本次暂存。

脚本只还原和发布桌面生产项目，按依赖锁执行 `dotnet publish -c Release -r win-x64 --self-contained false`。平台资产由 SDK 按 RID 选择，不手动删 DLL；不启用未经验证的裁剪、AOT 或单文件解包。命令依据：[Microsoft dotnet publish](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-publish)。

包包含 `desktop/` 程序及 worker、cu130 profile/哈希锁、必要模型准备脚本、使用说明和第三方许可；根目录提供 `Install.ps1`、`manifest.json`、`README.md`、`LICENSE` 和 `NOTICE`；程序目录也携带项目许可证与声明。不打入测试工程、Probe、夹具、Spec Kit、历史报告、讨论、录音、模型、虚拟环境、PDB 或缓存。源码保留测试及复现脚本，测试结果和内部验收文档不纳入 Git。

## 安装与升级

将 ZIP 解压到非 C 盘且不位于目标 app 目录内。在解压目录运行：

```powershell
./Install.ps1 -VerifyOnly
./Install.ps1
# 已有安装时：
./Install.ps1 -ReplaceExisting
```

程序须先退出。安装前校验完整文件清单，拒绝额外文件、越界路径、链接和哈希不符；复制后再次校验，然后切换到固定 `desktop` 目录。切换失败尝试恢复旧程序；成功后删除旧程序备份，设置和资产始终在应用目录外。残留事务目录会阻止自动重试，需先检查实际失败。安装不启动程序、不修改系统执行策略、不注册开机启动。

启动：`./scripts/Start-LiveLingo.ps1`，或直接运行正式目录的 EXE。依赖准备、运行限制和卸载见随包用户指南。初版交付为 ZIP + PowerShell 安装包，不宣称已提供签名的 MSI/EXE 安装向导。
