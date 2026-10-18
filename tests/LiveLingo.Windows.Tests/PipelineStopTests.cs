using LiveLingo.Probe;

namespace LiveLingo.Windows.Tests;

/// <summary>验证非交互评测能够取消并等待停止监视器退出。</summary>
public sealed class PipelineStopTests
{
    /// <summary>仅本次评测停止标记存在时取消会话；正常结束也能回收监视任务。</summary>
    /// <returns>真实文件触发和监视器退出验证任务。</returns>
    [Fact]
    public async Task StopMarkerCancelsSessionAndWatcherCanEndWithoutMarker()
    {
        var path = Path.Combine(Path.GetTempPath(), "pipeline-stop-" + Guid.NewGuid().ToString("N"));
        using var stop = new CancellationTokenSource();
        using var finish = new CancellationTokenSource();
        var watch = PipelineReplayCheck.WatchStopRequestAsync(path, stop, finish.Token);
        try
        {
            await File.WriteAllTextAsync(path, "stop\n", TestContext.Current.CancellationToken);
            await watch.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(stop.IsCancellationRequested);
        }
        finally { await finish.CancelAsync(); await watch; File.Delete(path); }
        using var untouched = new CancellationTokenSource();
        using var ended = new CancellationTokenSource();
        var idle = PipelineReplayCheck.WatchStopRequestAsync(path, untouched, ended.Token);
        await ended.CancelAsync(); await idle.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(untouched.IsCancellationRequested);
    }
}
