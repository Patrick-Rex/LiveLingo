namespace LiveLingo.Core.Desktop;

/// <summary>描述轻量依赖检查结果，不代表真实模型推理已经验证。</summary>
/// <param name="Name">依赖名称。</param><param name="Ready">初步检查是否通过。</param><param name="Message">结果或修复说明。</param>
/// <param name="Component">缺项对应的安装入口；为空时只显示诊断。</param>
public sealed record EnvironmentCheck(string Name, bool Ready, string Message, DependencyComponent? Component = null)
{
    /// <summary>获取是否提供安装操作。</summary>
    public bool CanInstall => !Ready && Component is not null;
    /// <summary>获取依赖安装按钮的显示名称。</summary>
    public string ActionText => Component == DependencyComponent.Driver ? "打开驱动下载" : "安装";
}

/// <summary>标识随程序提供的独立依赖安装入口。</summary>
public enum DependencyComponent
{
    /// <summary>隔离 Python 与推理包。</summary>
    Runtime,
    /// <summary>语音识别模型。</summary>
    QwenModel,
    /// <summary>Ollama 程序。</summary>
    Ollama,
    /// <summary>当前选择的翻译模型。</summary>
    TranslationModel,
    /// <summary>显卡厂商的驱动下载入口。</summary>
    Driver
}

/// <summary>提供平台环境检查与随包脚本入口，不自动安装外部软件。</summary>
public interface IDesktopEnvironment
{
    /// <summary>检查已应用配置的运行依赖，不启动推理服务。</summary>
    /// <param name="settings">已应用配置。</param><param name="cancellationToken">检查取消信号。</param>
    /// <returns>逐项检查结果。</returns>
    Task<IReadOnlyList<EnvironmentCheck>> CheckAsync(DesktopSettings settings, CancellationToken cancellationToken);

    /// <summary>在已知安装位置和环境变量中寻找缺失路径的可用替代，不修改现有资产。</summary>
    /// <param name="settings">当前配置。</param><param name="cancellationToken">取消信号。</param><returns>包含已发现路径的配置副本。</returns>
    Task<DesktopSettings> DiscoverAsync(DesktopSettings settings, CancellationToken cancellationToken);

    /// <summary>按用户点击运行随包安装脚本，传入当前配置并报告执行结果。</summary>
    /// <param name="component">安装组件。</param><param name="settings">已应用配置。</param>
    /// <param name="progress">有界安装输出。</param><param name="cancellationToken">关闭或用户取消信号。</param><returns>脚本结束任务。</returns>
    Task InstallAsync(DependencyComponent component, DesktopSettings settings, IProgress<string> progress, CancellationToken cancellationToken);
}

/// <summary>隔离更新 SDK，下载与应用均需要显式用户操作。</summary>
public interface IDesktopUpdates
{
    /// <summary>获取安装版本；开发及旧 ZIP 运行显示非托管安装。</summary>
    string CurrentVersion { get; }
    /// <summary>获取当前进程是否运行于受管理安装。</summary>
    bool IsInstalled { get; }
    /// <summary>获取已经下载且等待安装的版本。</summary>
    string? PendingVersion { get; }
    /// <summary>检查稳定发布，不下载更新资产。</summary>
    /// <param name="cancellationToken">生命周期取消信号。</param><returns>可更新版本，无更新时为空。</returns>
    Task<string?> CheckAsync(CancellationToken cancellationToken);
    /// <summary>下载已检查的版本，优先使用可用差分。</summary>
    /// <param name="progress">百分比通知。</param><param name="cancellationToken">下载取消信号。</param><returns>下载及验证完成任务。</returns>
    Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken);
}
