# Qwen ASR 工作进程

inference.py 使用固定本地 Qwen3-ASR 1.7B，在所选设备上通过 SDPA 执行原始生成与解码，返回生成状态和原始文本，不自动删除重复。

service.py、persistent.py 与 ipc.py 提供受 Job 监管的常驻工作进程、有限音频副本及独立控制/推理线程。宿主为每个窗口传入明确的 Chinese、English、Japanese 或 Korean，Qwen 按锁定 SDK 的 `language {Language}<asr_text>` 格式强制该语言。响应保留指定的语言和原始转写正文。缺失语言、`auto` 及未知值在推理前被拒绝。宿主还发送窗口范围、版本和预算，取消须等待推理实际结束。默认窗口30秒，有界回退上限120秒/2048输出token。

全零 PCM 直接报告 `exact-digital-silence`。普通低能量音频仍经模型处理，强制语言模式下生成内容为空或全空白且以 EOS 结束才报告 `model-no-speech`。启动时由一个线程校验模型文件指纹，主线程同时导入推理依赖，校验通过后才加载权重；推理不联网下载。私有缓存通过环境变量传入所选本地目录。默认 CPU 使用 FP32，AMD ROCm 使用 FP16，NVIDIA CUDA 根据设备支持使用 BF16 或 FP16。CPU 路径跳过 GPU 同步与显存统计。

环境与模型准备见 [运行配置](../../runtime-profiles/README.md) 和 [模型工具](../../tools/model_eval/README.md)，测试见 [测试说明](../../tests/README.md)。
