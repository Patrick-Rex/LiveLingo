# 模型准备与评测工具

这些脚本用于显式准备和评测本地模型；正常桌面推理不会下载权重。执行前使用 `scripts/Enter-ModelEvaluation.ps1` 设置进程级缓存，并准备 [独立 Python 环境](../../runtime-profiles/README.md)。

## 准备 Qwen

在仓库根运行：

```powershell
. ./scripts/Enter-ModelEvaluation.ps1
& 'D:\Soft Ware\LiveLingo\runtimes\win-x64-cu130\Scripts\python.exe' tools/model_eval/prepare_model.py --output 'D:\Soft Ware\LiveLingo\models\Qwen3-ASR-1.7B-7278e1e'
```

下载固定 revision，保存逐文件哈希；完整模型目录拒绝覆盖。桌面复用 common.py、prepare_model.py 和 download.py 校验资产。

## 评测入口

| 脚本 | 职责 |
| --- | --- |
| asr_benchmark.py | 固定音频窗口的 Qwen 识别 |
| whisper_benchmark.py | 独立 Whisper 对照 |
| private_ollama.py / translation.py | TranslateGemma 翻译及明确选择的提示策略 |
| compare.py | 串行/并发调度对比 |
| fault_benchmark.py | 受控 OOM、静音及恢复 |
| window_recovery_benchmark.py | 有界扩窗的耗时和显存测量 |
| corpus.py / quality.py | 显式语料准备与参考文本对比 |
| audit_pipeline.py | 已有流水线记录的版本、积压和资源一致性分析 |

用对应脚本的 `--help` 查看输入参数。模型评测应由 [Probe 的 model-eval](../LiveLingo.Probe/README.md) 监管，以回收本次子进程。输出使用新的非 C 盘目录；不要把退出码或规则检查通过等同于语义正确。

默认翻译使用官方单 user 提示，保持完整原文，关闭质量重试；其他提示、保守重复检查及重试选项仅供显式实验。

公开语料由使用者按其授权条款下载，不收入仓库。已登记素材可用 `scripts/Remove-EvaluationCorpus.ps1 -Directory <素材目录>` 删除，脚本先校验清单与哈希。测试命令见 [测试说明](../../tests/README.md)；评测结果和录音留在本地。
