using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LiveLingo.Platform.Windows.Windows;

/// <summary>实现 HWND 非激活和跨进程穿透；不依赖 UI 框架。</summary>
public static partial class OverlayNative
{
    /// <summary>非激活扩展样式。</summary>
    public const uint NoActivate = 0x08000000;
    /// <summary>跨进程透明命中扩展样式。</summary>
    public const uint Transparent = 0x20;
    /// <summary>分层窗口扩展样式。</summary>
    public const uint Layered = 0x80000;

    /// <summary>在框架重算样式时保留所需位，不触发递归更新。</summary><param name="styles">框架给出的扩展样式。</param><param name="interactive">允许鼠标交互。</param><param name="keepInactive">交互按钮仍不激活窗口。</param><returns>合并后的样式。</returns>
    public static uint MergeStyles(uint styles, bool interactive, bool keepInactive = false)
        => (interactive ? (styles | Layered) & ~(Transparent | NoActivate) : styles | Layered | Transparent | NoActivate) | (keepInactive ? NoActivate : 0);

    /// <summary>立即应用并刷新窗口样式，始终不抢占前台焦点。</summary><param name="window">有效 HWND。</param><param name="interactive">允许鼠标交互。</param><param name="keepInactive">点击也不激活窗口。</param>
    public static void Apply(nint window, bool interactive, bool keepInactive = false)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = GetWindowLongPtrW(window, -20);
        var current = MergeStyles(unchecked((uint)previous.ToInt64()), interactive, keepInactive);
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLongPtrW(window, -20, (nint)current) == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!SetWindowPos(window, new nint(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x20)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    /// <summary>读取当前真实 HWND 样式用于验证。</summary><param name="window">目标句柄。</param><returns>扩展样式位。</returns>
    public static uint ReadStyles(nint window) => unchecked((uint)GetWindowLongPtrW(window, -20).ToInt64());
    /// <summary>读取真实前台 HWND，不改变前台状态。</summary><returns>当前前台窗口。</returns>
    [LibraryImport("user32.dll")] public static partial nint GetForegroundWindow();
    /// <summary>读取窗口长整型属性。</summary><param name="window">窗口。</param><param name="index">属性编号。</param><returns>当前值。</returns>
    [LibraryImport("user32.dll", SetLastError = true)] private static partial nint GetWindowLongPtrW(nint window, int index);
    /// <summary>替换窗口长整型属性。</summary><param name="window">窗口。</param><param name="index">属性编号。</param><param name="value">新值。</param><returns>原值。</returns>
    [LibraryImport("user32.dll", SetLastError = true)] private static partial nint SetWindowLongPtrW(nint window, int index, nint value);
    /// <summary>无激活刷新窗口位置和样式缓存。</summary><param name="window">窗口。</param><param name="after">Z 序。</param><param name="x">位置。</param><param name="y">位置。</param><param name="width">宽。</param><param name="height">高。</param><param name="flags">更新标志。</param><returns>调用是否成功。</returns>
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
