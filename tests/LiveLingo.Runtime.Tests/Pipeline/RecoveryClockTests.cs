using LiveLingo.Core.Audio;
using LiveLingo.Core.Sessions;
using LiveLingo.Runtime.Pipeline;
using Microsoft.Extensions.Time.Testing;

namespace LiveLingo.Runtime.Tests.Pipeline;

/// <summary>推进虚拟时间验证真实协调器退避，不用墙钟等待掩盖竞态。</summary>
public sealed class RecoveryClockTests
{
    /// <summary>新代次只在退避到期后建立，取消可使第二代次收敛。</summary>
    /// <returns>完成确定性退避验证的任务。</returns>
    [Fact]
    public async Task RetryStartsOnlyWhenControlledDeadlineExpiresAsync()
    {
        var policy = new CapturePolicy { RetryBaseDelay = TimeSpan.FromSeconds(4), RetryMaxDelay = TimeSpan.FromSeconds(4) };
        var time = new ObservedTimeProvider(policy.RetryBaseDelay);
        var source = new FailsOnceSource();
        using var ingress = new AudioIngress(policy, new(44100, 2));
        using var stop = new CancellationTokenSource();
        var coordinator = new CaptureCoordinator(source, policy, ingress, _ => true, _ => null, _ => { }, time);
        var run = coordinator.RunAsync(new(123, DateTimeOffset.UnixEpoch, Path.GetFullPath("fake.exe")), Guid.NewGuid(), stop.Token);
        await time.RetryRegistered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMilliseconds(3999));
        Assert.Equal(1, source.Starts);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await source.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(2, source.Starts);
        stop.Cancel();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
    }

    /// <summary>暴露退避定时器建立时机，并委托微软虚拟时间实现。</summary>
    private sealed class ObservedTimeProvider : TimeProvider
    {
        private readonly FakeTimeProvider time = new();
        private readonly TimeSpan observedDelay;
        internal TaskCompletionSource RetryRegistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>指定本次需要观察的退避时间。</summary><param name="observedDelay">退避长度。</param>
        internal ObservedTimeProvider(TimeSpan observedDelay) => this.observedDelay = observedDelay;
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => time.GetUtcNow();
        /// <inheritdoc />
        public override long GetTimestamp() => time.GetTimestamp();
        /// <inheritdoc />
        public override long TimestampFrequency => time.TimestampFrequency;
        /// <inheritdoc />
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = time.CreateTimer(callback, state, dueTime, period);
            if (dueTime == observedDelay) RetryRegistered.TrySetResult();
            return timer;
        }
        /// <summary>推进虚拟时间并触发到期回调。</summary><param name="duration">推进长度。</param>
        internal void Advance(TimeSpan duration) => time.Advance(duration);
    }

    /// <summary>第一次失败、第二次等待取消的捕获源。</summary>
    private sealed class FailsOnceSource : ICaptureSource
    {
        private int starts;
        internal int Starts => Volatile.Read(ref starts);
        internal TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <inheritdoc />
        public Task RunAsync(TargetIdentity target, Guid sessionId, long epoch, TimeSpan activationTimeout,
            Func<AudioPacket, bool> publish, Action ready, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref starts) == 1) return Task.FromException(new IOException("simulated-first-failure"));
            ready();
            SecondStarted.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
