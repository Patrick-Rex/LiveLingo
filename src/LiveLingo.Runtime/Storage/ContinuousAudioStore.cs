using LiveLingo.Core.Audio;

namespace LiveLingo.Runtime.Storage;

/// <summary>持有独立只读窗口副本和缓存 pin，释放后不得继续访问。</summary>
public sealed class AudioWindowLease : IDisposable
{
    private float[]? samples;
    private Action? release;

    /// <summary>由连续缓存移交独立窗口和对应释放回调。</summary>
    /// <param name="range">窗口来源范围。</param><param name="samples">仅属于本租约的采样副本。</param>
    /// <param name="release">幂等解除 pin 的回调。</param>
    internal AudioWindowLease(SampleRange range, float[] samples, Action release)
    {
        Range = range;
        this.samples = samples;
        this.release = release;
    }

    /// <summary>获取本租约的音频来源。</summary>
    public SampleRange Range { get; }
    /// <summary>获取生命周期内有效的只读采样。</summary>
    /// <exception cref="ObjectDisposedException">租约已经释放。</exception>
    public ReadOnlyMemory<float> Samples => samples ?? throw new ObjectDisposedException(nameof(AudioWindowLease));

    /// <summary>解除 pin 并丢弃窗口引用；不会重用消费者仍持有的数组。</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref samples, null);
        Interlocked.Exchange(ref release, null)?.Invoke();
    }
}

/// <summary>以固定大小块保存连续采样，显式溢写和 pin 阻止未处理音频被覆盖。</summary>
public sealed class ContinuousAudioStore : IDisposable
{
    private readonly Lock gate = new();
    private readonly List<Block> blocks = [];
    private readonly Dictionary<long, SampleRange> pins = [];
    private readonly IAudioSpillStore spill;
    private readonly int blockSamples;
    private readonly int memoryLimit;
    private readonly long spillLimit;
    private readonly int leaseLimit;
    private readonly int maximumLeaseSamples;
    private long nextLease;
    private long releaseBefore;
    private long leaseSamples;
    private int memorySamples;
    private long spilledSamples;
    private long end;
    private bool disposed;

