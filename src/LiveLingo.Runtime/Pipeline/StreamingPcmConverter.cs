using System.Buffers.Binary;
using LiveLingo.Core.Audio;

namespace LiveLingo.Runtime.Pipeline;

/// <summary>保存转换后连续范围和仅由调用者持有的单声道采样。</summary>
/// <param name="Range">16kHz 时间轴半开范围。</param><param name="Samples">未按能量裁剪的 float32 采样。</param>
public sealed record ResampledAudio(SampleRange Range, float[] Samples);

/// <summary>跨包保存滤波历史和整数相位的 PCM16 混音/抗混叠转换器。</summary>
public sealed class StreamingPcmConverter
{
    private const int OutputRate = 16000;
    private const int Radius = 32;
    private readonly AudioFormat format;
    private readonly Guid session;
    private readonly long epoch;
    private readonly List<float> history = [];
    private readonly Dictionary<int, double[]> kernels = [];
    private long baseFrame;
    private long inputFrames;
    private long outputSamples;
    private long sequence;
    private bool completed;

    /// <summary>为单一采集身份建立独立的滤波状态。</summary>
    /// <param name="format">已验证的 PCM16 格式。</param><param name="session">会话身份。</param><param name="epoch">采集代次。</param>
    /// <exception cref="ArgumentException">身份或客户端采样率不受支持。</exception>
    public StreamingPcmConverter(AudioFormat format, Guid session, long epoch)
    {
        if (format.SampleRate is not (16000 or 44100 or 48000) || session == Guid.Empty || epoch <= 0)
            throw new ArgumentException("转换器需要有效身份与 16k/44.1k/48k PCM16 格式。");
        this.format = format;
        this.session = session;
        this.epoch = epoch;
    }

    /// <summary>获取已经消费的原始采样帧数。</summary>
    public long InputFrames => inputFrames;
    /// <summary>获取已经产生的连续 16kHz 采样数。</summary>
    public long OutputSamples => outputSamples;

    /// <summary>验证连续性后混音并产生已有充分右侧滤波上下文的采样。</summary>
    /// <param name="packet">调用方仍负责释放的原始包。</param><returns>可能为空的连续转换结果。</returns>
    /// <exception cref="InvalidDataException">身份/格式变化、包序号或范围缺口。</exception>
    /// <exception cref="InvalidOperationException">转换器已经结束。</exception>
    public ResampledAudio Convert(AudioPacket packet)
    {
        if (completed) throw new InvalidOperationException("转换器已结束，不能跨代次复用。");
        if (packet.SessionId != session || packet.Epoch != epoch || packet.Format != format || packet.FrameStart != inputFrames ||
            packet.Sequence != sequence + 1 || packet.FrameCount > format.SampleRate || packet.Flags.HasFlag(PacketFlags.Discontinuity))
            throw new InvalidDataException("capture_discontinuity_or_format_change");
        var bytes = packet.Pcm.Span;
        for (var frame = 0; frame < packet.FrameCount; frame++)
        {
            double sum = 0;
            if (!packet.Flags.HasFlag(PacketFlags.Silent))
                for (var channel = 0; channel < format.Channels; channel++)
                    sum += BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(frame * format.BlockAlign + channel * 2, 2));
            history.Add((float)(sum / (32768.0 * format.Channels)));
        }
        inputFrames = checked(inputFrames + packet.FrameCount);
        sequence = packet.Sequence;
        return Produce(false);
    }

    /// <summary>在明确流结束时补滤波右侧零值，只输出真实输入时长覆盖的采样。</summary>
    /// <returns>最后不足一组滤波上下文的尾部，不增加虚构音频时长。</returns>
    public ResampledAudio Flush()
    {
        if (completed) return new(new(outputSamples, outputSamples), []);
        completed = true;
        var result = Produce(true);
        history.Clear();
        return result;
    }

    /// <summary>按整数采样比产生输出，包边界不会重置相位或滤波历史。</summary>
    /// <param name="flush">是否已确定不会再收到真实输入。</param><returns>连续的输出块。</returns>
    private ResampledAudio Produce(bool flush)
    {
        var start = outputSamples;
        var output = new List<float>();
        var totalOutput = checked((inputFrames * OutputRate + format.SampleRate - 1) / format.SampleRate);
        while (outputSamples < totalOutput)
        {
            var center = Math.DivRem(checked(outputSamples * format.SampleRate), OutputRate, out var fraction);
            if (!flush && format.SampleRate != OutputRate && center + Radius >= inputFrames) break;
            if (format.SampleRate == OutputRate) output.Add(ReadSample(center));
            else
            {
                var weights = Kernel((int)fraction);
                double value = 0;
                for (var tap = 0; tap < weights.Length; tap++) value += ReadSample(center - Radius + 1 + tap) * weights[tap];
                output.Add((float)value);
            }
            outputSamples++;
        }
        var nextCenter = checked(outputSamples * format.SampleRate) / OutputRate;
        var release = Math.Max(0, nextCenter - Radius + 1);
        var count = checked((int)Math.Min(history.Count, Math.Max(0, release - baseFrame)));
        if (count > 0) { history.RemoveRange(0, count); baseFrame += count; }
        return new(new(start, outputSamples), output.ToArray());
    }

    /// <summary>读取真实输入或流边缘的明确定义零填充。</summary>
    /// <param name="frame">源采样位置。</param><returns>混音后采样。</returns>
    private float ReadSample(long frame) => frame < 0 || frame >= inputFrames ? 0 : history[checked((int)(frame - baseFrame))];

    /// <summary>缓存该有理采样比所需的有限相位 Blackman 窗 sinc 滤波核。</summary>
    /// <param name="fraction">以 16000 为分母的源采样小数部分。</param><returns>归一化的固定 64 tap 系数。</returns>
    private double[] Kernel(int fraction)
    {
        if (kernels.TryGetValue(fraction, out var cached)) return cached;
        var coefficients = new double[Radius * 2];
        var cutoff = Math.Min(1.0, (double)OutputRate / format.SampleRate) * 0.94;
        double sum = 0;
        for (var tap = 0; tap < coefficients.Length; tap++)
        {
            var distance = tap - Radius + 1 - (double)fraction / OutputRate;
            var x = Math.PI * distance * cutoff;
            var sinc = Math.Abs(x) < 1e-12 ? 1 : Math.Sin(x) / x;
            var window = Math.Abs(distance) <= Radius ? 0.42 + 0.5 * Math.Cos(Math.PI * distance / Radius) + 0.08 * Math.Cos(2 * Math.PI * distance / Radius) : 0;
            coefficients[tap] = cutoff * sinc * window;
            sum += coefficients[tap];
        }
        for (var tap = 0; tap < coefficients.Length; tap++) coefficients[tap] /= sum;
        kernels.Add(fraction, coefficients);
        return coefficients;
    }
}
