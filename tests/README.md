# 测试

测试源码和可生成的夹具入库；执行结果、录音、截图和机器环境记录不入库。

按本次变更影响选择最小验证范围。纯文档检查内容、链接与格式，无需业务构建；真实设备、模型和原生窗口检查按受影响行为补充，旧报告的通过状态不能替代当前版本验证。

## .NET

从仓库根运行。存储及桌面历史测试在当前临时目录中创建独立数据。

```powershell
dotnet restore LiveLingo.slnx --locked-mode
dotnet build LiveLingo.slnx -c Release --no-restore --disable-build-servers -clp:ErrorsOnly
dotnet test --solution LiveLingo.slnx -c Release --no-build --no-progress
```

构建使用本机 NuGet 配置，不要求固定盘符或执行环境脚本。开发环境变更运行 `./scripts/Test-DevelopmentEnvironment.ps1`，覆盖默认设置、已有环境变量、含空格路径、嵌套调用及无效缓存路径；实际还原／构建另按上述命令检查。要显式迁移当前终端的开发缓存，可先运行 `. ./scripts/Enter-Development.ps1 -CacheRoot <可写绝对路径>`。涉及模型、音频溢写的测试仍需其运行资源配置。

测试使用 xunit.v3 / Microsoft Testing Platform v2。Core 验证身份、字幕版本和对齐，Runtime 验证调度、缓存、设置与历史存储，Windows 验证原生适配、进程所有权和 DesktopHistory 宿主，App 验证视图模型与 Headless 布局。Windows 测试工程引用桌面宿主，DesktopHistoryTests 使用隔离文件、不启动 GPU 或模型；原生适配测试仍需要真实 Windows 环境。Headless 不替代真实窗口或多屏验收。

桌面界面与历史记录的最小测试可在相应工程完成 locked restore 和构建后按 MTP 类过滤运行；不使用 VSTest 的 `--filter`：

```powershell
dotnet test --project tests/LiveLingo.App.Tests/LiveLingo.App.Tests.csproj -c Release --no-build --no-progress --filter-class 'LiveLingo.App.Tests.*'
dotnet test --project tests/LiveLingo.Runtime.Tests/LiveLingo.Runtime.Tests.csproj -c Release --no-build --no-progress --filter-class 'LiveLingo.Runtime.Tests.Storage.*'
dotnet test --project tests/LiveLingo.Windows.Tests/LiveLingo.Windows.Tests.csproj -c Release --no-build --no-progress --filter-class LiveLingo.Windows.Tests.DesktopHistoryTests
```

普通异步等待、Channel 和文件读写传递 `TestContext.Current.CancellationToken`，使测试取消可中断等待；验证主动取消或超时行为时保留场景专用令牌。警告审计使用根 README 的 `-t:Rebuild -warnaserror` 命令，不以关闭分析器代替修复。

OwnedJobTests 会主动把测试哨兵句柄设为可继承，因此使用独占测试集合，避免同进程中并行执行的环境或安装探测继承该人工句柄。其余测试仍按原并行策略运行；保留真实管道、父孙进程归属、句柄白名单和退出断言。

App 测试覆盖三一级页面、设置分类及设置专属应用栏、主题独立性、最小尺寸、档案选择与删除确认、固定内容导出、保存失败反馈、字幕跟随/回看/修订/撤销及语言切换。回看测试拖动解锁浮窗的滚动条读取首句，检查实时和历史正文中的状态、悬停及读屏信息。小锁验证同一入口解锁与重新锁定、40 DIP 预留区不遮正文、按钮随字幕关闭；字幕在运行中可调，自动保存不包含未确认运行配置，两类保存串行，退出冲刷且保护损坏设置。

短句回归覆盖从识别到双语显示的各环节。Core 验证句末、引语、连续无标点长段、追加输入后的短句前缀与跨修订的逐句译文复用。Runtime 验证逐句回填、重复发言、尾句修订、翻译截断、切换模型和直播相邻短段的识别覆盖、2 秒短段重试失败后的缺口及续译。Windows 的 ApiSessionTests 通过本机 HTTP 检查每句的独立请求。App 的 ViewTests 用 40 句识别结果检查逐句跟随与回看，并在鼠标停留时检查控件身份、悬停、译文和无障碍版本提示的更新。DesktopBehaviorTests 覆盖重复快照及相同序号停止，验证实时停止标记和原有档案阶段。

