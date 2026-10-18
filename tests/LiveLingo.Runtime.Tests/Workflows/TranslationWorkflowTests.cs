using LiveLingo.Core.Subtitles;
using LiveLingo.Runtime.Translation;
using LiveLingo.Runtime.Workflows;

namespace LiveLingo.Runtime.Tests.Workflows;

/// <summary>在真实 MAF 运行时验证一次调用、失败和实际取消结束，不需要 GPU。</summary>
public sealed class TranslationWorkflowTests
{
    /// <summary>有限图只发起一次翻译，精确保留重复及所有版本字段。</summary>
    /// <returns>实际框架运行任务。</returns>
    [Fact]
    public async Task OneCompleteBlockProducesOneResult()
    {
        var input = Input();
        var count = 0;
        var service = new FakeService((item, _) => { count++; Assert.Equal(input, item); return Task.FromResult(Result(item)); });
        var result = await new TranslationWorkflow(service).RunAsync(input, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(input, result.Input);
        Assert.Equal("はい、はい。行きません。", result.Input.Source);
        Assert.True(result.Complete);
        Assert.Equal(1, count);
    }

    /// <summary>服务异常、错误身份及截断分别保留其失败语义。</summary>
    /// <returns>有限图的多个独立运行任务。</returns>
    [Fact]
    public async Task ErrorsCannotBecomeSuccessfulOutput()
    {
        var input = Input();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TranslationWorkflow(new FakeService((_, _) => throw new IOException("injected"))).RunAsync(input, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TranslationWorkflow(new FakeService((_, _) => Task.FromResult(Result(Input())))).RunAsync(input, CancellationToken.None));
        var incomplete = await new TranslationWorkflow(new FakeService((item, _) => Task.FromResult(Result(item) with { Complete = false, Reason = "length" }))).RunAsync(input, CancellationToken.None);
        Assert.False(incomplete.Complete);
        Assert.Equal("length", incomplete.Reason);
    }

    /// <summary>取消后观察结束不能遗留仍在执行的 executor。</summary>
    /// <returns>检查服务 finally 已执行的任务。</returns>
    [Fact]
    public async Task CancellationWaitsForExecutorExit()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = false;
        var service = new FakeService(async (item, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Result(item); }
            finally { exited = true; }
        });
        var run = new TranslationWorkflow(service).RunAsync(Input(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(exited);
    }

    /// <summary>生成独立有效块。</summary><returns>翻译输入。</returns>
    private static TranslationInput Input() => new(new(Guid.NewGuid(), 1, PipelineRequestKind.Translation,
        new(Guid.NewGuid(), 1, 1, 1), new(Guid.NewGuid(), 1), new(0, 16000), 1), "はい、はい。行きません。");
    /// <summary>构造可预测响应。</summary><param name="input">关联输入。</param><returns>正常响应。</returns>
    private static TranslationResult Result(TranslationInput input) => new(input, "是的，是的。不去。", true, "stop", TimeSpan.Zero, new('a', 64), 0, 0, 10);
    /// <summary>只替代模型边界，保留真实框架运行。</summary>
    private sealed class FakeService : ITranslationService
    {
        private readonly Func<TranslationInput, CancellationToken, Task<TranslationResult>> operation;
        /// <summary>配置受控的服务行为。</summary><param name="operation">实际测试委托。</param>
        public FakeService(Func<TranslationInput, CancellationToken, Task<TranslationResult>> operation) => this.operation = operation;
        /// <inheritdoc />
        public Task<TranslationResult> TranslateAsync(TranslationInput input, CancellationToken cancellationToken) => operation(input, cancellationToken);
    }
}
