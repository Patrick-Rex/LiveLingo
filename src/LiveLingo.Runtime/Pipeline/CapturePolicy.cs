namespace LiveLingo.Runtime.Pipeline;

/// <summary>保存可报告、可验证且有界的采集与恢复配置。</summary>
public sealed record CapturePolicy
{
    /// <summary>获取入口最多保留的音频秒数。</summary>
    public double QueueAudioSeconds { get; init; } = 2;
    /// <summary>获取入口包数上限，约束零帧包等异常输入。</summary>
    public int QueuePackets { get; init; } = 512;
    /// <summary>获取整场录音 PCM 字节上限，默认可容纳超过两小时基准格式。</summary>
    public long MaxOutputBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    /// <summary>获取激活超时。</summary>
    public TimeSpan ActivationTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>获取易失资源停止超时。</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>获取整个会话的累计重试预算。</summary>
    public int RetryBudget { get; init; } = 3;
    /// <summary>获取首次退避时间。</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>获取最大退避时间。</summary>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>获取目标实例巡检间隔。</summary>
    public TimeSpan TargetPollInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>获取受控消费者延迟；正常运行必须为零。</summary>
    public TimeSpan ConsumerDelay { get; init; } = TimeSpan.Zero;

    /// <summary>拒绝无限容量、不可取消的等待及无效配置。</summary>
    /// <exception cref="ArgumentOutOfRangeException">任一限额或时间不在有效范围。</exception>
    public void Validate()
    {
        if (!double.IsFinite(QueueAudioSeconds) || QueueAudioSeconds is <= 0 or > 60) throw new ArgumentOutOfRangeException(nameof(QueueAudioSeconds));
        if (QueuePackets is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(QueuePackets));
        if (MaxOutputBytes is < 4096 or > 3L * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaxOutputBytes));
        if (ActivationTimeout <= TimeSpan.Zero || ActivationTimeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(ActivationTimeout));
        if (StopTimeout <= TimeSpan.Zero || StopTimeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(StopTimeout));
        if (RetryBudget is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(RetryBudget));
        if (RetryBaseDelay <= TimeSpan.Zero || RetryMaxDelay < RetryBaseDelay || RetryMaxDelay > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(RetryMaxDelay));
        if (TargetPollInterval <= TimeSpan.Zero || TargetPollInterval > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(TargetPollInterval));
        if (ConsumerDelay < TimeSpan.Zero || ConsumerDelay > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(ConsumerDelay));
    }

    /// <summary>计算有限指数退避，实际等待由可控时钟负责。</summary>
    /// <param name="failureCount">从一开始的失败次数。</param>
    /// <returns>不超过配置上限的延迟。</returns>
    public TimeSpan RetryDelay(int failureCount) => TimeSpan.FromTicks((long)Math.Min(
        RetryMaxDelay.Ticks, RetryBaseDelay.Ticks * Math.Pow(2, Math.Clamp(failureCount - 1, 0, 20))));
}
