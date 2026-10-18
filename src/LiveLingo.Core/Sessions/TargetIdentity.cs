namespace LiveLingo.Core.Sessions;

/// <summary>以 PID、创建时间及可执行路径辨识实例，防止 PID 复用误绑。</summary>
public sealed record TargetIdentity
{
    /// <summary>建立已核实的应用实例身份。</summary>
    /// <param name="processId">正进程编号。</param>
    /// <param name="creationTimeUtc">进程实际 UTC 创建时间。</param>
    /// <param name="executablePath">可核实的绝对可执行路径。</param>
    /// <exception cref="ArgumentException">路径不是绝对路径。</exception>
    /// <exception cref="ArgumentOutOfRangeException">PID 无效。</exception>
    public TargetIdentity(int processId, DateTimeOffset creationTimeUtc, string executablePath)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        if (!Path.IsPathFullyQualified(executablePath)) throw new ArgumentException("需要绝对可执行路径。", nameof(executablePath));
        ProcessId = processId;
        CreationTimeUtc = creationTimeUtc.ToUniversalTime();
        ExecutablePath = Path.GetFullPath(executablePath);
    }

    /// <summary>获取进程编号。</summary>
    public int ProcessId { get; }
    /// <summary>获取创建时间。</summary>
    public DateTimeOffset CreationTimeUtc { get; }
    /// <summary>获取可执行路径身份。</summary>
    public string ExecutablePath { get; }

    /// <summary>比较可执行身份，用于唯一新实例重连。</summary>
    /// <param name="other">候选身份。</param>
    /// <returns>路径身份是否相同。</returns>
    public bool SameApplication(TargetIdentity other) =>
        StringComparer.OrdinalIgnoreCase.Equals(ExecutablePath, other.ExecutablePath);

    /// <summary>同时比较三项身份，用于检查所选实例是否仍然存在。</summary>
    /// <param name="other">当前查询到的身份。</param>
    /// <returns>是否为同一个进程实例。</returns>
    public bool SameInstance(TargetIdentity other) => ProcessId == other.ProcessId &&
        CreationTimeUtc == other.CreationTimeUtc && SameApplication(other);
}
