namespace LiveLingo.Probe.Recording;

/// <summary>验证显式录音目录，不允许回退 C 盘或覆盖现有运行。</summary>
internal static class OutputDirectory
{
    /// <summary>核实路径、非 C 盘、无链接祖先、空目录及最小剩余空间。</summary>
    /// <param name="path">用户提供的绝对目录。</param>
    /// <returns>已创建的独立运行目录。</returns>
    /// <exception cref="ArgumentException">路径不安全或不独立。</exception>
    /// <exception cref="IOException">空间不足或目录不可写。</exception>
    public static string Prepare(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("输出目录必须为绝对路径。");
        var absolute = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(absolute)!;
        if (root.StartsWith("C:", StringComparison.OrdinalIgnoreCase) || root.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("录音必须位于本机非 C 盘。");
        if (absolute.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("不能使用磁盘根目录。");
        for (var parent = new DirectoryInfo(absolute); parent is not null; parent = parent.Parent)
        {
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("输出路径不能经过符号链接或目录联接。");
        }
        if (Directory.Exists(absolute) && Directory.EnumerateFileSystemEntries(absolute).Any()) throw new ArgumentException("拒绝覆盖非空运行目录。");
        if (new DriveInfo(root).AvailableFreeSpace < 256L * 1024 * 1024) throw new IOException("输出卷可用空间低于 256MiB。");
        Directory.CreateDirectory(absolute);
        using (new FileStream(Path.Combine(absolute, ".run-lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        return absolute;
    }
}
