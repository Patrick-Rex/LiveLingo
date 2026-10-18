using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace LiveLingo.Platform.Windows.Audio;

/// <summary>在停止或超时后继续保活回调，并释放迟到的原生引用。</summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ActivationCompletion : IActivateAudioInterfaceCompletionHandler, IAgileObject, IDisposable
{
    private static readonly ConcurrentDictionary<Guid, ActivationCompletion> Pending = new();
    private readonly object sync = new();
    private readonly ManualResetEvent completed = new(false);
    private readonly Action<nint> release;
    private readonly Guid id = Guid.NewGuid();
    private bool abandoned;
    private bool finished;
    private nint pointer;
    private Exception? error;

    /// <summary>建立待完成激活的寿命屏障。</summary>
    public ActivationCompletion() : this(value => Marshal.Release(value)) { }

    /// <summary>允许 ABI 行为测试观察原生引用释放而不制造假 COM 指针。</summary>
    /// <param name="release">唯一引用释放操作。</param>
    internal ActivationCompletion(Action<nint> release)
    {
        this.release = release;
        Pending[id] = this;
    }

    /// <inheritdoc />
    public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
    {
        try
        {
            var callResult = operation.GetActivateResult(out var activationResult, out var audio);
            Complete(callResult, activationResult, audio);
        }
        catch (Exception exception)
        {
            Complete(unchecked((int)0x80004005), unchecked((int)0x80004005), 0, exception);
        }
        return 0;
    }

    /// <summary>检查结果读取和激活两层 HRESULT，并在迟到时回收引用。</summary>
    /// <param name="callResult">GetActivateResult 返回值。</param>
    /// <param name="activationResult">激活返回值。</param>
    /// <param name="audio">本次交付的 IUnknown 引用。</param>
    /// <param name="exception">可选回调异常。</param>
    internal void Complete(int callResult, int activationResult, nint audio, Exception? exception = null)
    {
        lock (sync)
        {
            if (finished)
            {
                if (audio != 0) release(audio);
                return;
            }
            finished = true;
            error = exception ?? (callResult < 0 ? new COMException("GetActivateResult 失败。", callResult) :
                activationResult < 0 ? new COMException("音频异步激活失败。", activationResult) :
                audio == 0 ? new COMException("激活没有返回音频接口。") : null);
            if (abandoned || error is not null)
            {
                if (audio != 0) release(audio);
            }
            else pointer = audio;
            if (!abandoned) completed.Set();
            Pending.TryRemove(id, out _);
        }
    }

    /// <summary>在专用同步音频线程等待结果，并将原生引用转交一次。</summary>
    /// <param name="timeout">有界激活等待。</param>
    /// <param name="cancellationToken">停止信号。</param>
    /// <returns>由调用方释放的 IUnknown 引用。</returns>
    /// <exception cref="TimeoutException">超时；回调继续保活直至实际完成。</exception>
    /// <exception cref="OperationCanceledException">采集停止。</exception>
    internal nint Take(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = WaitHandle.WaitAny([completed, cancellationToken.WaitHandle], timeout);
        cancellationToken.ThrowIfCancellationRequested();
        if (result == WaitHandle.WaitTimeout) throw new TimeoutException("Application Loopback 激活超时。");
        lock (sync)
        {
            if (error is not null) throw error;
            var audio = pointer;
            pointer = 0;
            return audio;
        }
    }

    /// <summary>在激活 API 同步失败时解除无回调操作的保活。</summary>
    internal void RejectCall() => Complete(unchecked((int)0x80004005), 0, 0);

    /// <summary>设置迟到释放屏障；尚未完成的回调继续由根集合持有。</summary>
    public void Dispose()
    {
        lock (sync)
        {
            if (abandoned) return;
            abandoned = true;
            if (pointer != 0) release(pointer);
            pointer = 0;
            completed.Dispose();
        }
    }
}
