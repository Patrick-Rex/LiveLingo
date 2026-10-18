using LiveLingo.Core.Audio;
using LiveLingo.Core.Subtitles;

namespace LiveLingo.Runtime.Pipeline;

/// <summary>保存完整未处理范围的调度快照；闭合仅表示调度范围固定，不代表字幕已定稿。</summary>
/// <param name="Request">完整结果关联身份。</param><param name="UnprocessedRange">仍需处理的完整来源范围。</param>
/// <param name="Closed">该快照是否必须按 FIFO 保留。</param>
/// <param name="OfferedTicks">宿主单调时钟入队时间，零表示未启用时延记录。</param>
/// <param name="RecoveryWindowSamples">零走默认窗口；非零仅用于同范围对齐失败后的有限扩窗实验。</param>
public sealed record ScheduledWork(PipelineRequest Request, SampleRange UnprocessedRange, bool Closed, long OfferedTicks = 0, int RecoveryWindowSamples = 0);

/// <summary>供单写入循环使用的一在途、一开放快照及有界闭合队列。</summary>
public sealed class BoundedWorkQueue
{
    private readonly Queue<ScheduledWork> closed = [];
    private readonly int capacity;
    private readonly long maximumBacklog;
    private readonly PipelineRequestKind kind;
    private ScheduledWork? pending;
    private ScheduledWork? running;

    /// <summary>建立某一种模型任务的有限队列。</summary>
    /// <param name="kind">本队列处理的任务类别。</param><param name="capacity">闭合队列上限。</param>
    /// <param name="maximumBacklog">所有未处理来源的并集采样数上限。</param>
    public BoundedWorkQueue(PipelineRequestKind kind, int capacity, long maximumBacklog)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBacklog);
        this.kind = kind; this.capacity = capacity; this.maximumBacklog = maximumBacklog;
    }

    /// <summary>获取当前在途请求；失效或取消不代表该计算已退出。</summary>
    public ScheduledWork? Running => running;
    /// <summary>获取最新开放请求。</summary>
    public ScheduledWork? Pending => pending;
    /// <summary>获取不能被合并覆盖的闭合任务数。</summary>
    public int ClosedCount => closed.Count;
    /// <summary>获取停止接收状态，正在执行的工作仍需完成确认。</summary>
    public bool Stopped { get; private set; }
    /// <summary>获取最近入队拒绝的结构化原因。</summary>
    public string? Rejection { get; private set; }
    /// <summary>获取未处理音频并集长度，不对重叠窗口重复计数。</summary>
    public long BacklogSamples => Coverage(AllWork());

    /// <summary>合并同一开放块的新快照或保留闭合快照；任何拒绝都不改变旧工作。</summary>
    /// <param name="work">调用方已校验身份的完整快照。</param><returns>队列接收成功时为 true。</returns>
    public bool Offer(ScheduledWork work)
    {
        if (Stopped) return Reject("stopped");
        if (work.Request.Kind != kind || work.Request.Id == Guid.Empty || work.Request.Sequence <= 0 ||
            work.UnprocessedRange.Length == 0 || !work.Request.Range.Contains(work.UnprocessedRange))
            return Reject("invalid_work");
        if (AllWork().Any(x => x.Request.Id == work.Request.Id)) return Reject("duplicate_request");
        if (closed.Any(x => x.Request.Segment.Id == work.Request.Segment.Id)) return Reject("already_closed");
        if (pending is not null && !CanReplace(pending, work)) return Reject("open_range_not_preserved");
        if (running is not null && running.Request.Segment == work.Request.Segment &&
            (!CanReplace(running, work) || running.Closed)) return Reject("running_range_not_preserved");
        if (work.Closed && closed.Count >= capacity) return Reject("closed_capacity");
        var candidates = closed.AsEnumerable().Append(work);
        if (running is not null) candidates = candidates.Append(running);
        if (Coverage(candidates) > maximumBacklog) return Reject("audio_backlog_capacity");
        if (work.Closed) { closed.Enqueue(work); pending = null; }
        else pending = work;
        Rejection = null;
        return true;
    }

    /// <summary>取得唯一在途许可，闭合队列优先；不会抢占仍在计算的请求。</summary>
    /// <returns>没有许可或没有工作时返回 null。</returns>
    public ScheduledWork? Take()
    {
        if (Stopped || running is not null) return null;
        if (closed.TryDequeue(out var next)) running = next;
        else { running = pending; pending = null; }
        return running;
    }

    /// <summary>仅在实际计算已结束后确认完成；取消信号本身不得调用此方法。</summary>
    /// <param name="requestId">确已退出的调用身份。</param>
    /// <exception cref="InvalidOperationException">完成身份不属于在途工作。</exception>
    public void Complete(Guid requestId)
    {
        if (running?.Request.Id != requestId) throw new InvalidOperationException("完成身份不匹配在途工作。");
        running = null;
    }

    /// <summary>代次失效时撤销排队工作，保留实际在途配额直到完成确认。</summary>
    public void InvalidatePending() { pending = null; closed.Clear(); }

    /// <summary>建立停止屏障，并撤销尚未执行的工作。</summary>
    public void Stop() { Stopped = true; InvalidatePending(); }

    /// <summary>比较同块递增版本及完整范围，禁止最新快照丢失旧锚点。</summary>
    /// <param name="old">仍需覆盖的工作。</param><param name="next">新工作。</param><returns>范围和身份守恒时为 true。</returns>
    private static bool CanReplace(ScheduledWork old, ScheduledWork next) =>
        old.Request.Identity == next.Request.Identity && old.Request.Segment == next.Request.Segment &&
        next.Request.Sequence > old.Request.Sequence && next.Request.SourceRevision >= old.Request.SourceRevision &&
        next.UnprocessedRange.Contains(old.UnprocessedRange);

    /// <summary>枚举有限队列的全部工作。</summary><returns>包含在途和开放快照的只读枚举。</returns>
    private IEnumerable<ScheduledWork> AllWork()
    {
        foreach (var item in closed) yield return item;
        if (pending is not null) yield return pending;
        if (running is not null) yield return running;
    }

    /// <summary>按采集身份分组后计算音频并集，跨代次不合并时间轴。</summary>
    /// <param name="items">有限的工作集合。</param><returns>并集采样数。</returns>
    private static long Coverage(IEnumerable<ScheduledWork> items)
    {
        long total = 0;
        foreach (var group in items.GroupBy(x => (x.Request.Identity.SessionId, x.Request.Identity.CaptureEpoch)))
        {
            long end = 0;
            foreach (var range in group.Select(x => x.UnprocessedRange).OrderBy(x => x.Start))
            { total = checked(total + Math.Max(0, range.End - Math.Max(end, range.Start))); end = Math.Max(end, range.End); }
        }
        return total;
    }

    /// <summary>拒绝新工作并保留之前的队列。</summary><param name="reason">原因码。</param><returns>固定 false。</returns>
    private bool Reject(string reason) { Rejection = reason; return false; }
}
