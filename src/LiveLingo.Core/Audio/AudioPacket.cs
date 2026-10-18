using System.Buffers;

namespace LiveLingo.Core.Audio;

/// <summary>保留 WASAPI 原始标志，时间戳错误不抹去音频内容。</summary>
[Flags]
public enum PacketFlags
{
    /// <summary>有效普通音频。</summary>
    None = 0,
    /// <summary>设备报告采样不连续，未知损失帧数不推断为零。</summary>
    Discontinuity = 1,
    /// <summary>对应帧的内容为静音。</summary>
    Silent = 2,
    /// <summary>本包设备或 QPC 时间戳无效。</summary>
    TimestampError = 4
}

/// <summary>持有单一所有者的托管 PCM 副本；消费者释放后内存归还池。</summary>
public sealed class AudioPacket : IDisposable
{
    private byte[]? buffer;

    /// <summary>接管从共享池租用的缓冲；失败时所有权仍属于调用者。</summary>
    /// <param name="buffer">足以容纳帧数据的共享池缓冲。</param>
    /// <param name="sessionId">本次会话身份。</param>
    /// <param name="epoch">连续采集代次。</param>
    /// <param name="sequence">当前代次包序号。</param>
    /// <param name="frameStart">代次内半开区间起点。</param>
    /// <param name="frameCount">本包帧数。</param>
    /// <param name="format">实际初始化格式。</param>
    /// <param name="flags">原生包标志。</param>
    /// <param name="devicePosition">设备帧位置。</param>
    /// <param name="qpc100ns">100ns 单位的原生单调时间。</param>
    /// <param name="availableTicks">复制前的 Stopwatch 时间戳。</param>
    /// <exception cref="ArgumentOutOfRangeException">范围或身份代次无效。</exception>
    /// <exception cref="ArgumentException">缓冲长度不足。</exception>
    public AudioPacket(byte[] buffer, Guid sessionId, long epoch, long sequence, long frameStart,
        int frameCount, AudioFormat format, PacketFlags flags, ulong devicePosition,
        ulong qpc100ns, long availableTicks)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentOutOfRangeException.ThrowIfNegative(frameStart);
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(epoch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        ByteCount = checked(frameCount * format.BlockAlign);
        if (buffer.Length < ByteCount) throw new ArgumentException("PCM 缓冲不足。", nameof(buffer));
        _ = checked(frameStart + frameCount);
        this.buffer = buffer;
        SessionId = sessionId;
        Epoch = epoch;
        Sequence = sequence;
        FrameStart = frameStart;
        FrameCount = frameCount;
        Format = format;
        Flags = flags;
        DevicePositionFrames = devicePosition;
        QpcPosition100ns = qpc100ns;
        AvailableTicks = availableTicks;
    }

    /// <summary>获取会话身份。</summary>
    public Guid SessionId { get; }
    /// <summary>获取采集代次。</summary>
    public long Epoch { get; }
    /// <summary>获取包序号。</summary>
    public long Sequence { get; }
    /// <summary>获取半开区间起点。</summary>
    public long FrameStart { get; }
    /// <summary>获取帧数。</summary>
    public int FrameCount { get; }
    /// <summary>获取半开区间终点。</summary>
    public long FrameEnd => checked(FrameStart + FrameCount);
    /// <summary>获取有效 PCM 长度。</summary>
    public int ByteCount { get; }
    /// <summary>获取客户端格式。</summary>
    public AudioFormat Format { get; }
    /// <summary>获取包标志。</summary>
    public PacketFlags Flags { get; }
    /// <summary>获取设备帧位置。</summary>
    public ulong DevicePositionFrames { get; }
    /// <summary>获取原生 QPC 时间，单位为 100ns。</summary>
    public ulong QpcPosition100ns { get; }
    /// <summary>获取复制前单调时间。</summary>
    public long AvailableTicks { get; }
    /// <summary>获取入队时间，仅由入口设置。</summary>
    public long EnqueuedTicks { get; set; }
    /// <summary>获取时间戳能否参与精确延迟计算。</summary>
    public bool TimestampValid => !Flags.HasFlag(PacketFlags.TimestampError);
    /// <summary>获取当前所有权有效期内的只读 PCM。</summary>
    /// <exception cref="ObjectDisposedException">缓冲已释放。</exception>
    public ReadOnlyMemory<byte> Pcm => (buffer ?? throw new ObjectDisposedException(nameof(AudioPacket))).AsMemory(0, ByteCount);

    /// <summary>幂等归还共享池内存；释放后不得继续使用 PCM。</summary>
    public void Dispose()
    {
        var rented = Interlocked.Exchange(ref buffer, null);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
    }
}

/// <summary>描述真实中断，不能由未知损失量伪造连续时间轴。</summary>
/// <param name="SessionId">会话身份。</param>
/// <param name="Epoch">中断的采集代次。</param>
/// <param name="Reason">可诊断原因。</param>
/// <param name="KnownFrameBoundary">最后已知半开边界。</param>
/// <param name="LostFrames">已知损失帧数；无法确定时为空。</param>
/// <param name="MonotonicTicks">观察到中断的单调时间。</param>
public sealed record AudioGap(Guid SessionId, long Epoch, string Reason, long? KnownFrameBoundary,
    long? LostFrames, long MonotonicTicks);
