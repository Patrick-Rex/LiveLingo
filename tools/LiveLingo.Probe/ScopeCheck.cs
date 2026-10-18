using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LiveLingo.Core.Sessions;
using LiveLingo.Platform.Windows.Processes;
using LiveLingo.Probe.Recording;

namespace LiveLingo.Probe;

/// <summary>验证独立 Edge 的多标签页、暂停和新页面音频仍属于选定进程树。</summary>
internal static class ScopeCheck
{
    /// <summary>操作私有浏览器并同时录制独立音源，保存可按单调时钟核对的阶段真值。</summary>
    /// <param name="edge">Edge 完整路径。</param><param name="fixtures">包含三种音调的夹具目录。</param>
    /// <param name="output">新的非 C 盘目录。</param><returns>运行和清理状态；频谱另行核验。</returns>
    public static async Task<int> RunAsync(string edge, string fixtures, string output)
    {
        foreach (var file in new[] { "target.html", "second.html", "independent.wav", "manifest.json" })
            if (!File.Exists(Path.Combine(fixtures, file))) throw new ArgumentException("请先生成包含第二标签页的音频夹具。");
        var directory = OutputDirectory.Prepare(output);
        var external = WindowsTargets.Enumerate().Where(x => x.IsApplicationRoot && x.Identity is not null && x.Name is "msedge.exe" or "ollama.exe")
            .Select(x => x.Identity!).ToArray();
        var observed = new HashSet<TargetIdentity>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        using var captureStop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var job = new OwnedJob();
        using var operations = new StreamWriter(Path.Combine(directory, "operations.jsonl"), false, new UTF8Encoding(false)) { AutoFlush = true };
        var profile = Path.Combine(directory, "edge-profile");
        var target = job.Start(edge, ["--headless=new", "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0",
            "--autoplay-policy=no-user-gesture-required", "--user-data-dir=" + profile, "about:blank"], directory, "scope-edge");
        var independent = job.Start(Path.Combine(AppContext.BaseDirectory, "LiveLingo.ProcessFixture.exe"),
            ["tone", Path.Combine(directory, "independent-player"), Path.Combine(fixtures, "independent.wav"), "70"], AppContext.BaseDirectory, "scope-independent");
        observed.Add(target.Identity);
        observed.Add(independent.Identity);
        Task<int[]>? captures = null;
        var exitCode = 3;
        string? failure = null;
        try
        {
            using var browser = await FixtureDevTools.ConnectAsync(profile, deadline.Token).ConfigureAwait(false);
            var first = await OpenAsync(browser, fixtures, deadline.Token, "target.html").ConfigureAwait(false);
            var second = await OpenAsync(browser, fixtures, deadline.Token, "second.html").ConfigureAwait(false);
            await AudioAsync(browser, deadline.Token, first.Session, true).ConfigureAwait(false);
            await AudioAsync(browser, deadline.Token, second.Session, true).ConfigureAwait(false);
            var browserProcesses = await browser.SendAsync("SystemInfo.getProcessInfo", new { }, deadline.Token).ConfigureAwait(false);
            File.WriteAllText(Path.Combine(directory, "browser-processes.json"), browserProcesses.ToString(), new UTF8Encoding(false));
            var browserPid = browserProcesses.GetProperty("processInfo").EnumerateArray().Single(x => x.GetProperty("type").GetString() == "browser").GetProperty("id").GetInt32();
            var browserIdentity = WindowsTargets.Query(browserPid).Identity ?? throw new IOException("无法核实实际浏览器主进程。");
            if (!job.Contains(browserIdentity)) throw new IOException("调试端点不是本次 Job 中的浏览器。");
            target = new(browserIdentity, "actual-scope-browser");
            observed.Add(browserIdentity);
            File.Copy(Path.Combine(fixtures, "manifest.json"), Path.Combine(directory, "fixture-manifest.json"));
            captures = Task.WhenAll(Capture(directory, captureStop.Token, target.Identity.ProcessId, "capture"), Capture(directory, captureStop.Token, independent.Identity.ProcessId, "independent-capture"));
            await Task.Delay(1000, deadline.Token).ConfigureAwait(false);
            await PhaseAsync(job, observed, target.Identity, operations, deadline.Token, "both-tabs", [440, 660]).ConfigureAwait(false);
            await AudioAsync(browser, deadline.Token, first.Session, false).ConfigureAwait(false);
            await AudioAsync(browser, deadline.Token, second.Session, false).ConfigureAwait(false);
            await PhaseAsync(job, observed, target.Identity, operations, deadline.Token, "paused-tabs", []).ConfigureAwait(false);
            await AudioAsync(browser, deadline.Token, first.Session, true).ConfigureAwait(false);
            await PhaseAsync(job, observed, target.Identity, operations, deadline.Token, "first-only", [440]).ConfigureAwait(false);
            await AudioAsync(browser, deadline.Token, second.Session, true).ConfigureAwait(false);
            await PhaseAsync(job, observed, target.Identity, operations, deadline.Token, "both-resumed", [440, 660]).ConfigureAwait(false);
            await browser.SendAsync("Target.closeTarget", new { targetId = first.Target }, deadline.Token).ConfigureAwait(false);
            await browser.SendAsync("Target.closeTarget", new { targetId = second.Target }, deadline.Token).ConfigureAwait(false);
            await PhaseAsync(job, observed, target.Identity, operations, deadline.Token, "audio-tabs-closed-root-retained", []).ConfigureAwait(false);
            var replacement = await OpenAsync(browser, fixtures, deadline.Token, "second.html").ConfigureAwait(false);
            await AudioAsync(browser, deadline.Token, replacement.Session, true).ConfigureAwait(false);
            await PhaseAsync(job, observed, target.Identity, operations, deadline.Token, "replacement-tab", [660]).ConfigureAwait(false);
            captureStop.Cancel();
            exitCode = (await captures.ConfigureAwait(false)).Max();
        }
        catch (Exception exception) { failure = exception.ToString(); }
        finally
        {
            captureStop.Cancel();
            if (captures is not null)
            {
                try { await captures.ConfigureAwait(false); }
                catch (Exception exception) { failure ??= exception.ToString(); }
            }
            try { job.Dispose(); }
            catch (Exception exception) { exitCode = 5; failure = exception.ToString(); }
            var cleanupTimer = Stopwatch.StartNew();
            while (observed.Any(SameAlive) && cleanupTimer.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(50).ConfigureAwait(false);
            var remaining = observed.Where(SameAlive).ToArray();
            var retained = external.Where(SameAlive).ToArray();
            if (remaining.Length != 0 || retained.Length != external.Length) exitCode = 5;
            if (failure is not null && exitCode == 0) exitCode = 3;
            File.WriteAllText(Path.Combine(directory, "scope-run.json"), JsonSerializer.Serialize(new { ExitCode = exitCode, Error = failure,
                BrowserMode = "headless-private-profile", StopwatchFrequency = Stopwatch.Frequency, OwnedObserved = observed,
                OwnedRemaining = remaining, ExternalBefore = external, ExternalRetained = retained }), new UTF8Encoding(false));
        }
        return exitCode;
    }

    /// <summary>以完整身份检查存活，避免 PID 复用误判。</summary>
    /// <param name="identity">已核实实例。</param><returns>原实例是否仍存活。</returns>
    private static bool SameAlive(TargetIdentity identity) => WindowsTargets.Query(identity.ProcessId).Identity is { } current && identity.SameInstance(current);

    /// <summary>创建背景标签页并绑定仅供夹具使用的协议会话。</summary>
    /// <param name="file">固定本地音频页。</param><returns>目标与会话编号。</returns>
    /// <param name="browser">本次私有浏览器调试连接。</param>
    /// <param name="fixtures">固定页面目录。</param>
    /// <param name="token">检查截止信号。</param>
    private static async Task<(string Target, string Session)> OpenAsync(FixtureDevTools browser, string fixtures, CancellationToken token, string file)
    {
        var created = await browser.SendAsync("Target.createTarget", new { url = new Uri(Path.Combine(fixtures, file)).AbsoluteUri }, token).ConfigureAwait(false);
        var id = created.GetProperty("targetId").GetString()!;
        var attached = await browser.SendAsync("Target.attachToTarget", new { targetId = id, flatten = true }, token).ConfigureAwait(false);
        return (id, attached.GetProperty("sessionId").GetString()!);
    }

    /// <summary>等待固定音频页就绪，实际调用播放或暂停并检查返回状态。</summary>
    /// <param name="session">标签页会话。</param><param name="play">是否播放。</param><returns>操作完成任务。</returns>
    /// <param name="browser">本次私有浏览器调试连接。</param>
    /// <param name="token">检查截止信号。</param>
    private static async Task AudioAsync(FixtureDevTools browser, CancellationToken token, string session, bool play)
    {
        if (play) await browser.SendAsync("Page.bringToFront", new { }, token, session).ConfigureAwait(false);
        JsonElement lastReply = default;
        var expression = "(async()=>{const a=document.querySelector('audio');if(!a)return null;" +
            (play ? "a.play().catch(e=>window.playError=e.message);" : "a.pause();") + "return {paused:a.paused,readyState:a.readyState,currentTime:a.currentTime,error:window.playError};})()";
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var reply = await browser.SendAsync("Runtime.evaluate", new { expression, awaitPromise = true, returnByValue = true }, token, session).ConfigureAwait(false);
            lastReply = reply;
            if (reply.TryGetProperty("exceptionDetails", out var exception)) throw new IOException(exception.ToString());
            if (reply.GetProperty("result").TryGetProperty("value", out var state) && state.ValueKind == JsonValueKind.Object &&
                state.GetProperty("paused").GetBoolean() == !play && (!play || state.GetProperty("readyState").GetInt32() >= 2)) return;
            await Task.Delay(100, token).ConfigureAwait(false);
        }
        throw new TimeoutException("标签页音频未达到预期状态: " + lastReply);
    }

