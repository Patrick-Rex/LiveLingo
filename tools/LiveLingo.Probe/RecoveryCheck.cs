using System.Text.Json;
using LiveLingo.Platform.Windows.Processes;
using LiveLingo.Probe.Recording;

namespace LiveLingo.Probe;

/// <summary>只重启独立配置的自有 Edge，验证原目标消失、多候选等待和唯一候选恢复。</summary>
internal static class RecoveryCheck
{
    /// <summary>执行有界的真实进程恢复，保留完整目标身份、操作时间和音频。</summary>
    /// <param name="edge">Edge 完整路径。</param><param name="fixtures">受控音频目录。</param>
    /// <param name="output">空的非 C 盘证据目录。</param><param name="stopWhileWaiting">在真实目标消失后停止，核对终止屏障。</param>
    /// <returns>采集与恢复检查退出码。</returns>
    public static async Task<int> RunAsync(string edge, string fixtures, string output, bool stopWhileWaiting = false)
    {
        var directory = OutputDirectory.Prepare(output);
        var page = Path.Combine(fixtures, "target.html");
        if (!File.Exists(page)) throw new ArgumentException("缺少受控 Edge 音频页。");
        using var original = new OwnedJob();
        using var candidateOne = new OwnedJob();
        using var candidateTwo = new OwnedJob();
        using var operations = new StreamWriter(Path.Combine(directory, "operations.jsonl"), false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        Start(edge, page, directory, operations, original, "original");
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var first = await ResolveAsync(directory, operations, startup.Token, original, "original").ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        var captureDirectory = Path.Combine(directory, "capture");
        using var captureStop = new CancellationTokenSource();
        var capture = Program.CaptureAsync(new(StringComparer.Ordinal)
        {
            ["pid"] = first.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["output"] = captureDirectory, ["duration-seconds"] = "26"
        }, captureStop.Token);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            original.Dispose();
            Record(operations, "original-stopped", first.ProcessId);
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            if (stopWhileWaiting)
            {
                Record(operations, "stop-requested-while-waiting", first.ProcessId);
                captureStop.Cancel();
                var stopCode = await capture.ConfigureAwait(false);
                var stopReport = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(captureDirectory, "report.json")), ProbeJsonContext.Default.CaptureRunReport)!;
                var diagnostics = File.ReadLines(Path.Combine(captureDirectory, "events.jsonl"))
                    .Select(line => JsonSerializer.Deserialize(line, ProbeJsonContext.Default.RunEvent)?.Diagnostic).Where(x => x is not null).ToArray();
                Record(operations, "stopped-while-waiting", first.ProcessId);
                return stopCode == 0 && stopReport.CleanupCompleted && stopReport.IncompleteRecoveries == 1 &&
                    diagnostics.Any(x => x!.Kind == "TargetLost") && !diagnostics.Any(x => x!.Kind == "TargetFound") ? 0 : 3;
            }
            // 将两个候选的创建安排在同一段同步代码，仍以实际事件判定是否成功覆盖竞争。
            Start(edge, page, directory, operations, candidateOne, "candidate-one");
            Start(edge, page, directory, operations, candidateTwo, "candidate-two");
            var one = await ResolveAsync(directory, operations, startup.Token, candidateOne, "candidate-one").ConfigureAwait(false);
            var two = await ResolveAsync(directory, operations, startup.Token, candidateTwo, "candidate-two").ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var secondStoppedAt = DateTimeOffset.UtcNow;
            candidateTwo.Dispose();
            Record(operations, "candidate-two-stopped", two.ProcessId);
            var code = await capture.ConfigureAwait(false);
            var report = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(captureDirectory, "report.json")), ProbeJsonContext.Default.CaptureRunReport)!;
            var events = File.ReadLines(Path.Combine(captureDirectory, "events.jsonl"))
                .Select(line => JsonSerializer.Deserialize(line, ProbeJsonContext.Default.RunEvent))
                .Where(item => item?.Diagnostic is not null).Select(item => item!.Diagnostic!).ToArray();
            var found = events.Where(item => item.Kind == "TargetFound").ToArray();
            Record(operations, "completed", one.ProcessId);
            return code != 0 ? code : report.RecoverySamples == 1 && report.Waves.Length >= 2 && found.Length == 1 &&
                found[0].Utc >= secondStoppedAt && found[0].Target is { } recovered && one.SameInstance(recovered) ? 0 : 3;
        }
        finally { captureStop.Cancel(); await capture.ConfigureAwait(false); }
    }

    /// <summary>创建带独立 profile 的受管实例，避免加入用户的现有浏览器。</summary>
    /// <param name="job">所有权容器。</param><param name="label">唯一目录名。</param><returns>受管身份。</returns>
    /// <param name="edge">Edge 可执行文件绝对路径。</param>
    /// <param name="page">固定夹具页面。</param>
    /// <param name="directory">本次独立证据目录。</param>
    /// <param name="operations">操作证据写入器。</param>
    private static OwnedProcess Start(string edge, string page, string directory, StreamWriter operations, OwnedJob job, string label)
    {
        var owned = job.Start(edge, ["--headless=new", "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0", "--autoplay-policy=no-user-gesture-required",
            "--user-data-dir=" + Path.Combine(directory, label), new Uri(page).AbsoluteUri], directory, label);
        Record(operations, label + "-started", owned.Identity.ProcessId);
        return owned;
    }

    /// <summary>记录真实浏览器根，启动器 PID 不作为音频目标。</summary>
    /// <param name="job">启动容器。</param><param name="label">私有目录。</param><returns>实际浏览器身份。</returns>
    /// <param name="directory">本次独立证据目录。</param>
    /// <param name="operations">操作证据写入器。</param>
    /// <param name="token">启动等待期限。</param>
    private static async Task<LiveLingo.Core.Sessions.TargetIdentity> ResolveAsync(string directory, StreamWriter operations, CancellationToken token, OwnedJob job, string label)
    {
        var identity = await FixtureDevTools.ResolveBrowserAsync(job, Path.Combine(directory, label), token).ConfigureAwait(false);
        Record(operations, label + "-browser", identity.ProcessId);
        return identity;
    }

    /// <summary>记录由本检查真实执行的操作，不据此伪造系统恢复事件。</summary>
    /// <param name="action">操作名称。</param><param name="pid">自有实例 PID。</param>
    /// <param name="operations">操作证据写入器。</param>
    private static void Record(StreamWriter operations, string action, int pid) => operations.WriteLine(JsonSerializer.Serialize(new { Utc = DateTimeOffset.UtcNow, Action = action, Pid = pid }));
}