SubtitlePipelineTests 使用缩小的积压上限验证直播取消旧调用、下一包在新代次继续以及后续设备采集代次变化。模型加载期间的用例覆盖新代次音频缓存至输入结束、连续重置和停止等待在途加载退出。回放用例验证超限终止，识别用例检查新音频到来后重新选择失败终点、保留安静尾部及仅将未处理音频计入压力。TranslationHistoryTests 检查旧代次字幕和缺口范围落盘、正常结束与中断、重复范围去重及摘要容量。Windows 的 ApiSessionTests 使用本机 HTTP 和可控输入，贯通生产宿主的恢复状态、同场历史缺口及恢复后的短句翻译。

词库测试覆盖旧设置默认关闭、命名表选择、开关及使用范围往返、API 实际请求跳过仅本地翻译的词表、无效草稿保护、整句命中跳过推理和句中匹配保持原文。

四语配置回归由 `ModelConfigurationTests` 验证表单标签、模型 JSON 的显式语言、保存、撤销和实时页摘要，`DesktopSettingsStoreTests` 检查旧 `auto` 或缺失语言的设置按日语加载，以及保存新选择后的重启。`HistoryViewModelTests` 检查新档案的语言标签和旧 `auto` 档案的显示。`AlignmentBoundaryTests` 检查不同源语言的断句证据及英韩词间分隔。`OpenAiApiTests` 验证四种转写请求中的 `language`、翻译提示和词条，`OllamaTranslationTests` 验证本地翻译提示。

Runtime Storage 测试覆盖旧设置默认值、历史同代次替换与撤销、真实重复、跨采集代次保留、异常中断、部分保存、存储失败重试、删除后不复活、固定导出和保留期边界。Windows DesktopHistoryTests 验证跨宿主重启、旧记录器固定目录、验证根隔离、旧失败不被新会话成功遮蔽、删除旧失败后保留后续失败、自动清理及退出冲刷。

Windows 的协议进程夹具使用 PATH 中的 Python 3，也可设置 LIVELINGO_TEST_PYTHON 指定解释器。

## Python 与设备

```powershell
. ./scripts/Enter-ModelEvaluation.ps1
$env:LIVELINGO_TEST_PROBE = (Resolve-Path tools/LiveLingo.Probe/bin/Release/net10.0-windows10.0.26100.0/LiveLingo.Probe.exe).Path
& (Join-Path $env:LOCALAPPDATA 'LiveLingoData\runtimes\win-x64-cpu\Scripts\python.exe') -m unittest discover -s tests/model_eval -v
```

未准备所需解释器或 Probe 时不能将跳过的检查计为通过。音频夹具见 [生成说明](Fixtures/Audio/README.md)，模型准备见 [运行配置](../runtime-profiles/README.md)。

原生窗口检查使用 `scripts/Test-DesktopNative.ps1`，设置及退出检查使用 `scripts/Test-DesktopSettings.ps1`；两者要求 `-Executable` 和新的本地 `-OutputDirectory`。原生脚本使用独立的 `<OutputDirectory>.settings.json`，设置脚本使用输出目录中的设置文件，均不更改用户配置；验证历史位于 `<OutputDirectory>.data/history`。原生窗口脚本设计覆盖拖动、四边四角缩放、Ctrl+滚轮与恢复穿透，移动位置读取实际拖动区中心。验证模式输出三一级页面和设置分类的明暗主题、默认及最小尺寸截图。

透明级别、原生样式、按钮几何与退出检查不能替代跨进程点击穿透、实际拖动或焦点检查。自动输入无法激活接收器时须记录为未完成。GPU、设备拔插、100%/150%/200% DPI 与物理多屏按实际执行范围单独记录。

## 安装包

```powershell
./scripts/Test-LiveLingoPackage.ps1 -Archive './artifacts/packages/LiveLingo-0.1.1-win-x64.zip' -TemporaryRoot $env:TEMP
```

示例版本及临时根按本机实际位置修改。`-TemporaryRoot` 使用本机可写目录，省略时使用当前 `TEMP`。脚本在独立临时目录验证完整性、错误包拒绝、首次安装、显式升级和设置保留，完成后清理本次临时目录；不替换正式安装，也不证明 GPU 或窗口交互通过。构建、备份、正式替换和回退见 [打包说明](../docs/packaging.md)。

## 安装引导与增量更新

MSIX 验证运行 `./scripts/Test-LiveLingoMsix.ps1 -Package './artifacts/msix/LiveLingo-0.2.6-win-x64-unsigned.msix'`（版本按实际修改），需要 Windows SDK MakeAppx；构建入口会自动运行同样检查。验证包含 SDK 解包、Windows 11 x64 full-trust 清单、独立 .NETCore 运行时及许可、必需载荷和禁止文件，不安装或启动包。使用 `--filter-class LiveLingo.Windows.Tests.DesktopUpdatesTests` 验证 MSIX 更新隔离，`LiveLingo.App.Tests.MaintenanceTests` 验证渠道提示及不发起 EXE 检查／下载，`LiveLingo.Runtime.Tests.Storage.DesktopSettingsStoreTests` 验证首次 API 默认和旧文件兼容；构建命令同上。真实安装及商店认证需另行验证。

