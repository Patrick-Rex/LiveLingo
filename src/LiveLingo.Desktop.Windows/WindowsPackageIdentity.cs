using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace LiveLingo.Desktop.Windows;

/// <summary>读取当前进程的 Windows 包身份，将 MSIX 与独立安装的更新生命周期分开。</summary>
internal static class WindowsPackageIdentity
{
    /// <summary>读取已注册的包版本；无包身份的源码、ZIP 与 EXE 安装返回空。</summary>
    /// <returns>Windows 提供的四段包版本，或无包身份时为空。</returns>
    /// <exception cref="Win32Exception">身份查询失败，且错误不是没有包身份。</exception>
    internal static Version? GetVersion()
    {
        uint length = 0;
        var result = GetCurrentPackageFullName(ref length, nint.Zero);
        if (result == 15700) return null; // APPMODEL_ERROR_NO_PACKAGE
        if (result != 122) throw new Win32Exception(result); // ERROR_INSUFFICIENT_BUFFER
        var version = Package.Current.Id.Version;
        return new(version.Major, version.Minor, version.Build, version.Revision);
    }

    /// <summary>查询当前进程的包名长度；空缓冲区用于检查是否具有 Windows 包身份。</summary>
    /// <param name="packageFullNameLength">接收包名所需的字符数，包含结尾空字符。</param>
    /// <param name="packageFullName">接收包名的缓冲区，此处固定传空。</param>
    /// <returns>Win32 状态码；122 表示有包身份，15700 表示没有包身份。</returns>
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, nint packageFullName);
}
