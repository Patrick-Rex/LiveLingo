# LiveLingo.Desktop.Windows

Windows 桌面组合根，连接 App、Runtime 和 Platform.Windows。发布包含固定 Python worker、必要校验模块、cu130 profile 和依赖锁，不包含模型权重。

控制窗提供实时翻译、历史记录、设置三个一级入口。主题默认跟随系统，也可选择浅色或深色；字幕使用独立深色背景。主题与字幕外观即时生效并串行自动保存，模型、语言、路径、保留期和快捷键停止后在设置页应用或撤销。未应用草稿不进入运行配置或自动保存，跨页面保留；数据目录切换只改变历史库位置，不迁移旧文件或设置文件。

每次会话仅为所选本地阶段创建受 Job 监管的 ASR 或私有 Ollama；所选 API 阶段使用 HTTP 客户端。全 API 不要求本地推理环境或模型。DesktopSession 在完整流水线快照进入 UI 合并器前交给历史记录器；DesktopHistory 固定每场的数据目录，协调保存状态与启动、会话结束、每日的保留期清理。历史写在已应用的 `DataPath/history`，恢复采集仍属同一会话，前后采集代次分别保存。失败独立提示并保留可导出的内存快照，后续成功会话不会掩盖此前未恢复的失败；容量缺失明确显示部分保存，重试不能恢复已丢失内容。存储与版本规则见 [协议](../../docs/pipeline-protocol.md)。

关闭时先停止档案交互和实时 UI 消费，冲刷外观设置并停止会话，继续回收自有模型、音频和热键；不等待保存对话框关闭才开始模型清理。单场历史冲刷最多等待 10 秒，历史服务全部库和维护任务共享另一段 10 秒退出预算；超时记录失败，继续宿主与窗口清理。这不是整个应用退出耗时上限。外部目标和外部 Ollama 不受清理影响。

OverlayBridge 负责 HWND、Screens、缩放与工作区位置。即时外观事件不重设位置、注册热键或重启模型；布局变化交给视图模型合并自动保存，退出时解除订阅。右上角小锁使用 40×40 DIP 从属窗口与同尺寸预留区，按钮具有无障碍名称与提示，正文避让它；锁定仅显示字幕和短字幕状态，解锁显示紧凑拖动、显示语言和字号工具。四边四角缩放、Ctrl+滚轮与回看保留，回到最新仅在回看时显示。原生策略保持正文穿透、锁图标独立命中及非激活，不使用轮询鼠标或全局钩子。

设置默认位于非 C 盘 `data/settings.json`；可用 `--settings <绝对JSON路径>` 指定独立配置。显式 `--verify <新目录> --settings <独立JSON路径> --seconds 20` 用于合成窗口检查，附加 `--wave <PCM16 WAV>` 或 `--target <PID>` 才启动真实模型。验证历史固定写到 `<验证目录>.data/history`，即使测试修改 DataPath 也不会切回用户历史；合成样稿只在验证入口建立。诊断结果不进入 Git。

验证模式使用 Avalonia 12 的 `PngBitmapEncoderOptions.Default` 保存 PNG，覆盖三一级页面和设置分类的浅深主题、默认及最小尺寸，以及字幕锁定／解锁的组合渲染图。`subtitle-controls.json` 核对按钮几何、样式和显隐，不等同于真实鼠标输入验收。原生脚本使用独立的 `<OutputDirectory>.settings.json`，不改用户设置；手动验证也应显式传入独立 `--settings` 路径。本轮已验证透明级别、原生样式、几何及退出；自动输入检查因无法激活自有接收器提前结束，跨进程点击穿透、实际拖动与多屏 DPI 尚不能记为通过。

单 EXE 首次安装默认使用用户应用目录；仓库 Start-LiveLingo.ps1 优先读取卸载记录，再查用户目录和既有 D 盘 app/LiveLingo，从安装根的稳定启动器打开。旧 ZIP 使用 app/desktop，迁移并确认更新正常后可清理。发布按 win-x64 选择平台资产并排除PDB。源码构建不会更新已安装程序，设置与模型保留在程序目录外，托管安装 packages 中的当前完整包用于后续差分还原。完整操作参见 [使用指南](../../packaging/USER-GUIDE.md) 和 [打包、替换与回退](../../docs/packaging.md)。

DesktopEnvironment 提供有限输出、15秒超时且可取消的 Python/驱动初检，本地模型只检查清单身份及长度；真实加载仍由会话负责。DesktopUpdates 隔离 Velopack，固定 GitHub 稳定源，元数据最多4 MiB、单请求30秒，更新包最多1 GiB、下载30分钟。Program 在创建宿主前禁用启动自动应用；用户请求安装后，DesktopApplication 先取消维护任务、结束既有退出流程，成功才调用等待退出的更新器，并保留原始启动参数。Velopack 使用 app/LiveLingo/current 与 packages；模型、设置和历史位于外部。

GitHubReleaseSource 在 API 403/429 时回退同仓库 latest/download 的渠道清单，下载保持与成功检查的来源一致。取消及其他错误不启动额外回退请求。

DesktopEnvironment.Setup 从已配置路径、固定本地资源目录、PATH、VIRTUAL_ENV 和 OLLAMA_MODELS 有界查找缺失路径，不递归扫描整盘，不替换已有不兼容资产。界面点击缺项后，通过 ArgumentList 执行包内 PowerShell，使用已应用设置的临时快照；进程执行策略不修改全局配置。输出保留最后8Ki字符，安装限时2小时，取消与关闭回收自有进程树并删除快照；完成后重新发现与检查。QwenModel 与 TranslationModel 分别执行，避免安装单个模型时要求另一套运行环境。

TranslateGemma 初检与路径发现优先读取 `manifests-v2/ollama.com/library/translategemma/<tag>`，仅在该文件不存在时使用 `manifests/registry.ollama.ai/library/translategemma/<tag>`。旧目录可能只是附加原清单引用的兼容副本，不能用其摘要代替有效新清单。新清单存在但指纹错误时明确失败；仍验证固定 digest 和所有引用 blob 的长度，不改写用户模型目录。

007 模型来源独立选择：DesktopEnvironment 仅发现与检查所选本地组件；全 API 不探测 Python/NVIDIA，也不读取本地 profile。DesktopSession 只创建所选本地进程，API 接入沿用有界调度与退出取消。整体 JSON 模板随构建和发布输出；安装快照清除 API Key，准备脚本同样按来源跳过不需要的组件。
