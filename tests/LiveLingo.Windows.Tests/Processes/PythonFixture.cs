namespace LiveLingo.Windows.Tests.Processes;

/// <summary>为纯标准库 IPC 测试寻找真实解释器，允许测试环境显式指定。</summary>
internal static class PythonFixture
{
    /// <summary>获取指定的解释器或 PATH 中的 Python，跳过 Windows 商店的空占位别名。</summary>
    internal static string Executable
    {
        get
        {
            if (Environment.GetEnvironmentVariable("LIVELINGO_TEST_PYTHON") is { Length: > 0 } configured) return configured;
            return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Where(Path.IsPathFullyQualified).Select(path => new FileInfo(Path.Combine(path, "python.exe")))
                .FirstOrDefault(file => file.Exists && file.Length > 0)?.FullName
                ?? throw new FileNotFoundException("请将 Python 3 加入 PATH 或设置 LIVELINGO_TEST_PYTHON。");
        }
    }
}