    /// <summary>建立带内存、磁盘和在途窗口上限的连续缓存。</summary>
    /// <param name="spill">本缓存接管并在退出时释放的临时存储。</param><param name="memoryLimit">块内存采样容量。</param>
    /// <param name="spillLimit">磁盘采样容量。</param><param name="maximumLeaseSamples">每个窗口最大采样数。</param>
    /// <param name="blockSamples">每块容量，默认一秒，避免逐帧创建临时文件。</param><param name="leaseLimit">同时活跃窗口数。</param>
    /// <exception cref="ArgumentOutOfRangeException">上限非正或内存不足以容纳一块。</exception>
    public ContinuousAudioStore(IAudioSpillStore spill, int memoryLimit, long spillLimit,
        int maximumLeaseSamples, int blockSamples = 16000, int leaseLimit = 1)
    {
        ArgumentNullException.ThrowIfNull(spill);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSamples);
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryLimit, blockSamples);
        ArgumentOutOfRangeException.ThrowIfNegative(spillLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLeaseSamples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(leaseLimit);
        this.spill = spill;
        this.memoryLimit = memoryLimit;
        this.spillLimit = spillLimit;
        this.maximumLeaseSamples = maximumLeaseSamples;
        this.blockSamples = blockSamples;
        this.leaseLimit = leaseLimit;
    }

    /// <summary>获取下次追加必须使用的采样位置。</summary>
    public long End { get { lock (gate) return end; } }
    /// <summary>获取块内存实际分配采样数，不包括独立窗口副本。</summary>
    public int MemorySamples { get { lock (gate) return memorySamples; } }
    /// <summary>获取已登记磁盘采样数。</summary>
    public long SpilledSamples { get { lock (gate) return spilledSamples; } }
    /// <summary>获取有独立限额的窗口副本采样数。</summary>
    public long LeaseSamples { get { lock (gate) return leaseSamples; } }
    /// <summary>获取当前 pin 数量。</summary>
    public int PinCount { get { lock (gate) return pins.Count; } }

    /// <summary>追加一个不大于块容量的连续输入；失败前不改变音频终点。</summary>
    /// <param name="start">必须等于当前终点的位置。</param><param name="samples">完整有限数值采样。</param>
    /// <exception cref="ArgumentException">范围不连续或采样非法。</exception>
    /// <exception cref="InvalidOperationException">容量不足，调用方必须显式报告压力/缺口。</exception>
    /// <exception cref="IOException">溢写失败，原音频仍保留。</exception>
    public void Append(long start, ReadOnlySpan<float> samples)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (start != end || samples.Length == 0 || samples.Length > blockSamples) throw new ArgumentException("输入必须连续、非空且不超过块容量。");
            foreach (var value in samples) if (!float.IsFinite(value)) throw new ArgumentException("PCM 含非有限数值。");
            var newEnd = checked(end + samples.Length);
            var tail = blocks.LastOrDefault();
            if (tail?.Data is null || tail.Range.End != end || tail.Count + samples.Length > blockSamples)
            {
                MakeMemoryAvailable();
                tail = new(end, new float[blockSamples]);
                blocks.Add(tail);
                memorySamples += blockSamples;
            }
            samples.CopyTo(tail.Data!.AsSpan(tail.Count));
            tail.Count += samples.Length;
            end = newEnd;
        }
    }

    /// <summary>创建完整只读窗口并 pin 住范围，不允许读过释放空洞。</summary>
    /// <param name="range">必须完整保留的来源范围。</param><returns>需显式释放的独立副本。</returns>
    /// <exception cref="InvalidOperationException">范围缺失或窗口租约预算耗尽。</exception>
    public AudioWindowLease Acquire(SampleRange range)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (range.Length <= 0 || range.Length > maximumLeaseSamples || pins.Count >= leaseLimit || !ContainsCore(range))
                throw new InvalidOperationException("窗口缺失或租约预算耗尽。");
            var data = new float[checked((int)range.Length)];
            float[]? scratch = null;
            foreach (var block in blocks)
            {
                if (!block.Range.Overlaps(range)) continue;
                var source = block.Data;
                if (source is null)
                {
                    source = scratch ??= new float[blockSamples];
                    spill.Read(block.SpillKey!, source.AsSpan(0, block.Count));
                }
                var left = Math.Max(block.Start, range.Start);
                var right = Math.Min(block.Range.End, range.End);
                source.AsSpan(checked((int)(left - block.Start)), checked((int)(right - left)))
                    .CopyTo(data.AsSpan(checked((int)(left - range.Start))));
            }
            var id = checked(++nextLease);
            pins.Add(id, range);
            leaseSamples += range.Length;
            return new(range, data, () => ReleaseLease(id));
        }
    }

    /// <summary>判断音频是否完整保留，不能只看最早和最新位置。</summary>
    /// <param name="range">待重识别或重开的范围。</param><returns>不存在释放空洞时为 true。</returns>
    public bool Contains(SampleRange range) { lock (gate) return !disposed && ContainsCore(range); }

    /// <summary>移动已处理释放界线，仅回收完整位于界线前且无人 pin 的块。</summary>
    /// <param name="sample">单调且不大于当前终点的释放位置。</param>
    /// <exception cref="ArgumentOutOfRangeException">界线回退或超出已采集音频。</exception>
    public void ReleaseBefore(long sample)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sample < releaseBefore || sample > end) throw new ArgumentOutOfRangeException(nameof(sample));
            releaseBefore = sample;
            Reclaim();
        }
    }

    /// <summary>关闭临时存储并释放块；尚存窗口副本仍需其所有者释放。</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            blocks.Clear();
            memorySamples = 0;
            spilledSamples = 0;
            spill.Dispose();
        }
    }

    /// <summary>为新块腾出空间，不能以覆盖未处理块获得容量。</summary>
    /// <exception cref="InvalidOperationException">磁盘也达到安全上限。</exception>
    private void MakeMemoryAvailable()
    {
        Reclaim();
        while (memorySamples > memoryLimit - blockSamples)
        {
            var block = blocks.First(x => x.Data is not null);
            if (spilledSamples > spillLimit - block.Count) throw new InvalidOperationException("audio_storage_pressure");
            var key = spill.Write(block.Data.AsSpan(0, block.Count));
            block.SpillKey = key;
            block.Data = null;
            memorySamples -= blockSamples;
            spilledSamples += block.Count;
        }
    }

    /// <summary>解除一个窗口 pin，回收仍由下一次显式释放/追加负责，避免后台抛出 I/O 错误。</summary>
    /// <param name="id">本实例登记的租约。</param>
    private void ReleaseLease(long id)
    {
        lock (gate)
            if (pins.Remove(id, out var range)) leaseSamples -= range.Length;
    }

    /// <summary>回收允许释放的块，文件删除失败时保留该块登记。</summary>
    private void Reclaim()
    {
        for (var index = blocks.Count - 1; index >= 0; index--)
        {
            var block = blocks[index];
            if (block.Range.End > releaseBefore || pins.Values.Any(x => x.Overlaps(block.Range))) continue;
            if (block.SpillKey is not null) { spill.Delete(block.SpillKey); spilledSamples -= block.Count; }
            else memorySamples -= blockSamples;
            blocks.RemoveAt(index);
        }
    }

    /// <summary>逐段证明整个窗口存在，不跨越被释放的音频。</summary>
    /// <param name="range">请求范围。</param><returns>每个采样都有来源时为 true。</returns>
    private bool ContainsCore(SampleRange range)
    {
        if (range.Length <= 0 || range.End > end) return false;
        var position = range.Start;
        foreach (var block in blocks)
        {
            if (block.Range.End <= position) continue;
            if (block.Start > position) return false;
            position = block.Range.End;
            if (position >= range.End) return true;
        }
        return false;
    }

    /// <summary>表示内存或临时文件中的一个连续块。</summary>
    /// <param name="start">块首采样位置。</param><param name="data">固定容量内存。</param>
    private sealed class Block(long start, float[] data)
    {
        /// <summary>获取块起点。</summary>
        public long Start { get; } = start;
        /// <summary>获取或设置有效采样数。</summary>
        public int Count { get; set; }
        /// <summary>获取或设置尚未溢写的块内存。</summary>
        public float[]? Data { get; set; } = data;
        /// <summary>获取或设置本缓存拥有的临时存储键。</summary>
        public string? SpillKey { get; set; }
        /// <summary>获取已填充音频的真实范围。</summary>
        public SampleRange Range => new(Start, checked(Start + Count));
    }
}
