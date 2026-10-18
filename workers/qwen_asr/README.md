# Qwen ASR 工作进程

inference.py 使用固定本地 Qwen3-ASR 1.7B，通过 BF16/SDPA 执行原始生成与解码，返回生成状态和原始文本，不自动删除重复。

service.py、persistent.py 与 ipc.py 提供受 Job 监管的常驻工作进程、有限音频副本及独立控制/推理线程。宿主发送窗口范围、版本和预算，取消须等待推理实际结束。默认窗口30秒，有界回退上限120秒/2048输出token。

全零PCM可明确报告数字静音；普通低能量音频仍需推理。配置和模型指纹在加载前校验；推理不联网下载。私有缓存通过环境变量传入非C盘目录。

环境与模型准备见 [运行配置](../../runtime-profiles/README.md) 和 [模型工具](../../tools/model_eval/README.md)，测试见 [测试说明](../../tests/README.md)。
