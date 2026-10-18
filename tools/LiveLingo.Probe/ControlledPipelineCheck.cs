using System.Diagnostics;
using System.Text.Json;
using LiveLingo.Core.Sessions;
using LiveLingo.Platform.Windows.Processes;
using LiveLingo.Probe.Recording;

namespace LiveLingo.Probe;

/// <summary>用独占 Edge 配置循环播放现有语料，验证真实采集到模型的路径。</summary>
internal static class ControlledPipelineCheck
{
    /// <summary>只管理此处新建的浏览器与临时 profile，保留用户浏览器和原始音频。</summary>
    /// <param name="repository">仓库。</param><param name="profile">ASR 配置。</param><param name="model">Qwen 目录。</param>
    /// <param name="ollama">Ollama EXE。</param><param name="edge">Edge EXE。</param><param name="input">现有日语 WAV。</param>
    /// <param name="output">独占证据目录。</param><param name="seconds">模型就绪后采集秒数。</param><returns>管道及夹具清理均成功时为零。</returns>
    public static async Task<int> RunAsync(string repository, string profile, string model, string ollama, string edge, string input, string output, int seconds)
    {
        if (!Path.IsPathFullyQualified(edge) || !Path.IsPathFullyQualified(input) || !File.Exists(input) || seconds is < 1 or > 600)
            throw new ArgumentException("受控采集需要本地绝对路径及 1–600 秒时长。");
        var directory = OutputDirectory.Prepare(output);
        var privateProfile = Path.GetFullPath(Path.Combine(directory, "owned-edge-profile"));
        if (!privateProfile.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(privateProfile))
            throw new IOException("fixture_profile_not_new_or_outside_output");
        var page = Path.Combine(directory, "audio.html");
        await File.WriteAllTextAsync(page, "<!doctype html><meta charset=\"utf-8\"><title>LiveLingo 受控语音采集</title><audio autoplay loop controls src=\"" +
            System.Net.WebUtility.HtmlEncode(new Uri(input).AbsoluteUri) + "\"></audio>\n").ConfigureAwait(false);
        using var job = new OwnedJob();
        var owned = new HashSet<TargetIdentity>();
        string? error = null; var result = 3;
        try
        {
            job.Start(edge, ["--headless=new", "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0",
                "--autoplay-policy=no-user-gesture-required", "--user-data-dir=" + privateProfile, new Uri(page).AbsoluteUri], directory, "pipeline-controlled-edge");
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var target = await FixtureDevTools.ResolveBrowserAsync(job, privateProfile, startup.Token).ConfigureAwait(false);
            owned.UnionWith(job.Members());
            result = await PipelineReplayCheck.RunAsync(repository, profile, model, ollama, "", Path.Combine(directory, "pipeline"), seconds,
                "translategemma:12b", target.ProcessId).ConfigureAwait(false);
        }
        catch (Exception exception) { error = exception.GetType().Name + ": " + exception.Message; }
        finally
        {
            try { owned.UnionWith(job.Members()); } catch (Exception exception) { error = (error ?? "") + "; audit:" + exception.GetType().Name; }
            try { job.Dispose(); } catch (Exception exception) { error = (error ?? "") + "; cleanup:" + exception.GetType().Name; }
        }
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5) && owned.Any(IsRunning)) await Task.Delay(20).ConfigureAwait(false);
        var remaining = owned.Where(IsRunning).ToArray();
        if (remaining.Length == 0 && Directory.Exists(privateProfile))
        {
            try
            {
                var info = new DirectoryInfo(privateProfile);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Any(x => x.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                    throw new IOException("fixture_profile_contains_link");
                // 精确限定为本调用创建、位于新证据目录内的 profile；不触及任何用户配置。
                Directory.Delete(privateProfile, recursive: true);
            }
            catch (Exception exception) { error = (error ?? "") + "; profile-cleanup:" + exception.GetType().Name; }
        }
        var passed = result == 0 && error is null && remaining.Length == 0 && !Directory.Exists(privateProfile);
        var report = new ControlledPipelineReport(passed, error, result, owned.ToArray(), remaining, Directory.Exists(privateProfile));
        await File.WriteAllTextAsync(Path.Combine(directory, "fixture-report.json"), JsonSerializer.Serialize(report, PipelineCheckJsonContext.Default.ControlledPipelineReport) + "\n").ConfigureAwait(false);
        Console.WriteLine($"ControlledPipelinePassed={passed}; OwnedRemaining={remaining.Length}; Output={directory}");
        return passed ? 0 : 3;
    }

    /// <summary>以完整实例身份核实存活，PID 复用不当成残留。</summary><param name="identity">本次自有实例。</param><returns>仍为同一实例时为 true。</returns>
    private static bool IsRunning(TargetIdentity identity) => WindowsTargets.Query(identity.ProcessId).Identity is { } actual && identity.SameInstance(actual);
}

/// <summary>受控语音浏览器与私有缓存退出证据，不自动评判翻译语义。</summary>
/// <param name="Passed">工程及清理通过。</param><param name="Error">明确失败。</param><param name="PipelineExit">实际流水线退出码。</param>
/// <param name="Owned">自有浏览器树身份。</param><param name="Remaining">仍存活实例。</param><param name="ProfileRemaining">私有配置是否残留。</param>
internal sealed record ControlledPipelineReport(bool Passed, string? Error, int PipelineExit, TargetIdentity[] Owned, TargetIdentity[] Remaining, bool ProfileRemaining);
