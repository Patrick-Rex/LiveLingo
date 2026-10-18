using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LiveLingo.Platform.Windows.Processes;
using LiveLingo.Probe.Recording;

namespace LiveLingo.Probe;

/// <summary>让用户操作普通 Edge 页面，自动监管独立音源并记录同钟域的现场阶段。</summary>
internal static class FieldScopeCheck
{
    /// <summary>执行有界交互式内容核验；用户 Edge 始终是外部进程。</summary>
    /// <param name="pid">用户已选择的 Edge 根。</param><param name="fixtures">已生成的音频夹具。</param>
    /// <param name="output">新证据目录。</param><returns>采集及自有进程清理状态，不代替频谱判定。</returns>
    public static async Task<int> RunAsync(int pid, string fixtures, string output)
    {
        var target = WindowsTargets.Query(pid).Identity ?? throw new ArgumentException("无法核实所选 Edge。");
        if (!Path.GetFileName(target.ExecutablePath).Equals("msedge.exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("本检查仅用于所选 Edge 根。");
        foreach (var name in new[] { "target.html", "second.html", "independent.wav", "manifest.json" })
            if (!File.Exists(Path.Combine(fixtures, name))) throw new ArgumentException("缺少三音源夹具。");
        var directory = OutputDirectory.Prepare(output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var job = new OwnedJob();
        using var operations = new StreamWriter(Path.Combine(directory, "operations.jsonl"), false, new UTF8Encoding(false)) { AutoFlush = true };
        var external = WindowsTargets.Enumerate().Where(x => x.IsApplicationRoot && x.Identity is not null && x.Name is "msedge.exe" or "ollama.exe")
            .Select(x => x.Identity!).ToArray();
        var player = job.Start(Path.Combine(AppContext.BaseDirectory, "LiveLingo.ProcessFixture.exe"),
            ["tone", Path.Combine(directory, "independent-player"), Path.Combine(fixtures, "independent.wav"), "300"], AppContext.BaseDirectory, "field-independent");
        File.Copy(Path.Combine(fixtures, "manifest.json"), Path.Combine(directory, "fixture-manifest.json"));
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        var captures = Task.WhenAll(Capture(directory, stop.Token, pid, "capture"), Capture(directory, stop.Token, player.Identity.ProcessId, "independent-capture"));
        var exitCode = 3;
        string? error = null;
        try
        {
            Console.Error.WriteLine("请保留一个空白 Edge 标签页，暂停其他有声标签页；按提示完成操作后回车。总限时 5 分钟，Ctrl+C 退出。");
            await PhaseAsync(operations, target, stop.Token, "both-tabs", [440, 660], "打开 target.html 与 second.html，分别点击播放。两个音调同时持续播放后回车。").ConfigureAwait(false);
            await PhaseAsync(operations, target, stop.Token, "paused-tabs", [], "暂停两个音频页；不要暂停程序自动播放的独立音源。完成后回车。").ConfigureAwait(false);
            await PhaseAsync(operations, target, stop.Token, "first-only", [440], "只恢复 target.html 的播放，second.html 保持暂停。完成后回车。").ConfigureAwait(false);
            await PhaseAsync(operations, target, stop.Token, "both-resumed", [440, 660], "再恢复 second.html，让两个标签页同时播放。完成后回车。").ConfigureAwait(false);
            await PhaseAsync(operations, target, stop.Token, "audio-tabs-closed-root-retained", [], "关闭这两个音频标签页，保留空白标签页和 Edge 主进程。完成后回车。").ConfigureAwait(false);
            await PhaseAsync(operations, target, stop.Token, "replacement-tab", [660], "重新打开 second.html 并播放。完成后回车。").ConfigureAwait(false);
            stop.Cancel();
            exitCode = (await captures.ConfigureAwait(false)).Max();
        }
        catch (OperationCanceledException) { error = "现场步骤未全部完成。"; }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            stop.Cancel();
            try { await captures.ConfigureAwait(false); }
            catch (Exception exception) { error ??= exception.ToString(); }
            try { job.Dispose(); }
            catch (Exception exception) { exitCode = 5; error = exception.ToString(); }
            Console.CancelKeyPress -= cancel;
            var remaining = WindowsTargets.Query(player.Identity.ProcessId).Identity is { } current && player.Identity.SameInstance(current);
            var retained = external.Where(identity => WindowsTargets.Query(identity.ProcessId).Identity is { } actual && identity.SameInstance(actual)).ToArray();
            if (remaining || retained.Length != external.Length) exitCode = 5;
            File.WriteAllText(Path.Combine(directory, "scope-run.json"), JsonSerializer.Serialize(new { ExitCode = exitCode, Error = error,
                BrowserMode = "user-profile-manual-operations", StopwatchFrequency = Stopwatch.Frequency,
                OwnedObserved = new[] { player.Identity }, OwnedRemaining = remaining ? new[] { player.Identity } : [],
                ExternalBefore = external, ExternalRetained = retained }), new UTF8Encoding(false));
            Console.Error.WriteLine("现场证据目录: " + directory);
            Console.Error.WriteLine("请关闭本次手动打开的音频标签页；程序未关闭用户 Edge。");
        }
        return exitCode;
    }

    /// <summary>复用生产采集器，五分钟后即使无人操作也会停止。</summary>
    /// <param name="root">音源根。</param><param name="name">证据子目录。</param><returns>采集退出码。</returns>
    /// <param name="directory">本次证据根目录。</param>
    /// <param name="token">现场检查的停止信号。</param>
    private static Task<int> Capture(string directory, CancellationToken token, int root, string name) => Program.CaptureAsync(new(StringComparer.Ordinal)
    {
        ["pid"] = root.ToString(CultureInfo.InvariantCulture), ["output"] = Path.Combine(directory, name), ["duration-seconds"] = "300"
    }, token);

    /// <summary>按用户确认记录现场操作，并强制保留四秒稳定采样。</summary>
    /// <param name="name">阶段。</param><param name="frequencies">预期频率。</param><param name="instruction">具体操作。</param><returns>完成稳定窗口的任务。</returns>
    /// <param name="operations">现场操作证据写入器。</param>
    /// <param name="target">用户选择的完整进程身份。</param>
    /// <param name="token">现场检查的停止信号。</param>
    private static async Task PhaseAsync(StreamWriter operations, LiveLingo.Core.Sessions.TargetIdentity target, CancellationToken token, string name, int[] frequencies, string instruction)
    {
        Console.Error.WriteLine(instruction);
        if (await Console.In.ReadLineAsync(token).ConfigureAwait(false) is null) throw new IOException("现场输入已关闭，不能自动确认操作。");
        operations.WriteLine(JsonSerializer.Serialize(new { Utc = DateTimeOffset.UtcNow, Ticks = Stopwatch.GetTimestamp(), Phase = name,
            ExpectedHz = frequencies, IndependentHz = 997, Target = target, Confirmation = "operator-enter" }));
        Console.Error.WriteLine("保持当前播放状态 4 秒……");
        await Task.Delay(TimeSpan.FromSeconds(4), token).ConfigureAwait(false);
    }
}
