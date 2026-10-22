# 打包、安装替换与回退

本文是从当前源码到 Windows 11 x64 安装的完整流程。运行依赖、模型准备及日常操作见 [随包用户指南](../packaging/USER-GUIDE.md)。安装目录默认为 `D:\Soft Ware\LiveLingo\app\desktop`，版本号用于 ZIP 名和包清单，不创建编号安装目录。

## 1. 区分构建产物与正式安装

| 内容 | 默认位置及用途 |
| --- | --- |
| 源码与构建输出 | 仓库及各项目 bin/obj；构建成功不会替换正在使用的程序 |
| 发布包 | D:\Soft Ware\LiveLingo\packages；ZIP 与同名 .zip.sha256 |
| 正式安装 | D:\Soft Ware\LiveLingo\app\desktop |
| 用户设置 | D:\Soft Ware\LiveLingo\data\settings.json；使用 --settings 时以实际路径为准 |
| 模型、Python 环境及缓存 | models、runtimes、cache 和独立的 Ollama 权重目录；不随程序替换 |

打包会读取当前工作树，包括未提交的代码与说明。发布前确认这些改动正是本次要交付的内容。文档状态或历史报告不证明当前安装已更新，须以实际安装文件核对。

## 2. 从源码验证并打包

使用 PowerShell 7、Git 和 global.json 指定的 .NET SDK。运行机器需要 Windows 11 x64、.NET 10 x64 Runtime 10.0.12 或更高的 10.0 修订版，以及已准备的模型环境；程序按依赖运行时方式发布，包不包含 .NET Runtime。

以下命令在仓库根、同一 PowerShell 7 终端顺序执行。0.1.1 只是下一次打包的示例版本，不表示已发布；按本次实际版本修改。同版本 ZIP 或校验文件存在时脚本拒绝覆盖，可选新版本或新的非 C 盘输出目录，保留可回退旧包。

```powershell
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath 'D:\Code Build\Repos\Patrick-Rex\LiveLingo'
$packageVersion = '0.1.1'
$packageOutput = 'D:\Soft Ware\LiveLingo\packages'
. ./scripts/Enter-Development.ps1
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
./scripts/Test-LiveLingoPackage.ps1 -Archive $archive
```

测试还须有实际通过数量，零测试不能算通过；检查范围见 [测试说明](../tests/README.md)。纯文档修改无需运行上述业务构建或测试；需要生成新分发包时，打包脚本会自行执行生产项目还原与发布。涉及原生交互、采集或模型路径的变更，按实际影响补充相应检查，安装包测试不能替代它们。

New-LiveLingoPackage.ps1 内部使用 locked restore 和 `dotnet publish -c Release -r win-x64 --self-contained false`，只发布生产工程；不启用裁剪、AOT 或单文件。暂存位于仓库忽略的 artifacts/packaging/唯一目录，完成后清理。不要向已有安装目录直接执行 Publish-LiveLingo.ps1；它要求空输出目录，正式替换使用安装器。

包内包含 desktop 程序、worker、cu130 profile/哈希锁、必要依赖准备脚本、用户指南和第三方许可。根目录含 Install.ps1、manifest.json、README.md、LICENSE、NOTICE。模型、虚拟环境、录音、测试、历史报告、缓存和 PDB 不入包。ZIP 校验覆盖整个归档，manifest 逐项校验 desktop 下的应用文件。

Test-LiveLingoPackage.ps1 在独立非 C 盘临时目录执行 10 项检查，覆盖 ZIP/应用哈希、损坏/额外文件/越界路径拒绝、C 盘拒绝、首次安装、拒绝隐式覆盖、显式替换保留设置及安装文件一致性，完成后清理。它不会替换正式安装，也不会启动 GPU 模型。

## 3. 关闭程序并留存回退资料

在控制窗停止会话、保存运行设置并关闭程序，等待本程序的模型进程退出；不结束浏览器或用户原有 Ollama。保留上一份已验证 ZIP 与 .sha256。安装器成功后会删除 .desktop-backup，它只是事务备份，不是长期回退副本。

替换前，按下列方式另存当前程序与设置；首次安装没有旧程序时只会备份实际存在的设置。下面变量在后续安装步骤继续使用；自定义设置路径须同步修改。

```powershell
$applicationRoot = 'D:\Soft Ware\LiveLingo\app'
$installedDirectory = Join-Path $applicationRoot 'desktop'
$settingsPath = 'D:\Soft Ware\LiveLingo\data\settings.json'
$backupRoot = Join-Path 'D:\Soft Ware\LiveLingo\backups' ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
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
$extractDirectory = Join-Path 'D:\Soft Ware\LiveLingo\packages\staging' ([guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $archive -DestinationPath $extractDirectory
$installer = Join-Path $extractDirectory 'Install.ps1'
& $installer -VerifyOnly
```

解压目录必须位于非 C 盘，且在目标 app 目录之外。VerifyOnly 只核对包内容，不检查运行环境或证明程序可启动。核对包后，在下面两条命令中选择一条：

```powershell
# 首次安装，目标 desktop 不存在：
& $installer -ApplicationRoot $applicationRoot

# 替换已有 LiveLingo 安装：
& $installer -ApplicationRoot $applicationRoot -ReplaceExisting
```

安装器支持 64 位 PowerShell 5.1 或 7，要求目标是非 C 本地盘绝对路径，末级为 app，程序目录始终为 desktop。更改 ApplicationRoot 不迁移模型或设置。安装会检查运行时、应用占用、链接、越界和文件哈希，复制到 .desktop-install 并复核，再切换目录；切换失败时尝试恢复旧程序。成功后输出 Installed=版本和实际 EXE 路径，清理事务旧备份。

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

已有环境直接复用；首次使用先按 [用户指南](../packaging/USER-GUIDE.md#首次准备依赖)准备 Python/cu130、Qwen 和 TranslateGemma。启动后检查路径和设置，选择实际发声应用，验证字幕、右上角解锁/锁定、拖动缩放、显示模式、热键及设置保存，最后关闭并确认自有模型进程退出。真实模型、原生输入和多屏等检查分别记录，不能从包哈希通过推断这些行为通过。

确认运行正常后可删除此次唯一解压目录，保留 ZIP、校验文件和回退备份。不要把 models、runtimes 或 data 当作安装暂存删除。需要记录本次结果时新建按日期/版本命名的记录，不继续追加首版 M4 报告。

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

[用户指南](../packaging/USER-GUIDE.md)提供 Python/uv、cu130 环境、Qwen 固定 revision、Ollama 模型准备及常用操作。依赖准备只在首次使用或明确改变配置时进行，程序替换不等于重新初始化环境。

卸载先退出程序，只处理目标 app/desktop；模型、Python 环境、设置及数据独立保留，按需要另外处理。不要删除其他应用共享的 Ollama 权重。当前交付形式为 ZIP + PowerShell，没有签名 MSI/EXE 安装向导、自动更新或注册表卸载项。
