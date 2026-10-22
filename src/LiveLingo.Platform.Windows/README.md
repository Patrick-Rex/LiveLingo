# LiveLingo.Platform.Windows

Windows Application Loopback、应用身份查询、MMDevice/电源通知、Job 进程所有权及原生窗口适配。

音频缓冲在专用 MTA 线程读取和释放。目标身份包含PID、创建时间和路径；迟到激活只释放资源。OwnedJob 在进程创建时关联子树，退出只回收本程序创建的进程。

Windows 目录提供透明、穿透、非激活和全局热键机制，保持无 Avalonia 依赖。原生样式支持可交互但不激活的按钮窗口，允许字幕正文继续独立穿透。Windows 测试需要实际系统，模拟测试不替代物理设备验收；命令见 [测试说明](../../tests/README.md)。
