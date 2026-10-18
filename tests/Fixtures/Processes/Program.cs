using LiveLingo.Platform.Windows.Processes;
using System.Runtime.InteropServices;

namespace LiveLingo.ProcessFixture;

/// <summary>用于 Job 生命周期及受控音源验收的明确自有进程。</summary>
internal static partial class Program
{
    /// <summary>创建有界自有子树或播放音源，默认存活 120 秒、可指定至多 7230 秒。</summary>
    /// <param name="args">角色、标记目录，以及可选的 WAV 路径和存活秒数。</param><returns>夹具状态。</returns>
    private static async Task<int> Main(string[] args)
    {
        if (args.Length is < 2 or > 4 || args[0] is not ("branch" or "leaf" or "tone" or "pipe") || !Path.IsPathFullyQualified(args[1])) return 2;
        var directory = args[1];
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, args[0] + ".pid");
        using var job = new OwnedJob();
        if (args[0] == "tone" && (args.Length < 3 || !PlaySoundW(args[2], 0, 0x00020000 | 1 | 8 | 2))) return 3;
        if (args[0] is "branch" or "pipe") job.Start(Environment.ProcessPath!, ["leaf", directory], AppContext.BaseDirectory, "fixture-grandchild");
        File.WriteAllText(marker, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (args[0] == "pipe")
        {
            await Console.Error.WriteLineAsync("pipe-ready");
            using var pipeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await Console.OpenStandardInput().CopyToAsync(Console.OpenStandardOutput(), pipeDeadline.Token);
            return 0;
        }
        var seconds = args.Length == 4 && int.TryParse(args[3], out var requested) ? Math.Clamp(requested, 1, 7230) : 120;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            while (!File.Exists(Path.Combine(directory, "stop"))) await Task.Delay(50, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        return 0;
    }

    /// <summary>以真实 Windows 渲染会话播放受控 WAV，进程退出时播放结束。</summary>
    /// <param name="sound">WAV 文件。</param><param name="module">保留。</param><param name="flags">文件、异步及循环标志。</param><returns>是否成功。</returns>
    [LibraryImport("winmm.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySoundW(string sound, nint module, uint flags);
}
