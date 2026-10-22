# 测试

测试源码和可生成的夹具入库；执行结果、录音、截图和机器环境记录不入库。

后续迭代按变更影响选择最小验证范围，不重跑首版阶段流程。纯文档检查内容、链接与格式，无需业务构建；真实设备、模型和原生窗口检查按受影响行为补充，旧报告的通过状态不能替代当前版本验证。

## .NET

从仓库根运行：

```powershell
. ./scripts/Enter-Development.ps1
dotnet restore LiveLingo.slnx --locked-mode
dotnet build LiveLingo.slnx -c Release --no-restore --disable-build-servers -clp:ErrorsOnly
dotnet test --solution LiveLingo.slnx -c Release --no-build --no-progress
```

测试使用 xunit.v3 / Microsoft Testing Platform。Core 验证身份、字幕版本和对齐，Runtime 验证调度、缓存和设置，Windows 验证原生适配及进程所有权，App 验证视图模型与 Headless 布局。Windows 测试需要真实 Windows 环境；Headless 不替代真实窗口或多屏验收。

普通异步等待、Channel 和文件读写传递 `TestContext.Current.CancellationToken`，使测试取消可中断等待；验证主动取消或超时行为时保留场景专用令牌。警告审计使用根 README 的 `-t:Rebuild -warnaserror` 命令，不以关闭分析器代替修复。

App 测试覆盖字幕末尾跟随、手动回看与返回最新、回看期间修订/撤销、新会话重置和三种显示语言切换；另验证字幕右上角同一按钮解锁与重新锁定、控制窗无重复开关、按钮随字幕关闭，以及四页最小窗口布局、运行中字幕可调、自动保存不包含未确认运行配置、手动/自动保存串行、退出冲刷与损坏文件保护。Runtime 设置测试覆盖旧配置缺省双语、显示模式往返保存和非法模式保护。

## Python 与设备

```powershell
. ./scripts/Enter-ModelEvaluation.ps1
$env:LIVELINGO_TEST_PROBE = (Resolve-Path tools/LiveLingo.Probe/bin/Release/net10.0-windows10.0.26100.0/LiveLingo.Probe.exe).Path
& 'D:\Soft Ware\LiveLingo\runtimes\win-x64-cu130\Scripts\python.exe' -m unittest discover -s tests/model_eval -v
```

未准备所需解释器或 Probe 时不能将跳过的检查计为通过。音频夹具见 [生成说明](Fixtures/Audio/README.md)，模型准备见 [运行配置](../runtime-profiles/README.md)。

原生窗口检查使用 `scripts/Test-DesktopNative.ps1`，设置及退出检查使用 `scripts/Test-DesktopSettings.ps1`；两者要求 `-Executable` 和新的非 C 盘 `-OutputDirectory`。原生脚本使用独立的 `<OutputDirectory>.settings.json`，设置脚本使用输出目录中的设置文件，均不更改用户配置。原生窗口脚本覆盖拖动、四边四角缩放、Ctrl+滚轮字号调整及恢复穿透；验证模式另输出控制窗四页的默认及最小尺寸截图。App 测试另覆盖实际字号按钮、双语文字即时更新、滚动区和关闭屏障。GPU、设备拔插和物理多屏需要单独验证。

## 安装包

```powershell
./scripts/Test-LiveLingoPackage.ps1 -Archive 'D:\Soft Ware\LiveLingo\packages\LiveLingo-0.1.1-win-x64.zip'
```

示例版本按实际 ZIP 修改。脚本在独立临时目录验证完整性、错误包拒绝、首次安装、显式升级和设置保留，完成后清理本次临时目录；不替换正式安装，也不证明 GPU 或窗口交互通过。构建、备份、正式替换和回退见 [打包说明](../docs/packaging.md)。