维护状态测试类 `LiveLingo.App.Tests.MaintenanceTests` 验证启动不下载、一次点击下载后请求安装、失败不重启、待应用设置保护、按缺项安装与关闭取消；`LiveLingo.Windows.Tests.EnvironmentCheckTests` 验证缺失和损坏模型提示，以及 Ollama 新版清单优先、旧版回退、错误新清单不回退和缺失 blob 拒绝。清单夹具不包含模型权重。`DependencySetupTests` 验证发现现有程序、保留已有路径，以及真实 PowerShell 的快照传递、错误与取消回收，不安装软件。沿用上文 MTP `--filter-class` 命令；多个类名作为独立参数传入。

`Install-LiveLingoDependencies.ps1 -Component Check` 只报告路径，不安装。`Test-LiveLingoRelease.ps1 -FullPackage <full.nupkg>` 检查必要文件、外部依赖排除及唯一根应用清单；同时提供 `-PreviousPackage <旧full> -DeltaPackage <新delta>` 时，在隔离目录调用随包原生 Update.exe 还原差分并逐项校验内容。第三方 `.nuspec` 必须另存为 `.nuspec.xml`，以覆盖原生还原后清单选择与 CLI ZIP 顺序不同的回归。测试不安装程序、不启动模型，不能代替干净机器缺少 .NET/驱动的真实安装验收。发布命令见 [交付说明](../docs/packaging.md)。

`scripts/Test-LiveLingoSetup.ps1` 可用 Windows PowerShell 5.1 或 PowerShell 7 执行，验证只检查不安装、C盘拒绝、旧 ZIP 混装拒绝、非空目录保护、错误资源路径拒绝和自定义模型缓存传播共6项，不下载依赖或修改正式安装。

UpdateReleaseSourceTests 使用真实 SimpleWebSource 与假下载器，验证固定更新 Release 的清单和包路径、多版本 full/delta 保留、404/403/429 不回退其他来源及取消传播。

`./scripts/Test-LiveLingoReleaseExport.ps1` 用隔离夹具检查普通下载仅安装包、完整历史更新清单保留、脚本排除、哈希一致、安装器产品／版本一致，以及坏哈希、缺失、越界、重复和拒绝覆盖。实际分流运行 `./scripts/Export-LiveLingoRelease.ps1 -Version 0.2.6 -ReleaseDirectory artifacts/releases`，只生成本地待发布目录，不上传。

`scripts/Start-LiveLingo.ps1` 优先读取当前用户卸载记录中的安装位置，再查默认用户目录，最后兼容既有 D 盘目录；从安装根的 `LiveLingo.exe` 启动。显式 `-ApplicationDirectory` 也支持独立发布目录中的 `LiveLingo.Desktop.Windows.exe`。修改入口时分别核对 Windows PowerShell 5.1 与 PowerShell 7 的路径选择；脚本使用 ASCII 提示，兼容无 BOM 文本在 PowerShell 5.1 中的解析。

模型来源回归：App 的 ModelConfigurationTests 覆盖四种组合、整体 JSON 原子导入、表单切换、草稿/自动保存隔离、Headless 可见性，以及两阶段来源下拉的展开/悬停/取消、明确点击/键盘选择和收起后滚轮滚动页面而不改来源；Runtime 的 OpenAiApiTests 覆盖请求路径、Bearer、PCM16 WAV、结束状态、HTTP 错误、响应上限、取消与超时，DesktopSettingsStoreTests 覆盖旧设置和 API 字段持久化。Windows 的 EnvironmentCheckTests、ApiSessionTests 与 LocalPipelineModelsTests 覆盖按来源检查、全 API 无本地资产启动与已有本地清理路径。使用上文 MTP --filter-class 指定这些类；假传输与空输入宿主检查不代表真实服务商联调通过。

ApiSessionTests 另通过随机回环端口的 HTTP 测试服务，从非空音频贯通生产 DesktopSession 的转写、翻译和字幕输出。四种源语言逐一核对转写代码、短句翻译和历史摘要，并检查独立鉴权、地址前缀及全 API 运行时的进程归属。响应为确定夹具，不代表外部服务商联调或模型质量验收。
