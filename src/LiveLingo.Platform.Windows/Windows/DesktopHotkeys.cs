using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LiveLingo.Platform.Windows.Windows;

/// <summary>只登记和释放本 HWND 拥有的三个全局快捷键。</summary>
/// <param name="window">在拥有 UI 消息循环线程创建的窗口。</param>
public sealed partial class DesktopHotkeys(nint window) : IDisposable
{
    private readonly List<int> registered = [];

    /// <summary>先注销旧配置，再登记新配置；冲突返回具体错误且保留其他可用操作。</summary>
    /// <param name="start">启停功能键。</param><param name="visibility">显隐功能键。</param><param name="layout">布局功能键。</param><returns>用户可见的冲突说明。</returns>
    public string Configure(int start, int visibility, int layout)
    {
        Dispose();
        var errors = new List<string>();
        var keys = new[] { start, visibility, layout };
        for (var index = 0; index < keys.Length; index++)
        {
            var id = 0x4C10 + index;
            if (RegisterHotKey(window, id, 0x4000 | 0x1 | 0x2, (uint)(0x70 + keys[index] - 1))) registered.Add(id);
            else errors.Add($"Ctrl+Alt+F{keys[index]}：{new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }
        return string.Join("；", errors);
    }

    /// <summary>将本程序已登记 ID 映射为操作序号，其他热键消息不处理。</summary><param name="id">WM_HOTKEY 标识。</param><returns>零至二，或负一。</returns>
    public int Resolve(int id) => registered.Contains(id) ? id - 0x4C10 : -1;

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var id in registered) UnregisterHotKey(window, id);
        registered.Clear();
    }

    /// <summary>登记 HWND 热键。</summary><param name="window">窗口。</param><param name="id">自有编号。</param><param name="modifiers">修饰键。</param><param name="key">虚拟键。</param><returns>是否登记成功。</returns>
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    /// <summary>释放自有热键。</summary><param name="window">窗口。</param><param name="id">登记编号。</param><returns>是否释放成功。</returns>
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint window, int id);
}
