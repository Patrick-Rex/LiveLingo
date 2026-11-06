using LiveLingo.App.ViewModels;
using LiveLingo.Core.Desktop;

namespace LiveLingo.App.Tests;

/// <summary>验证维护操作必须由用户确认且受窗口生命周期约束。</summary>
public sealed class MaintenanceTests
{
    /// <summary>启动仅检查，空闲时一次点击完成下载与安装请求。</summary><returns>交互检查任务。</returns>
    [Fact]
    public async Task StartupDoesNotDownloadAndInstallRequiresIdleConsent()
    {
        var updates = new FakeUpdates(); var idle = false;
        var vm = new MaintenanceViewModel(new FakeEnvironment(), updates, () => new(), () => idle);
        var requested = 0; vm.InstallRequested += () => requested++;
        await vm.InitializeAsync();
        Assert.True(vm.HasEnvironmentIssues); Assert.True(vm.CanUpdate); Assert.Equal(0, updates.Downloads);
        await vm.UpdateCommand.ExecuteAsync(null);
        Assert.Equal(0, updates.Downloads); Assert.Equal(0, requested);
        idle = true; await vm.UpdateCommand.ExecuteAsync(null);
        Assert.Equal(1, updates.Downloads); Assert.Equal(1, requested); Assert.True(vm.InstallAccepted);
        await vm.CloseAsync();
        await vm.UpdateCommand.ExecuteAsync(null); Assert.Equal(1, requested);
    }

    /// <summary>关闭取消下载并屏蔽迟到进度，不能误报已下载。</summary><returns>取消验证任务。</returns>
    [Fact]
    public async Task ClosingCancelsDownloadWithoutArmingInstall()
    {
        var updates = new FakeUpdates { WaitForCancellation = true };
        var vm = new MaintenanceViewModel(new FakeEnvironment(), updates, () => new(), () => true);
        await vm.InitializeAsync();
        var downloading = vm.UpdateCommand.ExecuteAsync(null);
        await updates.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await vm.CloseAsync(); await downloading;
        Assert.False(vm.UpdateReady); Assert.False(vm.CanUpdate); Assert.False(vm.InstallAccepted);
    }

    /// <summary>已下载版本在重新打开后保持需要明确安装，不隐式应用。</summary><returns>恢复状态检查任务。</returns>
    [Fact]
    public async Task PendingUpdateIsRestoredWithoutInstallRequest()
    {
        var vm = new MaintenanceViewModel(new FakeEnvironment(), new FakeUpdates { PendingVersion = "1.1.0" }, () => new(), () => true);
        var requested = false; vm.InstallRequested += () => requested = true;
        await vm.InitializeAsync();
        Assert.True(vm.UpdateReady); Assert.False(requested);
        await vm.UpdateCommand.ExecuteAsync(null);
        Assert.True(requested);
        await vm.CloseAsync();
    }

    /// <summary>失败的更新不能关闭应用或标记安装已接受。</summary><returns>失败验证任务。</returns>
    [Fact]
    public async Task FailedDownloadDoesNotRequestRestart()
    {
        var vm = new MaintenanceViewModel(new FakeEnvironment(), new FakeUpdates { FailDownload = true }, () => new(), () => true);
        await vm.InitializeAsync(); await vm.UpdateCommand.ExecuteAsync(null);
        Assert.False(vm.InstallAccepted); Assert.False(vm.ExclusiveOperation); Assert.Contains("下载失败", vm.UpdateMessage);
        await vm.CloseAsync();
    }

    /// <summary>只安装用户选择的缺项并在结束后复检，现成依赖不触发脚本。</summary><returns>安装验证任务。</returns>
    [Fact]
    public async Task SelectedDependencyIsInstalledAndRechecked()
    {
        var environment = new FakeEnvironment();
        var vm = new MaintenanceViewModel(environment, new FakeUpdates(), () => new(), () => true);
        await vm.InitializeAsync();
        Assert.Equal(0, environment.Installs);
        await vm.InstallDependencyCommand.ExecuteAsync(vm.EnvironmentChecks[0]);
        Assert.Equal(1, environment.Installs); Assert.False(vm.HasEnvironmentIssues);
        await vm.InstallDependencyCommand.ExecuteAsync(vm.EnvironmentChecks[0]);
        Assert.Equal(1, environment.Installs);
        await vm.CloseAsync();
    }

