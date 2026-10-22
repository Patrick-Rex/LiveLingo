# LiveLingo.Desktop.Windows

Windows 桌面组合根，连接 App、Runtime 和 Platform.Windows。发布包含固定 Python worker、必要校验模块、cu130 profile 和依赖锁，不包含模型权重。

每次会话使用受 Job 监管的 ASR 与私有 Ollama。关闭控制窗先停止接收更新并冲刷字幕设置，随后停止会话、清理自有模型、音频与热键，保留用户外部进程。OverlayBridge 负责 HWND、Screens、缩放与工作区位置；即时字号事件只更新外观，不重设位置、注册热键或重启模型，退出时解除订阅。布局变化交给视图模型合并自动保存，退出时先冲刷字幕调整再完成会话清理。

设置默认位于非 C 盘 data/settings.json；可用 `--settings <绝对JSON路径>` 指定独立配置。显式 `--verify <新目录> --settings <独立JSON路径> --seconds 20` 用于合成窗口检查，附加 `--wave <PCM16 WAV>` 或 `--target <PID>` 才启动真实模型。诊断结果不进入 Git。

浮窗跟随与回看由共享 App 管理；运行中显示语言切换复用即时外观事件，不改变原生窗口策略。右上角锁定按钮以从属 HWND 贴合字幕预留区域，正文穿透时按钮仍可点击且具有非激活样式；跟随位置、布局、显隐及关闭，不使用轮询鼠标或全局钩子。验证模式的控制窗和字幕窗渲染图使用 Avalonia 12 的 `PngBitmapEncoderOptions.Default` 保存为 PNG，另保存四个控制页的默认尺寸及最小尺寸渲染图，以及组合字幕正文和锁定按钮的锁定／解锁渲染图；subtitle-controls.json 核对按钮位置、样式和显隐，不等同于真实鼠标输入验收。原生测试脚本使用独立的 `<OutputDirectory>.settings.json`，不改用户设置；手动验证也应显式传入独立 `--settings` 路径。

正式安装统一使用 app/desktop，发布按 win-x64 选择平台资产并排除PDB。源码构建不会更新已安装程序；使用安装包校验、备份并显式替换，设置与模型保留在 app 外。完整操作参见 [使用指南](../../packaging/USER-GUIDE.md) 和 [打包、替换与回退](../../docs/packaging.md)。
