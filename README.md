# LiveLingo

LiveLingo 为 Windows 11 上正在播放声音的应用生成实时字幕。观看直播或视频时，可以在置顶浮窗里对照阅读原文和简体中文译文。语音识别支持中文、英语、日语和韩语，每场会话使用一种源语言。

[下载 Windows x64 安装包](https://github.com/Patrick-Rex/LiveLingo/releases/latest/download/LiveLingo-win-x64-Setup.exe) · [版本说明](https://github.com/Patrick-Rex/LiveLingo/releases/latest) · [使用指南](packaging/USER-GUIDE.md)

## 快速开始

1. **安装。** 在 Windows 11 x64（内部版本 22000 或更高）运行 `LiveLingo-win-x64-Setup.exe`。如果缺少 .NET 10 x64 Runtime，安装器会提示并在线安装。当前正式安装包未签名，Windows 首次运行时可能显示来源提示。
2. **选择语言和模型。** 打开「设置 → 运行环境」，按音频中使用的语言选择「识别语言」。首次默认为日语，之后会记住已应用的选择。识别和翻译可分别选择本地模型或 OpenAI 兼容 API。本地模型按需下载安装，API 在此填写服务地址、模型及凭据。完成后点击「应用设置」。
3. **开始翻译。** 让目标应用播放声音，在「实时翻译」中选中它，点击「开始翻译」。采集范围是所选进程及其子进程。选择浏览器时，同一进程树中其他发声标签页的声音也会进入字幕。

EXE 安装版每次启动会在后台检查稳定更新。发现新版后，停止翻译，在主窗或「设置 → 常规」点击「下载并更新」，程序会优先下载差分包，完成安装后重启。模型、设置和历史保存在程序目录之外。

## 识别、翻译与隐私

| 阶段 | 本地模型 | OpenAI 兼容 API |
| --- | --- | --- |
| 语音识别 | Qwen3-ASR-1.7B。默认使用 CPU，也可为兼容的 NVIDIA 或 AMD 显卡选择 CUDA 或 ROCm | 将采集的音频片段发送至配置的语音转写服务 |
| 翻译 | Ollama 运行 TranslateGemma，可选 4B 或 12B | 将识别出的原文及适用的词库提示发送至配置的聊天补全服务 |

例如，选择本地识别和 API 翻译，音频留在设备上，翻译服务接收文本。两阶段都选本地并备齐依赖后，可离线生成字幕。两阶段都选 API 时，无需安装本地模型运行环境。服务费用和数据保留规则取决于所选服务商，API 密钥保存在本机设置文件中。接口要求和配置示例见[整体模型配置](packaging/USER-GUIDE.md#整体模型配置)。

## 阅读与管理字幕

- **实时阅读。** 识别结果会随上下文修订，译文逐句填入对应原文下方。浮窗可切换双语、仅原文或仅中文，并可调整位置、字号和背景透明度。
- **回看前文。** 在主窗或解锁后的浮窗中向上滚动即可回看。「回到最新」会跳到最新识别句，即使译文还在生成。主窗按钮同时恢复两处跟随，浮窗按钮只恢复浮窗。回看时重新锁定浮窗，仍可点击该按钮，正文区域的鼠标操作会传给下方应用。
- **统一术语。** 可按作品或场景建立并切换词库。整句完全匹配时直接采用指定译文，句中匹配的术语作为模型翻译时的参考。每份词库都可设为「仅本地翻译使用」。[词库格式与示例](packaging/USER-GUIDE.md#可选的翻译词库)
- **保存和导出。** 双语历史按会话保存在本机，默认保留 30 天。可按应用查找会话、搜索原文与译文，复制或导出整场或选中条目，支持 TXT 和 Markdown。

| 默认快捷键 | 操作 |
| --- | --- |
| Ctrl + Alt + F7 | 开始或停止翻译 |
| Ctrl + Alt + F8 | 显示或隐藏字幕浮窗 |
| Ctrl + Alt + F9 | 锁定或解锁浮窗布局 |

字幕延迟受硬件、模型、网络和语音长度影响。专名、否定句和重复发言仍可能出现错译或漏识。直播中出现未识别时段时，主窗和会话历史会标明缺口。混合 DPI 多屏、显示器热拔插和独占全屏的兼容性仍在验证中。

## 从源码构建

在 Windows 11 x64 上安装 Git、PowerShell 7 和 .NET 10 稳定版 SDK（10.0.100 或同一主版本的后续稳定功能带与补丁）。在仓库根目录运行以下命令。

```powershell
dotnet restore LiveLingo.slnx --locked-mode
dotnet build LiveLingo.slnx -c Release --no-restore --disable-build-servers -clp:ErrorsOnly
dotnet test --solution LiveLingo.slnx -c Release --no-build --no-progress
```

桌面界面使用 Avalonia，Windows 音频采集、字幕流水线和模型运行环境分别维护。源码结构、环境准备和测试方式见[项目文档](docs/README.md)与[贡献指南](CONTRIBUTING.md)。EXE 打包、增量发布及未签名 MSIX 开发包的构建步骤见[打包说明](docs/packaging.md)。

## 许可证

LiveLingo 采用 Apache License 2.0。第三方组件和另行下载的模型遵循各自的许可条款。[许可证](LICENSE) · [版权声明](NOTICE)