    /// <summary>关闭时取消正在执行的安装脚本并解除独占状态。</summary><returns>取消验证任务。</returns>
    [Fact]
    public async Task ClosingCancelsDependencyInstallation()
    {
        var environment = new FakeEnvironment { WaitForCancellation = true };
        var vm = new MaintenanceViewModel(environment, new FakeUpdates(), () => new(), () => true);
        await vm.InitializeAsync();
        var installing = vm.InstallDependencyCommand.ExecuteAsync(vm.EnvironmentChecks[0]);
        await environment.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.ExclusiveOperation); Assert.False(vm.CanUpdate);
        await vm.CloseAsync(); await installing;
        Assert.False(vm.ExclusiveOperation); Assert.False(vm.InstallAccepted);
    }

    /// <summary>设置正在编辑时不应用环境搜索结果，也不安装依赖。</summary><returns>草稿保护验证任务。</returns>
    [Fact]
    public async Task PendingSettingsBlockDiscoveryAndInstallation()
    {
        var environment = new FakeEnvironment { Discovered = new() { OllamaPath = @"D:\other\ollama.exe" } };
        var applied = false;
        var vm = new MaintenanceViewModel(environment, new FakeUpdates(), () => new(), () => false,
            (_, _) => { applied = true; return Task.FromResult(true); });
        await vm.InitializeAsync(); await vm.InstallDependencyCommand.ExecuteAsync(vm.EnvironmentChecks[0]);
        Assert.False(applied); Assert.Equal(0, environment.Installs);
        await vm.CloseAsync();
    }

    /// <summary>发现路径通过真实设置保存入口应用，损坏原文件和用户草稿均受保护。</summary>
    /// <param name="draft">是否正在编辑设置。</param><param name="loadFailure">是否加载原设置失败。</param><returns>配置保护验证任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DiscoveredPathsRespectSettingsPersistence(bool draft, bool loadFailure)
    {
        var store = new FakeStore { LoadError = loadFailure ? "broken settings" : null };
        var vm = new DesktopViewModel(new FakeSession(), store, action => action());
        await vm.InitializeAsync();
        var original = vm.CurrentSettings;
        if (draft) vm.TranslationModel = "translategemma:4b";
        var changed = await vm.ApplyDiscoveredEnvironmentAsync(original, original with { OllamaPath = @"D:\Existing Ollama\ollama.exe" });
        Assert.Equal(!draft && !loadFailure, changed);
        Assert.Equal(original.DataPath, vm.CurrentSettings.DataPath);
        if (draft || loadFailure) Assert.Empty(store.Writes);
        else Assert.Equal(@"D:\Existing Ollama\ollama.exe", Assert.Single(store.Writes).OllamaPath);
        await vm.CloseAsync();
    }

    /// <summary>提供不启动服务的缺失环境。</summary>
    private sealed class FakeEnvironment : IDesktopEnvironment
    {
        public int Installs { get; private set; }
        public bool WaitForCancellation { get; init; }
        public DesktopSettings? Discovered { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <inheritdoc />
        public Task<IReadOnlyList<EnvironmentCheck>> CheckAsync(DesktopSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EnvironmentCheck>>([new("模型", Installs > 0, "测试模型", DependencyComponent.QwenModel)]);
        /// <inheritdoc />
        public Task<DesktopSettings> DiscoverAsync(DesktopSettings settings, CancellationToken cancellationToken) => Task.FromResult(Discovered ?? settings);
        /// <inheritdoc />
        public async Task InstallAsync(DependencyComponent component, DesktopSettings settings, IProgress<string> progress, CancellationToken cancellationToken)
        {
            Assert.Equal(DependencyComponent.QwenModel, component); Installs++; Started.TrySetResult();
            if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    /// <summary>模拟显式下载及可取消网络等待。</summary>
    private sealed class FakeUpdates : IDesktopUpdates
    {
        public string CurrentVersion => "1.0.0";
        public bool IsInstalled => true;
        public string? PendingVersion { get; set; }
        public bool WaitForCancellation { get; init; }
        public bool FailDownload { get; init; }
        public int Downloads { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <inheritdoc />
        public Task<string?> CheckAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("1.1.0");
        /// <inheritdoc />
        public async Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken)
        {
            Downloads++; Started.TrySetResult();
            if (FailDownload) throw new IOException("network unavailable");
            if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            progress(100); PendingVersion = "1.1.0";
        }
    }
}
