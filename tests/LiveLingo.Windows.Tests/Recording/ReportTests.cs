using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using LiveLingo.Core.Audio;
using LiveLingo.Core.Sessions;
using LiveLingo.Probe.Recording;
using LiveLingo.Runtime.Pipeline;

namespace LiveLingo.Windows.Tests.Recording;

/// <summary>通过实际 JSONL 和 WAV 文件验证报告与失败消费，不依赖音频设备。</summary>
public sealed class ReportTests
{
    /// <summary>两次中断只在下一次就绪完成一个恢复样本，超长恢复不伪装成 60 秒。</summary>
    [Fact]
    public void RecoveryAndInvalidTimestampEvidenceRemainExplicit()
    {
        var directory = DirectoryPath();
        var manifest = Manifest() with { StopwatchFrequency = 1000 };
        using (var recorder = new RunRecorder(directory, manifest))
        {
            recorder.WriteEvent(new("diagnostic", new("CaptureFailed", DateTimeOffset.UtcNow, 0, SessionState.Recovering, 1, 1)));
            recorder.WriteEvent(new("diagnostic", new("CaptureFailed", DateTimeOffset.UtcNow, 1000, SessionState.Recovering, 2, 2)));
            recorder.WriteEvent(new("diagnostic", new("Ready", DateTimeOffset.UtcNow, 65000, SessionState.Running, 3, 3)));
            recorder.WriteEvent(new("diagnostic", new("Suspend", DateTimeOffset.UtcNow, 70000, SessionState.Suspended, 3, 4)));
            recorder.WriteEvent(new("packet", Packet: new(3, 1, 0, 1, PacketFlags.TimestampError, 0, 0, 0, 0, 0, 0, null, 0)));
            recorder.WriteEvent(new("diagnostic", new("cleaned", DateTimeOffset.UtcNow, 75000, SessionState.Idle, 3, 5)));
        }
        var report = ReportBuilder.Build(directory, 0, null, [], 0);
        Assert.Equal(1, report.RecoverySamples);
        Assert.Null(report.RecoveryP95Ms);
        Assert.Equal(1, report.HistogramOverflowSamples);
        Assert.Equal(1, report.IncompleteRecoveries);
        Assert.Equal(1, report.InvalidTimestampSamples);
        Assert.Null(report.CaptureP95Ms);
        Assert.Equal(manifest.RequestedFormat, report.InitializedFormat);
        Assert.True(report.CleanupCompleted);
    }

    /// <summary>录音限额保留已写音频、释放超限包，并生成符合单声道格式的长度和头部。</summary>
    /// <returns>完成实际落盘与故障断言的任务。</returns>
    [Fact]
    public async Task OutputLimitFinalizesAcceptedWaveAndReleasesRejectedPacketAsync()
    {
        var directory = DirectoryPath();
        var policy = new CapturePolicy { MaxOutputBytes = 4096, QueueAudioSeconds = 10 };
        using var ingress = new AudioIngress(policy, new(1000, 1));
        using var recorder = new RunRecorder(directory, Manifest() with { Policy = policy });
        var first = Packet(1200);
        var rejected = Packet(1200);
        ingress.TryPublish(first);
        ingress.TryPublish(rejected);
        ingress.Complete();
        await Assert.ThrowsAsync<AudioCapacityException>(() => recorder.ConsumeAsync(ingress, CancellationToken.None));
        Assert.Throws<ObjectDisposedException>(() => first.Pcm);
        Assert.Throws<ObjectDisposedException>(() => rejected.Pcm);
        var wave = Assert.Single(recorder.Waves);
        Assert.Equal(1200, wave.Frames);
        Assert.Equal(2400, wave.Bytes);
        Assert.True(wave.HeaderFinalized);
        var bytes = File.ReadAllBytes(Path.Combine(directory, wave.FileName));
        Assert.Equal(2444, bytes.Length);
        Assert.Equal(2400u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
    }

    /// <summary>强杀产生的不完整末行必须使重放明确失败，不能判为清理成功。</summary>
    [Fact]
    public void TruncatedLogCannotReportSuccess()
    {
        var directory = DirectoryPath();
        using (var recorder = new RunRecorder(directory, Manifest())) { }
        File.AppendAllText(Path.Combine(directory, "events.jsonl"), "{\"Kind\":");
        var report = ReportBuilder.Build(directory, 0, null, [], 0);
        Assert.Equal(5, report.ExitCode);
        Assert.False(report.CleanupCompleted);
        Assert.NotNull(report.Error);
        Assert.Null(report.InitializedFormat);
    }

    /// <summary>无效录制频率拒绝重放，不使用本机频率伪造时延。</summary>
    [Fact]
    public void InvalidRecordedClockIsRejected()
    {
        var directory = DirectoryPath();
        using (var recorder = new RunRecorder(directory, Manifest() with { StopwatchFrequency = 0 })) { }
        Assert.Throws<InvalidDataException>(() => ReportBuilder.Build(directory, 0, null, [], 0));
    }

    /// <summary>创建 D 盘独立测试证据目录，不覆盖已有文件。</summary>
    /// <returns>新建的绝对目录。</returns>
    private static string DirectoryPath()
    {
        var path = Path.Combine(@"D:\Soft Ware\LiveLingo\data\tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>构造不绑定实际设备的报告清单。</summary><returns>模拟清单。</returns>
    private static RunManifest Manifest() => new(1, Guid.NewGuid(), DateTimeOffset.UtcNow,
        new(123, DateTimeOffset.UnixEpoch, Path.GetFullPath("fixture.exe")), "simulated", new(), new(1000, 1),
        Stopwatch.Frequency, Environment.OSVersion.ToString(), Environment.Version.ToString(), "simulated", "simulated");

    /// <summary>租用模拟 PCM 包，并交给消费者释放。</summary><param name="frames">帧数。</param><returns>独占包。</returns>
    private static AudioPacket Packet(int frames) => new(ArrayPool<byte>.Shared.Rent(frames * 2), Guid.NewGuid(), 1, 1,
        0, frames, new(1000, 1), PacketFlags.None, 0, 0, Stopwatch.GetTimestamp());
}
