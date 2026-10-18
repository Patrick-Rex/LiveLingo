using System.Collections.Immutable;
using LiveLingo.App.Services;
using LiveLingo.App.ViewModels;
using LiveLingo.Core.Desktop;
using LiveLingo.Core.Subtitles;

namespace LiveLingo.App.Tests;

/// <summary>验证 UI 会话屏障、完整投影和命令竞态；不伪装成原生设备验收。</summary>
public sealed class DesktopBehaviorTests
{
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
    /// <summary>获取可选启动闸门。</summary>
    public TaskCompletionSource? HoldStart { get; init; }
    /// <inheritdoc />
    public Task<IReadOnlyList<DesktopTarget>> GetTargetsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DesktopTarget>>([new("msedge.exe", new(123, DateTimeOffset.UtcNow, @"D:\edge.exe"))]);
    /// <inheritdoc />
    public async Task StartAsync(Guid sessionId, DesktopTarget target, DesktopSettings settings, CancellationToken cancellationToken)
    { Starts++; if (HoldStart is not null) await HoldStart.Task.WaitAsync(cancellationToken); }
    /// <inheritdoc />
    public Task StopAsync() => Task.CompletedTask;
    /// <summary>发布可控的完整快照。</summary><param name="update">完整状态。</param>
    public void Publish(DesktopUpdate update) => Updated?.Invoke(update);
}

/// <summary>内存设置夹具，允许验证持久化失败的用户反馈。</summary>
internal sealed class FakeStore : IDesktopSettingsStore
{
    /// <summary>获取是否注入保存故障。</summary>
    public bool FailSave { get; init; }
    /// <inheritdoc />
    public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SettingsLoadResult(new(), null));
    /// <inheritdoc />
    public Task SaveAsync(DesktopSettings settings, CancellationToken cancellationToken = default) => FailSave ? Task.FromException(new IOException("磁盘故障")) : Task.CompletedTask;
}
