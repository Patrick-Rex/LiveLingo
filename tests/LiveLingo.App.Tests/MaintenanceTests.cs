using LiveLingo.App.ViewModels;
using LiveLingo.Core.Desktop;

namespace LiveLingo.App.Tests;

/// <summary>验证维护操作必须由用户确认且受窗口生命周期约束。</summary>
public sealed class MaintenanceTests
{
    /// <summary>启动仅检查，下载完成后仍需允许安装且用户明确点击。</summary><returns>交互检查任务。</returns>
    [Fact]
    public async Task StartupDoesNotDownloadAndInstallRequiresIdleConsent()
    {
        var updates = new FakeUpdates(); var idle = false;
        var vm = new MaintenanceViewModel(new FakeEnvironment(), updates, () => new(), () => idle);
        var requested = 0; vm.InstallRequested += () => requested++;
        await vm.InitializeAsync();
        Assert.True(vm.HasEnvironmentIssues); Assert.True(vm.CanDownload); Assert.Equal(0, updates.Downloads);
        vm.InstallCommand.Execute(null); Assert.Equal(0, requested);
        await vm.DownloadCommand.ExecuteAsync(null);
        vm.InstallCommand.Execute(null); Assert.Equal(0, requested);
        idle = true; vm.InstallCommand.Execute(null); Assert.Equal(1, requested);
        await vm.CloseAsync();
        vm.InstallCommand.Execute(null); Assert.Equal(1, requested);
    }

    /// <summary>关闭取消下载并屏蔽迟到进度，不能误报已下载。</summary><returns>取消验证任务。</returns>
    [Fact]
    public async Task ClosingCancelsDownloadWithoutArmingInstall()
    {
        var updates = new FakeUpdates { WaitForCancellation = true };
        var vm = new MaintenanceViewModel(new FakeEnvironment(), updates, () => new(), () => true);
        await vm.InitializeAsync();
        var downloading = vm.DownloadCommand.ExecuteAsync(null);
        await updates.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await vm.CloseAsync(); await downloading;
        Assert.False(vm.UpdateReady); Assert.False(vm.CanDownload); Assert.False(vm.InstallAccepted);
    }

    /// <summary>已下载版本在重新打开后保持需要明确安装，不隐式应用。</summary><returns>恢复状态检查任务。</returns>
    [Fact]
    public async Task PendingUpdateIsRestoredWithoutInstallRequest()
    {
        var vm = new MaintenanceViewModel(new FakeEnvironment(), new FakeUpdates { PendingVersion = "1.1.0" }, () => new(), () => true);
        var requested = false; vm.InstallRequested += () => requested = true;
        await vm.InitializeAsync();
        Assert.True(vm.UpdateReady); Assert.False(requested);
        await vm.CloseAsync();
    }

    /// <summary>提供不启动服务的缺失环境。</summary>
    private sealed class FakeEnvironment : IDesktopEnvironment
    {
        /// <inheritdoc />
        public Task<IReadOnlyList<EnvironmentCheck>> CheckAsync(DesktopSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EnvironmentCheck>>([new("模型", false, "缺少模型")]);
        /// <inheritdoc />
        public void OpenSetupFolder() { }
    }

    /// <summary>模拟显式下载及可取消网络等待。</summary>
    private sealed class FakeUpdates : IDesktopUpdates
    {
        public string CurrentVersion => "1.0.0";
        public bool IsInstalled => true;
        public string? PendingVersion { get; set; }
        public bool WaitForCancellation { get; init; }
        public int Downloads { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <inheritdoc />
        public Task<string?> CheckAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("1.1.0");
        /// <inheritdoc />
        public async Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken)
        {
            Downloads++; Started.TrySetResult();
            if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            progress(100); PendingVersion = "1.1.0";
        }
    }
}
