# LiveLingo.Runtime

组合有界 Channels、单写入者状态机、连续音频缓存、重叠识别、Ollama 翻译和 MAF 工作流，不依赖 UI 或 Windows API。

SubtitlePipeline 管理字幕处理与积压，过期结果由身份屏障拒收。正常识别窗口30秒，对齐失败时默认最多回退到120秒；截断生成不进入字幕。取消与模型冷启动都有共同时间预算。

DesktopSettingsStore 以同目录原子替换保存版本化设置，损坏文件保留；WaveReplaySource 提供连续 PCM 转换和按墙钟回放。测试及构建见 [测试说明](../../tests/README.md)。
