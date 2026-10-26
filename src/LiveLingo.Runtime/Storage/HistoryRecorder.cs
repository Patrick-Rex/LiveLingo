using System.Collections.Immutable;
using LiveLingo.Core.History;
using LiveLingo.Core.Subtitles;

namespace LiveLingo.Runtime.Storage;

/// <summary>保留有界的会话快照并由唯一后台任务串行落盘，代次变化不会丢掉此前正文。</summary>
internal sealed class HistoryRecorder : IHistoryRecorder
{
    private readonly JsonTranslationHistoryStore store;
    private readonly HistorySessionInfo info;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly Dictionary<long, SubtitleSnapshot> latest = [];
    private readonly HashSet<long> dirty = [];
    private Task writer = Task.CompletedTask;
    private HistorySaveStatus status;
    private long version;
    private long savedVersion = -1;
    private DateTimeOffset? endedAt;
    private DateTimeOffset? lastSavedAt;
    private string? endError;
    private string? partialError;
    private string? saveError;
    private bool deleted;
    private bool failed;
    private bool writing;

    /// <summary>建立只保留内存状态的会话记录器，首次快照或结束时才启动写入。</summary>
    /// <param name="store">串行文件存储。</param><param name="info">固定会话信息。</param><param name="time">结束时间来源。</param>
    internal HistoryRecorder(JsonTranslationHistoryStore store, HistorySessionInfo info, TimeProvider time)
    {
        this.store = store; this.info = info; this.time = time;
        status = new(info.SessionId, false, false, null);
    }

    /// <inheritdoc />
    public Guid SessionId => info.SessionId;
    /// <inheritdoc />
    public HistorySaveStatus Status { get { lock (gate) return status; } }
    /// <inheritdoc />
    public event Action<HistorySaveStatus>? StatusChanged;
    /// <summary>获取是否仍可接收本会话字幕。</summary>
    internal bool IsActive { get { lock (gate) return endedAt is null && !deleted; } }
    /// <summary>获取用户是否已删除本记录，任何迟到写入都必须放弃。</summary>
    internal bool IsDeleted { get { lock (gate) return deleted; } }

    /// <inheritdoc />
    public void Offer(SubtitleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        HistorySaveStatus? notification = null;
        lock (gate)
        {
            if (deleted || endedAt is not null || snapshot.Identity.SessionId != SessionId) return;
            var epoch = snapshot.Identity.CaptureEpoch;
            if (epoch <= 0 || latest.Keys.Any(x => x > epoch)) return;
            if (latest.TryGetValue(epoch, out var old) && snapshot.StateSequence <= old.StateSequence) return;
            if ((!latest.ContainsKey(epoch) && latest.Count >= 257) || snapshot.Segments.Length > 4096 ||
                latest.Where(x => x.Key != epoch).Sum(x => x.Value.Segments.Length) + snapshot.Segments.Length > 12288)
            {
                if (partialError is null)
                {
                    partialError = "历史仅部分保存：超过记录容量，部分字幕未保留；重试无法恢复缺失内容，翻译仍可继续。";
                    version++;
                }
                if (!failed) StartWriterLocked();
                status = new(SessionId, writing, failed, ErrorLocked());
                notification = status;
            }
            else
            {
                latest[epoch] = snapshot; dirty.Add(epoch); version++;
                // 保存失败后仅更新有界内存快照，由用户明确重试，避免持续访问故障磁盘。
                if (!failed) notification = StartWriterLocked();
            }
        }
        if (notification is not null) Publish(notification);
    }

