# LiveLingo 安装与使用

Windows 11 x64 本地日语转简体中文字幕。安装目录固定为 `D:\Soft Ware\LiveLingo\app\desktop`；版本号用于压缩包名和包清单。包内没有模型、Python 环境、测试录音、测试源码或历史讨论。

## 安装与更新

1. 安装 .NET 10 x64 Runtime（10.0.12 或更高的 10.0 修订版）：https://dotnet.microsoft.com/download/dotnet/10.0 。运行程序无需 SDK。
2. 将 ZIP 解压到非 C 盘的临时目录，且不要解压到目标 `app` 目录内。可用 `Get-FileHash <ZIP路径> -Algorithm SHA256` 对照随包 `.sha256` 文件检查传输完整性。
3. 先关闭 LiveLingo；升级前按下节备份旧程序、设置和旧 ZIP。在解压目录打开 64 位 PowerShell 5.1 或 7，先运行 `./Install.ps1 -VerifyOnly`；首次安装运行 `./Install.ps1`，升级已有安装运行 `./Install.ps1 -ReplaceExisting`。脚本校验全部应用文件，复制并复核后切换目录，清理上一份程序。用户设置、模型、环境和数据位于应用目录外，不受替换影响。
4. 双击 `D:\Soft Ware\LiveLingo\app\desktop\LiveLingo.Desktop.Windows.exe`。按“安装后的核对”确认文件及运行正常后可以删除解压目录，保留 ZIP 用于重装。

这是 ZIP + PowerShell 安装包，没有 MSI/EXE 安装向导、数字签名、自动更新、开机启动或注册表卸载项。未修改 PowerShell 执行策略；组织策略禁止运行时，请由管理员按其策略处理。SHA-256 清单用于完整性检查，不代替可信来源或数字签名。

`./Install.ps1 -VerifyOnly` 只验证包。`-ApplicationRoot 'E:\LiveLingo\app'` 可改变程序安装位置，末级仍须为 `app`，程序目录恒为 `desktop`。当前依赖准备脚本默认使用以下 D 盘布局；改变程序安装位置不会迁移模型或设置。

安装失败若提示 `.desktop-install` / `.desktop-backup` 残留，请先检查目录；不要直接反复安装。`.desktop-backup` 为事务中的上一份应用，模型和设置不在其中；成功安装后该目录会被删除。脚本不会自动清除未知的旧事务目录。

## 升级前备份与回退

安装器在切换失败时尝试恢复旧程序；替换成功后会删除 app 下的 .desktop-backup。要在新版本运行异常时回退，升级前须保留上一份已验证 ZIP 和 .sha256，并在程序关闭后备份设置。默认设置路径如下，使用 --settings 时改成实际路径：

```powershell
$settingsPath = 'D:\Soft Ware\LiveLingo\data\settings.json'
$backupDirectory = Join-Path 'D:\Soft Ware\LiveLingo\backups' ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backupDirectory | Out-Null
if (Test-Path -LiteralPath $settingsPath) {
    Copy-Item -LiteralPath $settingsPath -Destination (Join-Path $backupDirectory 'settings.json')
}
# 没有可用旧 ZIP 时，另存当前完整程序；自定义安装位置需改为实际路径。
$desktopDirectory = 'D:\Soft Ware\LiveLingo\app\desktop'
if (Test-Path -LiteralPath $desktopDirectory) {
    Copy-Item -LiteralPath $desktopDirectory -Destination (Join-Path $backupDirectory 'desktop') -Recurse
}
Write-Output "RollbackBackup=$backupDirectory"
```

运行备份前先关闭程序，确认副本完整后再替换。models、runtimes、cache、data 及共享 Ollama 权重不在替换范围；已有依赖通常可以继续使用。

回退时关闭新程序，校验并解压旧 ZIP，在旧包目录运行 `./Install.ps1 -ReplaceExisting`；自定义安装须传入同一个 ApplicationRoot。安装器允许降级，但不回退用户设置。若旧版不兼容新设置，在应用完全退出后保留当前设置副本，再恢复升级前备份；恢复会丢失备份之后的设置变化。

