# LiveLingo 安装与使用

Windows 11 x64 本地日语转简体中文字幕。安装目录固定为 `D:\Soft Ware\LiveLingo\app\desktop`；版本号只用于压缩包名。包内没有模型、Python 环境、测试录音、测试源码或历史讨论。

## 安装与更新

1. 安装 .NET 10 x64 Runtime（10.0.12 或更高的 10.0 修订版）：https://dotnet.microsoft.com/download/dotnet/10.0 。运行程序无需 SDK。
2. 将 ZIP 解压到非 C 盘的临时目录，且不要解压到目标 `app` 目录内。可用 `Get-FileHash <ZIP路径> -Algorithm SHA256` 对照随包 `.sha256` 文件检查传输完整性。
3. 在解压目录打开 64 位 PowerShell 5.1 或 7，运行 `./Install.ps1`。升级已有安装运行 `./Install.ps1 -ReplaceExisting`；先关闭 LiveLingo。脚本校验全部应用文件，复制并复核后切换目录，清理上一份程序。用户设置、模型、环境和数据位于应用目录外，不受替换影响。
4. 双击 `D:\Soft Ware\LiveLingo\app\desktop\LiveLingo.Desktop.Windows.exe`。成功安装后可以删除解压目录，保留 ZIP 用于重装。

这是 ZIP + PowerShell 安装包，没有 MSI/EXE 安装向导、数字签名、自动更新、开机启动或注册表卸载项。未修改 PowerShell 执行策略；组织策略禁止运行时，请由管理员按其策略处理。SHA-256 清单用于完整性检查，不代替可信来源或数字签名。

`./Install.ps1 -VerifyOnly` 只验证包。`-ApplicationRoot 'E:\LiveLingo\app'` 可改变程序安装位置，末级仍须为 `app`，程序目录恒为 `desktop`。当前依赖准备脚本默认使用以下 D 盘布局；改变程序安装位置不会迁移模型或设置。

安装失败若提示 `.desktop-install` / `.desktop-backup` 残留，请先检查目录；不要直接反复安装。`.desktop-backup` 为上一份应用，模型和设置不在其中。脚本不会自动清除未知的旧事务目录。

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
- Ctrl+Alt+F9：进入/退出布局模式；布局模式可拖动、调整大小和滚动查看长内容，退出后恢复穿透。
- 在停止状态调整字体、背景、模型、路径和快捷键，然后保存并应用。
- 关闭控制窗会停止采集并退出本程序的模型进程；原有浏览器和外部 Ollama 保留。

默认设置为 `D:\Soft Ware\LiveLingo\data\settings.json`，损坏时会显示错误并保留原文件。可以用 `--settings <非C盘绝对JSON路径>` 选择独立设置。

## 限制与卸载

模型仍可能漏掉真实重复、人名或误译否定；首次可读中文 3–6 秒是目标，并非所有内容均能达到。正常 30 秒窗口，对齐失败时最多回退到 120 秒，超限会显示积压，不静默丢弃。物理混合 DPI、拔屏和独占全屏尚未完成完整验收。

卸载时关闭程序，只删除 `app\desktop`。模型、Python 环境、设置和数据独立保留，按需要另外处理。不要删除其他应用共享的 Ollama 模型。

## 许可证

LiveLingo 使用随包 LICENSE 中的 Apache-2.0 许可证，版权声明见 NOTICE。第三方许可文件位于 licenses，索引为 THIRD-PARTY-NOTICES.md；另行下载的模型遵循各自条款。
