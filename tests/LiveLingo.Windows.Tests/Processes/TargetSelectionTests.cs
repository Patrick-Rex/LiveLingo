using LiveLingo.Core.Sessions;
using LiveLingo.Platform.Windows.Processes;

namespace LiveLingo.Windows.Tests.Processes;

/// <summary>验证多候选和旧实例保护，不以进程名或 PID 猜测应用根。</summary>
public sealed class TargetSelectionTests
{
    /// <summary>多个新根保持等待；排除会话开始前其他实例后才允许唯一重连。</summary>
    [Fact]
    public void MultipleCandidatesWaitAndExistingOtherInstanceIsExcluded()
    {
        var path = Path.GetFullPath("edge.exe");
        var original = new TargetIdentity(1, DateTimeOffset.UnixEpoch, path);
        var first = new TargetIdentity(2, DateTimeOffset.UnixEpoch.AddSeconds(1), path);
        var second = new TargetIdentity(3, DateTimeOffset.UnixEpoch.AddSeconds(2), path);
        TargetQuery[] candidates = [new(2, 0, "edge.exe", first, true, null), new(3, 0, "edge.exe", second, true, null)];
        Assert.Null(WindowsTargets.UniqueMatch(original, candidates, new HashSet<TargetIdentity>()));
        Assert.Same(second, WindowsTargets.UniqueMatch(original, candidates, new HashSet<TargetIdentity> { first }));
        Assert.Null(WindowsTargets.UniqueMatch(original, [candidates[0] with { IsApplicationRoot = false }], new HashSet<TargetIdentity>()));
    }

    /// <summary>真实当前进程查询包含路径和创建时间，零 PID 留下失败诊断。</summary>
    [Fact]
    public void QueryRecordsIdentityOrExplicitError()
    {
        var current = WindowsTargets.Query(Environment.ProcessId);
        Assert.NotNull(current.Identity);
        Assert.True(Path.IsPathFullyQualified(current.Identity.ExecutablePath));
        var inaccessible = WindowsTargets.Query(0);
        Assert.Null(inaccessible.Identity);
        Assert.NotEmpty(inaccessible.Error!);
    }
}