没有旧 ZIP 时，核实备份和正式路径，将当前 desktop 重命名保留为故障副本，再将备份 desktop 完整复制回 app/desktop，不逐文件混合覆盖。人工移动或清理前核实绝对路径和链接状态；不能确认时保留现场。程序回退不撤销另行修改的 Python 环境或模型。

若出现安装事务残留，desktop 缺失且 .desktop-backup 存在时，应先核对并恢复旧程序；两者都存在时先核对是否已经切换到新包，不立即删除旧备份。.desktop-install 不能直接当作完整安装。脚本会拒绝带残留的重试，先查明原因再处理。

## 安装后的核对

安装器输出 Installed=版本及 EXE 路径。使用实际正式目录启动，确认设置、模型路径、所选应用采集与字幕显示正常，测试右上角解锁/锁定、拖动缩放和快捷键，再关闭程序确认本程序模型进程退出。首次使用须先完成下面的依赖准备。

需要确认已替换到刚解压的版本时，在包目录运行；自定义 ApplicationRoot 请修改第一行：

```powershell
$installedDirectory = 'D:\Soft Ware\LiveLingo\app\desktop'
$manifest = Get-Content -LiteralPath './manifest.json' -Raw | ConvertFrom-Json
foreach ($entry in $manifest.files) {
    $installedFile = Join-Path $installedDirectory $entry.path
    if (-not (Test-Path -LiteralPath $installedFile -PathType Leaf) -or
        (Get-Item -LiteralPath $installedFile).Length -ne $entry.bytes -or
        (Get-FileHash -LiteralPath $installedFile -Algorithm SHA256).Hash -ne $entry.sha256) {
        throw "Installed file mismatch: $($entry.path)"
    }
}
Write-Output "Installed files match package $($manifest.version)"
```

包哈希及文件核对不代表模型、真实鼠标输入或多屏兼容性通过。运行正常后可以删除本次解压目录，保留 ZIP、校验文件和回退备份。不要删除共享模型或 data。

## 首次准备依赖

已有环境和模型可以复用，无需重新下载。新增的大文件全部放非 C 盘。当前主要面向 RTX 5080 16GB；12B 同时需要充足系统内存，不能只按显存容量估算。

1. 安装 Python 3.13.2 x64、uv 和支持 CUDA 13.0 的 NVIDIA 驱动。使用 PyTorch 自带 CUDA wheel，无需另装完整 CUDA Toolkit。Python 官方下载：https://www.python.org/downloads/release/python-3132/ ，uv：https://docs.astral.sh/uv/getting-started/installation/ 。
2. 在安装后的 `desktop` 目录运行，替换本机基础 Python 路径：

```powershell
. ./scripts/Enter-ModelEvaluation.ps1
./scripts/Initialize-ModelRuntime.ps1 -Profile win-x64-cu130 -BasePython 'C:\你的Python安装路径\python.exe'
& 'D:\Soft Ware\LiveLingo\runtimes\win-x64-cu130\Scripts\python.exe' -c "import torch; x=torch.ones(256, device='cuda'); print(torch.__version__, torch.cuda.get_device_name(), (x+x).sum().item())"
& 'D:\Soft Ware\LiveLingo\runtimes\win-x64-cu130\Scripts\python.exe' ./tools/model_eval/prepare_model.py --output 'D:\Soft Ware\LiveLingo\models\Qwen3-ASR-1.7B-7278e1e'
```

环境按哈希锁安装，CUDA 运算必须实际成功。Qwen 下载固定 revision `7278e1e70fe206f11671096ffdd38061171dd6e5`，已有模型目录拒绝覆盖。缓存脚本只影响当前终端，不修改全局环境。

3. 安装 Ollama：https://ollama.com/download/windows 。准备 TranslateGemma 12B（默认）和可选 4B。若尚未准备模型，在一个独立 PowerShell 窗口运行私有服务：

