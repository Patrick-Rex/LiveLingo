using LiveLingo.Core.Desktop;

namespace LiveLingo.App.Services;

/// <summary>合并完整快照，限制单个待执行调度，并在 UI 执行时重新校验会话。</summary>
/// <param name="post">异步投递 UI 回调，不能同步调用。</param><param name="apply">UI 线程应用完整状态。</param>
public sealed class SnapshotPump(Action<Action> post, Action<DesktopUpdate> apply)
{
    private readonly object gate = new();
    private Guid session;
    private long applied = -1;
    private DesktopUpdate? pending;
    private bool scheduled;
    private bool closed;

    /// <summary>显式建立新会话屏障；从 UI 线程调用，旧排队回调不获得新身份。</summary><param name="identity">新会话。</param>
    public void SwitchSession(Guid identity)
    {
        lock (gate) { session = identity; applied = -1; pending = null; }
    }

    /// <summary>在 UI 线程停止会话，先应用最后完整状态再撤销其身份，保留已排队调度槽。</summary>
    public void EndSession()
    {
        DesktopUpdate? value;
        lock (gate)
        {
            value = pending;
            if (closed || value?.SessionId != session || value.Sequence <= applied) value = null;
            pending = null; session = Guid.Empty; applied = -1;
        }
        if (value is not null) apply(value);
    }

    /// <summary>接收完整状态，仅保留当前会话中最新的序号。</summary><param name="value">后台发布的完整快照。</param>
    public void Offer(DesktopUpdate value)
    {
        lock (gate)
        {
            if (closed || value.SessionId != session || value.Sequence <= applied || value.Sequence <= (pending?.Sequence ?? -1)) return;
            pending = value;
            if (scheduled) return;
            scheduled = true;
        }
        post(Drain);
    }

    /// <summary>关闭后永久拒收；从 UI 线程与视图生命周期串行调用。</summary>
    public void Close() { lock (gate) { closed = true; pending = null; } }

    /// <summary>消费最新状态，释放调度槽；执行时重新检查而非相信投递时状态。</summary>
    private void Drain()
    {
        DesktopUpdate? value;
        lock (gate)
        {
            scheduled = false; value = pending; pending = null;
            if (closed || value is null || value.SessionId != session || value.Sequence <= applied) return;
            applied = value.Sequence;
        }
        apply(value);
    }
}
