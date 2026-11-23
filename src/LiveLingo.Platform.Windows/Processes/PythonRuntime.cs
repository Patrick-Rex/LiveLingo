namespace LiveLingo.Platform.Windows.Processes;

/// <summary>在启动前识别会通过商店包激活、无法由当前 Windows Job 回收的 Python。</summary>
public static class PythonRuntime
{
    /// <summary>校验解释器以及虚拟环境记录的基础 Python 能由当前进程直接启动。</summary>
    /// <param name="executable">解释器的绝对路径。</param>
    /// <exception cref="InvalidDataException">基础解释器位于 WindowsApps，或环境配置超出读取上限。</exception>
    public static void ValidateForOwnedProcess(string executable)
    {
        RejectStorePath(executable);
        var directory = Path.GetDirectoryName(executable)!;
        var config = Path.Combine(directory, "pyvenv.cfg");
        if (!File.Exists(config) && Path.GetDirectoryName(directory) is { } parent) config = Path.Combine(parent, "pyvenv.cfg");
        if (!File.Exists(config)) return;
        if (new FileInfo(config).Length > 64 * 1024) throw new InvalidDataException("Python 隔离环境配置过大。");
        foreach (var line in File.ReadLines(config))
        {
            var separator = line.IndexOf('=');
            if (separator > 0 && line[..separator].Trim().Equals("home", StringComparison.OrdinalIgnoreCase))
            {
                RejectStorePath(Path.GetFullPath(line[(separator + 1)..].Trim(), Path.GetDirectoryName(config)!));
                return;
            }
        }
    }

    /// <summary>排除通过 Microsoft Store 包激活的解释器和应用执行别名。</summary>
    /// <param name="path">解释器文件或基础解释器目录。</param>
    private static void RejectStorePath(string path)
    {
        var location = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        var localAliases = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        var storePackages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        if (location.StartsWith(localAliases + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            location.StartsWith(storePackages + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Microsoft Store Python 的子进程无法随会话可靠退出。请在高级路径中选用新环境并点击安装，或选择基于 python.org 的隔离解释器。");
    }
}
