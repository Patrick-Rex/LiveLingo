using System.Diagnostics;
using System.Text.Json;
using LiveLingo.Core.Sessions;
using LiveLingo.Platform.Windows.Processes;
using LiveLingo.Probe.Recording;

namespace LiveLingo.Probe;

/// <summary>一条真实 Job 退出场景的可复核结果。</summary>
/// <param name="Scenario">场景。</param><param name="Parent">自有父身份。</param><param name="Grandchild">自有孙身份。</param>
/// <param name="ParentExited">父已退出。</param><param name="GrandchildExited">孙已退出。</param><param name="Error">失败。</param>
internal sealed record LifecycleCase(string Scenario, TargetIdentity? Parent, TargetIdentity? Grandchild,
    bool ParentExited, bool GrandchildExited, string? Error);

/// <summary>记录父级观察者看到的清理状态，强杀宿主无需自行写尾部。</summary>
/// <param name="SchemaVersion">版本。</param><param name="Utc">日期。</param><param name="Cases">实际场景。</param>
/// <param name="ExternalBefore">观察前外部应用根。</param><param name="ExternalStillAlive">观察后仍存在的相同实例。</param>
/// <param name="Passed">全部受控检查通过。</param>
internal sealed record LifecycleReport(int SchemaVersion, DateTimeOffset Utc, LifecycleCase[] Cases,
    TargetIdentity[] ExternalBefore, TargetIdentity[] ExternalStillAlive, bool Passed);

/// <summary>运行有界自有子树夹具，并保留用户外部进程的观察证据。</summary>
internal static class LifecycleRunner
{
    /// <summary>执行正常退出、强杀宿主和创建后立即关闭 Job 的实际检查。</summary>
    /// <param name="output">新的非 C 盘输出目录。</param><returns>契约退出码。</returns>
    public static async Task<int> RunAsync(string output)
    {
        var directory = OutputDirectory.Prepare(output);
        var fixture = Path.Combine(AppContext.BaseDirectory, "LiveLingo.ProcessFixture.exe");
        if (!File.Exists(fixture)) throw new FileNotFoundException("构建/发布输出缺少 ProcessFixture。", fixture);
        var external = WindowsTargets.Enumerate().Where(x => x.IsApplicationRoot && x.Identity is not null &&
            x.Name is "msedge.exe" or "ollama.exe").Select(x => x.Identity!).ToArray();
        var cases = new List<LifecycleCase>();
        foreach (var force in new[] { false, true })
            cases.Add(await RunCaseAsync(fixture, Path.Combine(directory, force ? "forced" : "normal"), force).ConfigureAwait(false));
        for (var index = 0; index < 5; index++)
        {
            OwnedProcess? owned = null;
            try
            {
                using (var job = new OwnedJob())
                    owned = job.Start(fixture, ["leaf", Path.Combine(directory, $"creation-{index}")], AppContext.BaseDirectory, "creation-race-leaf");
                var alive = IsSameInstanceAlive(owned.Identity);
                cases.Add(new($"creation-close-{index}", owned.Identity, null, !alive, true, alive ? "自有进程仍存活。" : null));
            }
            catch (Exception exception) { cases.Add(new($"creation-close-{index}", owned?.Identity, null, false, false, exception.Message)); }
        }
        try
        {
            using var invalid = new OwnedJob();
            invalid.Start(Path.Combine(directory, "does-not-exist.exe"), [], directory, "invalid-start");
            cases.Add(new("start-failure", null, null, false, false, "不存在的 EXE 居然启动。"));
        }
        catch (ArgumentException) { cases.Add(new("start-failure", null, null, true, true, null)); }
        var remainingExternal = external.Where(IsSameInstanceAlive).ToArray();
        var report = new LifecycleReport(1, DateTimeOffset.UtcNow, cases.ToArray(), external, remainingExternal,
            cases.All(item => item.Error is null && item.ParentExited && item.GrandchildExited) && remainingExternal.Length == external.Length);
        var json = JsonSerializer.SerializeToUtf8Bytes(report, ProbeJsonContext.Default.LifecycleReport);
        File.WriteAllBytes(Path.Combine(directory, "lifecycle-report.json"), json);
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(json));
        return report.Passed ? 0 : 5;
    }

    /// <summary>在外层 Job 中启动持有内层 Job 的夹具宿主并观察父孙退出。</summary>
    /// <param name="fixture">夹具路径。</param><param name="directory">独立场景目录。</param><param name="force">强杀宿主。</param>
    /// <returns>真实观察结果。</returns>
    private static async Task<LifecycleCase> RunCaseAsync(string fixture, string directory, bool force)
    {
        Directory.CreateDirectory(directory);
        TargetIdentity? parentIdentity = null, grandchildIdentity = null;
        try
        {
            using var outer = new OwnedJob();
            parentIdentity = outer.Start(fixture, ["branch", directory], AppContext.BaseDirectory, "fixture-host").Identity;
            using var parent = Process.GetProcessById(parentIdentity.ProcessId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var marker = Path.Combine(directory, "leaf.pid");
            while (!File.Exists(marker) || new FileInfo(marker).Length == 0) await Task.Delay(20, timeout.Token).ConfigureAwait(false);
            var pid = int.Parse(await File.ReadAllTextAsync(marker, timeout.Token).ConfigureAwait(false));
            grandchildIdentity = WindowsTargets.Query(pid).Identity ?? throw new InvalidOperationException("孙进程在观察前退出。");
            using var grandchild = Process.GetProcessById(pid);
            if (force) parent.Kill(); else await File.WriteAllTextAsync(Path.Combine(directory, "stop"), "normal", timeout.Token).ConfigureAwait(false);
            await parent.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await grandchild.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new(force ? "forced-host-exit" : "normal-host-exit", parentIdentity, grandchildIdentity, true, true, null);
        }
        catch (Exception exception)
        {
            return new(force ? "forced-host-exit" : "normal-host-exit", parentIdentity, grandchildIdentity,
                parentIdentity is not null && !IsSameInstanceAlive(parentIdentity),
                grandchildIdentity is not null && !IsSameInstanceAlive(grandchildIdentity), exception.Message);
        }
    }

    /// <summary>以完整实例身份检查存活，不能把 PID 复用报告成自有进程残留。</summary>
    /// <param name="identity">原实例。</param><returns>是否仍为同一实例。</returns>
    private static bool IsSameInstanceAlive(TargetIdentity identity) =>
        WindowsTargets.Query(identity.ProcessId).Identity is { } current && identity.SameInstance(current);
}
