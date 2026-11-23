# Python 运行配置

桌面本地识别使用 Qwen3-ASR 1.7B。首次安装默认使用 CPU。只需准备所选配置，模型权重可在各配置之间共用。

| 配置 | Python x64 | PyTorch | 设备与精度 |
| --- | --- | --- | --- |
| `win-x64-cpu` | 3.13.x | 2.10.0+cpu | CPU，FP32 |
| `win-x64-rocm` | 3.12.x | 2.9.1+rocm7.2.1 | 兼容的 AMD GPU，FP16 |
| `win-x64-cu128` | 3.13.x | 2.10.0+cu128 | 兼容 CUDA 12.8 的 NVIDIA GPU，支持原生 BF16 时使用 BF16，其余使用 FP16 |
| `win-x64-cu130` | 3.13.x | 2.10.0+cu130 | 兼容 CUDA 13.0 的 NVIDIA GPU，精度规则与 cu128 相同 |

选择 ROCm 前，请核对显卡、Windows 和驱动。7.2.1 的 AMD 安装说明指定 26.2.2 驱动。AMD、NVIDIA 和 CPU 环境分别安装，按用户选择运行。完整 API 模式可跳过本地环境。[Radeon 兼容表](https://rocm.docs.amd.com/projects/radeon-ryzen/en/docs-7.2.1/docs/compatibility/compatibilityrad/windows/windows_compatibility.html) · [Ryzen 兼容表](https://rocm.docs.amd.com/projects/radeon-ryzen/en/docs-7.2.1/docs/compatibility/compatibilityryz/windows/windows_compatibility.html) · [AMD 安装说明](https://rocm.docs.amd.com/projects/radeon-ryzen/en/docs-7.2.1/docs/install/installrad/windows/install-pytorch.html)

使用桌面设置中的安装按钮即可准备依赖。开发时也可从仓库根目录创建隔离环境。

```powershell
./scripts/Initialize-ModelRuntime.ps1 -Profile win-x64-cpu
```

默认环境位于 `%LOCALAPPDATA%\LiveLingoData\runtimes\<profile>`，缓存位于同一用户资源根目录下的 `cache`。可用 `-RuntimeRoot`、`-CacheRoot` 和 `-BasePython` 指定其他本地位置或已有兼容解释器。默认由 uv 获取和管理对应 Python。指定基础解释器时可使用 python.org 的标准 CPython。Microsoft Store 版的应用执行别名会使部分模型子进程脱离 Windows Job，因此本地识别环境须使用受支持的解释器。

每个配置的 `requirements.in` 记录直接依赖，`requirements.lock` 固定完整依赖与 SHA-256，`profile.json` 记录运行时身份和锁文件哈希。AMD 的 ROCm 组件从 AMD 获取，PyTorch CPU/CUDA wheel 从 PyTorch 获取，运行时与驱动按需安装。模型加载前验证指纹，实际推理性能取决于硬件和窗口长度。[用户指南](../packaging/USER-GUIDE.md)

独立的 `win-x64-whisper` 仅供开发评测使用。
