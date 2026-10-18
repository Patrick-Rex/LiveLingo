using System.Diagnostics;
using System.Runtime.InteropServices;
using LiveLingo.Platform.Windows.Processes;

namespace LiveLingo.Windows.Tests.Processes;

/// <summary>真实启动明确自有夹具验证嵌套 Job，绝不按进程名结束外部应用。</summary>
public sealed partial class OwnedJobTests
{
    /// <summary>真实标准管道往返并 EOF 退出，父孙进程都属于原子 Job，额外可继承句柄不泄漏。</summary>
    /// <returns>父孙已退出、所有管道关闭后的任务。</returns>
    [Fact]
    public async Task RedirectedPipesHaveWhitelistAndOwnedDescendants()
    {
        var fixture = FixturePath();
        var directory = Path.Combine(@"D:\Soft Ware\LiveLingo\data\lifecycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sentinelPath = Path.Combine(directory, "inheritable-sentinel.bin");
        using var sentinel = new FileStream(sentinelPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        Assert.True(SetHandleInformation(sentinel.SafeFileHandle.DangerousGetHandle(), 1, 1));
        using var job = new OwnedJob();
        using var pipes = job.StartRedirected(fixture, ["pipe", directory], Path.GetDirectoryName(fixture)!, "pipe-test");
        using var parent = Process.GetProcessById(pipes.Process.Identity.ProcessId);
        _ = parent.Handle;
        await WaitForFileAsync(Path.Combine(directory, "leaf.pid"));
        using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "leaf.pid"), TestContext.Current.CancellationToken)));
        _ = child.Handle;
        Assert.True(job.Contains(pipes.Process.Identity));
        Assert.True(job.Contains(WindowsTargets.Query(child.Id).Identity!));
        sentinel.Dispose();
        File.Delete(sentinelPath);
        Assert.False(File.Exists(sentinelPath));
        var payload = Enumerable.Range(0, 65536).Select(x => (byte)x).ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new byte[payload.Length];
        var read = pipes.Output.ReadExactlyAsync(received, timeout.Token).AsTask();
        using var errors = new StreamReader(pipes.Error, leaveOpen: true);
        var error = errors.ReadLineAsync(timeout.Token).AsTask();
        await pipes.Input.WriteAsync(payload, timeout.Token);
        await pipes.Input.FlushAsync(timeout.Token);
        pipes.Input.Dispose();
        await read;
        Assert.Equal(payload, received);
        Assert.Equal("pipe-ready", await error);
        await parent.WaitForExitAsync(timeout.Token);
        await child.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, parent.ExitCode);
        Assert.Equal(0, await pipes.Output.ReadAsync(new byte[1], timeout.Token));
    }

    /// <summary>设置测试自有哨兵句柄的继承位以检查白名单隔离。</summary>
    /// <param name="handle">仅测试创建的句柄。</param><param name="mask">需更新位。</param><param name="flags">新位值。</param><returns>成功标志。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    /// <summary>验证正常退出与强杀父宿主后，孙进程均由内层 Job 回收。</summary>
    /// <param name="force">是否只强杀明确自有父进程。</param>
    /// <returns>观察到父孙进程退出后的任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentExitClosesNestedJobAndReapsGrandchildAsync(bool force)
    {
        var fixture = FixturePath();
        var directory = Path.Combine(@"D:\Soft Ware\LiveLingo\data\lifecycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var outer = new OwnedJob();
        var owned = outer.Start(fixture, ["branch", directory], Path.GetDirectoryName(fixture)!, "test-parent");
        using var parent = Process.GetProcessById(owned.Identity.ProcessId);
        await WaitForFileAsync(Path.Combine(directory, "leaf.pid"));
        var childPid = int.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "leaf.pid"), TestContext.Current.CancellationToken));
        using var child = Process.GetProcessById(childPid);
        Assert.NotEqual(Environment.ProcessId, childPid);
        Assert.True(outer.Contains(owned.Identity));
        Assert.True(outer.Contains(WindowsTargets.Query(childPid).Identity!));
        Assert.Contains(outer.Members(), x => x.ProcessId == childPid);
        Assert.DoesNotContain(outer.Members(), x => x.ProcessId == Environment.ProcessId);
        Assert.False(outer.Contains(WindowsTargets.Query(Environment.ProcessId).Identity!));
        Assert.False(outer.Contains(new(owned.Identity.ProcessId, owned.Identity.CreationTimeUtc.AddSeconds(-1), owned.Identity.ExecutablePath)));
        if (force) parent.Kill(); else await File.WriteAllTextAsync(Path.Combine(directory, "stop"), "normal", TestContext.Current.CancellationToken);
        await parent.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(parent.HasExited);
        Assert.True(child.HasExited);
        Assert.True(WindowsTargets.Query(Environment.ProcessId).Identity is not null);
    }

    /// <summary>启动路径不存在时失败，不创建无监管进程。</summary>
    [Fact]
    public void InvalidExecutableFailsWithoutFallback()
    {
        using var job = new OwnedJob();
        Assert.Throws<ArgumentException>(() => job.Start(@"D:\Soft Ware\LiveLingo\missing-fixture.exe", [], AppContext.BaseDirectory, "invalid"));
    }

    /// <summary>真实 CreateProcess 拒绝无效 PE 时不启动替代 shell 或失去 Job 监管。</summary>
    [Fact]
    public void NativeCreateFailureDoesNotFallback()
    {
        var directory = Path.Combine(@"D:\Soft Ware\LiveLingo\data\lifecycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var invalid = Path.Combine(directory, "invalid.exe");
        File.WriteAllText(invalid, "Not a Windows PE image.\n");
        using var job = new OwnedJob();
        Assert.Throws<System.ComponentModel.Win32Exception>(() => job.Start(invalid, [], directory, "invalid-pe"));
    }

    /// <summary>包含空格、引号或末尾反斜杠的参数不会改变边界。</summary>
    [Fact]
    public void ArgumentsAreQuotedForCreateProcess()
    {
        Assert.Equal("\"\"", OwnedJob.Quote(""));
        Assert.Equal("\"a b\"", OwnedJob.Quote("a b"));
        Assert.Equal("\"a\\\"b\"", OwnedJob.Quote("a\"b"));
        Assert.Equal("\"x\\\\\"", OwnedJob.Quote("x\\"));
    }

    /// <summary>从解决方案定位当前配置的夹具，不调用 shell。</summary>
    /// <returns>实际可执行路径。</returns>
    private static string FixturePath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "LiveLingo.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
        var path = Path.Combine(root.FullName, "tests", "Fixtures", "Processes", "bin", configuration, "net10.0-windows10.0.26100.0", "LiveLingo.ProcessFixture.exe");
        Assert.True(File.Exists(path), "请先构建完整解决方案的 ProcessFixture。");
        return path;
    }

    /// <summary>等待夹具写入完整标记，并有界超时。</summary>
    /// <param name="path">自有夹具标记。</param><returns>等待任务。</returns>
    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(path) || new FileInfo(path).Length == 0) await Task.Delay(20, timeout.Token);
    }
}
