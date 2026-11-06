using System.Diagnostics;
using LiveLingo.Core.Desktop;
using LiveLingo.Desktop.Windows;

namespace LiveLingo.Windows.Tests;

/// <summary>验证发现路径与真实脚本进程边界，不安装外部软件。</summary>
public sealed class DependencySetupTests
{
    /// <summary>发现 PATH 中的现有 Ollama，同时保留用户已配置的现有可执行文件。</summary>
    [Fact]
    public void DiscoveryReusesMissingPathsWithoutReplacingExistingOnes()
    {
        var root = Path.Combine(Path.GetTempPath(), "discover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "ollama.exe"); File.WriteAllText(executable, "existing");
            var settings = new DesktopSettings { OllamaPath = Path.Combine(root, "missing.exe") };
            var found = DesktopEnvironment.DiscoverCandidates(settings, [], ["relative", root], []);
            Assert.Equal(executable, found.OllamaPath); Assert.Equal(settings.PythonPath, found.PythonPath);
            var custom = Path.Combine(root, "custom.exe"); File.WriteAllText(custom, "custom");
            Assert.Equal(custom, DesktopEnvironment.DiscoverCandidates(settings with { OllamaPath = custom }, [], [root], []).OllamaPath);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>在含空格和引号的路径执行真实 PowerShell，配置以快照传入且脚本失败可见。</summary>
    /// <param name="exitCode">脚本模拟退出码。</param><returns>执行验证任务。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task InstallerUsesSnapshotAndPropagatesExitCode(int exitCode)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "setup test's " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        try
        {
            var script = "param($Component,$SettingsPath)\n$s=Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json\n" +
                "if ($Component -ne 'Ollama' -or $s.TranslationModel -ne 'translategemma:4b') { throw 'Wrong snapshot' }\n" +
                $"Write-Output 'script finished'\nexit {exitCode}\n";
            await File.WriteAllTextAsync(Path.Combine(root, "scripts", "Install-LiveLingoDependencies.ps1"), script, TestContext.Current.CancellationToken);
            var operation = new DesktopEnvironment(root).InstallAsync(DependencyComponent.Ollama,
                new() { CachePath = Path.Combine(root, "cache"), TranslationModel = "translategemma:4b" }, new Progress<string>(), TestContext.Current.CancellationToken);
            if (exitCode == 0) await operation;
            else { var error = await Assert.ThrowsAsync<IOException>(() => operation); Assert.Contains("script finished", error.Message); }
            Assert.Empty(Directory.GetDirectories(Path.Combine(root, "cache", "setup")));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>取消安装回收脚本进程并删除本次配置快照。</summary><returns>取消验证任务。</returns>
    [Fact]
    public async Task CancelingInstallerStopsOwnedProcess()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "cancel-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "scripts", "Install-LiveLingoDependencies.ps1"),
                "param($Component,$SettingsPath)\n$s=Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json\n$PID | Set-Content -LiteralPath (Join-Path $s.CachePath 'pid.txt')\nStart-Sleep -Seconds 30\n", TestContext.Current.CancellationToken);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var cache = Path.Combine(root, "cache");
            var operation = new DesktopEnvironment(root).InstallAsync(DependencyComponent.Runtime, new() { CachePath = cache }, new Progress<string>(), cancel.Token);
            var pidPath = Path.Combine(cache, "pid.txt");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while (!File.Exists(pidPath)) await Task.Delay(50, deadline.Token);
            var pid = int.Parse(await File.ReadAllTextAsync(pidPath, deadline.Token));
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
            Assert.Empty(Directory.GetDirectories(Path.Combine(cache, "setup")));
        }
        finally { Directory.Delete(root, true); }
    }
}
