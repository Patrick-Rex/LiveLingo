# LiveLingo

Windows 本地实时双语字幕工具。采集指定应用的声音，使用 Qwen3-ASR 识别外语，再通过 Ollama 运行 TranslateGemma 翻译为简体中文，以透明置顶窗口显示可修订字幕。

音频采集和模型推理在本机进行。首次准备依赖和模型需要联网。

M0–M4 首版里程碑已完成，项目进入持续迭代；当前能力与支持范围以本文和使用指南为准。

## 功能

- 采集指定应用及其子进程的音频，支持目标重启、设备变化与睡眠恢复处理。
- 连续音频缓存、重叠识别和字幕版本控制，显示草稿、修订及积压状态。
- 支持 TranslateGemma 4B / 12B；默认 Qwen 自动语言识别和 TranslateGemma 12B。
- 双语字幕置顶、鼠标穿透、布局调整，以及可配置的全局快捷键。
- 实时翻译、历史记录和设置三个页面，支持跟随系统、浅色和深色主题。
- 本地保存每次翻译会话，支持查找、复制、TXT / Markdown 导出，以及结束后确认删除和保留期清理。
- 关闭程序时停止采集并回收程序启动的模型进程。

## 系统要求

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 11 x64，build 22000 或更高 |
| 应用运行时 | .NET 10.0 x64 Runtime，兼容各稳定补丁版本，建议使用最新维护版本 |
| ASR 环境 | Python 3.13.2，独立 PyTorch 2.10.0+cu130 环境 |
| GPU | 支持该 CUDA 配置的 NVIDIA GPU；目前主要适配 RTX 50系显卡 |
| 翻译引擎 | Ollama 和已准备的 TranslateGemma 权重 |
| 存储 | 非 C 盘的模型、环境、缓存及数据目录 |

模型不随程序分发。12B 对系统内存也有较高要求；16GB 显存不意味着只需要16GB系统内存。其他硬件组合需要自行验证。

## 安装与使用

获取由本仓库构建的 Windows x64 ZIP 安装包，将其解压到目标安装目录之外。在解压目录打开64位 PowerShell：

```powershell
./Install.ps1
# 更新已有安装时，先关闭 LiveLingo：
./Install.ps1 -ReplaceExisting
```

默认程序位置为 `D:\Soft Ware\LiveLingo\app\desktop\LiveLingo.Desktop.Windows.exe`。准备好模型与依赖后，在“设置 → 运行环境”确认模型和路径并应用，再到“实时翻译”选择发声应用、点击开始。

| 默认快捷键 | 功能 |
| --- | --- |
| Ctrl+Alt+F7 | 开始 / 停止 |
| Ctrl+Alt+F8 | 显示 / 隐藏字幕 |
| Ctrl+Alt+F9 | 进入 / 退出布局模式 |

点击悬浮字幕右上角的小锁进入布局模式，解锁后显示紧凑拖动条、语言和字号控件。拖动顶部移动，四边四角调整窗口大小；A− / A+ 或 Ctrl+滚轮即时缩放双语文字，普通滚轮查看长句。再次点击小锁恢复正文鼠标穿透；小锁保留独立的40×40 DIP命中区，提示和无障碍名称随锁态变化。位置、尺寸、字号、背景和显示模式即时生效并自动保存，运行中也可调整。

浮窗默认自动跟随最新内容。布局模式中向上滚动回看或操作滚动条后暂停跟随，保留当前阅读的两块并继续接收修订与撤销；点击浮窗或实时页的“回到最新”恢复。重新锁定保留回看状态，新会话恢复跟随。“设置 → 字幕外观”和浮窗布局工具条可切换双语／仅中文／仅原文，历史仍保留双语。

设置分为常规、字幕外观、运行环境、快捷键四个内部分组。主题与字幕外观即时生效并自动保存；模型、语言、路径、快捷键和历史保留期停止后点击设置页底部“应用设置”，或用“撤销”放弃未应用输入。切页保留草稿，有待应用设置时不能开始翻译；外观自动保存不会带入运行草稿。

历史记录默认保存于数据目录的 `history` 子目录，保留30天，可改为7天、90天或永久。运行中可阅读、复制和导出，结束后可确认删除选中条目、选中会话或全部已结束记录。无条目选择时复制、导出整场，有选择时只处理选中条目；导出固定点击时的内容，保存对话框取消不生成文件。历史保存失败会持续提示并提供重试。

完整步骤见 [安装与模型准备](packaging/USER-GUIDE.md)。Ollama 是 TranslateGemma 的运行引擎；识别和翻译使用不同模型。

## 从源码构建

