# 打包、增量发布与安装回退

本文覆盖 Velopack 增量发布、MSIX 构建，以及保留的旧 ZIP 手工替换流程。运行依赖与日常操作见 [用户指南](../packaging/USER-GUIDE.md)。

## Velopack 发布

固定 SDK 与本地 CLI 为 1.2.161（MIT）；不使用 Velopack Flow。生产程序保持 framework-dependent，安装器声明 `net10.0-x64-runtime`，缺失时在线安装；外部 .NET 安装脚本使用 winget。模型、Python 环境、Ollama、驱动与 .NET 不入包，必要脚本、worker、profile/锁和第三方许可随包。

当前稳定版本为 [v0.2.9](https://github.com/Patrick-Rex/LiveLingo/releases/tag/v0.2.9)，支持四种显式识别语言、可选词库和字幕回看。Windows x64 客户端通过固定的 [自动更新渠道](https://github.com/Patrick-Rex/LiveLingo/releases/tag/updates-win-x64) 获取更新。v0.2.1 曾因原生增量清单冲突标记为预发布。以下命令展示从 0.2.8 验证 0.2.9 差分的过程，在仓库根目录使用 PowerShell 7 执行。

```powershell
# 首次建立更新渠道时，先在输出目录放入与 GitHub v0.2.8 SHA-256 一致的上一版 full。
./scripts/New-LiveLingoRelease.ps1 -Version 0.2.9
./scripts/Test-LiveLingoRelease.ps1 -FullPackage artifacts/releases/LiveLingo-0.2.9-win-x64-full.nupkg -PreviousPackage artifacts/releases/LiveLingo-0.2.8-win-x64-full.nupkg -DeltaPackage artifacts/releases/LiveLingo-0.2.9-win-x64-delta.nupkg
```

实际文件名以输出为准（默认 win-x64 渠道可能带渠道后缀）；差分验证同时传 `-PreviousPackage <旧full> -DeltaPackage <新delta>`，使用随包原生 Update.exe 在隔离目录还原并逐项比较 SHA-256。完整包与还原包均检查唯一的根应用清单。打包只生成本地产物，不上传。首版无 delta；后续同一输出目录有旧 full 时自动生成差分。保留全包供跨版本回退下载。`-OutputDirectory` 可覆盖输出位置，同版本不覆盖。

构建目录包含 Setup.exe、full/delta nupkg、渠道 JSON/RELEASES 和 SHA256SUMS，不直接整目录上传。构建前拒绝残留独立脚本、指南和其他非预期文件；构建校验清单显式选择当前安装器、full/delta 与渠道清单。`assets.win-x64.json` 和兼容用 `RELEASES-win-x64` 留在本地。发布前分流：

```powershell
./scripts/Export-LiveLingoRelease.ps1 -Version 0.2.6 -ReleaseDirectory artifacts/releases
```

默认输出 `artifacts/distribution/0.2.6`，可用 `-OutputDirectory` 指定新的空输出位置。`installers/` 只含 EXE；可显式传 `-MsixPackage <路径>` 添加同版 MSIX，报告记录签名状态，当前未签名开发包不自动加入正式安装下载。`updates/` 仅含 `releases.win-x64.json` 与其引用的全部 full/delta。哈希清单及分流报告留在输出根，供发布核验，不作为普通版本附件。导出先验证每个包的名称、尺寸、SHA-256、当前版本和缺失引用，拒绝路径越界及覆盖已有输出；不上传。

同仓库使用两个入口：

- 普通版本 `v0.2.6` 等：只上传 `installers/` 的正式安装包，设为 latest。GitHub 自动显示的源码归档由平台生成，不属于手动上传的附件。
- 固定 tag `updates-win-x64`：更新专用 Release，设为正式发布（非 prerelease）、不设为 latest。上传 `updates/` 的包和清单，所有引用文件必须存在于此 Release。新客户端使用 `https://github.com/Patrick-Rex/LiveLingo/releases/download/updates-win-x64/`，不使用 GitHub API 或 latest 别名，也不内置令牌。

先上传带版本号的 nupkg 并验证远程大小／SHA-256，最后更新渠道 JSON，再发布普通安装包版本。不要覆盖已发布的同名版本包；不要删除旧清单仍引用的包。专用 Release 需要可更新附件，不能启用 immutable release；每次替换清单的短暂失败由应用提示重试。若需要保留多版本增量链，保留本地历史 full/delta 后再构建、导出完整清单。`-DownloadPrevious` 仅从固定更新源取上一 full，不能恢复完整历史链；新目录缺少旧 delta 时，旧客户端可回退完整包。首轮建立更新源时需先手动提供上一已验证 full，并省略 `-DownloadPrevious`。本地导出不等于线上发布完成。

旧 0.2.5 使用 GitHub API 查前 10 个 Release，正常时能从其中正式发布的更新专用 Release 发现新版，普通安装版本缺少清单会被跳过。旧版遇 403/429 会回退 `latest/download`；新的普通版本只含安装包后，此旧回退会失效，固定专用 Release 离开前 10 项也会影响尚未迁移的旧版。此时等待限流结束重试，或运行新版安装器迁移；不能承诺旧客户端永久自动迁移。先建立并核验专用更新源，再迁移普通发布附件，保留可回退包；新客户端不受旧列表和 latest 路径限制。

Setup.exe 是普通用户唯一安装入口，.NET 缺项由安装器在线补齐，首次使用默认用户目录，明确自定义目录可传 `--installto`。Velopack 使用 current/packages 布局和稳定启动入口。仓库中的安装 PowerShell 保留为高级自动化入口。旧 ZIP 的 app/desktop 不原地转换；先关闭旧版并独立安装，默认 data/settings.json 与模型保持复用。以下第1–7节只描述旧 ZIP 布局，不用于修改 Velopack 的 current 或 packages。

完成托管安装及更新核对、不再需要旧版回退后，可清理旧 desktop、旧 ZIP、解压暂存、验证副本及发布临时目录。仓库启动脚本默认通过托管安装根的 LiveLingo.exe 启动；指定 ApplicationDirectory 时也支持独立发布目录。保留托管安装 packages 中的当前完整包供后续差分还原；模型、Python、共享 Ollama 权重、设置与历史不属于安装残留。


## MSIX 构建

MSIX 使用 Windows SDK 的 MakeAppx，面向 Windows 11 x64，与 EXE 使用独立安装及更新生命周期。MSIX 自带应用所需的 `Microsoft.NETCore.App` 运行时，不包含 ASP.NET Core、Windows Desktop Runtime、Python、模型、Ollama 或驱动；保留本地模式所需的小型脚本、worker、profile 和许可。发布不启用裁剪、AOT 或单文件，避免改变现有 Avalonia 与依赖的运行行为。

安装 Windows SDK 10.0.26100.0 或更新版本，在仓库根 PowerShell 7 执行：

```powershell
./scripts/New-LiveLingoMsix.ps1 -Version 0.2.6
./scripts/Test-LiveLingoMsix.ps1 -Package artifacts/msix/LiveLingo-0.2.6-win-x64-unsigned.msix
```

脚本默认查找本机 Windows SDK，也可用 `-MakeAppxPath <绝对路径>` 指定工具；`-OutputDirectory` 可覆盖产物目录，同名包拒绝覆盖。脚本使用 locked restore 和 self-contained publish，复用生产载荷与第三方许可收集，在隔离暂存中组装包并清理中间 ZIP。输出目录只新增一个 `*-unsigned.msix`，哈希打印到终端，不附带安装脚本、证书或额外 ZIP。构建完成自动执行包校验：SDK 解包、Windows 11 x64 full-trust 清单、运行时与许可、必要文件和排除项。它不安装、不启动、不上传程序。

未提供正式身份时使用 `LiveLingo.Development` 和带微软未签名测试 OID 的开发 Publisher；与未来正式包身份隔离，不承诺原位升级。未来取得 Partner Center 身份或侧载签名证书后，同时传 `-IdentityName <正式名称> -Publisher <精确发布者>`，可用 `-PublisherDisplayName` 设置显示名；脚本仍只生成未签名包。三段版本映射为 `主.次.修订.0`，最后一段保留为零。清单、工具及开发图标见 [MSIX 资源说明](../packaging/msix/README.md)。

当前没有签名证书或商店关联，包不能作为普通用户双击安装的正式下载。商店提交必须使用 Partner Center 分配的身份并完成认证；侧载分发须使用目标设备信任的签名。签名和商店提交未接入本脚本，`runFullTrust` 能力的商店审核也未验证。参考 [Microsoft 未签名包测试](https://learn.microsoft.com/en-us/windows/msix/package/unsigned-package)和 [MSIX 签名说明](https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview)。

MSIX 启动依据真实 Windows 包身份跳过 Velopack，仅展示 Windows／商店更新说明，不访问 EXE 更新源。首次没有设置文件时默认双 API，已有本地或 API 设置继续复用；API 地址、模型及凭据由用户配置，不内置服务商密钥。数据与大型资源可放在任一本机可写盘。构建和单元测试不替代实际注册安装、字幕采集、外部服务商联调或商店认证。

## 1. 区分构建产物与正式安装

| 内容 | 默认位置及用途 |
| --- | --- |
| 源码与构建输出 | 仓库及各项目 bin/obj；构建成功不会替换正在使用的程序 |
| 发布包 | 仓库 artifacts/packages，可用 -OutputDirectory 覆盖；ZIP 与同名 .zip.sha256 |
| 正式安装 | %LOCALAPPDATA%\LiveLingoData\app\desktop |
| 用户设置 | %LOCALAPPDATA%\LiveLingoData\data\settings.json；使用 --settings 时以实际路径为准 |
| 模型、Python 环境及缓存 | models、runtimes、cache 和独立的 Ollama 权重目录；不随程序替换 |

打包会读取当前工作树，包括未提交的代码与说明。发布前确认这些改动正是本次要交付的内容。文档状态或历史报告不证明当前安装已更新，须以实际安装文件核对。

## 2. 从源码验证并打包

使用 PowerShell 7、Git 和 .NET 10 稳定 SDK（10.0.100 起，global.json 允许后续功能带及补丁）。运行机器需要 Windows 11 x64、.NET 10.0 x64 Runtime（兼容各稳定补丁版本，建议使用最新维护版本），以及所选本地阶段的模型环境或 API 配置；程序按依赖运行时方式发布，包不包含 .NET Runtime。

以下命令在仓库根、同一 PowerShell 7 终端顺序执行。0.1.1 只是下一次打包的示例版本，不表示已发布；按本次实际版本修改。同版本 ZIP 或校验文件存在时脚本拒绝覆盖，可选新版本或新的可写输出目录，保留可回退旧包。源码、打包、安装和回归测试均可使用本机可写盘。

```powershell
$ErrorActionPreference = 'Stop'
$packageVersion = '0.1.1'
$packageOutput = Join-Path $PWD 'artifacts/packages'
$installTestRoot = Join-Path $env:TEMP 'LiveLingo-package-tests'
New-Item -ItemType Directory -Path $installTestRoot -Force | Out-Null
$env:TEMP = $installTestRoot
$env:TMP = $installTestRoot
git status --short

# 发布前按改动范围验证；下面是完整 .NET 回归示例。
$buildLog = Join-Path $env:TEMP ('delivery-build-' + [guid]::NewGuid().ToString('N') + '.log')
dotnet restore LiveLingo.slnx --locked-mode *> $buildLog
if ($LASTEXITCODE -ne 0) {
    Select-String -LiteralPath $buildLog -Pattern '\berror\b'
    throw "Restore failed; log=$buildLog"
}
dotnet build LiveLingo.slnx -c Release --no-restore --disable-build-servers -clp:ErrorsOnly *> $buildLog
$buildExit = $LASTEXITCODE
Select-String -LiteralPath $buildLog -Pattern '\berror\b'
if ($buildExit -ne 0) { throw "BuildExit=$buildExit; log=$buildLog" }
dotnet test --solution LiveLingo.slnx -c Release --no-build --no-progress
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

./scripts/New-LiveLingoPackage.ps1 -Version $packageVersion -OutputDirectory $packageOutput
$archive = Join-Path $packageOutput "LiveLingo-$packageVersion-win-x64.zip"
./scripts/Test-LiveLingoPackage.ps1 -Archive $archive -TemporaryRoot $installTestRoot
```

测试还须有实际通过数量，零测试不能算通过；检查范围见 [测试说明](../tests/README.md)。纯文档修改无需运行上述业务构建或测试；需要生成新分发包时，打包脚本会自行执行生产项目还原与发布。涉及原生交互、采集或模型路径的变更，按实际影响补充相应检查，安装包测试不能替代它们。

New-LiveLingoPackage.ps1 内部使用 locked restore 和 `dotnet publish -c Release -r win-x64 --self-contained false`，只发布生产工程；不启用裁剪、AOT 或单文件。NuGet 使用本机配置及已有环境变量；如需指定开发缓存，预先执行 `. ./scripts/Enter-Development.ps1 -CacheRoot <可写绝对路径>`。第三方许可从还原结果记录的包目录收集，不要求设置 NUGET_PACKAGES。暂存位于仓库忽略的 artifacts/packaging/唯一目录，完成后清理。Publish-LiveLingo.ps1 默认输出到仓库 artifacts/publish/desktop，可用 -OutputDirectory 覆盖；它要求空输出目录，正式替换使用安装器。

包内包含 desktop 程序、worker、CPU、ROCm、cu128 和 cu130 profile 及哈希锁、必要依赖准备脚本、整体模型配置模板 model-config.example.json、用户指南和第三方许可。第三方 NuGet 元数据原文以 `.nuspec.xml` 保存，避免原生增量还原后的 ZIP 顺序使其被误识别为应用清单；nupkg 只能有根 `LiveLingo.nuspec`。根目录含 Install.ps1、manifest.json、README.md、LICENSE、NOTICE。模型、虚拟环境、录音、测试、历史报告、缓存和 PDB 不入包。ZIP 校验覆盖整个归档，manifest 逐项校验 desktop 下的应用文件。

Test-LiveLingoPackage.ps1 在 -TemporaryRoot 指定的本地盘下创建独立临时目录（省略时使用当前 TEMP），执行 10 项检查，覆盖 ZIP/应用哈希、损坏/额外文件/越界路径拒绝、相对路径拒绝、首次安装、拒绝隐式覆盖、显式替换保留设置及安装文件一致性，完成后清理。它不会替换正式安装，也不会启动 GPU 模型。

## 3. 关闭程序并留存回退资料

在控制窗停止会话、保存运行设置并关闭程序，等待本程序的模型进程退出；不结束浏览器或用户原有 Ollama。保留上一份已验证 ZIP 与 .sha256。安装器成功后会删除 .desktop-backup，它只是事务备份，不是长期回退副本。

替换前，按下列方式另存当前程序与设置；首次安装没有旧程序时只会备份实际存在的设置。下面变量在后续安装步骤继续使用；自定义设置路径须同步修改。

```powershell
$resourceRoot = Join-Path $env:LOCALAPPDATA 'LiveLingoData'
$applicationRoot = Join-Path $resourceRoot 'app'
$installedDirectory = Join-Path $applicationRoot 'desktop'
$settingsPath = Join-Path $resourceRoot 'data\settings.json'
$backupRoot = Join-Path (Join-Path $resourceRoot 'backups') ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backupRoot | Out-Null
if (Test-Path -LiteralPath $installedDirectory) {
    Copy-Item -LiteralPath $installedDirectory -Destination (Join-Path $backupRoot 'desktop') -Recurse
}
if (Test-Path -LiteralPath $settingsPath) {
    Copy-Item -LiteralPath $settingsPath -Destination (Join-Path $backupRoot 'settings.json')
}
Write-Output "RollbackBackup=$backupRoot"
```

确认备份目录内容完整后继续。升级保持 app 外的模型、环境和数据不变，通常不必重新下载。若本次另行修改 Python 依赖或模型，还需保留相应旧运行配置与资产；只回退 desktop 不能撤销这些外部变更。

## 4. 校验、解压并安装

继续在同一终端运行，使用第 2 步的 $archive。只有安装包时，先将 $archive 设为实际 ZIP 绝对路径；下载的包须来自可信来源，SHA-256 检查不代替签名。

```powershell
$expectedHash = ((Get-Content -LiteralPath "$archive.sha256" -Raw).Trim() -split '\s+')[0]
$actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if ($expectedHash -notmatch '^[a-fA-F0-9]{64}$' -or $actualHash -ne $expectedHash) {
    throw 'ZIP SHA-256 mismatch.'
}
$extractDirectory = Join-Path (Join-Path $resourceRoot 'packages\staging') ([guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $archive -DestinationPath $extractDirectory
$installer = Join-Path $extractDirectory 'Install.ps1'
& $installer -VerifyOnly
```

解压目录须位于目标 app 目录之外。VerifyOnly 只核对包内容，不检查运行环境或证明程序可启动。核对包后，在下面两条命令中选择一条：

```powershell
# 首次安装，目标 desktop 不存在：
& $installer -ApplicationRoot $applicationRoot

# 替换已有 LiveLingo 安装：
& $installer -ApplicationRoot $applicationRoot -ReplaceExisting
```

安装器支持 64 位 PowerShell 5.1 或 7，要求目标是本机绝对路径，末级为 app，程序目录始终为 desktop。更改 ApplicationRoot 不迁移模型或设置。安装会检查运行时、应用占用、链接、越界和文件哈希，复制到 .desktop-install 并复核，再切换目录；切换失败时尝试恢复旧程序。成功后输出 Installed=版本和实际 EXE 路径，清理事务旧备份。

安装不会启动程序、修改执行策略或注册开机启动。组织策略禁止执行时按组织规则处理，不用安装步骤绕过策略。

## 5. 确认替换结果并启动

使用新包 manifest 核对正式目录，避免只看到编译成功就误以为安装完成：

```powershell
$manifest = Get-Content -LiteralPath (Join-Path $extractDirectory 'manifest.json') -Raw | ConvertFrom-Json
foreach ($entry in $manifest.files) {
    $installedFile = Join-Path $installedDirectory $entry.path
    if (-not (Test-Path -LiteralPath $installedFile -PathType Leaf) -or
        (Get-Item -LiteralPath $installedFile).Length -ne $entry.bytes -or
        (Get-FileHash -LiteralPath $installedFile -Algorithm SHA256).Hash -ne $entry.sha256) {
        throw "Installed file mismatch: $($entry.path)"
    }
}
Write-Output "Installed files match package $($manifest.version)"
& (Join-Path $installedDirectory 'LiveLingo.Desktop.Windows.exe')
```

已有环境直接复用；首次使用先按 [用户指南](../packaging/USER-GUIDE.md#首次准备依赖)仅为所选本地阶段准备 所选 Python 推理环境、Qwen 或 TranslateGemma；全 API 只需配置接口。启动后检查路径和设置，选择实际发声应用，验证字幕、右上角解锁/锁定、拖动缩放、显示模式、热键及设置保存，最后关闭并确认自有模型进程退出。真实模型、原生输入和多屏等检查分别记录，不能从包哈希通过推断这些行为通过。

确认运行正常后可删除此次唯一解压目录，保留 ZIP、校验文件和回退备份。不要把 models、runtimes 或 data 当作安装暂存删除。需要记录本次结果时新建按日期/版本命名的记录。

## 6. 回退与失败恢复

**新版本安装成功但运行异常：**关闭新程序，保留错误信息和当前设置副本。校验并解压上一份已验证 ZIP，使用其中的 Install.ps1 对同一 ApplicationRoot 执行 -ReplaceExisting；然后按旧包 manifest 核对文件并启动。安装器不禁止版本降级，也不自动回退 settings.json。若配置不兼容，在所有 LiveLingo 实例退出后保留当前文件，再恢复第 3 步的 settings.json；自定义 --settings 使用对应路径。恢复旧设置会丢弃备份之后的设置变化。

**没有可用旧 ZIP：**第 3 步的 desktop 副本可以用于人工恢复。关闭应用，核实备份和正式路径，将当前 desktop 重命名保留为故障副本，再把备份的 desktop 完整复制到 app/desktop；不要逐文件覆盖新旧版本。确认恢复成功后再处理故障副本。备份不是带 manifest 的安装包，不要伪造包清单或直接当作 Install.ps1 输入。

**安装中途失败：**先读取错误，检查正式 desktop、.desktop-install 和 .desktop-backup。不要反复执行 -ReplaceExisting 或直接删除旧备份。

| 现场状态 | 处理 |
| --- | --- |
| desktop 存在，无事务残留 | 核对实际版本；修正占用、权限、运行时或坏包后再安装 |
| desktop 不存在，.desktop-backup 存在 | 保留现场，核对旧 EXE 和备份内容后恢复为 desktop，再验证旧版 |
| desktop 与 .desktop-backup 同时存在 | 可能已切换但旧备份清理失败；核对 desktop 是否匹配新包，保留旧备份直到确认 |
| 仅有 .desktop-install，或目录状态不明 | 保留并检查暂存、已保存的旧包和独立备份，不把暂存自动视为完整安装 |

人工移动或清理前，核实解析后的绝对路径确在选定的 app 或本次唯一暂存目录内，并检查没有链接；不能确认时停止并报告目录状态。安装器会拒绝带事务残留的重试，也不自动清除未知残留。

## 7. 首次准备与卸载

[用户指南](../packaging/USER-GUIDE.md)提供 CPU、AMD ROCm 与 NVIDIA CUDA 独立环境、Qwen 固定 revision、Ollama 模型准备及常用操作。依赖准备只在首次使用或明确改变配置时进行，程序替换不等于重新初始化环境。

卸载先退出程序，只处理目标 app/desktop；模型、Python 环境、设置及数据独立保留，按需要另外处理。不要删除其他应用共享的 Ollama 权重。上述卸载方式仅适用于旧 ZIP。Velopack 安装使用 Windows“已安装的应用”卸载；模型和数据独立保留。

发布清单 releases.win-x64.json 也用于 GitHub API 403/429 时的公开下载回退，因此必须与当前 full/delta 一起上传。回退不使用用户凭据；API 正常时仍通过 GithubSource 查找历史差分。
