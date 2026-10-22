using System.Collections.Immutable;
using LiveLingo.App.Services;
using LiveLingo.App.ViewModels;
using LiveLingo.Core.Desktop;
using LiveLingo.Core.Subtitles;

namespace LiveLingo.App.Tests;

/// <summary>验证 UI 会话屏障、完整投影和命令竞态；不伪装成原生设备验收。</summary>
public sealed class DesktopBehaviorTests
{
    /// <summary>运行中的字幕保存失败独立显示，重试成功不改变会话状态。</summary><returns>失败反馈与重试检查任务。</returns>
    [Fact]
    public async Task AppearanceSaveFailureCanBeRetriedWithoutStoppingSession()
    {
        var store = new FakeStore { FailSave = true }; var session = new FakeSession();
        var vm = new DesktopViewModel(session, store, action => action()); await vm.InitializeAsync(); await vm.ToggleCommand.ExecuteAsync(null);
        var status = vm.Status;
        var failureShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(vm.AppearanceSaveMessage) && vm.AppearanceSaveFailed) failureShown.TrySetResult(); };
        vm.FontSize = 40;
        await failureShown.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Contains("磁盘故障", vm.AppearanceSaveMessage); Assert.True(vm.Active); Assert.Equal(status, vm.Status);
        store.FailSave = false; vm.RetryAppearanceSaveCommand.Execute(null);
        await store.SaveCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await vm.CloseAsync(); Assert.False(vm.AppearanceSaveFailed); Assert.Equal(40, store.Writes.Last().FontSize);
    }

    /// <summary>字幕自动保存合并连续变化，未确认模型和路径不会被带入持久化或启动。</summary><returns>自动保存检查任务。</returns>
    [Fact]
    public async Task AppearanceAutoSaveExcludesPendingRuntimeInputs()
    {
        var store = new FakeStore(); var session = new FakeSession();
        var vm = new DesktopViewModel(session, store, action => action()); await vm.InitializeAsync();
        vm.TranslationModel = "translategemma:4b"; vm.PythonPath = @"D:\unfinished\python.exe";
        vm.FontSize = 38; vm.FontSize = 40; vm.BackgroundOpacity = 0.5; vm.DisplayModeIndex = 1;
        vm.RememberBounds(-600, 200, 700, 300);
        var saved = await store.SaveCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(40, saved.FontSize); Assert.Equal(0.5, saved.BackgroundOpacity); Assert.Equal(-600, saved.OverlayX);
        Assert.Equal(SubtitleDisplayMode.TranslationOnly, saved.DisplayMode);
        Assert.Equal("translategemma:12b", saved.TranslationModel); Assert.Equal(new DesktopSettings().PythonPath, saved.PythonPath);
        Assert.True(vm.HasPendingSettings); Assert.False(vm.CanToggle);
        await vm.ToggleCommand.ExecuteAsync(null); Assert.Equal(0, session.Starts);
        vm.RevertSettingsCommand.Execute(null); Assert.False(vm.HasPendingSettings); Assert.True(vm.CanToggle); Assert.Equal(40, vm.FontSize);
        await vm.CloseAsync(); Assert.Single(store.Writes);
    }

    /// <summary>自动保存与手动保存串行提交，保存期间的新外观不会丢失或回滚已保存模型。</summary><returns>并发保存检查任务。</returns>
    [Fact]
    public async Task ManualAndAutomaticSavesPreserveLatestValues()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeStore { HoldSave = hold };
        var vm = new DesktopViewModel(new FakeSession(), store, action => action()); await vm.InitializeAsync();
        vm.FontSize = 34;
        await store.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        vm.TranslationModel = "translategemma:4b";
        var manual = vm.SaveCommand.ExecuteAsync(null);
        vm.FontSize = 42; store.HoldSave = null; hold.SetResult();
        await manual; await vm.CloseAsync();
        Assert.Equal(1, store.MaxConcurrentSaves);
        Assert.Equal("translategemma:4b", store.Writes.Last().TranslationModel); Assert.Equal(42, store.Writes.Last().FontSize);
        Assert.False(vm.HasPendingSettings); Assert.Equal("运行设置已保存", vm.SettingsSaveMessage);
    }

    /// <summary>关闭立即冲刷末次字幕调整，保存失败仍停止会话并向宿主报告。</summary><returns>退出失败检查任务。</returns>
    [Fact]
    public async Task ClosingFlushesAppearanceAndStillStopsWhenSaveFails()
    {
        var store = new FakeStore { FailSave = true }; var session = new FakeSession();
        var vm = new DesktopViewModel(session, store, action => action()); await vm.InitializeAsync();
        vm.FontSize = 44;
        await Assert.ThrowsAsync<IOException>(() => vm.CloseAsync());
        Assert.Equal(1, session.Stops);
    }

    /// <summary>损坏原配置不会被字幕自动保存或退出冲刷覆盖，明确手动保存后才恢复。</summary><returns>损坏配置保护检查。</returns>
    [Fact]
    public async Task CorruptSettingsRequireExplicitSaveBeforeAutoSave()
    {
        var store = new FakeStore { LoadError = "设置损坏" };
        var vm = new DesktopViewModel(new FakeSession(), store, action => action()); await vm.InitializeAsync();
        vm.FontSize = 40; await vm.CloseAsync(); Assert.Empty(store.Writes);
        var recovered = new DesktopViewModel(new FakeSession(), store, action => action()); await recovered.InitializeAsync();
        await recovered.SaveCommand.ExecuteAsync(null); recovered.FontSize = 42;
        await recovered.CloseAsync(); Assert.Equal(42, store.Writes.Last().FontSize);
    }

    /// <summary>回看固定语义身份但不冻结修订与撤销，返回最新和新会话均恢复跟随。</summary><returns>会话及投影测试任务。</returns>
    [Fact]
    public async Task ReviewingPreservesIdentityButAcceptsCorrectionsAndRetirements()
    {
        var session = new FakeSession(); var vm = new DesktopViewModel(session, new FakeStore(), action => action());
        await vm.InitializeAsync(); await vm.ToggleCommand.ExecuteAsync(null);
        var identity = new PipelineIdentity(vm.SessionId, 1, 1, 1);
        var first = Segment(); var second = Segment(); var third = Segment();
        session.Publish(new(vm.SessionId, 1, new(identity, 1, false, [first, second], []), "running", 0, true));
        vm.PauseFollowing();
        first = first with { Translation = "修订译文", SubtitleRevision = 2 };
        session.Publish(new(vm.SessionId, 2, new(identity, 2, false, [first, second, third], []), "running", 0, true));
        Assert.Equal(new[] { first.Key, second.Key }, vm.OverlayRows.Select(x => x.Segment.Key));
        Assert.Equal("修订译文", vm.OverlayRows[0].Translation);
        session.Publish(new(vm.SessionId, 3, new(identity, 3, false, [second, third], ImmutableHashSet.Create(first.Key.Id)), "running", 0, true));
        Assert.Equal(second.Key, Assert.Single(vm.OverlayRows).Segment.Key);
        vm.DisplayModeIndex = 1; Assert.False(vm.FollowLatest); Assert.Equal(SubtitleDisplayMode.TranslationOnly, vm.CurrentSettings.DisplayMode); Assert.Equal(1, session.Starts);
        vm.ResumeFollowingCommand.Execute(null);
        Assert.True(vm.FollowLatest); Assert.Equal(new[] { second.Key, third.Key }, vm.OverlayRows.Select(x => x.Segment.Key));
        vm.PauseFollowing(); await vm.ToggleCommand.ExecuteAsync(null); await vm.ToggleCommand.ExecuteAsync(null);
        Assert.True(vm.FollowLatest); Assert.Empty(vm.OverlayRows); Assert.Equal(1, vm.DisplayModeIndex);
        await vm.CloseAsync(); vm.PauseFollowing(); vm.DisplayModeIndex = 2;
        Assert.True(vm.FollowLatest); Assert.Equal(1, vm.DisplayModeIndex);
    }

    /// <summary>运行中缩放只更新视觉，保留位置和未应用模型输入，关闭后不再发布。</summary><returns>生命周期检查任务。</returns>
    [Fact]
    public async Task FontZoomKeepsSessionAndPendingSettingsSeparate()
    {
        var session = new FakeSession();
        var vm = new DesktopViewModel(session, new FakeStore(), action => action());
        await vm.InitializeAsync(); await vm.ToggleCommand.ExecuteAsync(null);
        var id = vm.SessionId;
        vm.RememberBounds(-200, 100, 700, 240);
        vm.TranslationModel = "translategemma:4b";
        var changes = new List<DesktopSettings>(); vm.AppearanceChanged += changes.Add;
        vm.IncreaseSubtitleFontCommand.Execute(null);
        Assert.Equal(30, Assert.Single(changes).FontSize);
        Assert.Equal("translategemma:12b", changes[0].TranslationModel);
        Assert.Equal(-200, changes[0].OverlayX); Assert.Equal(700, changes[0].OverlayWidth);
        Assert.Equal("translategemma:4b", vm.TranslationModel);
        Assert.Equal(id, vm.SessionId); Assert.Equal(1, session.Starts); Assert.True(vm.Active);
        vm.AdjustSubtitleFontSize(1000); Assert.Equal(64, vm.FontSize);
        vm.AdjustSubtitleFontSize(-1000); Assert.Equal(16, vm.FontSize);
        vm.AdjustSubtitleFontSize(double.NaN); Assert.Equal(3, changes.Count);
        await vm.CloseAsync(); vm.IncreaseSubtitleFontCommand.Execute(null); Assert.Equal(3, changes.Count);
    }

    /// <summary>大量更新只排一个回调，顺序倒退不会覆盖最新版本。</summary>
    [Fact]
    public void CoalescesAndRejectsOlderSnapshots()
    {
        var queue = new Queue<Action>(); var received = new List<DesktopUpdate>(); var id = Guid.NewGuid();
        var pump = new SnapshotPump(queue.Enqueue, received.Add); pump.SwitchSession(id);
        for (var i = 1; i <= 10000; i++) pump.Offer(new(id, i, null, "running", i, true));
        pump.Offer(new(id, 2, null, "stale", 0, false));
        Assert.Single(queue); queue.Dequeue()(); Assert.Equal(10000, Assert.Single(received).Sequence);
        pump.Offer(new(id, 9999, null, "stale", 0, true)); Assert.Empty(queue);
    }

    /// <summary>旧回调在新会话开始后执行也只能提交当前会话；关闭后永久拒收。</summary>
    [Fact]
    public void SessionAndCloseBarriersApplyAtExecution()
    {
        var queue = new Queue<Action>(); var received = new List<DesktopUpdate>(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var pump = new SnapshotPump(queue.Enqueue, received.Add); pump.SwitchSession(first);
        pump.Offer(new(first, 100, null, "old", 0, true)); pump.SwitchSession(second);
        pump.Offer(new(first, 101, null, "late", 0, true)); pump.Offer(new(second, 1, null, "new", 0, true));
        Assert.Single(queue); queue.Dequeue()(); Assert.Equal(second, Assert.Single(received).SessionId);
        pump.Offer(new(second, 2, null, "pending", 0, true)); pump.Close(); queue.Dequeue()();
        pump.Offer(new(second, 3, null, "closed", 0, true)); Assert.Empty(queue); Assert.Single(received);
    }

    /// <summary>停止应用最后状态并拒绝迟到活动快照；重启仍复用唯一已排队回调。</summary>
    [Fact]
    public void StopBarrierRejectsLateResultsWithoutLosingFinalState()
    {
        var queue = new Queue<Action>(); var received = new List<DesktopUpdate>(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var pump = new SnapshotPump(queue.Enqueue, received.Add); pump.SwitchSession(first);
        pump.Offer(new(first, 1, null, "final", 1.3, false)); pump.EndSession();
        Assert.Equal("final", Assert.Single(received).Status);
        pump.Offer(new(first, 2, null, "late active", 0, true));
        pump.SwitchSession(second); pump.Offer(new(second, 1, null, "new", 0, true));
        Assert.Single(queue); queue.Dequeue()(); Assert.Equal(second, received.Last().SessionId); Assert.Equal(2, received.Count);
    }

    /// <summary>相同文本不同身份保留，撤销和源版本变化不会产生重复行或错误定稿标签。</summary><returns>异步交互测试。</returns>
    [Fact]
    public async Task ProjectionPreservesRealRepeatsAndRemovesRetiredRows()
    {
        var session = new FakeSession(); var queue = new Queue<Action>(); var vm = new DesktopViewModel(session, new FakeStore(), queue.Enqueue);
        await vm.InitializeAsync(); await vm.ToggleCommand.ExecuteAsync(null);
        var first = Segment(); var second = Segment(); var identity = new PipelineIdentity(vm.SessionId, 1, 1, 1);
        session.Publish(new(vm.SessionId, 1, new(identity, 1, false, [first, second], []), "running", 2, true)); queue.Dequeue()();
        Assert.Equal(2, vm.OverlayRows.Count); Assert.Equal(vm.History[0].Source, vm.History[1].Source);
        second = second with { SourceRevision = 2, SubtitleRevision = 3, Source = "行かない。", Phase = SubtitlePhase.Revised };
        session.Publish(new(vm.SessionId, 2, new(identity, 2, false, [second], ImmutableHashSet.Create(first.Key.Id)), "running", 4, true)); queue.Dequeue()();
        Assert.Single(vm.History); Assert.Contains("中文待更新", vm.History[0].Detail); Assert.Equal("行かない。", vm.History[0].Source);
        await vm.CloseAsync(); session.Publish(new(vm.SessionId, 3, null, "late", 0, true)); Assert.Empty(queue);
    }

    /// <summary>启动正在等待时，重复请求不会另建会话；停止与下一次启动使用不同身份。</summary><returns>竞态测试。</returns>
    [Fact]
    public async Task CommandsDoNotStartTwoSessions()
    {
        var session = new FakeSession { HoldStart = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var vm = new DesktopViewModel(session, new FakeStore(), _ => { }); await vm.InitializeAsync();
        var first = vm.ToggleCommand.ExecuteAsync(null); await vm.ToggleCommand.ExecuteAsync(null);
        Assert.Equal(1, session.Starts); Assert.False(vm.CanEdit);
        session.HoldStart.SetResult(); await first; var identity = vm.SessionId;
        await vm.ToggleCommand.ExecuteAsync(null); Assert.False(vm.Active);
        await vm.ToggleCommand.ExecuteAsync(null); Assert.NotEqual(identity, vm.SessionId); Assert.Equal(2, session.Starts);
        await vm.CloseAsync();
    }

    /// <summary>模拟保存失败时用户能看到错误且会话仍可继续操作。</summary><returns>交互任务。</returns>
    [Fact]
    public async Task SaveFailureIsVisibleAndDoesNotDisableControls()
    {
        var vm = new DesktopViewModel(new FakeSession(), new FakeStore { FailSave = true }, _ => { }); await vm.InitializeAsync();
        await vm.SaveCommand.ExecuteAsync(null); Assert.Contains("磁盘故障", vm.Error); Assert.True(vm.CanEdit); await vm.CloseAsync();
    }

    /// <summary>磁盘保存成功不能清除随后由宿主报告的热键冲突。</summary><returns>交互任务。</returns>
    [Fact]
    public async Task SavingDoesNotHideHostConfigurationFailure()
    {
        var vm = new DesktopViewModel(new FakeSession(), new FakeStore(), _ => { }); await vm.InitializeAsync();
        vm.SettingsApplied += _ => vm.Error = "快捷键冲突";
        await vm.SaveCommand.ExecuteAsync(null); Assert.Equal("快捷键冲突", vm.Error); Assert.True(vm.CanEdit); await vm.CloseAsync();
    }

    /// <summary>三档缩放及负坐标屏幕仅转换一次，窗口完整留在工作区。</summary><param name="scale">屏幕缩放。</param>
    [Theory, InlineData(1.0), InlineData(1.5), InlineData(2.0)]
    public void ClampsNegativeScreenAtEachScale(double scale)
    {
        var result = new OverlayPlacement(-50, 2000, 1000, 260).Clamp(-1920, -200, 1920, 1080, scale);
        Assert.True(result.X >= -1920); Assert.True(result.X + Math.Ceiling(result.Width * scale) <= 0);
        Assert.True(result.Y + Math.Ceiling(result.Height * scale) <= 880);
    }

    /// <summary>不可用路径与非有限字号在写入和模型启动前被拒绝。</summary>
    [Fact]
    public void RejectsInvalidSettings()
    {
        Assert.Throws<ArgumentException>(() => (new DesktopSettings() with { CachePath = @"C:\bad" }).Validate());
        Assert.Throws<ArgumentException>(() => (new DesktopSettings() with { FontSize = double.NaN }).Validate());
        Assert.Throws<ArgumentException>(() => (new DesktopSettings() with { StartKey = 8 }).Validate());
    }

    /// <summary>构造具有真实身份和精确源版本的完整重复块。</summary><returns>初始字幕。</returns>
    private static SubtitleSegment Segment() => new(new(Guid.NewGuid(), 1), new(0, 16000), 1, 1, "行く。", "去。", 1, SubtitlePhase.Draft, TranslationState.Complete, null, BoundaryDecision.Pending);
}

/// <summary>可控制启动完成的中立会话夹具，不启动外部进程。</summary>
internal sealed class FakeSession : IDesktopSession
{
    /// <inheritdoc />
    public event Action<DesktopUpdate>? Updated;
    /// <summary>获取实际启动次数。</summary>
    public int Starts { get; private set; }
    /// <summary>获取已执行的停止次数。</summary>
    public int Stops { get; private set; }
    /// <summary>获取可选启动闸门。</summary>
    public TaskCompletionSource? HoldStart { get; init; }
    /// <inheritdoc />
    public Task<IReadOnlyList<DesktopTarget>> GetTargetsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DesktopTarget>>([new("msedge.exe", new(123, DateTimeOffset.UtcNow, @"D:\edge.exe"))]);
    /// <inheritdoc />
    public async Task StartAsync(Guid sessionId, DesktopTarget target, DesktopSettings settings, CancellationToken cancellationToken)
    { Starts++; if (HoldStart is not null) await HoldStart.Task.WaitAsync(cancellationToken); }
    /// <inheritdoc />
    public Task StopAsync() { Stops++; return Task.CompletedTask; }
    /// <summary>发布可控的完整快照。</summary><param name="update">完整状态。</param>
    public void Publish(DesktopUpdate update) => Updated?.Invoke(update);
}

/// <summary>内存设置夹具，允许验证持久化失败的用户反馈。</summary>
internal sealed class FakeStore : IDesktopSettingsStore
{
    private int activeSaves;
    /// <summary>获取是否注入保存故障。</summary>
    public bool FailSave { get; set; }
    /// <summary>获取注入的读取错误。</summary>
    public string? LoadError { get; init; }
    /// <summary>获取可选保存完成闸门。</summary>
    public TaskCompletionSource? HoldSave { get; set; }
    /// <summary>获取首个保存开始信号。</summary>
    public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>获取首个成功保存快照。</summary>
    public TaskCompletionSource<DesktopSettings> SaveCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>获取成功写入的快照序列。</summary>
    public List<DesktopSettings> Writes { get; } = [];
    /// <summary>获取观测到的最大并发写入数。</summary>
    public int MaxConcurrentSaves { get; private set; }
    /// <inheritdoc />
    public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SettingsLoadResult(new(), LoadError));
    /// <inheritdoc />
    public async Task SaveAsync(DesktopSettings settings, CancellationToken cancellationToken = default)
    {
        var active = Interlocked.Increment(ref activeSaves); MaxConcurrentSaves = Math.Max(MaxConcurrentSaves, active);
        try
        {
            SaveEntered.TrySetResult();
            if (HoldSave is { } hold) await hold.Task.WaitAsync(cancellationToken);
            if (FailSave) throw new IOException("磁盘故障");
            Writes.Add(settings); SaveCompleted.TrySetResult(settings);
        }
        finally { Interlocked.Decrement(ref activeSaves); }
    }
}
