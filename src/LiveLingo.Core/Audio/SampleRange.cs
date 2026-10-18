namespace LiveLingo.Core.Audio;

/// <summary>表示同一采集代次内 16kHz 时间轴的半开采样范围。</summary>
public readonly record struct SampleRange
{
    /// <summary>建立无负数且终点不小于起点的范围。</summary>
    /// <param name="start">包含的首个采样位置。</param><param name="end">不包含的末端位置。</param>
    /// <exception cref="ArgumentOutOfRangeException">起点为负或终点小于起点。</exception>
    public SampleRange(long start, long end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        Start = start;
        End = end;
    }

    /// <summary>获取包含的起点。</summary>
    public long Start { get; }
    /// <summary>获取不包含的终点。</summary>
    public long End { get; }
    /// <summary>获取采样数量。</summary>
    public long Length => End - Start;
    /// <summary>判断另一范围是否完整包含于本范围。</summary>
    /// <param name="other">待验证的范围。</param><returns>两个边界均在本范围内时为 true。</returns>
    public bool Contains(SampleRange other) => Start <= other.Start && End >= other.End;
    /// <summary>判断两个半开范围是否共享实际采样。</summary>
    /// <param name="other">待比较的范围。</param><returns>存在非空交集时为 true。</returns>
    public bool Overlaps(SampleRange other) => Start < other.End && other.Start < End;
}
