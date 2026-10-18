using LiveLingo.Core.Processes;
using LiveLingo.Core.Subtitles;
using LiveLingo.Runtime.Pipeline;
using LiveLingo.Runtime.Storage;

namespace LiveLingo.Runtime.Asr;

/// <summary>串行管理有限 worker 重建，仅传输当前有效模型窗口缺失的音频。</summary>
public sealed class AsrWorkerManager : IAsyncDisposable
{
    private readonly Func<long, IWorkerConnection> create;
    private readonly WorkerInformation expected;
    private readonly PipelineOptions options;
    private readonly int maximumRestarts;
    private AsrWorkerClient? client;
    private PipelineIdentity? audioIdentity;
    private long uploadedEnd, retainedStart, sequence;
    private bool initialized;

    /// <summary>配置受监管进程工厂和显式恢复预算，不隐式切换模型。</summary>
    /// <param name="create">按新代次创建独立 Job 连接。</param><param name="expected">预期运行配置。</param>
    /// <param name="options">有界配置。</param><param name="maximumRestarts">本会话最大重建次数。</param>
    public AsrWorkerManager(Func<long, IWorkerConnection> create, WorkerInformation expected, PipelineOptions options, int maximumRestarts = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRestarts);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumRestarts, 10);
        options.Validate();
        this.create = create; this.expected = expected; this.options = options; this.maximumRestarts = maximumRestarts;
    }

    /// <summary>获取当前实例代次，调用方必须将其提交到单写入归约器。</summary>
    public long WorkerEpoch { get; private set; }
    /// <summary>获取当前会话已经消耗的恢复次数。</summary>
    public int Restarts { get; private set; }

    /// <summary>等待旧实例实际退出后建立严格递增的新代次，不自动重放旧请求。</summary>
    /// <param name="epoch">归约器已准备的新 WorkerEpoch。</param><param name="cancellationToken">初始化取消。</param>
    /// <returns>新模型已就绪时完成。</returns>
    public async Task StartAsync(long epoch, CancellationToken cancellationToken)
    {
        if (epoch <= WorkerEpoch) throw new ArgumentOutOfRangeException(nameof(epoch));
        if (initialized && Restarts >= maximumRestarts) throw new InvalidOperationException("worker_restart_capacity");
        if (client is not null) { await client.DisposeAsync().ConfigureAwait(false); client = null; }
        if (initialized) Restarts++;
        initialized = true; WorkerEpoch = epoch;
        audioIdentity = null; uploadedEnd = retainedStart = 0; sequence = 0;
        var connection = create(epoch);
        client = new(connection, epoch, expected, options);
        await client.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>从宿主租约同步有限新增音频，只执行调用方重新校验后的当前请求。</summary>
    /// <param name="request">当前代次的逻辑/窗口身份。</param><param name="window">仍持有的完整窗口租约。</param>
    /// <param name="cancellationToken">推理取消。</param><returns>匹配当前窗口的原始结果。</returns>
    /// <exception cref="TimeoutException">完整窗口上传及推理超过共同请求预算，worker 已回收。</exception>
    public async Task<IpcHeader> RecognizeAsync(AsrRequest request, AudioWindowLease window, CancellationToken cancellationToken)
    {
        if (client is null || request.Ticket.Identity.WorkerEpoch != WorkerEpoch || window.Range != request.WindowRange)
            throw new InvalidOperationException("worker_or_lease_identity_mismatch");
        var identity = request.Ticket.Identity;
        if (audioIdentity is not null && (audioIdentity.SessionId != identity.SessionId || audioIdentity.CaptureEpoch != identity.CaptureEpoch))
            throw new InvalidOperationException("capture_change_requires_worker_restart");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RequestTimeout);
        try
        {
            if (window.Range.Length > options.MaximumLeaseSamples) throw new InvalidOperationException("worker_window_budget");
            if (audioIdentity is null) { audioIdentity = identity; uploadedEnd = retainedStart = window.Range.Start; }
            else if (window.Range.Start < retainedStart)
            {
                if (options.AlignmentRecoverySamples == 0 || window.Range.Length <= options.MaximumWindowSamples)
                    throw new InvalidOperationException("worker_expansion_not_enabled");
                if (window.Range.End < uploadedEnd) throw new InvalidOperationException("worker_window_discontinuity");
                await client.ResetAudioAsync(identity, window.Range.Start, deadline.Token).ConfigureAwait(false);
                uploadedEnd = retainedStart = window.Range.Start;
            }
            else
            {
                if (window.Range.End < uploadedEnd) throw new InvalidOperationException("worker_window_discontinuity");
                await client.ReleaseBeforeAsync(identity, Math.Min(window.Range.Start, uploadedEnd), deadline.Token).ConfigureAwait(false);
                // 已释放的模型缓存可以从下一有效块重建；宿主仍独立记录中间静音及完整来源。
                uploadedEnd = Math.Max(uploadedEnd, window.Range.Start);
                retainedStart = window.Range.Start;
            }
            for (var start = uploadedEnd; start < window.Range.End;)
            {
                var count = checked((int)Math.Min(16000, window.Range.End - start));
                var offset = checked((int)(start - window.Range.Start));
                await client.AppendAudioAsync(identity, ++sequence, new(start, start + count), window.Samples.Slice(offset, count), deadline.Token).ConfigureAwait(false);
                start += count; uploadedEnd = start;
            }
            return await client.RecognizeAsync(request, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("asr_window_deadline", error);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (client is not null) { await client.DisposeAsync().ConfigureAwait(false); client = null; }
    }
}
