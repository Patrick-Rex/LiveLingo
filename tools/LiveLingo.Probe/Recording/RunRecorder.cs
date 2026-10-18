using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LiveLingo.Core.Audio;
using LiveLingo.Runtime.Diagnostics;
using LiveLingo.Runtime.Pipeline;

namespace LiveLingo.Probe.Recording;

/// <summary>在消费者侧写 WAV 与 JSONL，限额和错误保留为可报告事实。</summary>
internal sealed class RunRecorder : IDisposable
{
    private readonly object sync = new();
    private readonly FileStream events;
    private readonly string directory;
    private readonly CapturePolicy policy;
    private readonly List<WaveEvidence> waves = [];
    private WaveWriter? wave;
    private string? waveFile;
    private long waveEpoch;
    private int waveBlockAlign;
    private int part;
    private long totalBytes;

    /// <summary>创建独立 JSONL 文件及固定启动清单。</summary>
    /// <param name="directory">已准备的空运行目录。</param><param name="manifest">启动事实。</param>
    public RunRecorder(string directory, RunManifest manifest)
    {
        this.directory = directory;
        policy = manifest.Policy;
        File.WriteAllBytes(Path.Combine(directory, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, ProbeJsonContext.Default.RunManifest));
        events = new(Path.Combine(directory, "events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536);
    }

    /// <summary>获取已关闭录音的文件证据。</summary>
    public WaveEvidence[] Waves => waves.ToArray();

    /// <summary>从消费者或状态循环写元数据；禁止从原生回调调用。</summary>
    /// <param name="item">有限形状的事件。</param>
    public void WriteEvent(RunEvent item)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(item, ProbeJsonContext.Default.RunEvent);
        lock (sync)
        {
            if (events.Length + json.Length + 1 > 512L * 1024 * 1024) throw new AudioCapacityException("诊断文件达到 512MiB 上限。");
            events.Write(json);
            events.WriteByte(10);
        }
    }

    /// <summary>消费全部已接受音频；任何写入失败都释放当前包并返回错误。</summary>
    /// <param name="ingress">有界音频入口。</param>
    /// <param name="token">清理超时的消费取消信号。</param>
    /// <returns>正常排空或错误任务。</returns>
    public async Task ConsumeAsync(AudioIngress ingress, CancellationToken token)
    {
        try
        {
            await foreach (var packet in ingress.ReadAllAsync(token).ConfigureAwait(false))
            {
                using (packet)
                {
                    var dequeued = Stopwatch.GetTimestamp();
                    if (policy.ConsumerDelay > TimeSpan.Zero) await Task.Delay(policy.ConsumerDelay, token).ConfigureAwait(false);
                    if (totalBytes + packet.ByteCount > policy.MaxOutputBytes) throw new AudioCapacityException("录音达到配置的 PCM 输出限额。");
                    if (wave is null || waveEpoch != packet.Epoch || packet.Flags.HasFlag(PacketFlags.Discontinuity))
                    {
                        CloseWave();
                        waveEpoch = packet.Epoch;
                        waveFile = $"epoch-{waveEpoch:D4}-part-{++part:D4}.wav";
                        wave = new(Path.Combine(directory, waveFile), packet.Format);
                        waveBlockAlign = packet.Format.BlockAlign;
                    }
                    wave.Write(packet.Pcm.Span);
                    totalBytes += packet.ByteCount;
                    var written = Stopwatch.GetTimestamp();
                    WriteEvent(new("packet", Packet: new(packet.Epoch, packet.Sequence, packet.FrameStart, packet.FrameCount,
                        packet.Flags, packet.DevicePositionFrames, packet.QpcPosition100ns, packet.AvailableTicks,
                        packet.EnqueuedTicks, dequeued, written, MonotonicClock.CaptureLatencyMs(packet.QpcPosition100ns,
                            packet.AvailableTicks, packet.TimestampValid), ingress.QueuedFrames)));
                    if (packet.Flags.HasFlag(PacketFlags.Discontinuity))
                        WriteEvent(new("diagnostic", new("gap", DateTimeOffset.UtcNow, written, LiveLingo.Core.Sessions.SessionState.Running,
                            packet.Epoch, 0, "WASAPI DATA_DISCONTINUITY", new(packet.SessionId, packet.Epoch,
                                "WASAPI DATA_DISCONTINUITY", packet.FrameStart, null, written))));
                }
            }
        }
        finally { CloseWave(); }
    }

    /// <summary>关闭当前 WAV 并计算完成文件的 SHA-256。</summary>
    private void CloseWave()
    {
        var owned = wave;
        if (owned is null) return;
        wave = null;
        var finalized = false;
        try { owned.Dispose(); finalized = true; }
        finally
        {
            using var input = File.OpenRead(Path.Combine(directory, waveFile!));
            waves.Add(new(waveFile!, waveEpoch, owned.Bytes / waveBlockAlign, owned.Bytes,
                Convert.ToHexStringLower(SHA256.HashData(input)), finalized));
        }
    }

    /// <summary>关闭消费资源并冲刷元数据；调用前需等待消费者完成。</summary>
    public void Dispose()
    {
        try { CloseWave(); }
        finally
        {
            lock (sync)
            {
                try { events.Flush(true); }
                finally { events.Dispose(); }
            }
        }
    }
}
