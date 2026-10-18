# LiveLingo.App

共享 Avalonia 表现层，仅引用 Core；使用 CommunityToolkit.Mvvm 和 AXAML 编译绑定，平台操作由中立契约交给宿主。

SnapshotPump 合并最新完整快照，并在执行回调时核对会话、单调序号与停止屏障。控制窗显示有效历史，浮窗显示最近两个非空块；旧译文标为待更新，不修改模型原文。

视图模型互斥启停与保存；布局、字号、背景和路径在停止后保存应用。测试使用 Headless，真实点击穿透需额外原生检查。命令见 [测试说明](../../tests/README.md)。
