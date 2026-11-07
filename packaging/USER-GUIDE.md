# LiveLingo 安装与使用

Windows 11 x64 实时日语转简体中文字幕，识别和翻译分别选择本地或 API。只需一个 Setup.exe 安装程序；程序包不包含 .NET、模型、Python 环境、Ollama、驱动、测试录音或测试源码。

## 推荐安装与增量更新

下载 [LiveLingo-win-x64-Setup.exe](https://github.com/Patrick-Rex/LiveLingo/releases/latest/download/LiveLingo-win-x64-Setup.exe)，双击后自动安装程序本体并启动。缺少 .NET 10 x64 Runtime 时安装器会提示并在线安装，已有兼容运行时直接复用。无需另行下载脚本、清单或 nupkg；Release 的其他附件由更新程序使用。

首次双击 Setup.exe 使用用户本地应用目录。高级用户可用 `Setup.exe --installto "D:\Soft Ware\LiveLingo\app\LiveLingo"` 指定程序目录。Velopack 管理安装根内的 `current`、`packages` 和 `Update.exe`，从开始菜单或安装根的稳定启动程序打开，不手工改动 `current`。程序文件允许使用安装器的用户目录；模型、Python 隔离环境和大型缓存仍须非 C 盘。

每次普通启动后台检查稳定更新，断网失败不影响本地翻译。发现版本后，主窗口或“设置 → 常规”点击一次“下载并更新”，即可下载并自动退出安装、重启。操作前须停止翻译并应用或撤销设置，下载期间暂停开始翻译和编辑运行配置；失败或取消不触发退出。重新启动时不会擅自安装上次留下的包，可点击“安装并重启”继续。退出先清理模型、保存与宿主，再交给更新器；差分不可用或还原失败时回退完整包。

旧 ZIP 版需先退出，运行新安装器建立独立的 `app\LiveLingo` 安装。默认设置、历史和模型路径继续复用；使用自定义 `--settings` 时在新的启动快捷方式保留该参数。程序内更新重启会保留启动参数。确认新安装正常后再处理旧 `desktop`，不要把旧包复制进 `current`。发布产物当前未配置数字签名，SHA-256 仅验证传输完整性。

确认托管安装和更新正常、不再需要旧版回退后，可以删除旧 `app\desktop`、旧 ZIP、解压暂存和测试副本。保留托管安装根内的 `current`、`packages`、`Update.exe` 与稳定启动器；`packages` 中的当前完整包是后续增量还原的基础。模型、环境、用户设置与历史继续保留，不将整个 LiveLingo 根目录当作临时目录清理。

安装或更新失败时保留原安装与数据，重新打开查看状态；保留旧版安装包和设置备份。需要人工回退时先退出并备份数据，再按对应旧版本安装方式恢复；禁止手工混合 current 与 desktop 文件。增量更新不会自动降级或迁移用户设置。

## 旧 ZIP 安装与更新

1. 安装 [.NET 10.0 x64 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)，兼容各稳定补丁版本，建议使用最新维护版本。运行程序无需 SDK。
2. 将 ZIP 解压到非 C 盘的临时目录，且不要解压到目标 `app` 目录内。可用 `Get-FileHash <ZIP路径> -Algorithm SHA256` 对照随包 `.sha256` 文件检查传输完整性。
3. 先关闭 LiveLingo；升级前按下节备份旧程序、设置和旧 ZIP。在解压目录打开 64 位 PowerShell 5.1 或 7，先运行 `./Install.ps1 -VerifyOnly`；首次安装运行 `./Install.ps1`，升级已有安装运行 `./Install.ps1 -ReplaceExisting`。脚本校验全部应用文件，复制并复核后切换目录，清理上一份程序。用户设置、模型、环境和数据位于应用目录外，不受替换影响。
4. 双击 `D:\Soft Ware\LiveLingo\app\desktop\LiveLingo.Desktop.Windows.exe`。按“安装后的核对”确认文件及运行正常后可以删除解压目录，保留 ZIP 用于重装。

本节仅适用于旧 ZIP + PowerShell 安装包，该布局不支持程序内增量更新，也不创建注册表卸载项。未修改 PowerShell 执行策略；组织策略禁止运行时，请由管理员按其策略处理。SHA-256 清单用于完整性检查，不代替可信来源或数字签名。

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

运行备份前先关闭程序，确认副本完整后再替换。models、runtimes、cache、data 及共享 Ollama 权重不在替换范围；已有依赖通常可以继续使用。需要保留翻译记录时，也备份当前数据目录下的 `history` 文件夹；默认是 `D:\Soft Ware\LiveLingo\data\history`。上面的设置备份不会自动包含历史正文。

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

## 整体模型配置

进入“设置 → 运行环境”，选择表单或“整体 JSON”，两种方式编辑同一份模型配置。可一次导入程序目录中的 `model-config.example.json`，替换两个阶段的地址、模型和 Key，再点击一次“应用设置”。示例地址是占位符，不会提供模型服务。导入只更新模型草稿，保留路径、外观和历史设置；任一阶段 JSON 无效时不部分应用。

```json
{
  "asr": {
    "provider": "openai",
    "base_url": "https://asr.example/v1",
    "api_key": "",
    "model": "your-asr-model"
  },
  "translation": {
    "provider": "openai",
    "base_url": "https://translation.example/v1",
    "api_key": "",
    "model": "your-translation-model"
  },
  "asr_language": "auto"
}
```

这是 LiveLingo 的整体配置结构，API 字段沿用常见 OpenAI 客户端命名；并非 OpenAI 官方统一配置文件标准。两个阶段可使用同一或不同服务。Base URL 填含版本前缀的根地址（例如 `https://api.openai.com/v1`），不要追加接口路径；远程服务要求 HTTPS，本机服务允许 HTTP。服务无需鉴权时 Key 可为空。`asr_language` 支持 `auto` 或 `Japanese`。

要把某阶段改为本地，将其配置改为 `{"provider":"local","model":"Qwen/Qwen3-ASR-1.7B"}`（识别）或 `{"provider":"local","model":"translategemma:12b"}`（翻译，也可选 `translategemma:4b`）；本地阶段不要求 API 地址或 Key。表单中也可直接切换。两者都选 API 时无需 Python、CUDA、NVIDIA 驱动、Ollama 或本地权重；混合使用时只准备所选本地阶段。

API 识别通过 `/audio/transcriptions` 发送有界 WAV 窗口，API 翻译通过 `/chat/completions` 发送当前完整原文。沿用重叠窗口识别和修订，同一音频可能多次发送并产生费用。标准 JSON 转写结果未必提供生成截断信息，因此不能保证字字完整；翻译要求普通文本且 `finish_reason=stop`，拒绝空内容、截断或工具调用。接口须支持所用模型的 JSON 转写或非流式聊天及 `max_completion_tokens`；不包含 Realtime、厂商专有鉴权或自动提供商切换。

按当前选择，Key 直接保存在本地 `settings.json`，表单隐藏显示；整体 JSON 编辑器会显示明文。导入后无需保留对原文件的引用，不监控文件变化。常规日志、历史和安装脚本快照不保存 Key。启动初检只验证配置，不发送付费测试请求；首次实际识别或翻译验证服务可用性，失败会显示状态。真实服务的模型名称、权限与费用由用户配置的服务决定。

## 首次准备依赖

先在“设置 → 运行环境”分别选择识别与翻译来源。本地识别检查 Python、Qwen 与 NVIDIA r580+；本地翻译检查 Ollama 与所选 TranslateGemma。API 来源只检查配置，不要求对应本地组件；全 API 无需 Python、CUDA、Ollama 或本地权重。检测不启动模型服务；初检通过不等于 GPU 推理已通过，正式开始仍执行完整校验。

启动会搜索已配置路径、常用非 C 盘资源目录、PATH、VIRTUAL_ENV 与 OLLAMA_MODELS，找到兼容环境时在空闲且没有设置草稿的情况下复用缺失路径。已有文件和不兼容资产不会被覆盖。缺少的组件旁显示“安装”，点击即可执行包内脚本，界面显示输出，完成后自动重新检查；无需打开终端。先安装对应运行环境再安装模型。驱动通过“打开驱动下载”使用厂商安装程序。

关闭或取消会停止本次安装脚本；未完成可复检后重试。脚本执行策略仅对本次 PowerShell 进程生效，不修改系统配置，也不能覆盖组织策略。已有模型指纹不匹配时保留原文件并显示原因。

本地翻译兼容 Ollama 新旧模型清单。Ollama 更新清单存储格式不代表权重丢失，无需因此重新下载模型；程序仍检查当前有效清单的固定模型指纹和文件长度。

以下仅供高级用户排查：随包 `scripts` 目录仍可在64位 PowerShell中按组件执行：

```powershell
./Install-LiveLingoDependencies.ps1                  # 仅显示路径检查和组件选择
./Install-LiveLingoDependencies.ps1 -Component DotNet
./Install-LiveLingoDependencies.ps1 -Component Ollama
./Install-LiveLingoDependencies.ps1 -Component Driver
./Install-LiveLingoDependencies.ps1 -Component Runtime
./Install-LiveLingoDependencies.ps1 -Component Models
```

Driver 打开 NVIDIA 官方驱动页面，由厂商安装器完成匹配与安装，要求 r580 或更新驱动；按提示重启后再准备 Runtime。Runtime 使用 Python 3.13.2、uv 和固定哈希锁并运行一次 CUDA 运算；可用 `-BasePython '实际路径'` 复用准确版本，已有其他 Python 版本不会被自动降级。Models 仅为所选本地来源准备固定 Qwen revision 或当前选择的 TranslateGemma，复用已验证资产、允许恢复未完成的 Qwen 下载；已有翻译模型指纹不符时停止，不覆盖或绕过校验。下载期间只运行脚本自己的临时 Ollama 服务，结束后回收，不停止已有服务。

脚本默认读取 `D:\Soft Ware\LiveLingo\data\settings.json`，可传 `-SettingsPath '实际设置路径'`；不会改写设置。自定义路径先在程序中应用，再运行脚本。脚本联网安装只在明确指定组件时发生；winget 安装可能需要系统权限和软件许可确认。下列手动步骤继续可用。


以下手动步骤仅适用于所选本地阶段：本地识别执行 1–2，本地翻译执行 3，再统一执行 4。已有环境和模型可以复用，无需重新下载。新增的大文件全部放非 C 盘。当前主要面向 RTX 5080 16GB；12B 同时需要充足系统内存，不能只按显存容量估算。

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

4. 打开 LiveLingo，进入“设置 → 运行环境”，选择模型和识别语言；展开“高级路径配置”，填写或浏览选择所选本地阶段的 Python、Qwen、Ollama 路径及共享数据和缓存路径。点击“检查路径”查看文件或目录是否存在，再点击底部“应用设置”。应用只运行已经准备的资产，不自动下载或更换模型；路径检查不替代开始翻译时的模型完整性验证。

## 日常使用

主窗口左侧有“实时翻译、历史记录、设置”三个入口。在实时页选择实际发声应用的根进程，点击“开始翻译”。采集范围是整个进程树，浏览器可能包括多个标签页。冷启动需要加载模型。默认识别语言 auto、TranslateGemma 12B；原文与中文允许后续修订，运行状态、积压及失败原因显示在主窗口，版本信息放在条目的详情中。

- Ctrl+Alt+F7：开始/停止。
- Ctrl+Alt+F8：显示/隐藏字幕。
- Ctrl+Alt+F9：切换字幕锁定；也可点击悬浮字幕右上角的小锁。锁定时保留正文和小锁；解锁时出现紧凑拖动条、语言和字号工具。拖动顶部移动，四边四角调整大小，A− / A+ 或 Ctrl+滚轮缩放双语文字，普通滚轮查看长内容。运行中也可调整；再次锁定恢复正文穿透，小锁仍可点击，其提示为“解锁拖动”或“锁定位置”。小锁使用40×40 DIP独立命中区，位置、尺寸和字号自动保存。
- 浮窗长字幕自动跟随末尾；布局模式中用滚轮或滚动条回看后暂停跟随，当前两块仍接收修订与撤销，新块不会替换正在看的内容。点击浮窗或控制窗的“回到最新”恢复跟随；退出布局模式不会自动恢复，新会话会恢复。
- “设置 → 字幕外观”和浮窗布局工具条提供双语／仅中文／仅原文切换，运行中即时生效；历史保持双语，识别和翻译照常进行。仅中文模式没有译文时显示等待提示，状态提示仍保留。显示模式自动保存；旧配置默认双语。
- 关闭控制窗会停止采集并退出本程序的模型进程；原有浏览器和外部 Ollama 保留。

## 设置与保存

设置包含四个内部分组：

- **常规**：选择跟随系统、浅色或深色主题，设置历史保留期。
- **字幕外观**：通过合成示例预览显示语言、字号和背景；不会开始采集或读取真实历史。
- **运行环境**：选择模型与识别语言，所选来源的资源路径收在“高级路径配置”中。
- **快捷键**：配置三个互不重复的 Ctrl+Alt+F1–F11 功能键。

主题与字幕外观即时生效并自动保存，翻译运行时也可修改。模型、语言、路径、快捷键和保留期须停止翻译后，在设置页底部点击“应用设置”；点击“撤销”放弃尚未应用的输入，不撤销即时外观。切换页面保留草稿；未应用时实时页提供设置入口，开始翻译前必须先应用或撤销。外观自动保存不会顺带保存这些草稿。

默认设置为 `D:\Soft Ware\LiveLingo\data\settings.json`。设置损坏时显示错误并保留原文件，同时暂停自动保存；停止后明确点击“应用设置”才会替换原文件并恢复自动保存。外观自动保存失败可在常规或字幕外观分组重试，退出前会补存末次调整。可以用 `--settings <非C盘绝对JSON路径>` 选择独立设置。更改数据目录会切换历史库，不会搬迁旧记录、模型或设置文件；历史页底部显示当前历史位置。

## 历史记录

每次开始至停止形成一场本地会话，双语正文保存于当前数据目录的 `history` 子目录；默认是 `D:\Soft Ware\LiveLingo\data\history`。记录保留语义块的最新有效版本，不按相同文字合并真实重复。历史文件包含原文和译文，常规运行日志不因此记录字幕正文。

- 按应用名称查找会话；选中会话后，可以搜索本场原文和译文，分页阅读正文。
- “复制”“导出 TXT”“导出 Markdown”在运行中也可用。无条目选择时处理整场；勾选条目后只处理所选内容。选择可跨正文页保留，“选择…”中可选择本页或取消全部选择。
- 导出在打开保存对话框前固定内容，后续字幕修订不会改变这次导出；取消对话框不生成文件。目前不导出音频、SRT 或 VTT。
- 正在翻译的会话不能删除。结束后可以选择单条或多条正文、单场或多场会话再确认删除；“清空已结束记录”保留正在翻译的会话。删除前显示范围，取消不修改记录。
- 保留期默认为30天，可在“设置 → 常规”选择7天、30天、90天或永久。启动、应用设置、会话结束和每日维护会清理到期的已结束记录；缩短保留期前先导出重要内容。
- 历史未完整保存会显示持续错误及重试入口，不与翻译运行状态混为成功。遇到失败先重试，仍失败时及时导出当前可读记录；不要把正在显示的内容都视为已经写入磁盘。

## 限制与卸载

模型仍可能漏掉真实重复、人名或误译否定；首次可读中文 3–6 秒是目标，并非所有内容均能达到。正常 30 秒窗口，对齐失败时最多回退到 120 秒，超限会显示积压，不静默丢弃。现有证据尚未覆盖完整的物理混合 DPI、拔屏和独占全屏组合，不据首版里程碑完成扩大兼容性声明。

托管安装先关闭程序，在 Windows“已安装的应用”中卸载 LiveLingo；旧 ZIP 安装只删除 `app\desktop`。模型、Python 环境、设置和数据独立保留，按需要另外处理。不要删除其他应用共享的 Ollama 模型。

## 许可证

LiveLingo 使用随包 LICENSE 中的 Apache-2.0 许可证，版权声明见 NOTICE。第三方许可文件位于 licenses，索引为 THIRD-PARTY-NOTICES.md，NuGet 元数据原文以 `.nuspec.xml` 保存；另行下载的模型遵循各自条款。

GitHub API 返回限流状态时，程序自动使用同仓库最新稳定 Release 的公开下载清单；不要求填写令牌。此路径只有当前发布的差分，缺少跨版本差分时仍可回退完整包。
