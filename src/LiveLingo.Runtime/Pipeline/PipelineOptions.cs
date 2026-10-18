namespace LiveLingo.Runtime.Pipeline;

/// <summary>集中验证音频保留、调度及超时预算，模型窗口不是语义定稿限制。</summary>
public sealed record PipelineOptions
{
    /// <summary>获取内存音频的最大采样数，默认两分钟。</summary>
    public int MemorySamples { get; init; } = 16000 * 120;
    /// <summary>获取临时磁盘音频的最大采样数，默认十分钟。</summary>
    public long SpillSamples { get; init; } = 16000L * 600;
    /// <summary>获取单次模型已测输入上限。</summary>
    public int MaximumWindowSamples { get; init; } = 16000 * 30;
    /// <summary>获取对齐恢复上限，默认 120 秒；零关闭，也可选择 60 秒。</summary>
    public int AlignmentRecoverySamples { get; init; } = 16000 * 120;
    /// <summary>获取是否启用扩窗已观察重复减少拒收实验，默认关闭。</summary>
    public bool PreserveObservedRepetitions { get; init; }
    /// <summary>获取包含显式恢复实验的最大租约长度。</summary>
    public int MaximumLeaseSamples => Math.Max(MaximumWindowSamples, AlignmentRecoverySamples);
    /// <summary>获取新增音频达到该长度后才考虑重识别。</summary>
    public int RefreshSamples { get; init; } = 16000 * 2;
    /// <summary>获取闭合待处理块的容量。</summary>
    public int ClosedQueueCapacity { get; init; } = 32;
    /// <summary>获取最大积压采样数，超过后必须显式报告压力。</summary>
    public long MaximumBacklogSamples { get; init; } = 16000L * 600;
    /// <summary>获取模型初始化的总限时。</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>获取单次模型调用的总限时。</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>获取正常关闭的最大等待时间，超时后回收自有进程。</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>拒绝无界或互相矛盾的配置，避免运行中隐式丢音频。</summary>
    /// <exception cref="ArgumentOutOfRangeException">配置超出支持范围。</exception>
    /// <exception cref="ArgumentException">保留容量不能覆盖窗口或积压。</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MemorySamples, 16000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MemorySamples, 16000 * 3600);
        ArgumentOutOfRangeException.ThrowIfNegative(SpillSamples);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SpillSamples, 16000L * 86400);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumWindowSamples, 16000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumWindowSamples, 16000 * 30);
        if (AlignmentRecoverySamples is not (0 or 960000 or 1920000))
            throw new ArgumentOutOfRangeException(nameof(AlignmentRecoverySamples));
        if (PreserveObservedRepetitions && AlignmentRecoverySamples == 0)
            throw new ArgumentException("重复保留检查需要显式启用扩窗实验。");
        ArgumentOutOfRangeException.ThrowIfLessThan(RefreshSamples, 1600);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RefreshSamples, MaximumWindowSamples);
        ArgumentOutOfRangeException.ThrowIfLessThan(ClosedQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ClosedQueueCapacity, 4096);
        if (MemorySamples < MaximumLeaseSamples || MaximumBacklogSamples < MaximumLeaseSamples ||
            MaximumBacklogSamples > MemorySamples + SpillSamples)
            throw new ArgumentException("保留容量、窗口与积压预算不一致。");
        foreach (var timeout in new[] { StartupTimeout, RequestTimeout, ShutdownTimeout })
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))
                throw new ArgumentOutOfRangeException(nameof(RequestTimeout), "超时必须有限且不超过十分钟。");
    }
}
