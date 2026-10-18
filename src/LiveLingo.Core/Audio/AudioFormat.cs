namespace LiveLingo.Core.Audio;

/// <summary>描述已成功初始化的 PCM16 客户端格式，供队列和录音计算帧边界。</summary>
public sealed record AudioFormat
{
    /// <summary>建立 PCM16 格式并拒绝无法表示的采样率或声道数。</summary>
    /// <param name="sampleRate">每秒采样帧数。</param>
    /// <param name="channels">交错声道数量。</param>
    /// <exception cref="ArgumentOutOfRangeException">采样率或声道数超出有效范围。</exception>
    public AudioFormat(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (channels is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(channels));
        SampleRate = sampleRate;
        Channels = channels;
    }

    /// <summary>获取每秒采样帧数。</summary>
    public int SampleRate { get; }
    /// <summary>获取交错声道数量。</summary>
    public int Channels { get; }
    /// <summary>获取每帧字节数。</summary>
    public int BlockAlign => Channels * sizeof(short);
    /// <summary>获取样本位深。</summary>
    public int BitsPerSample => 16;
}
