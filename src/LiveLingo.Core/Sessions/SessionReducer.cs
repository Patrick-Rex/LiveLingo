namespace LiveLingo.Core.Sessions;

/// <summary>表示采集生命周期的权威阶段。</summary>
public enum SessionState
{
    /// <summary>尚未启动或清理完成。</summary>
    Idle,
    /// <summary>正在激活新代次。</summary>
    Starting,
    /// <summary>客户端已经就绪。</summary>
    Running,
    /// <summary>正在等待有界重试。</summary>
    Recovering,
    /// <summary>目标不存在或无法唯一确定。</summary>
    WaitingForTarget,
    /// <summary>睡眠期间释放了易失资源。</summary>
    Suspended,
    /// <summary>预算耗尽或不可恢复。</summary>
    Faulted,
    /// <summary>拒绝任何重新启动副作用的终止屏障。</summary>
    Stopping
}

/// <summary>定义所有回调必须归约的事件种类。</summary>
public enum SessionEventKind
{
    /// <summary>启动所选目标。</summary>
    Start,
    /// <summary>激活成功。</summary>
    Ready,
    /// <summary>真实客户端失败。</summary>
    CaptureFailed,
    /// <summary>重试延迟结束。</summary>
    Retry,
    /// <summary>目标实例退出。</summary>
    TargetLost,
    /// <summary>发现唯一匹配候选。</summary>
    TargetFound,
    /// <summary>设备通知，仅要求健康检查。</summary>
    DeviceChanged,
    /// <summary>系统将进入睡眠。</summary>
    Suspend,
    /// <summary>系统恢复。</summary>
    Resume,
    /// <summary>资源限额或无法继续的错误。</summary>
    Fatal,
    /// <summary>用户停止。</summary>
    Stop,
    /// <summary>全部资源释放完成。</summary>
    Cleaned
}

/// <summary>不可变事件携带来源代次，拒绝迟到回调。</summary>
/// <param name="Kind">事件种类。</param>
/// <param name="Epoch">来源代次；全局控制事件可使用零。</param>
/// <param name="Target">新实例身份。</param>
/// <param name="Detail">诊断原因。</param>
public sealed record SessionEvent(SessionEventKind Kind, long Epoch = 0, TargetIdentity? Target = null, string? Detail = null);

/// <summary>纯状态归约器，只允许调用方在一个事件循环内写入。</summary>
public sealed class SessionReducer
{
    private bool terminated;

    /// <summary>获取权威状态。</summary>
    public SessionState State { get; private set; } = SessionState.Idle;
    /// <summary>获取单调状态版本。</summary>
    public long StateSequence { get; private set; }
    /// <summary>获取单调采集代次。</summary>
    public long CaptureEpoch { get; private set; }
    /// <summary>获取当前绑定的实例。</summary>
    public TargetIdentity? Target { get; private set; }
    /// <summary>获取累计失败次数，不以短暂就绪重置预算。</summary>
    public int Failures { get; private set; }

    /// <summary>归约一条事件，停止后拒绝重新创建资源。</summary>
    /// <param name="message">待校验事件。</param>
    /// <param name="retryBudget">整个会话允许的重试次数。</param>
    /// <returns>是否接受事件；副作用由外部比较快照后执行。</returns>
    public bool Apply(SessionEvent message, int retryBudget)
    {
        if (terminated || (State == SessionState.Stopping && message.Kind != SessionEventKind.Cleaned)) return false;
        if (message.Epoch != 0 && message.Epoch != CaptureEpoch) return false;
        switch (message.Kind)
        {
            case SessionEventKind.Start when State == SessionState.Idle && message.Target is not null:
                Target = message.Target;
                BeginEpoch();
                break;
            case SessionEventKind.Ready when State == SessionState.Starting:
                State = SessionState.Running;
                break;
            case SessionEventKind.CaptureFailed when State is SessionState.Starting or SessionState.Running:
                State = ++Failures > retryBudget ? SessionState.Faulted : SessionState.Recovering;
                break;
            case SessionEventKind.Retry when State == SessionState.Recovering:
                BeginEpoch();
                break;
            case SessionEventKind.TargetLost when State is SessionState.Starting or SessionState.Running or SessionState.Recovering:
                State = SessionState.WaitingForTarget;
                break;
            case SessionEventKind.TargetFound when State == SessionState.WaitingForTarget && message.Target is not null:
                if (Target is null || !Target.SameApplication(message.Target)) return false;
                Target = message.Target;
                BeginEpoch();
                break;
            case SessionEventKind.DeviceChanged when State == SessionState.Running:
                break;
            case SessionEventKind.Suspend when State is SessionState.Starting or SessionState.Running or SessionState.Recovering or SessionState.WaitingForTarget:
                State = SessionState.Suspended;
                break;
            case SessionEventKind.Resume when State == SessionState.Suspended:
                State = SessionState.WaitingForTarget;
                break;
            case SessionEventKind.Fatal:
                State = SessionState.Faulted;
                break;
            case SessionEventKind.Stop:
                State = SessionState.Stopping;
                break;
            case SessionEventKind.Cleaned when State == SessionState.Stopping:
                State = SessionState.Idle;
                terminated = true;
                break;
            default:
                return false;
        }
        StateSequence = checked(StateSequence + 1);
        return true;
    }

    /// <summary>为实际重新创建的客户端建立新连续时间轴。</summary>
    private void BeginEpoch()
    {
        CaptureEpoch = checked(CaptureEpoch + 1);
        State = SessionState.Starting;
    }
}
