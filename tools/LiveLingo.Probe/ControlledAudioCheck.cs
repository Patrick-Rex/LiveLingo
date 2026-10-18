using LiveLingo.Platform.Windows.Processes;
using LiveLingo.Probe.Recording;
using System.Text.Json;

namespace LiveLingo.Probe;

/// <summary>在独立 Edge 配置与独立播放器中执行低音量的受控音调隔离采集。</summary>
internal static class ControlledAudioCheck
{
    /// <summary>启动明确自有的两个音源，捕获 Edge 后清理全部测试进程。</summary>
    /// <param name="edge">Edge 可执行路径。</param><param name="fixtures">已生成并记录指纹的夹具目录。</param>
    /// <param name="output">独立输出目录。</param><param name="durationSeconds">有界录制秒数。</param>
    /// <returns>采集退出码，内容须另做频谱核验。</returns>
    public static async Task<int> RunAsync(string edge, string fixtures, string output, int durationSeconds = 10)
    {
        if (durationSeconds is < 1 or > 7200) throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        var directory = OutputDirectory.Prepare(output);
        var page = Path.Combine(fixtures, "target.html");
        var independent = Path.Combine(fixtures, "independent.wav");
        if (!File.Exists(page) || !File.Exists(independent)) throw new ArgumentException("请先运行音频夹具生成脚本。");
        using var job = new OwnedJob();
        job.Start(edge, ["--headless=new", "--no-first-run", "--no-default-browser-check", "--remote-debugging-port=0", "--autoplay-policy=no-user-gesture-required",
            "--user-data-dir=" + Path.Combine(directory, "edge-profile"), new Uri(page).AbsoluteUri], directory, "controlled-edge");
        var other = job.Start(Path.Combine(AppContext.BaseDirectory, "LiveLingo.ProcessFixture.exe"),
            ["tone", Path.Combine(directory, "independent-player"), independent, (durationSeconds + 30).ToString(System.Globalization.CultureInfo.InvariantCulture)], AppContext.BaseDirectory, "independent-tone");
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var target = await FixtureDevTools.ResolveBrowserAsync(job, Path.Combine(directory, "edge-profile"), startup.Token).ConfigureAwait(false);
        var processes = WindowsTargets.Enumerate().ToArray();
        File.WriteAllBytes(Path.Combine(directory, "processes.json"), JsonSerializer.SerializeToUtf8Bytes(processes, ProbeJsonContext.Default.TargetQueryArray));
        File.Copy(Path.Combine(fixtures, "manifest.json"), Path.Combine(directory, "fixture-manifest.json"));
        var results = await Task.WhenAll(Program.CaptureAsync(new(StringComparer.Ordinal)
        {
            ["pid"] = target.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["output"] = Path.Combine(directory, "capture"), ["duration-seconds"] = durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }), Program.CaptureAsync(new(StringComparer.Ordinal)
        {
            ["pid"] = other.Identity.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["output"] = Path.Combine(directory, "independent-capture"), ["duration-seconds"] = durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        })).ConfigureAwait(false);
        return results.Max();
    }
}
