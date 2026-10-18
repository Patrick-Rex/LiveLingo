using System.Buffers.Binary;
using System.Text;
using LiveLingo.Core.Audio;
using LiveLingo.Core.Processes;
using LiveLingo.Core.Subtitles;
using LiveLingo.Runtime.Pipeline;

namespace LiveLingo.Runtime.Asr;

/// <summary>单个 worker 代次的有限客户端；损坏、超时或取消后回收实例，旧结果不能复用。</summary>
public sealed class AsrWorkerClient : IAsyncDisposable
{
    private readonly IWorkerConnection connection;
    private readonly long epoch;
    private readonly WorkerInformation expected;
    private readonly PipelineOptions options;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly object sync = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task reader;
    private readonly Task diagnostics;
    private TaskCompletionSource<IpcHeader>? response;
    private AsrRequest? active;
    private TaskCompletionSource? pong;
    private string? nonce;
    private Exception? failure;
    private bool helloSeen, readySeen, shuttingDown;
    private long stderrBytes;
    private readonly byte[] diagnosticTail;
    private int tailPosition, tailCount;

    /// <summary>立即开始有限 stdout/stderr 读取，模型就绪仍需显式等待。</summary>
    /// <param name="connection">当前原子监管的连接。</param><param name="epoch">本实例代次。</param>
    /// <param name="expected">已验证的运行时和模型信息，空设备名允许实际 GPU 型号。</param>
    /// <param name="options">有限启动、请求和关闭时间。</param>
    /// <param name="captureDiagnosticTail">仅显式评测保留最多 8KiB 诊断尾部，普通运行不保存内容。</param>
    public AsrWorkerClient(IWorkerConnection connection, long epoch, WorkerInformation expected, PipelineOptions options, bool captureDiagnosticTail = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(epoch);
        options.Validate();
        this.connection = connection; this.epoch = epoch; this.expected = expected; this.options = options;
        diagnosticTail = captureDiagnosticTail ? new byte[8192] : [];
        reader = ReadLoopAsync();
        diagnostics = DrainErrorAsync();
    }

    /// <summary>获取已实际消费的诊断字节数，不把字幕或原始诊断永久记录。</summary>
    public long StderrBytes => Interlocked.Read(ref stderrBytes);
    /// <summary>获取实例是否已失效，恢复必须创建新的 WorkerEpoch。</summary>
    public bool Faulted { get { lock (sync) return failure is not null; } }

