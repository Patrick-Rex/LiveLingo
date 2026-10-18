using System.Buffers;
using System.Buffers.Binary;
using LiveLingo.Core.Audio;
using LiveLingo.Runtime.Pipeline;

namespace LiveLingo.Runtime.Tests.Pipeline;

/// <summary>验证跨包相位连续、抗混叠与身份边界，不用听感代替数值证据。</summary>
public sealed class StreamingPcmConverterTests
{
    /// <summary>立体声按帧取均值，格式改变必须创建新转换器而非复用旧相位。</summary>
    [Fact]
    public void StereoMixUsesBothChannelsAndRejectsFormatChange()
    {
        var session = Guid.NewGuid();
        var converter = new StreamingPcmConverter(new(16000, 2), session, 1);
        var bytes = ArrayPool<byte>.Shared.Rent(8);
        BinaryPrimitives.WriteInt16LittleEndian(bytes, 1000);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2), -1000);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(4), 1000);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(6), 3000);
        using var stereo = new AudioPacket(bytes, session, 1, 1, 0, 2, new(16000, 2), PacketFlags.None, 0, 0, 0);
        Assert.Equal(new float[] { 0, 2000 / 32768f }, converter.Convert(stereo).Samples);
        using var changed = Packet([1], 16000, session, 2, 2);
        Assert.Throws<InvalidDataException>(() => converter.Convert(changed));
    }

    /// <summary>同一输入无论一次提交或分包提交都得到逐采样一致的结果。</summary>
    /// <param name="rate">实际支持的源采样率。</param>
    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(16000)]
    public void PacketizationDoesNotChangeOutput(int rate)
    {
        var samples = Tone(rate, 1000);
        var whole = Convert(samples, rate, rate);
        var packets = Convert(samples, rate, 137);
        Assert.Equal(16000, whole.Length);
        Assert.Equal(whole, packets);
        Assert.InRange(Rms(whole.AsSpan(100, 15800)), 0.34, 0.37);
    }

    /// <summary>12kHz 输入不得以显著混叠进入 16kHz 输出的 4kHz 频率。</summary>
    [Fact]
    public void DownsamplingSuppressesOutOfBandEnergy()
    {
        var speechBand = Convert(Tone(44100, 1000), 44100, 441);
        var aboveNyquist = Convert(Tone(44100, 12000), 44100, 441);
        Assert.True(Rms(aboveNyquist.AsSpan(100, 15800)) < Rms(speechBand.AsSpan(100, 15800)) / 100);
    }

    /// <summary>最低 PCM 单位不能作为噪声被阈值过滤；静音标志则按真实采集契约置零。</summary>
    [Fact]
    public void QuietSamplesSurviveAndSilentFlagIsExplicit()
    {
        var session = Guid.NewGuid();
        var converter = new StreamingPcmConverter(new(16000, 1), session, 1);
        using var quiet = Packet([1, 1, 1, 1], 16000, session, 1, 0);
        Assert.All(converter.Convert(quiet).Samples, x => Assert.Equal(1f / 32768, x));
        using var silence = Packet([123, 123], 16000, session, 2, 4, PacketFlags.Silent);
        Assert.All(converter.Convert(silence).Samples, x => Assert.Equal(0, x));
    }

    /// <summary>代次或帧范围变化必须显式失败，不能拼成连续语音。</summary>
    [Fact]
    public void EpochOrFrameGapCannotReuseFilterState()
    {
        var session = Guid.NewGuid();
        var converter = new StreamingPcmConverter(new(44100, 1), session, 1);
        using var first = Packet(new short[441], 44100, session, 1, 0);
        converter.Convert(first);
        using var gap = Packet(new short[441], 44100, session, 2, 442);
        Assert.Throws<InvalidDataException>(() => converter.Convert(gap));
        using var epoch = Packet(new short[441], 44100, session, 2, 441, epoch: 2);
        Assert.Throws<InvalidDataException>(() => converter.Convert(epoch));
        Assert.Equal(441, converter.InputFrames);
        converter.Flush();
        Assert.Throws<InvalidOperationException>(() => converter.Convert(first));
    }

    /// <summary>生成有确定频率和幅度的一秒 PCM16 输入。</summary>
    /// <param name="rate">源采样率。</param><param name="frequency">正弦频率。</param><returns>一秒采样。</returns>
    private static short[] Tone(int rate, double frequency) => Enumerable.Range(0, rate)
        .Select(index => (short)(16384 * Math.Sin(2 * Math.PI * frequency * index / rate))).ToArray();

    /// <summary>按指定包长转换同一输入并显式结束滤波尾部。</summary>
    /// <param name="samples">源 PCM16。</param><param name="rate">源采样率。</param><param name="packetFrames">每包最大帧数。</param>
    /// <returns>完整 16kHz 转换输出。</returns>
    private static float[] Convert(short[] samples, int rate, int packetFrames)
    {
        var session = Guid.NewGuid();
        var converter = new StreamingPcmConverter(new(rate, 1), session, 1);
        var result = new List<float>();
        long sequence = 0;
        for (var start = 0; start < samples.Length; start += packetFrames)
        {
            using var packet = Packet(samples.AsSpan(start, Math.Min(packetFrames, samples.Length - start)), rate, session, ++sequence, start);
            result.AddRange(converter.Convert(packet).Samples);
        }
        result.AddRange(converter.Flush().Samples);
        return result.ToArray();
    }

    /// <summary>构造拥有独立池缓冲的单声道测试包。</summary>
    /// <param name="samples">源采样。</param><param name="rate">采样率。</param><param name="session">会话。</param>
    /// <param name="sequence">包序号。</param><param name="start">帧起点。</param><param name="flags">显式采集标志。</param>
    /// <param name="epoch">采集代次。</param><returns>由测试调用方释放的包。</returns>
    private static AudioPacket Packet(ReadOnlySpan<short> samples, int rate, Guid session, long sequence, long start,
        PacketFlags flags = PacketFlags.None, long epoch = 1)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(samples.Length * 2);
        for (var index = 0; index < samples.Length; index++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * 2, 2), samples[index]);
        return new(bytes, session, epoch, sequence, start, samples.Length, new(rate, 1), flags, 0, 0, 0);
    }

    /// <summary>计算能量用于带内与阻带幅度比较。</summary>
    /// <param name="samples">需测量的真实输出。</param><returns>均方根幅度。</returns>
    private static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var value in samples) sum += value * value;
        return Math.Sqrt(sum / samples.Length);
    }
}
