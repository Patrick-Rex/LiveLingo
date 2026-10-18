using LiveLingo.Core.Audio;
using LiveLingo.Core.Sessions;
using LiveLingo.Runtime.Diagnostics;
using LiveLingo.Runtime.Pipeline;

namespace LiveLingo.Runtime.Tests.Pipeline;

/// <summary>用实际异步协调器验证恢复、停止和消费者错误，而不是复制归约实现。</summary>
public sealed class CaptureCoordinatorTests
{
    /// <summary>设备通知和合法无数据不会重启，取消后不会接受迟到就绪。</summary>
    /// <returns>完成停止断言的任务。</returns>
    [Fact]
    public async Task DeviceNotificationsAndSilenceDoNotRestartAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var ingress = new AudioIngress(new(), new(44100, 2));
        var source = new ControlledSource();
        var diagnostics = new List<CaptureDiagnostic>();
        var coordinator = new CaptureCoordinator(source, new(), ingress, _ => true, _ => null, diagnostics.Add);
        var run = coordinator.RunAsync(Target(), Guid.NewGuid(), cancellation.Token);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        coordinator.Post(new(SessionEventKind.DeviceChanged, Detail: "simulated-device-event"));
        cancellation.Cancel();
        Assert.Equal(0, await run);
        coordinator.Post(new(SessionEventKind.Ready, 1));
        Assert.Equal(1, source.Starts);
        Assert.True(source.Stopped);
        Assert.Contains(diagnostics, item => item.Kind == "cleaned");
        Assert.DoesNotContain(diagnostics, item => item.Gap is not null);
    }

    /// <summary>资源错误必须中断当前代次、记录缺口并停止生产。</summary>
    /// <returns>完成失败清理断言的任务。</returns>
    [Fact]
    public async Task ConsumerFailureCreatesGapAndStopsCaptureAsync()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var ingress = new AudioIngress(new(), new(44100, 2));
        var source = new ControlledSource();
        var diagnostics = new List<CaptureDiagnostic>();
        var coordinator = new CaptureCoordinator(source, new(), ingress, _ => true, _ => null, diagnostics.Add);
        var run = coordinator.RunAsync(Target(), Guid.NewGuid(), cancellation.Token);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        coordinator.Post(new(SessionEventKind.Fatal, Detail: "AudioCapacity: simulated-disk-full"));
        Assert.Equal(4, await run);
        Assert.True(source.Stopped);
        Assert.Single(diagnostics, item => item.Gap is not null);
        Assert.Null(diagnostics.Single(item => item.Gap is not null).Gap!.LostFrames);
    }

    /// <summary>真实任务失败只按配置重试有限次，不被反复 Ready 重置预算。</summary>
    /// <param name="defaults">是否使用实际交付的默认预算和退避。</param>
    /// <returns>观察到预算耗尽的任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedFailuresExhaustSessionBudgetAsync(bool defaults)
    {
        var policy = defaults ? new CapturePolicy() : new CapturePolicy { RetryBudget = 2, RetryBaseDelay = TimeSpan.FromMilliseconds(1), RetryMaxDelay = TimeSpan.FromMilliseconds(1) };
        using var ingress = new AudioIngress(policy, new(44100, 2));
        var source = new FailingSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var coordinator = new CaptureCoordinator(source, policy, ingress, _ => true, _ => null, _ => { });
        Assert.Equal(3, await coordinator.RunAsync(Target(), Guid.NewGuid(), deadline.Token));
        Assert.Equal(policy.RetryBudget + 1, source.Starts);
    }

    /// <summary>恢复退避期间停止，不允许迟到重试再次启动捕获。</summary>
    /// <returns>完成恢复中停止断言的任务。</returns>
    [Fact]
    public async Task StopDuringRecoveryPreventsNextActivationAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var ingress = new AudioIngress(new(), new(44100, 2));
        var source = new FailingSource();
        var coordinator = new CaptureCoordinator(source, new(), ingress, _ => true, _ => null,
            diagnostic => { if (diagnostic.Kind == "CaptureFailed") stop.Cancel(); });
        Assert.Equal(0, await coordinator.RunAsync(Target(), Guid.NewGuid(), stop.Token));
        coordinator.Post(new(SessionEventKind.Retry, 1));
        Assert.Equal(1, source.Starts);
    }

    /// <summary>为纯逻辑测试建立不指向真实采集的身份。</summary><returns>模拟身份。</returns>
    private static TargetIdentity Target() => new(123, DateTimeOffset.UnixEpoch, Path.GetFullPath("fake-edge.exe"));

    /// <summary>中断清理超时时返回清理失败，不把超时传播为普通采集失败或创建新代次。</summary>
    /// <returns>完成超时后收敛检查的任务。</returns>
    [Fact]
    public async Task InterruptedSourceStopTimeoutReturnsCleanupFailureAsync()
    {
        var policy = new CapturePolicy { StopTimeout = TimeSpan.FromMilliseconds(30) };
        using var ingress = new AudioIngress(policy, new(44100, 2));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var source = new DelayedStopSource();
        var diagnostics = new List<CaptureDiagnostic>();
        var coordinator = new CaptureCoordinator(source, policy, ingress, _ => true, _ => null, diagnostics.Add);
        var run = coordinator.RunAsync(Target(), Guid.NewGuid(), stop.Token);
        try
        {
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            coordinator.Post(new(SessionEventKind.Suspend, Detail: "simulated-suspend-with-stalled-source"));
            Assert.Equal(5, await run.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Assert.Equal(1, source.Starts);
            Assert.Contains(diagnostics, item => item.Kind == "cleanup-failed");
            coordinator.Post(new(SessionEventKind.Resume));
            Assert.Equal(1, source.Starts);
        }
        finally { source.Completion.TrySetResult(); stop.Cancel(); }
    }

    /// <summary>延迟响应取消的模拟源，使清理超时路径可重复执行。</summary>
    private sealed class DelayedStopSource : ICaptureSource
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Starts { get; private set; }
        /// <inheritdoc />
        public Task RunAsync(TargetIdentity target, Guid sessionId, long epoch, TimeSpan activationTimeout,
            Func<AudioPacket, bool> publish, Action ready, CancellationToken cancellationToken)
        {
            Starts++;
            ready();
            Started.TrySetResult();
            return Completion.Task;
        }
    }

    /// <summary>日志写入抛出磁盘满错误时仍停止活动捕获并关闭 Channel。</summary>
    /// <returns>完成故障注入与资源清理断言的任务。</returns>
    [Fact]
    public async Task DiagnosticDiskFullStillStopsActiveCaptureAsync()
    {
        using var ingress = new AudioIngress(new(), new(44100, 2));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var source = new ControlledSource();
        var coordinator = new CaptureCoordinator(source, new(), ingress, _ => true, _ => null,
            diagnostic =>
            {
                if (diagnostic.Kind == "Ready") throw new IOException("injected disk full", unchecked((int)0x80070070));
            });
        Assert.Equal(4, await coordinator.RunAsync(Target(), Guid.NewGuid(), deadline.Token));
        Assert.True(source.Stopped);
        Assert.Equal(1, source.Starts);
        await foreach (var packet in ingress.ReadAllAsync(TestContext.Current.CancellationToken)) packet.Dispose();
        coordinator.Post(new(SessionEventKind.Ready, 1));
    }

    /// <summary>无数据但健康的模拟捕获器，用于观察取消与启动次数。</summary>
    private sealed class ControlledSource : ICaptureSource
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Starts { get; private set; }
        internal bool Stopped { get; private set; }
        /// <inheritdoc />
        public async Task RunAsync(TargetIdentity target, Guid sessionId, long epoch, TimeSpan activationTimeout,
            Func<AudioPacket, bool> publish, Action ready, CancellationToken cancellationToken)
        {
            Starts++;
            ready();
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { Stopped = true; }
        }
    }

    /// <summary>每次就绪后都失败的模拟源，用于验证会话总预算。</summary>
    private sealed class FailingSource : ICaptureSource
    {
        internal int Starts { get; private set; }
        /// <inheritdoc />
        public Task RunAsync(TargetIdentity target, Guid sessionId, long epoch, TimeSpan activationTimeout,
            Func<AudioPacket, bool> publish, Action ready, CancellationToken cancellationToken)
        {
            Starts++;
            ready();
            return Task.FromException(new IOException("simulated-native-error"));
        }
    }
}