```powershell
$env:OLLAMA_HOST = '127.0.0.1:11435'
$env:OLLAMA_MODELS = 'D:\Soft Ware\Ollama'
ollama serve
```

在另一个 PowerShell 窗口显式下载：

```powershell
$env:OLLAMA_HOST = '127.0.0.1:11435'
ollama pull translategemma:12b
ollama pull translategemma:4b
```

下载完成后，在刚才的私有服务窗口按 Ctrl+C 退出，不停止用户原先运行的服务。固定模型标签的上游内容可能更新；应用会校验既定模型指纹，指纹不一致时应按源码运行配置核对版本，不跳过校验。

4. 打开 LiveLingo，在控制窗填写实际 Python、Qwen 模型、Ollama EXE 和 Ollama 模型路径并保存。应用只运行已经准备的资产，不自动下载或更换模型。

## 日常使用

选择实际发声应用的根进程，点击开始。采集范围是整个进程树，浏览器可能包括多个标签页。冷启动需要加载模型。默认识别语言 auto、TranslateGemma 12B；允许原文与中文后续修订，显示积压及失败状态。

- Ctrl+Alt+F7：开始/停止。
- Ctrl+Alt+F8：显示/隐藏字幕。
- Ctrl+Alt+F9：切换字幕锁定；也可直接点击悬浮字幕右上角“解锁拖动／锁定位置”；拖动顶部拖动条或状态行移动，四边四角调整窗口大小，A− / A+ 或 Ctrl+滚轮缩放双语文字，普通滚轮查看长内容。运行中也可调整；点击“锁定位置”恢复正文穿透，右上角解锁入口仍可点击，控制窗没有重复布局开关。位置、尺寸和字号自动保存。
- 控制窗分为会话历史、字幕外观、模型与路径、快捷键四页；开始／停止与运行状态始终可见。字幕外观即时生效并自动保存，运行中也可修改。模型、路径和快捷键停止后点击底部“保存运行设置”；未确认的修改可“撤销修改”，开始前须先保存或撤销。
- 浮窗长字幕自动跟随末尾；布局模式中用滚轮或滚动条回看后暂停跟随，当前两块仍接收修订与撤销，新块不会替换正在看的内容。点击浮窗或控制窗的“回到最新”恢复跟随；退出布局模式不会自动恢复，新会话会恢复。
- 控制窗及浮窗布局工具条提供双语／仅中文／仅原文切换，运行中即时生效；控制窗历史保持双语，识别和翻译照常进行。仅中文模式没有译文时显示等待提示，状态提示仍保留。显示模式自动保存；旧配置默认双语。
- 关闭控制窗会停止采集并退出本程序的模型进程；原有浏览器和外部 Ollama 保留。

默认设置为 `D:\Soft Ware\LiveLingo\data\settings.json`，损坏时会显示错误并保留原文件，同时暂停字幕自动保存；停止后明确点击“保存运行设置”才会替换原文件并恢复自动保存。自动保存失败可在“字幕外观”页重试；退出前会补存最后一次字幕调整。可以用 `--settings <非C盘绝对JSON路径>` 选择独立设置。

## 限制与卸载

模型仍可能漏掉真实重复、人名或误译否定；首次可读中文 3–6 秒是目标，并非所有内容均能达到。正常 30 秒窗口，对齐失败时最多回退到 120 秒，超限会显示积压，不静默丢弃。现有证据尚未覆盖完整的物理混合 DPI、拔屏和独占全屏组合，不据首版里程碑完成扩大兼容性声明。

卸载时关闭程序，只删除 `app\desktop`。模型、Python 环境、设置和数据独立保留，按需要另外处理。不要删除其他应用共享的 Ollama 模型。

## 许可证

LiveLingo 使用随包 LICENSE 中的 Apache-2.0 许可证，版权声明见 NOTICE。第三方许可文件位于 licenses，索引为 THIRD-PARTY-NOTICES.md；另行下载的模型遵循各自条款。
