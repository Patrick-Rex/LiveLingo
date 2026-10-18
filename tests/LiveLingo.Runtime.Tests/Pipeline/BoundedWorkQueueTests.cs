using LiveLingo.Core.Audio;
using LiveLingo.Core.Subtitles;
using LiveLingo.Runtime.Pipeline;

namespace LiveLingo.Runtime.Tests.Pipeline;

/// <summary>验证慢消费者、开放合并、闭合保留和取消后仍占用的真实执行许可。</summary>
public sealed class BoundedWorkQueueTests
{
    /// <summary>积压刷新只保留最新开放快照，但必须包含全部尚未处理范围。</summary>
    [Fact]
    public void OpenRefreshPreservesAnchorAndClosedQueue()
    {
        var queue = new BoundedWorkQueue(PipelineRequestKind.Recognition, 2, 1000);
        var first = Work(new(0, 100));
        Assert.True(queue.Offer(first));
        Assert.Equal(first, queue.Take());
        var next = first with { Request = first.Request with { Id = Guid.NewGuid(), Sequence = 2, Range = new(0, 200) }, UnprocessedRange = new(0, 200) };
        Assert.True(queue.Offer(next));
        Assert.False(queue.Offer(next with { Request = next.Request with { Id = Guid.NewGuid(), Sequence = 3 }, UnprocessedRange = new(100, 200) }));
        Assert.Equal(next, queue.Pending);
        Assert.Equal(200, queue.BacklogSamples);
        var final = next with { Closed = true, Request = next.Request with { Id = Guid.NewGuid(), Sequence = 4 } };
        Assert.True(queue.Offer(final));
        var other = Work(new(200, 300)) with { Request = Work(new(200, 300)).Request with { Identity = first.Request.Identity } };
        Assert.True(queue.Offer(other));
        Assert.Null(queue.Take());
        queue.Complete(first.Request.Id);
        Assert.Equal(final, queue.Take());
        queue.Complete(final.Request.Id);
        Assert.Equal(other, queue.Take());
    }

    /// <summary>容量拒绝不覆盖已接受任务，代次失效仍等待旧计算退出。</summary>
    [Fact]
    public void CapacityAndInvalidationKeepRunningOwnership()
    {
        var queue = new BoundedWorkQueue(PipelineRequestKind.Recognition, 1, 100);
        var first = Work(new(0, 100)) with { Closed = true };
        Assert.True(queue.Offer(first));
        Assert.False(queue.Offer(Work(new(100, 200)) with { Closed = true }));
        Assert.Equal("closed_capacity", queue.Rejection);
        Assert.Equal(first, queue.Take());
        Assert.False(queue.Offer(Work(new(0, 101))));
        Assert.Equal("audio_backlog_capacity", queue.Rejection);
        queue.InvalidatePending();
        Assert.Equal(first, queue.Running);
        queue.Stop();
        Assert.Null(queue.Take());
        Assert.Throws<InvalidOperationException>(() => queue.Complete(Guid.NewGuid()));
        queue.Complete(first.Request.Id);
        Assert.Equal(0, queue.BacklogSamples);
    }

    /// <summary>底层忽略取消时，第二个操作只有在第一个真正完成后才能进入。</summary>
    /// <returns>完成两个受控操作的测试任务。</returns>
    [Fact]
    public async Task CancellationCannotReleaseGpuEarly()
    {
        var dispatcher = new SerialGpuDispatcher();
        using var cancellation = new CancellationTokenSource();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = dispatcher.RunAsync(_ => release.Task, cancellation.Token);
        cancellation.Cancel();
        var entered = false;
        var second = dispatcher.RunAsync(_ => { entered = true; return Task.FromResult(2); }, CancellationToken.None);
        Assert.False(entered);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        release.SetResult(1);
        Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(2, await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(entered);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.RunAsync<int>(_ => throw new InvalidOperationException(), CancellationToken.None));
        Assert.True(dispatcher.Faulted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.RunAsync(_ => Task.FromResult(3), CancellationToken.None));
    }

    /// <summary>构造完整关联的独立识别工作。</summary><param name="range">音频来源。</param><returns>开放快照。</returns>
    private static ScheduledWork Work(SampleRange range) => new(new(Guid.NewGuid(), 1, PipelineRequestKind.Recognition,
        new(Guid.NewGuid(), 1, 1, 1), new(Guid.NewGuid(), 1), range, 0), range, false);
}