    /// <summary>保存完整实例关系与阶段真值，给变更前后的声学核验留出稳定窗口。</summary>
    /// <param name="name">阶段名。</param><param name="frequencies">预期目标频率。</param><returns>四秒稳定窗口。</returns>
    /// <param name="job">本次自有进程容器。</param>
    /// <param name="observed">累计观察到的完整进程身份。</param>
    /// <param name="target">本阶段选定的浏览器根身份。</param>
    /// <param name="operations">操作证据写入器。</param>
    /// <param name="token">检查截止信号。</param>
    private static async Task PhaseAsync(OwnedJob job, HashSet<TargetIdentity> observed, TargetIdentity target, StreamWriter operations, CancellationToken token, string name, int[] frequencies)
    {
        var all = WindowsTargets.Enumerate();
        var tree = all.Where(x => x.Identity is not null && job.Contains(x.Identity)).ToArray();
        foreach (var item in tree) observed.Add(item.Identity!);
        if (!SameAlive(target)) throw new IOException("选定根进程身份不符: " + JsonSerializer.Serialize(new { Expected = target, Actual = WindowsTargets.Query(target.ProcessId) }));
        operations.WriteLine(JsonSerializer.Serialize(new { Utc = DateTimeOffset.UtcNow, Ticks = Stopwatch.GetTimestamp(), Phase = name,
            ExpectedHz = frequencies, IndependentHz = 997, Target = target, Processes = tree }));
        await Task.Delay(TimeSpan.FromSeconds(4), token).ConfigureAwait(false);
    }

    /// <summary>使用生产采集器录制一个音源，统一接受夹具停止信号。</summary>
    /// <param name="pid">已核实 PID。</param><param name="name">输出子目录。</param><returns>采集退出码。</returns>
    /// <param name="directory">本次证据根目录。</param>
    /// <param name="token">音源采集停止信号。</param>
    private static Task<int> Capture(string directory, CancellationToken token, int pid, string name) => Program.CaptureAsync(new(StringComparer.Ordinal)
    {
        ["pid"] = pid.ToString(CultureInfo.InvariantCulture), ["output"] = Path.Combine(directory, name), ["duration-seconds"] = "60"
    }, token);
}