    /// <summary>取得显式评测开启的有限诊断副本，默认空字符串。</summary><returns>至多 8KiB 字节解码的尾部。</returns>
    public string DiagnosticTail()
    {
        lock (sync)
        {
            var bytes = new byte[tailCount];
            for (var index = 0; index < bytes.Length; index++) bytes[index] = diagnosticTail[(tailPosition - tailCount + index + diagnosticTail.Length) % diagnosticTail.Length];
            return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>等待匹配的 Hello 与实际模型 Ready，超时必须回收进程。</summary>
    /// <param name="cancellationToken">启动取消。</param><returns>模型可接受请求时完成。</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try { await ready.Task.WaitAsync(options.StartupTimeout, cancellationToken).ConfigureAwait(false); }
        catch { await AbortAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>向 worker 追加有限连续音频；调用方仍拥有其音频缓存。</summary>
    /// <param name="identity">有效音频身份。</param><param name="sequence">传输序号。</param><param name="range">完整输入范围。</param>
    /// <param name="audio">有限采样，不允许隐式剪裁。</param><param name="cancellationToken">写入取消。</param>
    /// <returns>音频帧完整提交任务。</returns>
    public async Task AppendAudioAsync(PipelineIdentity identity, long sequence, SampleRange range, ReadOnlyMemory<float> audio, CancellationToken cancellationToken)
    {
        EnsureReady();
        if (identity.WorkerEpoch != epoch || range.Length != audio.Length || audio.Length is <= 0 or > 16000)
            throw new ArgumentException("音频身份、范围或单包预算无效。");
        var payload = new byte[checked(audio.Length * 4)];
        for (var index = 0; index < audio.Length; index++) BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(index * 4, 4), audio.Span[index]);
        await SendCheckedAsync(new(new() { Type = "AudioChunk", WorkerEpoch = epoch, SessionId = identity.SessionId,
            CaptureEpoch = identity.CaptureEpoch, FrameSequence = sequence, StartSample = range.Start, EndSample = range.End }, payload), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>执行唯一在途请求，超时与取消均等待实例退出后才返回异常。</summary>
    /// <param name="request">必须回显的完整逻辑与窗口身份。</param><param name="cancellationToken">用户取消信号。</param>
    /// <returns>精确匹配本次窗口的原始响应。</returns>
    public async Task<IpcHeader> RecognizeAsync(AsrRequest request, CancellationToken cancellationToken)
    {
        EnsureReady();
        var header = new IpcHeader { Type = "Recognize", WorkerEpoch = epoch, Correlation = request };
        IpcProtocol.Validate(header, 0);
        TaskCompletionSource<IpcHeader> completion;
        lock (sync)
        {
            if (active is not null) throw new InvalidOperationException("仅允许一个 ASR 请求在途。");
            active = request;
            completion = response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            await SendCheckedAsync(new(header, []), cancellationToken).ConfigureAwait(false);
            var result = await completion.Task.WaitAsync(options.RequestTimeout, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Type != "Transcription") throw new OperationCanceledException("worker_cancelled");
            return result;
        }
        catch { await AbortAsync().ConfigureAwait(false); throw; }
        finally { lock (sync) { active = null; response = null; } }
    }

    /// <summary>释放模型不再需要的音频，不能跨越仍在途窗口的起点。</summary>
    /// <param name="identity">当前音频身份。</param><param name="before">连续释放界线。</param><param name="cancellationToken">写入取消。</param>
    /// <returns>释放帧提交任务。</returns>
    public Task ReleaseBeforeAsync(PipelineIdentity identity, long before, CancellationToken cancellationToken)
    {
        EnsureReady();
        lock (sync) if (active is not null && before > active.WindowRange.Start) throw new InvalidOperationException("不能释放在途窗口。");
        return SendCheckedAsync(new(new() { Type = "ReleaseBefore", WorkerEpoch = epoch, SessionId = identity.SessionId,
            CaptureEpoch = identity.CaptureEpoch, StartSample = before }, []), cancellationToken);
    }

    /// <summary>无识别在途时重置模型音频副本，允许从宿主完整租约重传更早的扩展上下文。</summary>
    /// <param name="identity">保持不变的会话与采集身份。</param><param name="start">重新上传的精确起点。</param>
    /// <param name="cancellationToken">共同请求预算。</param><returns>重置控制提交任务。</returns>
    public Task ResetAudioAsync(PipelineIdentity identity, long start, CancellationToken cancellationToken)
    {
        EnsureReady();
        lock (sync) if (active is not null) throw new InvalidOperationException("不能重置在途窗口。");
        return SendCheckedAsync(new(new() { Type = "ResetAudio", WorkerEpoch = epoch, SessionId = identity.SessionId,
            CaptureEpoch = identity.CaptureEpoch, StartSample = start }, []), cancellationToken);
    }

    /// <summary>验证控制通路存活，不延长在途请求的超时。</summary>
    /// <param name="cancellationToken">心跳取消。</param><returns>匹配 Pong 到达时完成。</returns>
    public async Task PingAsync(CancellationToken cancellationToken)
    {
        EnsureReady();
        TaskCompletionSource completion;
        string value;
        lock (sync)
        {
            if (pong is not null) throw new InvalidOperationException("心跳已在途。");
            nonce = value = Guid.NewGuid().ToString("N");
            pong = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            await SendCheckedAsync(new(new() { Type = "Ping", WorkerEpoch = epoch, Nonce = value }, []), cancellationToken).ConfigureAwait(false);
            await completion.Task.WaitAsync(options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch { await AbortAsync().ConfigureAwait(false); throw; }
        finally { lock (sync) { pong = null; nonce = null; } }
    }

    /// <summary>停止接收并等待模型清理，任何路径最终回收自有 Job 与管道任务。</summary>
    /// <returns>自有实例及读写任务退出后的任务。</returns>
    public async ValueTask DisposeAsync()
    {
        bool graceful;
        lock (sync) { graceful = !shuttingDown && failure is null && readySeen; shuttingDown = true; }
        Task? sending = null;
        try
        {
            if (graceful)
            {
                using var timeout = new CancellationTokenSource(options.ShutdownTimeout);
                sending = SendAsync(new(new() { Type = "Shutdown", WorkerEpoch = epoch }, []), timeout.Token);
                await sending.WaitAsync(options.ShutdownTimeout).ConfigureAwait(false);
                await stopped.Task.WaitAsync(options.ShutdownTimeout).ConfigureAwait(false);
            }
        }
        finally
        {
            await connection.TerminateAsync().ConfigureAwait(false);
            if (sending is not null)
                try { await sending.WaitAsync(options.ShutdownTimeout).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
            await Task.WhenAll(reader, diagnostics).WaitAsync(options.ShutdownTimeout).ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>为每个完整帧取得唯一 writer，任何中断都使整个流失效。</summary>
    /// <param name="frame">完整消息。</param><param name="cancellationToken">取消信号。</param><returns>写入任务。</returns>
    private async Task SendCheckedAsync(IpcFrame frame, CancellationToken cancellationToken)
    {
        var sending = SendAsync(frame, cancellationToken);
        try { await sending.WaitAsync(options.RequestTimeout, cancellationToken).ConfigureAwait(false); }
        catch
        {
            await AbortAsync().ConfigureAwait(false);
            try { await sending.WaitAsync(options.ShutdownTimeout).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
            throw;
        }
    }

    /// <summary>实际执行单 writer 写入，确保没有两个帧字节交错。</summary>
    /// <param name="frame">待提交帧。</param><param name="cancellationToken">取消信号。</param><returns>写入任务。</returns>
    private async Task SendAsync(IpcFrame frame, CancellationToken cancellationToken)
    {
        await writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await IpcProtocol.WriteAsync(connection.Input, frame, cancellationToken).ConfigureAwait(false); }
        finally { writer.Release(); }
    }

    /// <summary>限制为一个响应和一个心跳，任何非预期帧都使当前代次失效。</summary>
    /// <returns>读循环结束任务。</returns>
    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var frame = await IpcProtocol.ReadAsync(connection.Output, CancellationToken.None).ConfigureAwait(false);
                var header = frame.Header;
                lock (sync)
                {
                    if (header.WorkerEpoch != epoch) throw new InvalidDataException("stale_worker_epoch");
                    switch (header.Type)
                    {
                        case "Hello" when !helloSeen && !readySeen:
                            ValidateInformation(header.Information!); helloSeen = true; break;
                        case "Ready" when helloSeen && !readySeen:
                            ValidateInformation(header.Information!); readySeen = true; ready.TrySetResult(); break;
                        case "Transcription" or "Cancelled" when readySeen && active is not null && header.Correlation == active && response is not null:
                            if (!response.TrySetResult(header)) throw new InvalidDataException("duplicate_asr_response");
                            break;
                        case "Pong" when pong is not null && header.Nonce == nonce:
                            if (!pong.TrySetResult()) throw new InvalidDataException("duplicate_pong");
                            break;
                        case "Stopped" when shuttingDown:
                            stopped.TrySetResult(); return;
                        default: throw new InvalidDataException(header.Type is "Failed" or "Error" ? header.Reason : "unexpected_worker_message");
                    }
                }
            }
        }
        catch (Exception error)
        {
            Fail(error);
            await connection.TerminateAsync().ConfigureAwait(false);
        }
    }

    /// <summary>持续排空诊断，使用固定小缓冲且不积累无限日志或正文。</summary>
    /// <returns>stderr EOF 或受控关闭后完成。</returns>
    private async Task DrainErrorAsync()
    {
        var buffer = new byte[8192];
        try
        {
            int read;
            while ((read = await connection.Error.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            {
                Interlocked.Add(ref stderrBytes, read);
                if (diagnosticTail.Length == 0) continue;
                lock (sync) for (var index = 0; index < read; index++)
                {
                    diagnosticTail[tailPosition] = buffer[index]; tailPosition = (tailPosition + 1) % diagnosticTail.Length;
                    tailCount = Math.Min(tailCount + 1, diagnosticTail.Length);
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            lock (sync) if (!shuttingDown && failure is null) Fail(error);
        }
    }

    /// <summary>拒绝环境与模型替换，实际设备信息不能为空。</summary>
    /// <param name="actual">worker 实际报告。</param>
    private void ValidateInformation(WorkerInformation actual)
    {
        if (actual.Python != expected.Python || actual.Torch != expected.Torch || actual.Cuda != expected.Cuda ||
            actual.ModelRevision != expected.ModelRevision || string.IsNullOrWhiteSpace(actual.Device) || expected.Device.Length != 0 && actual.Device != expected.Device)
            throw new InvalidDataException("worker_profile_mismatch");
    }

    /// <summary>只允许实际 Ready 且未失效的实例接收音频和任务。</summary>
    private void EnsureReady()
    {
        lock (sync) if (!readySeen || failure is not null || shuttingDown) throw new InvalidOperationException("worker_not_ready_or_invalidated", failure);
    }

    /// <summary>发布失败屏障，唤醒所有有限等待者。</summary><param name="error">失败原因。</param>
    private void Fail(Exception error)
    {
        lock (sync)
        {
            failure ??= error;
            ready.TrySetException(error); response?.TrySetException(error); pong?.TrySetException(error); stopped.TrySetException(error);
        }
    }

    /// <summary>失效实例并等待平台终止，不能通过取消令牌假定 GPU 已退出。</summary><returns>实际终止任务。</returns>
    private async Task AbortAsync()
    {
        Fail(new IOException("worker_invalidated"));
        await connection.TerminateAsync().ConfigureAwait(false);
    }
}
