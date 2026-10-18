using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LiveLingo.Core.Audio;
using LiveLingo.Core.Sessions;
using LiveLingo.Platform.Windows.Processes;

namespace LiveLingo.Platform.Windows.Audio;

/// <summary>在专用 MTA 线程执行事件驱动的进程树采集，不将原生指针传出。</summary>
public sealed class WindowsLoopbackCapture : ICaptureSource
{
    /// <summary>获取复现官方示例的基准客户端请求格式。</summary>
    public static AudioFormat RequestedFormat { get; } = new(44100, 2);

    /// <inheritdoc />
    public Task RunAsync(TargetIdentity target, Guid sessionId, long epoch, TimeSpan activationTimeout,
        Func<AudioPacket, bool> publish, Action ready, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Capture(target, sessionId, epoch, activationTimeout, publish, ready, cancellationToken);
                completion.TrySetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true, Name = $"LiveLingo.Audio.{epoch}" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return completion.Task;
    }

    /// <summary>激活、读取和释放均位于同一线程；每包复制后立即归还原生缓冲。</summary>
    /// <param name="target">已核实目标。</param><param name="sessionId">会话。</param><param name="epoch">代次。</param>
    /// <param name="activationTimeout">激活限时。</param><param name="publish">所有权入口。</param>
    /// <param name="ready">就绪通知。</param><param name="cancellationToken">停止信号。</param>
    private static void Capture(TargetIdentity target, Guid sessionId, long epoch, TimeSpan activationTimeout,
        Func<AudioPacket, bool> publish, Action ready, CancellationToken cancellationToken)
    {
        Check(AudioInterop.CoInitializeEx(0, 0), "CoInitializeEx(MTA)");
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        IActivateAudioInterfaceAsyncOperation? operation = null;
        var started = false;
        try
        {
            var current = WindowsTargets.Query(target.ProcessId);
            if (current.Identity is null || !target.SameInstance(current.Identity)) throw new InvalidOperationException("目标身份已变化，拒绝仅按 PID 激活。");
            using var activation = new ActivationCompletion();
            var parameters = new ActivationParams { ActivationType = 1, TargetProcessId = (uint)target.ProcessId, ProcessLoopbackMode = 0 };
            var blob = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
            try
            {
                Marshal.StructureToPtr(parameters, blob, false);
                var variant = new PropVariant { VariantType = 65, BlobSize = (uint)Marshal.SizeOf<ActivationParams>(), BlobData = blob };
                var result = AudioInterop.ActivateAudioInterfaceAsync("VAD\\Process_Loopback", AudioInterop.AudioClientId, variant, activation, out operation);
                if (result < 0) { activation.RejectCall(); Check(result, "ActivateAudioInterfaceAsync"); }
            }
            finally { Marshal.FreeHGlobal(blob); }
            var pointer = activation.Take(activationTimeout, cancellationToken);
            try { client = (IAudioClient)Marshal.GetObjectForIUnknown(pointer); }
            finally { Marshal.Release(pointer); }
            cancellationToken.ThrowIfCancellationRequested();
            var format = new WaveFormat
            {
                FormatTag = 1, Channels = 2, SamplesPerSecond = 44100,
                AverageBytesPerSecond = 44100 * 4, BlockAlign = 4, BitsPerSample = 16
            };
            Check(client.Initialize(0, 0x00020000 | 0x00040000 | 0x80000000, 0, 0, format, 0), "IAudioClient.Initialize");
            Check(client.GetService(AudioInterop.CaptureClientId, out var service), "IAudioClient.GetService");
            capture = (IAudioCaptureClient)service;
            using var sampleReady = new AutoResetEvent(false);
            Check(client.SetEventHandle(sampleReady.SafeWaitHandle.DangerousGetHandle()), "IAudioClient.SetEventHandle");
            Check(client.Start(), "IAudioClient.Start");
            started = true;
            ready();
            long frameStart = 0;
            long sequence = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny([sampleReady, cancellationToken.WaitHandle]) != 0) break;
                Check(capture.GetNextPacketSize(out var next), "GetNextPacketSize");
                while (next > 0 && !cancellationToken.IsCancellationRequested)
                {
                    Check(capture.GetBuffer(out var data, out var frames, out var flags, out var position, out var qpc), "GetBuffer");
                    AudioPacket? packet = null;
                    try
                    {
                        var available = Stopwatch.GetTimestamp();
                        var length = checked((int)frames * RequestedFormat.BlockAlign);
                        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
                        try
                        {
                            if ((flags & 2) != 0) buffer.AsSpan(0, length).Clear();
                            else Marshal.Copy(data, buffer, 0, length);
                            packet = new AudioPacket(buffer, sessionId, epoch, ++sequence, frameStart, checked((int)frames),
                                RequestedFormat, (PacketFlags)flags, position, qpc, available);
                        }
                        catch { ArrayPool<byte>.Shared.Return(buffer); throw; }
                    }
                    finally
                    {
                        var releaseResult = capture.ReleaseBuffer(frames);
                        if (releaseResult < 0) { packet?.Dispose(); Check(releaseResult, "ReleaseBuffer"); }
                    }
                    frameStart = checked(frameStart + frames);
                    if (!publish(packet!)) throw new IOException("音频入口拒绝了数据；停止当前采集代次。");
                    if (sequence > 1 && (flags & 1) != 0)
                        throw new IOException("WASAPI DATA_DISCONTINUITY：已保留断点包，结束旧代次并重新建立连续时间轴。");
                    Check(capture.GetNextPacketSize(out next), "GetNextPacketSize");
                }
            }
        }
        finally
        {
            try
            {
                if (started && client is not null) Check(client.Stop(), "IAudioClient.Stop");
            }
            finally
            {
                if (capture is not null) Marshal.ReleaseComObject(capture);
                if (client is not null) Marshal.ReleaseComObject(client);
                if (operation is not null) Marshal.ReleaseComObject(operation);
                AudioInterop.CoUninitialize();
            }
        }
    }

    /// <summary>保留失败调用名和原始 HRESULT，避免激活错误被吞掉。</summary>
    /// <param name="result">HRESULT。</param><param name="operation">API 名称。</param>
    /// <exception cref="COMException">原生调用失败。</exception>
    private static void Check(int result, string operation)
    {
        if (result < 0) throw new COMException($"{operation} 失败 (0x{result:X8})。", result);
    }
}
