using LiveLingo.Core.Sessions;

namespace LiveLingo.Core.Tests.Sessions;

/// <summary>验证停止屏障、旧代次隔离和不同恢复原因。</summary>
public sealed class SessionReducerTests
{
    /// <summary>确认设备通知不会破坏健康代次，旧就绪回调不能复活停止会话。</summary>
    [Fact]
    public void DeviceNotificationKeepsEpochAndStopIsPermanent()
    {
        var reducer = new SessionReducer();
        var target = new TargetIdentity(1, DateTimeOffset.UtcNow, Path.GetFullPath("edge.exe"));
        Assert.True(reducer.Apply(new(SessionEventKind.Start, Target: target), 3));
        Assert.True(reducer.Apply(new(SessionEventKind.Ready, 1), 3));
        Assert.True(reducer.Apply(new(SessionEventKind.DeviceChanged), 3));
        Assert.Equal(1, reducer.CaptureEpoch);
        Assert.True(reducer.Apply(new(SessionEventKind.Stop), 3));
        Assert.False(reducer.Apply(new(SessionEventKind.Ready, 1), 3));
        Assert.True(reducer.Apply(new(SessionEventKind.Cleaned), 3));
        Assert.False(reducer.Apply(new(SessionEventKind.Start, Target: target), 3));
    }

    /// <summary>确认重试预算有限且旧代次的失败不会污染新客户端。</summary>
    [Fact]
    public void OldEpochIsRejectedAndRetryBudgetIsFinite()
    {
        var reducer = new SessionReducer();
        reducer.Apply(new(SessionEventKind.Start, Target: new(1, DateTimeOffset.UtcNow, Path.GetFullPath("edge.exe"))), 1);
        reducer.Apply(new(SessionEventKind.CaptureFailed, 1), 1);
        Assert.Equal(SessionState.Recovering, reducer.State);
        reducer.Apply(new(SessionEventKind.Retry, 1), 1);
        Assert.Equal(2, reducer.CaptureEpoch);
        Assert.False(reducer.Apply(new(SessionEventKind.CaptureFailed, 1), 1));
        reducer.Apply(new(SessionEventKind.CaptureFailed, 2), 1);
        Assert.Equal(SessionState.Faulted, reducer.State);
    }

    /// <summary>确认睡眠恢复必须等待重新查询目标，不沿用睡眠前时间轴。</summary>
    [Fact]
    public void ResumeRequiresVerifiedTargetBeforeNewEpoch()
    {
        var reducer = new SessionReducer();
        var target = new TargetIdentity(1, DateTimeOffset.UtcNow, Path.GetFullPath("edge.exe"));
        reducer.Apply(new(SessionEventKind.Start, Target: target), 3);
        reducer.Apply(new(SessionEventKind.Suspend), 3);
        reducer.Apply(new(SessionEventKind.Resume), 3);
        Assert.Equal(SessionState.WaitingForTarget, reducer.State);
        Assert.False(reducer.Apply(new(SessionEventKind.TargetFound, Target: new(2, DateTimeOffset.UtcNow, Path.GetFullPath("other.exe"))), 3));
        Assert.True(reducer.Apply(new(SessionEventKind.TargetFound, Target: target), 3));
        Assert.Equal(2, reducer.CaptureEpoch);
    }
}
