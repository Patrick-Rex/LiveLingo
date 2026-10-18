using System.Runtime.InteropServices;

namespace LiveLingo.Platform.Windows.Audio;

/// <summary>定义官方 Application Loopback 使用的 x64 ABI。</summary>
internal static partial class AudioInterop
{
    /// <summary>IAudioClient 接口身份。</summary>
    internal static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    /// <summary>IAudioCaptureClient 接口身份。</summary>
    internal static readonly Guid CaptureClientId = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    /// <summary>异步激活指定进程树的音频接口；COM 参数使用运行时封送。</summary>
    /// <param name="deviceInterfacePath">虚拟进程树设备路径。</param>
    /// <param name="riid">请求的 COM 接口。</param>
    /// <param name="activationParams">VT_BLOB 参数。</param>
    /// <param name="completionHandler">敏捷完成回调。</param>
    /// <param name="operation">异步操作引用。</param>
    /// <returns>调用层 HRESULT，与完成层 HRESULT 分开校验。</returns>
    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int ActivateAudioInterfaceAsync(string deviceInterfacePath, in Guid riid,
        in PropVariant activationParams, IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    /// <summary>在专用线程初始化 MTA。</summary>
    /// <param name="reserved">必须为空。</param>
    /// <param name="coInit">COM 初始化标志。</param>
    /// <returns>HRESULT。</returns>
    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint reserved, uint coInit);

    /// <summary>平衡当前线程成功的 COM 初始化。</summary>
    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();
}

/// <summary>描述 process loopback 的官方 12 字节激活布局。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ActivationParams
{
    /// <summary>激活类型；1 表示进程 loopback。</summary>
    internal int ActivationType;
    /// <summary>目标根 PID。</summary>
    internal uint TargetProcessId;
    /// <summary>0 表示包含进程树。</summary>
    internal int ProcessLoopbackMode;
}

/// <summary>以 x64 PROPVARIANT 显式布局传递 VT_BLOB。</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    /// <summary>变体类型；65 为 VT_BLOB。</summary>
    [FieldOffset(0)] internal ushort VariantType;
    /// <summary>BLOB 字节数。</summary>
    [FieldOffset(8)] internal uint BlobSize;
    /// <summary>仅在激活调用期间有效的参数地址。</summary>
    [FieldOffset(16)] internal nint BlobData;
}

/// <summary>保持 WAVEFORMATEX 的 2 字节打包和 18 字节大小。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormat
{
    /// <summary>PCM 标志。</summary>
    internal ushort FormatTag;
    /// <summary>声道数量。</summary>
    internal ushort Channels;
    /// <summary>采样率。</summary>
    internal uint SamplesPerSecond;
    /// <summary>每秒字节数。</summary>
    internal uint AverageBytesPerSecond;
    /// <summary>每帧字节数。</summary>
    internal ushort BlockAlign;
    /// <summary>位深。</summary>
    internal ushort BitsPerSample;
    /// <summary>扩展长度。</summary>
    internal ushort ExtraSize;
}

/// <summary>获取异步激活的双层结果。</summary>
[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivateAudioInterfaceAsyncOperation
{
    /// <summary>获取完成层状态和一个需要释放的 IUnknown 引用。</summary>
    /// <param name="activationResult">音频激活 HRESULT。</param>
    /// <param name="activatedInterface">音频接口指针。</param>
    /// <returns>获取结果操作自身的 HRESULT。</returns>
    [PreserveSig] int GetActivateResult(out int activationResult, out nint activatedInterface);
}

/// <summary>由 Windows 在 MTA 调用的完成入口。</summary>
[ComVisible(true), Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivateAudioInterfaceCompletionHandler
{
    /// <summary>交付异步操作，回调必须保持存活直到本次完成。</summary>
    /// <param name="operation">已完成操作。</param>
    /// <returns>回调处理 HRESULT。</returns>
    [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

/// <summary>向 COM 声明完成回调可跨 apartment 调用。</summary>
[ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAgileObject { }

/// <summary>按 audioclient.h 顺序声明原始 IAudioClient vtable。</summary>
[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    /// <summary>初始化共享事件采集。</summary>
    /// <param name="shareMode">共享模式。</param><param name="streamFlags">流标志。</param>
    /// <param name="bufferDuration">100ns 缓冲时间。</param><param name="periodicity">共享模式周期零值。</param>
    /// <param name="format">客户端请求格式。</param><param name="sessionGuid">会话 GUID 或空指针。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, in WaveFormat format, nint sessionGuid);
    /// <summary>获取已分配缓冲帧数。</summary><param name="frames">帧数。</param><returns>HRESULT。</returns>
    [PreserveSig] int GetBufferSize(out uint frames);
    /// <summary>获取流固有延迟。</summary><param name="latency">100ns 延迟。</param><returns>HRESULT。</returns>
    [PreserveSig] int GetStreamLatency(out long latency);
    /// <summary>获取缓冲填充量。</summary><param name="frames">帧数。</param><returns>HRESULT。</returns>
    [PreserveSig] int GetCurrentPadding(out uint frames);
    /// <summary>查询格式支持。</summary><param name="shareMode">共享模式。</param><param name="format">请求格式。</param><param name="closest">最近格式。</param><returns>HRESULT。</returns>
    [PreserveSig] int IsFormatSupported(int shareMode, in WaveFormat format, out nint closest);
    /// <summary>获取混音格式。</summary><param name="format">CoTaskMem 分配的格式。</param><returns>HRESULT。</returns>
    [PreserveSig] int GetMixFormat(out nint format);
    /// <summary>获取设备周期。</summary><param name="defaultPeriod">默认周期。</param><param name="minimumPeriod">最小周期。</param><returns>HRESULT。</returns>
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    /// <summary>启动音频流。</summary><returns>HRESULT。</returns>
    [PreserveSig] int Start();
    /// <summary>停止音频流。</summary><returns>HRESULT。</returns>
    [PreserveSig] int Stop();
    /// <summary>重置音频流。</summary><returns>HRESULT。</returns>
    [PreserveSig] int Reset();
    /// <summary>关联事件句柄。</summary><param name="eventHandle">事件句柄。</param><returns>HRESULT。</returns>
    [PreserveSig] int SetEventHandle(nint eventHandle);
    /// <summary>获取捕获服务接口。</summary><param name="riid">服务接口身份。</param><param name="service">COM 服务对象。</param><returns>HRESULT。</returns>
    [PreserveSig] int GetService(in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

/// <summary>按 audioclient.h 顺序声明捕获缓冲 API。</summary>
[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    /// <summary>获取只在 ReleaseBuffer 前有效的原生包。</summary>
    /// <param name="data">PCM 指针。</param><param name="frames">帧数。</param><param name="flags">包标志。</param>
    /// <param name="devicePosition">设备帧位置。</param><param name="qpcPosition">100ns QPC 时间。</param><returns>HRESULT。</returns>
    [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    /// <summary>在 GetBuffer 的同一线程释放当前包。</summary><param name="frames">已读取帧数。</param><returns>HRESULT。</returns>
    [PreserveSig] int ReleaseBuffer(uint frames);
    /// <summary>获取下一包大小，用于排空一次唤醒的全部数据。</summary><param name="frames">下一包帧数。</param><returns>HRESULT。</returns>
    [PreserveSig] int GetNextPacketSize(out uint frames);
}
