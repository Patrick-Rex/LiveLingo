using System.Buffers;
using LiveLingo.Core.Audio;
using LiveLingo.Runtime.Pipeline;

namespace LiveLingo.Runtime.Tests.Pipeline;

/// <summary>验证原始采集连续性进入模型时间轴时没有被忽略或误补。</summary>
public sealed class CapturePipelineInputTests
{
    /// <summary>原生 discontinuity、格式和采集代次变化产生单独重置，包所有权最终全部释放。</summary>
    /// <returns>原始输入转换与停止释放检查任务。</returns>
    [Fact]
    public async Task DiscontinuityAndEpochResetFilterWithoutLosingPcm()
    {
        var session = Guid.NewGuid();
        var packets = new[] { Packet(session, 1, 1, 0), Packet(session, 1, 2, 320),
            Packet(session, 1, 3, 640, PacketFlags.Discontinuity), Packet(session, 2, 1, 0) };
        var output = new List<PipelineInput>();
        await foreach (var value in CapturePipelineInput.ReadAsync(Enumerate(packets), session, TestContext.Current.CancellationToken)) output.Add(value);
        var audio = output.OfType<PipelineAudio>().ToArray();
        Assert.Equal(4, audio.Length);
        Assert.Equal(new long[] { 1, 1, 2, 3 }, audio.Select(x => x.CaptureEpoch));
        Assert.Equal(new long[] { 0, 320, 0, 0 }, audio.Select(x => x.Audio.Range.Start));
        Assert.All(audio, x => { Assert.Equal(320, x.Audio.Samples.Length); Assert.All(x.Audio.Samples, value => Assert.Equal(0.125f, value)); });
        Assert.Equal(new[] { "capture_discontinuity_or_format_change", "capture_epoch_changed" }, output.OfType<PipelineCaptureReset>().Select(x => x.Reason));
        Assert.All(packets, x => Assert.Throws<ObjectDisposedException>(() => x.Pcm));
    }

    /// <summary>消费方提前退出时当前原始包仍归还池。</summary><returns>枚举器停止完成任务。</returns>
    [Fact]
    public async Task EarlyConsumerStopDisposesCurrentPacket()
    {
        var session = Guid.NewGuid(); var packet = Packet(session, 1, 1, 0);
        await foreach (var _ in CapturePipelineInput.ReadAsync(Enumerate([packet]), session, TestContext.Current.CancellationToken)) break;
        Assert.Throws<ObjectDisposedException>(() => packet.Pcm);
    }

    /// <summary>生成固定低音量、已知精确转换值的 PCM16 包。</summary>
    /// <param name="session">会话。</param><param name="epoch">代次。</param><param name="sequence">包号。</param><param name="start">起点。</param>
    /// <param name="flags">原生标志。</param><returns>交给消费者释放的包。</returns>
    private static AudioPacket Packet(Guid session, long epoch, long sequence, long start, PacketFlags flags = PacketFlags.None)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(640);
        for (var i = 0; i < 640; i += 2) { buffer[i] = 0; buffer[i + 1] = 16; }
        return new(buffer, session, epoch, sequence, start, 320, new(16000, 1), flags, 0, 0, 0);
    }

    /// <summary>按序转移原始包，模拟异步捕获入口。</summary><param name="packets">测试拥有的原始包。</param><returns>原始异步序列。</returns>
    private static async IAsyncEnumerable<AudioPacket> Enumerate(AudioPacket[] packets)
    { foreach (var packet in packets) { await Task.Yield(); yield return packet; } }
}
