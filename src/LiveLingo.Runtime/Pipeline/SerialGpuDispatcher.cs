namespace LiveLingo.Runtime.Pipeline;

/// <summary>串行执行 ASR 与翻译；取消观察不能在底层计算结束前释放配额。</summary>
public sealed class SerialGpuDispatcher
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool faulted;

    /// <summary>获取执行失败屏障；远端计算状态不明确时须先终止自有服务，再建立新调度器。</summary>
    public bool Faulted => Volatile.Read(ref faulted);

    /// <summary>等待全局 GPU 配额，并等待委托实际退出后才释放。</summary>
    /// <typeparam name="T">结果类型。</typeparam><param name="operation">必须代表底层计算完整生命周期的异步操作。</param>
    /// <param name="cancellationToken">等待或执行的取消信号。</param><returns>操作结果。</returns>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (faulted) throw new InvalidOperationException("GPU 执行已失败；确认旧计算退出前禁止继续调度。");
            try { return await operation(cancellationToken).ConfigureAwait(false); }
            catch { Volatile.Write(ref faulted, true); throw; }
        }
        finally { gate.Release(); }
    }
}
