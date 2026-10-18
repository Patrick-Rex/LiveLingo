using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LiveLingo.Core.Audio;

namespace LiveLingo.Runtime.Pipeline;

/// <summary>表示无法继续保留全部未处理音频的显式限额失败。</summary>
public sealed class AudioCapacityException : Exception
{
    /// <summary>建立供报告和退出码识别的限额异常。</summary>
    /// <param name="message">已触及的容量说明。</param>
    public AudioCapacityException(string message) : base(message) { }
}

/// <summary>同时限制音频帧数和包数，生产者永不等待磁盘或 Channel 空间。</summary>
public sealed class AudioIngress : IDisposable
{
    private readonly Channel<AudioPacket> channel;
    private readonly long maxFrames;
    private long queuedFrames;
    private long peakFrames;
    private int completed;

    /// <summary>建立单写入者、单读取者的有界音频入口。</summary>
    /// <param name="policy">已确认的限额。</param>
    /// <param name="format">客户端格式。</param>
    public AudioIngress(CapturePolicy policy, AudioFormat format)
    {
        policy.Validate();
        maxFrames = checked((long)Math.Ceiling(policy.QueueAudioSeconds * format.SampleRate));
        channel = Channel.CreateBounded<AudioPacket>(new BoundedChannelOptions(policy.QueuePackets)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    /// <summary>获取当前等待消费的帧数。</summary>
    public long QueuedFrames => Interlocked.Read(ref queuedFrames);
    /// <summary>获取观察到的最大已接受帧数。</summary>
    public long PeakFrames => Interlocked.Read(ref peakFrames);
    /// <summary>获取实际帧数容量。</summary>
    public long MaxFrames => maxFrames;

    /// <summary>接管音频包；关闭或超限时释放包并明确结束入口。</summary>
    /// <param name="packet">唯一所有权的音频包。</param>
    /// <returns>是否已转交消费者；失败要求生产者停止。</returns>
    public bool TryPublish(AudioPacket packet)
    {
        var frames = Interlocked.Add(ref queuedFrames, packet.FrameCount);
        packet.EnqueuedTicks = Stopwatch.GetTimestamp();
        if (Volatile.Read(ref completed) == 0 && frames <= maxFrames && channel.Writer.TryWrite(packet))
        {
            long previous;
            do { previous = Interlocked.Read(ref peakFrames); }
            while (frames > previous && Interlocked.CompareExchange(ref peakFrames, frames, previous) != previous);
            return true;
        }
        Interlocked.Add(ref queuedFrames, -packet.FrameCount);
        packet.Dispose();
        Complete(new AudioCapacityException("音频入口容量耗尽或入口已关闭；当前代次必须终止并记录缺口。"));
        return false;
    }

    /// <summary>按队列顺序移交所有权；消费者必须逐包释放。</summary>
    /// <param name="cancellationToken">消费取消信号。</param>
    /// <returns>已从入口移除的包。</returns>
    public async IAsyncEnumerable<AudioPacket> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var packet in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Add(ref queuedFrames, -packet.FrameCount);
            yield return packet;
        }
    }

    /// <summary>结束写入，仍允许消费者排空已经接受的音频。</summary>
    /// <param name="error">显式失败原因；正常停止时为空。</param>
    public void Complete(Exception? error = null)
    {
        if (Interlocked.Exchange(ref completed, 1) == 0) channel.Writer.TryComplete(error);
    }

    /// <summary>在生产与消费均已退出后释放尚未消费的包。</summary>
    public void Dispose()
    {
        Complete();
        while (channel.Reader.TryRead(out var packet))
        {
            Interlocked.Add(ref queuedFrames, -packet.FrameCount);
            packet.Dispose();
        }
    }
}
