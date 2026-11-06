using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveLingo.Core.Desktop;

namespace LiveLingo.App.ViewModels;

/// <summary>呈现环境缺项和显式更新操作，关闭时取消所有在途操作。</summary>
/// <param name="environment">平台环境检测。</param><param name="updates">更新服务。</param>
/// <param name="settings">已应用配置快照入口。</param><param name="canInstall">实时会话与设置是否允许退出安装。</param>
public sealed partial class MaintenanceViewModel(IDesktopEnvironment environment, IDesktopUpdates updates,
    Func<DesktopSettings> settings, Func<bool> canInstall) : ObservableObject
{
    private readonly CancellationTokenSource lifetime = new();
    private bool closing;
    private Task initialization = Task.CompletedTask;
    /// <summary>通知宿主执行正常异步退出，清理成功后才交接安装。</summary>
    public event Action? InstallRequested;
    /// <summary>获取当前安装版本。</summary>
    public string CurrentVersion => updates.CurrentVersion;
    /// <summary>获取环境初检结果。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasEnvironmentIssues))]
    public partial IReadOnlyList<EnvironmentCheck> EnvironmentChecks { get; private set; } = [];
    /// <summary>获取是否存在待处理依赖。</summary>
    public bool HasEnvironmentIssues => EnvironmentChecks.Any(x => !x.Ready);
    /// <summary>获取环境检查反馈。</summary>
    [ObservableProperty] public partial string EnvironmentMessage { get; private set; } = "正在检查运行依赖…";
    /// <summary>获取检查是否正在进行。</summary>
    [ObservableProperty] public partial bool CheckingEnvironment { get; private set; }
    /// <summary>获取更新操作状态。</summary>
    [ObservableProperty] public partial string UpdateMessage { get; private set; } = "尚未检查更新";
    /// <summary>获取可下载版本。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanDownload)), NotifyPropertyChangedFor(nameof(HasUpdateNotification))]
    public partial string? AvailableVersion { get; private set; }
    /// <summary>获取更新检查或下载是否正在进行。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanDownload))]
    public partial bool Updating { get; private set; }
    /// <summary>获取下载百分比。</summary>
    [ObservableProperty] public partial int DownloadProgress { get; private set; }
    /// <summary>获取已下载版本是否等待显式安装。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanDownload)), NotifyPropertyChangedFor(nameof(HasUpdateNotification))]
    public partial bool UpdateReady { get; private set; }
    /// <summary>获取主窗口是否应提示新版本。</summary>
    public bool HasUpdateNotification => AvailableVersion is not null || UpdateReady;
    /// <summary>获取当前是否允许开始下载。</summary>
    public bool CanDownload => !closing && !Updating && AvailableVersion is not null && !UpdateReady;
    /// <summary>获取用户是否已请求本次退出后安装。</summary>
    public bool InstallAccepted { get; private set; }

    /// <summary>登记可等待的初始化，不下载资产。</summary><returns>两类检查完成任务。</returns>
    public Task InitializeAsync() => initialization = Task.WhenAll(CheckEnvironmentAsync(), CheckUpdatesAsync());

    /// <summary>按已应用配置检查环境，失败保持可见并允许重试。</summary><returns>检查完成任务。</returns>
    [RelayCommand]
    private async Task CheckEnvironmentAsync()
    {
        if (closing || CheckingEnvironment) return;
        CheckingEnvironment = true;
        try
        {
            var result = await environment.CheckAsync(settings(), lifetime.Token);
            if (closing) return;
            EnvironmentChecks = result;
            EnvironmentMessage = HasEnvironmentIssues ? "有依赖需要准备，请查看下方结果并运行安装脚本。" : "初步检查通过；开始翻译时仍会校验模型完整性并实际加载 GPU。";
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception error)
        {
            if (!closing)
            {
                EnvironmentMessage = "环境检查失败：" + error.Message;
                EnvironmentChecks = [new("运行环境", false, EnvironmentMessage)];
            }
        }
        finally { CheckingEnvironment = false; }
    }

    /// <summary>检查稳定版本，恢复已下载但尚未确认安装的更新。</summary><returns>检查完成任务。</returns>
    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (closing || Updating) return;
        Updating = true; AvailableVersion = null; UpdateMessage = "正在检查更新…";
        try
        {
            if (!updates.IsInstalled) { UpdateMessage = "当前为源码或旧 ZIP 安装，请先运行新版安装器以启用增量更新。"; return; }
            if (updates.PendingVersion is { } pending)
            { UpdateReady = true; UpdateMessage = $"{pending} 已下载，点击退出并安装。"; return; }
            var available = await updates.CheckAsync(lifetime.Token);
            if (closing) return;
            AvailableVersion = available;
            UpdateReady = updates.PendingVersion is not null;
            UpdateMessage = UpdateReady ? $"{updates.PendingVersion} 已下载，点击退出并安装。" :
                AvailableVersion is { } version ? $"发现新版本 {version}，可以下载更新。" : "当前已是最新版本。";
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception error) { if (!closing) UpdateMessage = "检查更新失败，可稍后重试：" + error.Message; }
        finally { Updating = false; }
    }

    /// <summary>显式下载更新，取消或失败不触发退出。</summary>
    /// <param name="cancellationToken">用户取消下载的信号。</param><returns>下载完成任务。</returns>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task DownloadAsync(CancellationToken cancellationToken)
    {
        if (!CanDownload) return;
        Updating = true; DownloadProgress = 0; UpdateMessage = "正在下载更新…";
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var progress = new Progress<int>(value => { if (!closing && Updating) DownloadProgress = value; });
        try
        {
            await updates.DownloadAsync(value => ((IProgress<int>)progress).Report(value), cancel.Token);
            if (closing) return;
            UpdateReady = true; DownloadProgress = 100; UpdateMessage = "更新已下载。停止翻译并应用或撤销设置后，点击退出并安装。";
        }
        catch (OperationCanceledException) { if (!closing) UpdateMessage = "下载已取消，可重新下载。"; }
        catch (Exception error) { if (!closing) UpdateMessage = "下载失败，可重试：" + error.Message; }
        finally { Updating = false; }
    }

    /// <summary>只在当前操作允许退出时记录明确安装意图。</summary>
    [RelayCommand]
    private void Install()
    {
        if (closing || Updating || !UpdateReady || InstallAccepted) return;
        if (!canInstall()) { UpdateMessage = "请先停止翻译，并应用或撤销待保存设置。"; return; }
        InstallAccepted = true; InstallRequested?.Invoke();
    }

    /// <summary>打开随包脚本目录，不执行安装或下载。</summary>
    [RelayCommand]
    private void OpenSetupFolder()
    {
        if (closing) return;
        try { environment.OpenSetupFolder(); }
        catch (Exception error) { EnvironmentMessage = "无法打开脚本目录：" + error.Message; }
    }

    /// <summary>取消检查和下载并观察所有任务，防止关闭后继续写入界面。</summary><returns>清理完成任务。</returns>
    public async Task CloseAsync()
    {
        if (closing) return;
        closing = true; await lifetime.CancelAsync();
        OnPropertyChanged(nameof(CanDownload));
        await Task.WhenAll(initialization, CheckEnvironmentCommand.ExecutionTask ?? Task.CompletedTask,
            CheckUpdatesCommand.ExecutionTask ?? Task.CompletedTask, DownloadCommand.ExecutionTask ?? Task.CompletedTask);
        lifetime.Dispose();
    }
}