    /// <inheritdoc />
    public async Task CompleteAsync(string? error = null, CancellationToken cancellationToken = default)
    {
        Task pending; HistorySaveStatus? notification;
        lock (gate)
        {
            if (deleted) return;
            if (endedAt is null) { endedAt = time.GetUtcNow(); endError = error; version++; }
            failed = false; notification = StartWriterLocked(); pending = writer;
        }
        if (notification is not null) Publish(notification);
        await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RetryAsync(CancellationToken cancellationToken = default)
    {
        Task pending; HistorySaveStatus? notification;
        lock (gate)
        {
            if (deleted) return;
            failed = false; notification = StartWriterLocked(); pending = writer;
        }
        if (notification is not null) Publish(notification);
        await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>冻结完整内存历史，包括尚未写入的前后采集代次。</summary><returns>固定正文。</returns>
    internal HistoryDocument Freeze()
    {
        lock (gate)
        {
            var entries = latest.OrderBy(x => x.Key).SelectMany(x => JsonTranslationHistoryStore.ValidSegments(x.Value).Select(segment => new HistoryEntry(x.Key, segment))).ToArray();
            return new(SummaryLocked() with { EntryCount = entries.Length }, entries);
        }
    }

    /// <summary>获取当前摘要，不复制全部条目。</summary><returns>固定摘要。</returns>
    internal HistorySessionSummary GetSummary() { lock (gate) return SummaryLocked(); }

    /// <summary>用户删除后永久阻止迟到写入与重试。</summary>
    internal void MarkDeleted()
    {
        lock (gate) { deleted = true; latest.Clear(); dirty.Clear(); status = new(SessionId, false, false, null); }
    }

    /// <summary>由存储层释放文件锁和注册锁后通知删除，避免观察者与宿主锁倒置。</summary>
    internal void NotifyDeleted()
    {
        HistorySaveStatus value;
        lock (gate) { if (!deleted) return; value = status; }
        Publish(value);
    }

    /// <summary>同步已经落盘的条目删除，保留部分保存警告而不让记录器的旧正文重新出现。</summary>
    /// <param name="entries">已删除身份。</param><param name="savedAt">删除摘要的保存时间。</param>
    internal void ApplyPersistedDeletion(HashSet<HistoryEntryKey> entries, DateTimeOffset savedAt)
    {
        lock (gate)
        {
            foreach (var (epoch, snapshot) in latest.ToArray())
                latest[epoch] = snapshot with { Segments = snapshot.Segments.Where(x => !entries.Contains(new(epoch, x.Key.Id))).ToImmutableArray() };
            lastSavedAt = savedAt;
        }
    }

    /// <summary>在持有内存锁时创建唯一写入任务。</summary><returns>需要发布的新状态或空。</returns>
    private HistorySaveStatus? StartWriterLocked()
    {
        if (writing || version == savedVersion) return null;
        writing = true;
        saveError = null;
        status = new(SessionId, true, false, ErrorLocked());
        writer = Task.Run(DrainAsync);
        return status;
    }

    /// <summary>合并短时间内同代次完整快照，按固定批次写入，失败保留全部待写版本。</summary><returns>本次写入直到稳定或失败的任务。</returns>
    private async Task DrainAsync()
    {
        await Task.Delay(100).ConfigureAwait(false);
        while (true)
        {
            SubtitleSnapshot[] batch; HistorySessionSummary summary; long writingVersion;
            lock (gate)
            {
                if (deleted) { writing = false; return; }
                batch = dirty.Order().Select(epoch => latest[epoch]).ToArray();
                summary = SummaryLocked(); writingVersion = version;
            }
            DateTimeOffset persistedAt;
            try { persistedAt = await store.SaveAsync(this, batch, summary).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
            {
                HistorySaveStatus failure;
                lock (gate)
                {
                    if (deleted) { writing = false; return; }
                    failed = true; writing = false; saveError = "历史未完整保存：" + error.Message;
                    failure = status = new(SessionId, false, true, ErrorLocked());
                }
                Publish(failure); return;
            }
            HistorySaveStatus? completed = null; bool release = false;
            lock (gate)
            {
                if (deleted) { writing = false; return; }
                foreach (var snapshot in batch)
                    if (ReferenceEquals(latest[snapshot.Identity.CaptureEpoch], snapshot)) dirty.Remove(snapshot.Identity.CaptureEpoch);
                savedVersion = writingVersion;
                lastSavedAt = persistedAt;
                if (version == writingVersion)
                {
                    writing = false;
                    saveError = null;
                    completed = status = new(SessionId, false, false, ErrorLocked());
                    release = endedAt is not null && partialError is null;
                }
            }
            if (completed is not null)
            {
                if (release) store.Release(this);
                Publish(completed); return;
            }
        }
    }

    /// <summary>在内存锁内生成与当前全部代次对应的摘要。</summary><returns>当前摘要。</returns>
    private HistorySessionSummary SummaryLocked() => new(info, endedAt,
        endedAt is null ? HistorySessionState.Recording : partialError is not null ? HistorySessionState.PartiallySaved :
            endError is null ? HistorySessionState.Completed : HistorySessionState.Interrupted,
        latest.Values.Sum(x => x.Segments.Count(segment => !x.RetiredIds.Contains(segment.Key.Id) && !string.IsNullOrWhiteSpace(segment.Source))),
        ErrorLocked() ?? endError) { LastSavedAt = lastSavedAt };

    /// <summary>将不可恢复的内容缺失与可重试的磁盘错误分开保留，保存成功不能掩盖缺失。</summary><returns>当前持久化错误。</returns>
    private string? ErrorLocked() => partialError is null ? saveError : saveError is null ? partialError : partialError + " " + saveError;

    /// <summary>隔离观察者异常，保存结果通知不能让翻译或后台写入失效。</summary><param name="value">固定状态。</param>
    private void Publish(HistorySaveStatus value)
    {
        if (StatusChanged is not { } handlers) return;
        foreach (Action<HistorySaveStatus> handler in handlers.GetInvocationList())
            try { handler(value); } catch (Exception) { /* 状态观察者不拥有持久化事务。 */ }
    }
}
