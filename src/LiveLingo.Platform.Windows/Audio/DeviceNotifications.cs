using System.Runtime.InteropServices;

namespace LiveLingo.Platform.Windows.Audio;

/// <summary>注册设备通知；回调只调用非阻塞事件入口，注销在消费线程执行。</summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class DeviceNotifications : IMMNotificationClient, IAgileObject, IDisposable
{
    private readonly Action<string> post;
    private IMMDeviceEnumerator? enumerator;

    /// <summary>订阅所有音频端点的变化。</summary>
    /// <param name="post">仅投递元数据的非阻塞函数。</param>
    public DeviceNotifications(Action<string> post)
    {
        this.post = post;
        enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!)!;
        var result = enumerator.RegisterEndpointNotificationCallback(this);
        if (result < 0)
        {
            Marshal.ReleaseComObject(enumerator);
            enumerator = null;
            Marshal.ThrowExceptionForHR(result);
        }
    }

    /// <inheritdoc />
    public int OnDeviceStateChanged(string deviceId, uint newState) { post($"state:{newState}:{deviceId}"); return 0; }
    /// <inheritdoc />
    public int OnDeviceAdded(string deviceId) { post($"added:{deviceId}"); return 0; }
    /// <inheritdoc />
    public int OnDeviceRemoved(string deviceId) { post($"removed:{deviceId}"); return 0; }
    /// <inheritdoc />
    public int OnDefaultDeviceChanged(int flow, int role, string? deviceId) { post($"default:{flow}:{role}:{deviceId}"); return 0; }
    /// <inheritdoc />
    public int OnPropertyValueChanged(string deviceId, PropertyKey key) { post($"property:{key.FormatId}:{key.PropertyId}:{deviceId}"); return 0; }

    /// <summary>在回调外注销并释放最后的枚举器引用。</summary>
    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref enumerator, null);
        if (owned is null) return;
        try { Marshal.ThrowExceptionForHR(owned.UnregisterEndpointNotificationCallback(this)); }
        finally { Marshal.ReleaseComObject(owned); }
    }
}

/// <summary>设备属性身份的官方布局。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PropertyKey
{
    /// <summary>属性集合身份。</summary>
    public Guid FormatId;
    /// <summary>集合内属性编号。</summary>
    public uint PropertyId;
}

/// <summary>设备回调的官方 vtable 顺序。</summary>
[ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMNotificationClient
{
    /// <summary>投递状态变化。</summary><param name="deviceId">不解析的端点身份。</param><param name="newState">状态标志。</param><returns>HRESULT。</returns>
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);
    /// <summary>投递新增通知。</summary><param name="deviceId">端点身份。</param><returns>HRESULT。</returns>
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    /// <summary>投递移除通知。</summary><param name="deviceId">端点身份。</param><returns>HRESULT。</returns>
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    /// <summary>投递默认端点变化。</summary><param name="flow">方向。</param><param name="role">角色。</param><param name="deviceId">可空身份。</param><returns>HRESULT。</returns>
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? deviceId);
    /// <summary>投递属性变化。</summary><param name="deviceId">端点身份。</param><param name="key">属性身份。</param><returns>HRESULT。</returns>
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
}

/// <summary>保留未调用槽位以保证注册函数的 ABI 正确。</summary>
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    /// <summary>枚举端点。</summary><param name="flow">方向。</param><param name="mask">状态。</param><param name="devices">集合。</param><returns>HRESULT。</returns>
    [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out nint devices);
    /// <summary>获取默认端点。</summary><param name="flow">方向。</param><param name="role">角色。</param><param name="device">端点。</param><returns>HRESULT。</returns>
    [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out nint device);
    /// <summary>按身份获取端点。</summary><param name="id">身份。</param><param name="device">端点。</param><returns>HRESULT。</returns>
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out nint device);
    /// <summary>注册通知。</summary><param name="client">回调。</param><returns>HRESULT。</returns>
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    /// <summary>注销通知。</summary><param name="client">回调。</param><returns>HRESULT。</returns>
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}
