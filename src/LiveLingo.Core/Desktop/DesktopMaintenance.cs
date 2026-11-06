namespace LiveLingo.Core.Desktop;

/// <summary>描述轻量依赖检查结果，不代表真实模型推理已经验证。</summary>
/// <param name="Name">依赖名称。</param><param name="Ready">初步检查是否通过。</param><param name="Message">结果或修复说明。</param>
public sealed record EnvironmentCheck(string Name, bool Ready, string Message);

/// <summary>提供平台环境检查与随包脚本入口，不自动安装外部软件。</summary>
public interface IDesktopEnvironment
{
    /// <summary>检查已应用配置的运行依赖，不启动推理服务。</summary>
    /// <param name="settings">已应用配置。</param><param name="cancellationToken">检查取消信号。</param>
    /// <returns>逐项检查结果。</returns>
    Task<IReadOnlyList<EnvironmentCheck>> CheckAsync(DesktopSettings settings, CancellationToken cancellationToken);

    /// <summary>打开本版本随附的依赖安装脚本目录。</summary>
    void OpenSetupFolder();
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
