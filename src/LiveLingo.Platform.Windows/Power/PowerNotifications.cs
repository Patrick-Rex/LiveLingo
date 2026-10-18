using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LiveLingo.Platform.Windows.Power;

/// <summary>以系统回调注册睡眠恢复通知，不依赖 UI 消息循环。</summary>
public sealed partial class PowerNotifications : IDisposable
{
    private readonly NotificationCallback callback;
    private nint registration;

    /// <summary>注册系统通知，回调仅投递睡眠或恢复事件。</summary>
    /// <param name="post">true 表示睡眠，false 表示恢复的非阻塞入口。</param>
    /// <exception cref="Win32Exception">注册失败。</exception>
    public unsafe PowerNotifications(Action<bool> post)
    {
        callback = (_, type, _) =>
        {
            if (type == 4) post(true);
            else if (type is 7 or 18) post(false);
            return 0;
        };
        var subscription = new Subscription { Callback = Marshal.GetFunctionPointerForDelegate(callback), Context = 0 };
        var result = PowerRegisterSuspendResumeNotification(2, (nint)(&subscription), out registration);
        if (result != 0) throw new Win32Exception((int)result);
    }

    /// <summary>注销后才允许托管回调被回收。</summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref registration, 0);
        if (handle == 0) return;
        var result = PowerUnregisterSuspendResumeNotification(handle);
        GC.KeepAlive(callback);
        if (result != 0) throw new Win32Exception((int)result);
    }

    /// <summary>电源通知回调签名。</summary>
    /// <param name="context">注册上下文。</param><param name="type">事件编号。</param><param name="setting">保留。</param>
    /// <returns>处理状态。</returns>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint NotificationCallback(nint context, uint type, nint setting);

    /// <summary>原生回调注册参数。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Subscription
    {
        /// <summary>函数指针。</summary>
        internal nint Callback;
        /// <summary>上下文。</summary>
        internal nint Context;
    }

    /// <summary>注册睡眠恢复通知。</summary><param name="flags">回调模式。</param><param name="recipient">参数指针。</param><param name="handle">注册句柄。</param><returns>Win32 状态。</returns>
    [LibraryImport("powrprof.dll")]
    private static partial uint PowerRegisterSuspendResumeNotification(uint flags, nint recipient, out nint handle);
    /// <summary>注销睡眠恢复通知。</summary><param name="handle">注册句柄。</param><returns>Win32 状态。</returns>
    [LibraryImport("powrprof.dll")]
    private static partial uint PowerUnregisterSuspendResumeNotification(nint handle);
}
