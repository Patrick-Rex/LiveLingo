# 测试

测试源码和可生成的夹具入库；执行结果、录音、截图和机器环境记录不入库。

后续迭代按变更影响选择最小验证范围，不重跑首版阶段流程。纯文档检查内容、链接与格式，无需业务构建；真实设备、模型和原生窗口检查按受影响行为补充，旧报告的通过状态不能替代当前版本验证。

## .NET

从仓库根运行。存储及桌面历史测试会用系统临时目录创建运行数据，仍要求非 C 本地盘；先将下面示例路径换成本机可写的非 C 盘目录。设置仅影响当前终端及子进程，不改变 NuGet 缓存位置：

```powershell
$testTemp = 'D:\LiveLingo-tests\tmp'
New-Item -ItemType Directory -Path $testTemp -Force | Out-Null
$env:TEMP = $testTemp
$env:TMP = $testTemp
dotnet restore LiveLingo.slnx --locked-mode
dotnet build LiveLingo.slnx -c Release --no-restore --disable-build-servers -clp:ErrorsOnly
dotnet test --solution LiveLingo.slnx -c Release --no-build --no-progress
```

构建使用本机 NuGet 配置，不要求固定盘符或执行环境脚本。开发环境变更运行 `./scripts/Test-DevelopmentEnvironment.ps1`，覆盖默认设置、已有环境变量、含空格路径、嵌套调用及无效缓存路径；实际还原／构建另按上述命令检查。要显式迁移当前终端的开发缓存，可先运行 `. ./scripts/Enter-Development.ps1 -CacheRoot <可写绝对路径>`。涉及模型、音频溢写的测试仍需其运行资源配置。

测试使用 xunit.v3 / Microsoft Testing Platform v2。Core 验证身份、字幕版本和对齐，Runtime 验证调度、缓存、设置与历史存储，Windows 验证原生适配、进程所有权和 DesktopHistory 宿主，App 验证视图模型与 Headless 布局。Windows 测试工程引用桌面宿主，DesktopHistoryTests 使用隔离文件、不启动 GPU 或模型；原生适配测试仍需要真实 Windows 环境。Headless 不替代真实窗口或多屏验收。

桌面体验改造的最小测试可在相应工程完成 locked restore 和构建后按 MTP 类过滤运行；不使用 VSTest 的 `--filter`：

```powershell
dotnet test --project tests/LiveLingo.App.Tests/LiveLingo.App.Tests.csproj -c Release --no-build --no-progress --filter-class 'LiveLingo.App.Tests.*'
dotnet test --project tests/LiveLingo.Runtime.Tests/LiveLingo.Runtime.Tests.csproj -c Release --no-build --no-progress --filter-class 'LiveLingo.Runtime.Tests.Storage.*'
dotnet test --project tests/LiveLingo.Windows.Tests/LiveLingo.Windows.Tests.csproj -c Release --no-build --no-progress --filter-class LiveLingo.Windows.Tests.DesktopHistoryTests
```

普通异步等待、Channel 和文件读写传递 `TestContext.Current.CancellationToken`，使测试取消可中断等待；验证主动取消或超时行为时保留场景专用令牌。警告审计使用根 README 的 `-t:Rebuild -warnaserror` 命令，不以关闭分析器代替修复。

App 测试覆盖三一级页面、设置分类及设置专属应用栏、主题独立性、最小尺寸、档案选择与删除确认、固定内容导出、保存失败反馈、字幕跟随/回看/修订/撤销及语言切换。小锁验证同一入口解锁与重新锁定、40 DIP 预留区不遮正文、按钮随字幕关闭；字幕在运行中可调，自动保存不包含未确认运行配置，两类保存串行，退出冲刷且保护损坏设置。

Runtime Storage 测试覆盖旧设置默认值、历史同代次替换与撤销、真实重复、跨采集代次保留、异常中断、部分保存、存储失败重试、删除后不复活、固定导出和保留期边界。Windows DesktopHistoryTests 验证跨宿主重启、旧记录器固定目录、验证根隔离、旧失败不被新会话成功遮蔽、删除旧失败后保留后续失败、自动清理及退出冲刷。

## Python 与设备

```powershell
. ./scripts/Enter-ModelEvaluation.ps1
$env:LIVELINGO_TEST_PROBE = (Resolve-Path tools/LiveLingo.Probe/bin/Release/net10.0-windows10.0.26100.0/LiveLingo.Probe.exe).Path
& 'D:\Soft Ware\LiveLingo\runtimes\win-x64-cu130\Scripts\python.exe' -m unittest discover -s tests/model_eval -v
```

未准备所需解释器或 Probe 时不能将跳过的检查计为通过。音频夹具见 [生成说明](Fixtures/Audio/README.md)，模型准备见 [运行配置](../runtime-profiles/README.md)。

原生窗口检查使用 `scripts/Test-DesktopNative.ps1`，设置及退出检查使用 `scripts/Test-DesktopSettings.ps1`；两者要求 `-Executable` 和新的非 C 盘 `-OutputDirectory`。原生脚本使用独立的 `<OutputDirectory>.settings.json`，设置脚本使用输出目录中的设置文件，均不更改用户配置；验证历史位于 `<OutputDirectory>.data/history`。原生窗口脚本设计覆盖拖动、四边四角缩放、Ctrl+滚轮与恢复穿透，移动位置读取实际拖动区中心。验证模式输出三一级页面和设置分类的明暗主题、默认及最小尺寸截图。

本轮已通过透明级别、原生样式、按钮几何和退出检查；自动输入检查因无法激活自有接收器提前结束，不能据此声称跨进程点击穿透、实际拖动或焦点验收通过。GPU、设备拔插、100%/150%/200% DPI 与物理多屏仍需单独验证。

## 安装包

```powershell
./scripts/Test-LiveLingoPackage.ps1 -Archive './artifacts/packages/LiveLingo-0.1.1-win-x64.zip' -TemporaryRoot 'D:\Soft Ware\LiveLingo\cache\tmp'
```

示例版本及临时根按本机实际位置修改。`-TemporaryRoot` 必须在可写的非 C 本地盘（省略时使用当前 `TEMP`），因为本项验证正式安装的非 C 盘约束；普通构建和打包没有此限制。脚本在独立临时目录验证完整性、错误包拒绝、首次安装、显式升级和设置保留，完成后清理本次临时目录；不替换正式安装，也不证明 GPU 或窗口交互通过。构建、备份、正式替换和回退见 [打包说明](../docs/packaging.md)。
