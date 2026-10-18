using System.Runtime.InteropServices;
using LiveLingo.Platform.Windows.Audio;

namespace LiveLingo.Windows.Tests.Audio;

/// <summary>验证固定 x64 ABI 和模拟完成回调的引用所有权。</summary>
public sealed class ActivationTests
{
    /// <summary>请求不支持的接口以触发真实 Windows 激活失败，核对双层错误传播与引用清理。</summary>
    /// <returns>专用 MTA 线程完成后的任务；不冒充设备拔插故障。</returns>
    [Fact]
    public async Task NativeUnsupportedInterfaceFailsWithPreservedHResultAsync()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { CheckUnsupportedInterface(); finished.TrySetResult(); }
            catch (Exception exception) { finished.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    /// <summary>使用生产 ABI 和完成对象调用真实 API，释放异步操作后记录实际 HRESULT。</summary>
    private static void CheckUnsupportedInterface()
    {
        var initialized = AudioInterop.CoInitializeEx(0, 0);
        Marshal.ThrowExceptionForHR(initialized);
        IActivateAudioInterfaceAsyncOperation? operation = null;
        var blob = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        var started = System.Diagnostics.Stopwatch.StartNew();
        var callResult = 0;
        var failureResult = 0;
        try
        {
            using var completion = new ActivationCompletion();
            Marshal.StructureToPtr(new ActivationParams { ActivationType = 1, TargetProcessId = (uint)Environment.ProcessId }, blob, false);
            var variant = new PropVariant { VariantType = 65, BlobSize = (uint)Marshal.SizeOf<ActivationParams>(), BlobData = blob };
            callResult = AudioInterop.ActivateAudioInterfaceAsync("VAD\\Process_Loopback", Guid.Empty, variant, completion, out operation);
            if (callResult < 0)
            {
                completion.RejectCall();
                failureResult = callResult;
            }
            else
            {
                nint pointer = 0;
                try { pointer = completion.Take(TimeSpan.FromSeconds(5), CancellationToken.None); }
                catch (COMException exception) { failureResult = exception.HResult; }
                finally { if (pointer != 0) Marshal.Release(pointer); }
            }
            Assert.True(failureResult < 0, "真实 Windows 应拒绝不支持的接口，不能把成功算成失败覆盖。");
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
            if (operation is not null) Marshal.ReleaseComObject(operation);
            AudioInterop.CoUninitialize();
        }
        var directory = Path.Combine(@"D:\Soft Ware\LiveLingo\data\capture", "native-activation-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "result.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            Kind = "real-api-unsupported-interface-not-device-removal", CallHResult = $"0x{callResult:X8}",
            FailureHResult = $"0x{failureResult:X8}", ElapsedMs = started.Elapsed.TotalMilliseconds, CleanupCompleted = true
        }));
    }

    /// <summary>真实 Windows 激活在不同停止时机有界收敛，不声称每次都命中同一原生竞态。</summary>
    /// <param name="delayMs">发出取消前的延迟。</param><returns>完成采集任务后的断言任务。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    public async Task NativeCaptureCancellationIsBoundedAsync(int delayMs)
    {
        var identity = LiveLingo.Platform.Windows.Processes.WindowsTargets.Query(Environment.ProcessId).Identity!;
        using var stop = new CancellationTokenSource();
        var capture = new WindowsLoopbackCapture().RunAsync(identity, Guid.NewGuid(), 1, TimeSpan.FromSeconds(2),
            packet => { packet.Dispose(); return true; }, () => { }, stop.Token);
        if (delayMs > 0) await Task.Delay(delayMs, TestContext.Current.CancellationToken);
        stop.Cancel();
        try { await capture.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        Assert.True(capture.IsCompleted);
    }

    /// <summary>同进程多个真实通知订阅各自释放引用，不使其他 RCW 失效。</summary>
    [Fact]
    public void DeviceSubscriptionsCanBeDisposedIndependently()
    {
        using var first = new DeviceNotifications(_ => { });
        using var second = new DeviceNotifications(_ => { });
        first.Dispose();
        second.Dispose();
    }

    /// <summary>核对 VT_BLOB 及 WAVEFORMATEX 与官方 x64 结构布局。</summary>
    [Fact]
    public void NativeStructuresHaveExpectedSizesAndOffsets()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(12, Marshal.SizeOf<ActivationParams>());
        Assert.Equal(24, Marshal.SizeOf<PropVariant>());
        Assert.Equal(16, Marshal.OffsetOf<PropVariant>(nameof(PropVariant.BlobData)).ToInt32());
        Assert.Equal(18, Marshal.SizeOf<WaveFormat>());
    }

    /// <summary>两层任意失败均释放交付指针，并保留原始失败码。</summary>
    /// <param name="callResult">结果查询状态。</param><param name="activationResult">音频激活状态。</param>
    [Theory]
    [InlineData(unchecked((int)0x80004005), 0)]
    [InlineData(0, unchecked((int)0x80070005))]
    public void EitherFailureReleasesPointer(int callResult, int activationResult)
    {
        var released = new List<nint>();
        using var completion = new ActivationCompletion(released.Add);
        completion.Complete(callResult, activationResult, 42);
        var exception = Assert.Throws<COMException>(() => completion.Take(TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal(callResult < 0 ? callResult : activationResult, exception.HResult);
        Assert.Equal([(nint)42], released);
    }

    /// <summary>停止先于回调时，迟到的成功引用只能释放，不能重新启动资源。</summary>
    [Fact]
    public void LateSuccessAfterStopOnlyReleases()
    {
        var released = new List<nint>();
        var completion = new ActivationCompletion(released.Add);
        completion.Dispose();
        completion.Complete(0, 0, 42);
        completion.Dispose();
        Assert.Equal([(nint)42], released);
    }

    /// <summary>激活中取消后仍等待真实回调完成其所有权。</summary>
    [Fact]
    public void CancelDuringActivationDoesNotLeakLateResult()
    {
        var released = new List<nint>();
        var completion = new ActivationCompletion(released.Add);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => completion.Take(TimeSpan.FromSeconds(1), cancellation.Token));
        completion.Dispose();
        completion.Complete(0, 0, 99);
        Assert.Equal([(nint)99], released);
    }
}