需要 Windows 11 x64、.NET 10 SDK（10.0.100 起的稳定功能带及补丁版本）、PowerShell 7 和 Git。源码可放在任意可写目录。`global.json` 使用 `latestFeature`，不要求安装特定的 10.0.4xx SDK，也不自动跨到 .NET 11 或预览版。

普通还原、构建无需先执行环境脚本。NuGet 使用本机配置，默认包缓存为 `%USERPROFILE%\.nuget\packages`；已有 `NUGET_PACKAGES` 等环境变量保持有效。可选的环境脚本默认保留这些设置，显式指定 `-CacheRoot <本机可写绝对路径>` 时才将 NuGet、解包与临时缓存重定向到该目录，仅影响当前终端及其子进程。

```powershell
dotnet restore LiveLingo.slnx --locked-mode
dotnet build LiveLingo.slnx -c Release --no-restore --disable-build-servers -clp:ErrorsOnly
# 存储测试需要非 C 盘临时目录；先按 tests/README.md 设置 TEMP/TMP。
dotnet test --solution LiveLingo.slnx -c Release --no-build --no-progress
./scripts/New-LiveLingoPackage.ps1 -Version 0.1.1
```

安装包默认输出到仓库的 `artifacts/packages`，可用 `-OutputDirectory` 覆盖；直接执行发布脚本默认输出到 `artifacts/publish/desktop`。0.1.1 为命令示例，按本次实际版本替换；同版本包不覆盖。版本号用于压缩包和包清单，安装目录统一为 `app\desktop`。完整的打包、包测试、备份、安装替换、核对与回退步骤见 [交付说明](docs/packaging.md)。构建或打包成功不会自动替换已安装程序。

Windows 目标框架中的 `10.0.26100.0` 是编译 API 基线，最低运行平台由 `SupportedOSPlatformVersion=10.0.22000.0` 声明，因此支持 Windows 11 各后续版本。NuGet 库仍按集中版本和依赖锁还原，不随本机 SDK 补丁改变。

从旧配置迁移时，先关闭曾执行旧环境脚本的终端，再开新终端执行 `dotnet restore LiveLingo.slnx --locked-mode --force`，重新生成含本机缓存路径的中间文件。若仍出现旧路径，使用 `dotnet nuget locals global-packages --list` 核对缓存位置，并检查自己设置的 `NUGET_PACKAGES` 或用户级 NuGet 配置。

检查完整编译警告时，使用 `dotnet build LiveLingo.slnx -c Release --no-restore --disable-build-servers -t:Rebuild -warnaserror`；强制重建避免增量构建漏报，并将警告视为失败。测试取消约定见 [测试说明](tests/README.md)。

## 项目结构

| 目录 | 职责 |
| --- | --- |
| `src/LiveLingo.Core` | 音频、字幕、状态机和中立契约 |
| `src/LiveLingo.Runtime` | 连续缓存、识别/翻译调度及持久化 |
| `src/LiveLingo.Platform.Windows` | Application Loopback、Win32 和进程生命周期 |
| `src/LiveLingo.App` | Avalonia 界面与视图模型 |
| `src/LiveLingo.Desktop.Windows` | Windows 桌面入口与依赖组合 |
| `workers` / `runtime-profiles` | Python ASR 工作进程与固定依赖配置 |
| `tests` / `tools` | 回归测试、诊断与评测源码 |
| `scripts` / `packaging` | 构建、安装与依赖准备 |

技术栈：C# 14 / .NET 10、Avalonia、Channels、Microsoft Agent Framework、Qwen3-ASR 和 TranslateGemma。

## 已知限制

- 当前面向外语到简体中文；浏览器采集覆盖所选根进程的全部子孙，不能按单个标签页隔离。
- 模型仍可能漏识重复、人名或误译否定。字幕可以继续修订，不能将首次显示视为最终正确结果。
- 首次可读中文通常以3–6秒为目标，冷启动、长句和积压可能增加延迟。
- 普通识别窗口30秒，对齐失败时最多扩展到120秒；无法继续处理时显示限制和积压。
- 物理混合DPI、显示器拔插及独占全屏的完整兼容性仍需验证。
- 模型准备与正式安装脚本仍默认使用D盘布局；源码构建与打包不依赖该布局。暂不支持跨平台安装、自动更新或签名安装向导。

## 贡献

欢迎提交问题和改进。请提供复现步骤、运行环境与必要的脱敏日志；不要上传私人录音、模型权重或完整本机运行目录。贡献与测试要求见 [CONTRIBUTING](CONTRIBUTING.md)。

仓库保留测试源码与可复现脚本，测试结果、录音、安装包、本机环境记录和内部开发工作记录不纳入 Git。

## 许可证

本项目采用 [Apache License 2.0](LICENSE)，版权声明见 [NOTICE](NOTICE)。第三方依赖及模型遵循各自许可证；本项目许可证不替代其条款。
