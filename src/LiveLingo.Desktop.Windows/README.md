# LiveLingo.Desktop.Windows

Windows 桌面组合根，连接 App、Runtime 和 Platform.Windows。发布包含固定 Python worker、必要校验模块、cu130 profile 和依赖锁，不包含模型权重。

每次会话使用受 Job 监管的 ASR 与私有 Ollama。关闭控制窗先停止会话，再清理自有模型、音频与热键，保留用户外部进程。OverlayBridge 负责 HWND、Screens、缩放与工作区位置。

设置默认位于非 C 盘 data/settings.json；可用 `--settings <绝对JSON路径>` 指定独立配置。显式 `--verify <新目录> --seconds 20` 用于合成窗口检查，附加 `--wave <PCM16 WAV>` 或 `--target <PID>` 才启动真实模型。诊断结果不进入 Git。

验证模式的控制窗和字幕窗渲染图使用 Avalonia 12 的 `PngBitmapEncoderOptions.Default` 保存为 PNG。

正式安装统一使用 app/desktop，发布按 win-x64 选择平台资产并排除PDB。参见 [使用指南](../../packaging/USER-GUIDE.md) 和 [打包流程](../../docs/packaging.md)。
