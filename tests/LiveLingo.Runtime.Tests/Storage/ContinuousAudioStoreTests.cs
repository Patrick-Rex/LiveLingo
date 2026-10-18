using LiveLingo.Core.Audio;
using LiveLingo.Runtime.Pipeline;
using LiveLingo.Runtime.Storage;

namespace LiveLingo.Runtime.Tests.Storage;

/// <summary>验证采样保留、租约、压力及真实临时文件的所有权边界。</summary>
public sealed class ContinuousAudioStoreTests
{
    /// <summary>跨内存与磁盘读取完整窗口，并在 pin 解除前保留原始范围。</summary>
    [Fact]
    public void PinnedWindowSurvivesSpillAndReleaseWatermark()
    {
        var directory = NewDirectory();
        using (var store = new ContinuousAudioStore(new FileAudioSpillStore(directory), 8, 16, 16, 4))
        {
            store.Append(0, [1, 2, 3, 4]);
            store.Append(4, [5, 6, 7, 8]);
            using var window = store.Acquire(new(0, 8));
            store.Append(8, [9, 10, 11, 12]);
            Assert.Equal(4, store.SpilledSamples);
            store.ReleaseBefore(8);
            Assert.True(store.Contains(new(0, 8)));
            Assert.Equal([1f, 2, 3, 4, 5, 6, 7, 8], window.Samples.ToArray());
            Assert.Throws<InvalidOperationException>(() => store.Acquire(new(8, 12)));
            window.Dispose();
            Assert.Equal(0, store.PinCount);
            store.ReleaseBefore(8);
            Assert.False(store.Contains(new(0, 8)));
            Assert.Equal(0, store.SpilledSamples);
            using var current = store.Acquire(new(8, 12));
            Assert.Equal([9f, 10, 11, 12], current.Samples.ToArray());
        }
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>磁盘和内存都满时不改变终点或覆盖尚未处理的内容。</summary>
    [Fact]
    public void CapacityFailureKeepsAllAcceptedSamples()
    {
        var directory = NewDirectory();
        using (var store = new ContinuousAudioStore(new FileAudioSpillStore(directory), 4, 4, 8, 4))
        {
            store.Append(0, [1, 2, 3, 4]);
            store.Append(4, [5, 6, 7, 8]);
            Assert.Throws<InvalidOperationException>(() => store.Append(8, [9, 10, 11, 12]));
            Assert.Equal(8, store.End);
            Assert.Equal(4, store.MemorySamples);
            Assert.Equal(4, store.SpilledSamples);
            using var full = store.Acquire(new(0, 8));
            Assert.Equal([1f, 2, 3, 4, 5, 6, 7, 8], full.Samples.ToArray());
        }
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>释放界线中存在旧 pin 时，读取不得跨越已经回收的中间空洞。</summary>
    [Fact]
    public void RetainedOldPinDoesNotHideReleasedGap()
    {
        using var store = new ContinuousAudioStore(new MemorySpill(), 12, 20, 16, 4, 2);
        store.Append(0, [1, 2, 3, 4]);
        using var old = store.Acquire(new(0, 4));
        store.Append(4, [5, 6, 7, 8]);
        store.ReleaseBefore(8);
        store.Append(8, [9, 10]);
        Assert.True(store.Contains(new(0, 4)));
        Assert.False(store.Contains(new(0, 10)));
        Assert.Throws<InvalidOperationException>(() => store.Acquire(new(0, 10)));
        using var current = store.Acquire(new(8, 10));
        Assert.Equal([9f, 10], current.Samples.ToArray());
    }

    /// <summary>溢写故障不提交新样本，已有窗口仍可读取。</summary>
    [Fact]
    public void DiskWriteFailurePreservesMemoryAndEnd()
    {
        using var store = new ContinuousAudioStore(new MemorySpill { FailWrite = true }, 4, 16, 8, 4);
        store.Append(0, [1, 2, 3, 4]);
        Assert.Throws<IOException>(() => store.Append(4, [5, 6, 7, 8]));
        Assert.Equal(4, store.End);
        Assert.Equal(4, store.MemorySamples);
        Assert.Equal(0, store.SpilledSamples);
        using var current = store.Acquire(new(0, 4));
        Assert.Equal([1f, 2, 3, 4], current.Samples.ToArray());
    }

    /// <summary>不连续、非有限或超预算窗口必须显式失败而非被修剪。</summary>
    [Fact]
    public void InvalidInputAndOversizedWindowAreRejected()
    {
        using var store = new ContinuousAudioStore(new MemorySpill(), 8, 8, 4, 4);
        Assert.Throws<ArgumentException>(() => store.Append(1, [1]));
        Assert.Throws<ArgumentException>(() => store.Append(0, [float.NaN]));
        store.Append(0, [0, float.Epsilon, 0, 0]);
        store.Append(4, [1, 1, 1, 1]);
        Assert.Throws<InvalidOperationException>(() => store.Acquire(new(0, 8)));
        using var quiet = store.Acquire(new(0, 4));
        Assert.Equal(float.Epsilon, quiet.Samples.Span[1]);
        store.Dispose();
        Assert.Equal(1, store.PinCount);
        Assert.Equal(float.Epsilon, quiet.Samples.Span[1]);
        quiet.Dispose();
        Assert.Equal(0, store.PinCount);
        Assert.Throws<ObjectDisposedException>(() => quiet.Samples);
    }

    /// <summary>临时存储不删除未知文件，清理失败必须报告。</summary>
    [Fact]
    public void UnknownFileIsPreservedDuringOwnedCleanup()
    {
        var directory = NewDirectory();
        var spill = new FileAudioSpillStore(directory);
        var stranger = Path.Combine(directory, "not-owned.txt");
        File.WriteAllText(stranger, "user data");
        try
        {
            _ = spill.Write([1, 2]);
            Assert.Throws<AggregateException>(() => spill.Dispose());
            Assert.Equal("user data", File.ReadAllText(stranger));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            File.Delete(stranger);
            Directory.Delete(directory, false);
        }
    }

    /// <summary>不一致的窗口、缓存及积压配置在启动前失败。</summary>
    [Fact]
    public void PipelineConfigurationIsBoundedAndInternallyConsistent()
    {
        var defaults = new PipelineOptions();
        defaults.Validate();
        Assert.Equal(1920000, defaults.AlignmentRecoverySamples);
        Assert.False(defaults.PreserveObservedRepetitions);
        Assert.Throws<ArgumentException>(() => new PipelineOptions { MemorySamples = 16000 }.Validate());
        Assert.Throws<ArgumentException>(() => new PipelineOptions { SpillSamples = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PipelineOptions { RequestTimeout = Timeout.InfiniteTimeSpan }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PipelineOptions { MaximumWindowSamples = 16000 * 31 }.Validate());
    }

    /// <summary>为本测试分配 D 盘独占目录名，不提前建立目录。</summary>
    /// <returns>仅此测试使用的绝对路径。</returns>
    private static string NewDirectory() => Path.Combine(@"D:\Soft Ware\LiveLingo\cache\tmp", "m3-spill-" + Guid.NewGuid().ToString("N"));

    /// <summary>用于故障注入的有限测试存储，不假装实际磁盘耗尽。</summary>
    private sealed class MemorySpill : IAudioSpillStore
    {
        private readonly Dictionary<string, float[]> data = [];
        /// <summary>获取或设置是否注入写入错误。</summary>
        public bool FailWrite { get; init; }
        /// <inheritdoc />
        public string Write(ReadOnlySpan<float> samples)
        {
            if (FailWrite) throw new IOException("injected disk full");
            var key = Guid.NewGuid().ToString("N");
            data.Add(key, samples.ToArray());
            return key;
        }
        /// <inheritdoc />
        public void Read(string key, Span<float> samples) => data[key].CopyTo(samples);
        /// <inheritdoc />
        public void Delete(string key) => data.Remove(key);
        /// <inheritdoc />
        public void Dispose() => data.Clear();
    }
}
