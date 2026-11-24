using LiveLingo.Desktop.Windows;

namespace LiveLingo.Windows.Tests;

/// <summary>验证 Windows 包安装不进入 Velopack 下载或退出安装流程。</summary>
public sealed class DesktopUpdatesTests
{
    /// <summary>MSIX 使用 Windows 包版本且拒绝应用内下载和安装，即使调用方绕过界面。</summary>
    /// <returns>渠道隔离验证任务。</returns>
    [Fact]
    public async Task PackagedInstallUsesPackageVersionAndRejectsVelopackOperations()
    {
        using var updates = new DesktopUpdates(new Version(1, 2, 3, 0));
        Assert.Equal("1.2.3.0", updates.CurrentVersion);
        Assert.False(updates.IsInstalled);
        Assert.Null(updates.PendingVersion);
        Assert.Contains("MSIX", updates.UnavailableMessage);
        Assert.Null(await updates.CheckAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            updates.DownloadAsync(_ => Assert.Fail("MSIX 不能下载 Velopack 更新。"), TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => updates.ApplyAfterExit([]));
    }

    /// <summary>未打包测试宿主读取原生身份返回空，不把普通源码运行误识别为 MSIX。</summary>
    [Fact]
    public void UnpackagedProcessHasNoWindowsPackageVersion() => Assert.Null(WindowsPackageIdentity.GetVersion());
}
