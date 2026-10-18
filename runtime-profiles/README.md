# Python 运行配置

每个 profile 提供固定 Python/依赖版本、解释器路径及 requirements.lock 哈希。桌面默认使用 `win-x64-cu130`；`win-x64-cu128` 和 `win-x64-whisper` 为独立对照配置，不需全部安装。

```powershell
. ./scripts/Enter-ModelEvaluation.ps1
./scripts/Initialize-ModelRuntime.ps1 -Profile win-x64-cu130 -BasePython 'C:\你的Python安装路径\python.exe'
```

基线解释器为 Python 3.13.2。脚本将环境建立在 `D:\Soft Ware\LiveLingo\runtimes\<profile>`，按哈希锁安装并检查依赖；不会修改其他环境。cu130 使用 PyTorch 2.10.0+cu130，cu128 使用对应2.10.0+cu128。不要在同一环境反复覆盖 CUDA 构建。

安装成功不证明 GPU 或模型可用。运行实际 CUDA 运算，再按 [用户指南](../packaging/USER-GUIDE.md) 准备模型。固定 profile 与哈希锁用于重现依赖，不代表任意设备或语料质量通过。

改变依赖时同步 requirements.in、requirements.lock 及 profile 中的锁哈希；日常构建不重新解析版本。本机评测报告与准入记录不入库。
