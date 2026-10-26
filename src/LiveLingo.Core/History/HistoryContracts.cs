using LiveLingo.Core.Subtitles;

namespace LiveLingo.Core.History;

/// <summary>区分仍在录制、正常结束和未完整结束的历史会话。</summary>
public enum HistorySessionState
{
    /// <summary>当前宿主仍在接收字幕。</summary>
    Recording,
    /// <summary>会话已正常停止并完成保存。</summary>
    Completed,
    /// <summary>上次运行中断或会话以错误结束。</summary>
    Interrupted,
    /// <summary>会话已经结束，但记录容量限制导致部分字幕未被保存。</summary>
    PartiallySaved
}

/// <summary>选择不承诺逐字时间对齐的文本导出格式。</summary>
public enum HistoryExportFormat
{
    /// <summary>UTF-8 纯文本。</summary>
    Text,
    /// <summary>便于阅读整理的 Markdown。</summary>
    Markdown
}

/// <summary>固定一次翻译会话的可读来源及开始时间，不包含模型或用户文件路径。</summary>
/// <param name="SessionId">不可复用的会话身份。</param><param name="ApplicationName">采集应用名称。</param>
/// <param name="SourceLanguage">本次识别语言策略。</param><param name="TranslationModel">本次翻译模型。</param>
/// <param name="StartedAt">开始时间，存储保留时区偏移。</param>
public sealed record HistorySessionInfo(Guid SessionId, string ApplicationName, string SourceLanguage, string TranslationModel, DateTimeOffset StartedAt);

/// <summary>提供历史列表所需的摘要，正文按需读取。</summary>
/// <param name="Info">固定会话信息。</param><param name="EndedAt">结束时间，未结束时为空。</param>
/// <param name="State">当前或恢复后的状态。</param><param name="EntryCount">已知有效字幕条数。</param>
/// <param name="Error">会话或持久化错误，不包含字幕正文。</param>
public sealed record HistorySessionSummary(HistorySessionInfo Info, DateTimeOffset? EndedAt, HistorySessionState State, int EntryCount, string? Error)
{
    /// <summary>获取可用于选择、删除和读取的会话身份。</summary>
    public Guid SessionId => Info.SessionId;
    /// <summary>获取最后成功保存摘要的时间；旧文件缺失时由存储层以文件修改时间恢复。</summary>
    public DateTimeOffset? LastSavedAt { get; init; }
}

/// <summary>标识历史中的一条语义块，不按文字相等合并真实重复。</summary>
/// <param name="CaptureEpoch">独立音频时间轴代次。</param><param name="SegmentId">稳定语义块身份。</param>
public readonly record struct HistoryEntryKey(long CaptureEpoch, Guid SegmentId);

/// <summary>保留原译文的版本关系和代次内来源范围。</summary>
/// <param name="CaptureEpoch">独立音频时间轴代次。</param><param name="Segment">最后接受的完整语义块。</param>
public sealed record HistoryEntry(long CaptureEpoch, SubtitleSegment Segment)
{
    /// <summary>获取删除或选择所用的稳定身份。</summary>
    public HistoryEntryKey Key => new(CaptureEpoch, Segment.Key.Id);
}

/// <summary>冻结一次读取或导出所见的会话正文。</summary>
/// <param name="Summary">本次读取的摘要。</param><param name="Entries">按代次、音频范围排列的有效条目。</param>
public sealed record HistoryDocument(HistorySessionSummary Summary, IReadOnlyList<HistoryEntry> Entries);

/// <summary>携带一页摘要和损坏文件的可见提示。</summary>
/// <param name="Sessions">本页摘要。</param><param name="TotalCount">搜索匹配总数。</param><param name="Warnings">被保留而未载入的文件错误。</param>
public sealed record HistoryPage(IReadOnlyList<HistorySessionSummary> Sessions, int TotalCount, IReadOnlyList<string> Warnings);

/// <summary>将历史写入状态与翻译运行状态分开报告。</summary>
/// <param name="SessionId">状态所属会话。</param><param name="IsSaving">是否有串行写入正在进行。</param>
/// <param name="CanRetry">是否存在尚未持久化的数据可重试。</param><param name="Error">失败原因；为空不代表尚在写入的数据已落盘。</param>
public sealed record HistorySaveStatus(Guid SessionId, bool IsSaving, bool CanRetry, string? Error);

/// <summary>快速接收权威字幕，停止时等待本会话历史落盘。</summary>
public interface IHistoryRecorder
{
    /// <summary>获取绑定的会话身份。</summary>
    Guid SessionId { get; }
    /// <summary>获取独立持久化状态。</summary>
    HistorySaveStatus Status { get; }
    /// <summary>通知保存状态，订阅者负责切换 UI 线程。</summary>
    event Action<HistorySaveStatus>? StatusChanged;
    /// <summary>接收完整快照，不在调用线程执行磁盘操作。</summary><param name="snapshot">权威字幕快照。</param>
    void Offer(SubtitleSnapshot snapshot);
    /// <summary>关闭记录入口并冲刷最后版本；失败通过状态返回，保留可重试内容。</summary>
    /// <param name="error">会话结束原因，正常停止为空。</param><param name="cancellationToken">等待预算，不取消已经登记的安全写入。</param><returns>冲刷完成任务。</returns>
    Task CompleteAsync(string? error = null, CancellationToken cancellationToken = default);
    /// <summary>重试当前未保存内容；失败仍保留独立错误。</summary><param name="cancellationToken">等待预算。</param><returns>重试结束任务。</returns>
    Task RetryAsync(CancellationToken cancellationToken = default);
}

/// <summary>隔离共享 UI 与本地历史文件，所有正文操作使用固定快照。</summary>
public interface ITranslationHistoryStore : IAsyncDisposable
{
    /// <summary>开始独立记录；调用不执行磁盘 I/O。</summary><param name="info">固定的会话来源。</param><returns>非阻塞记录入口。</returns>
    IHistoryRecorder BeginSession(HistorySessionInfo info);
    /// <summary>分页查询会话摘要，搜索只匹配应用名称。</summary>
    /// <param name="offset">跳过条数。</param><param name="limit">页大小，1 到 200。</param><param name="search">可选应用名片段。</param><param name="cancellationToken">取消查询。</param><returns>分页摘要与损坏提示。</returns>
    Task<HistoryPage> ListAsync(int offset = 0, int limit = 50, string? search = null, CancellationToken cancellationToken = default);
    /// <summary>按需冻结正文，活动会话包含尚未落盘的最新版本。</summary><param name="sessionId">目标会话。</param><param name="cancellationToken">取消读取。</param><returns>固定文档。</returns>
    Task<HistoryDocument> ReadAsync(Guid sessionId, CancellationToken cancellationToken = default);
    /// <summary>删除已停止会话中选定的条目；活动会话拒绝操作。</summary>
    /// <param name="sessionId">目标会话。</param><param name="entries">稳定条目身份。</param><param name="cancellationToken">取消提交。</param><returns>删除任务。</returns>
    Task DeleteEntriesAsync(Guid sessionId, IReadOnlyCollection<HistoryEntryKey> entries, CancellationToken cancellationToken = default);
    /// <summary>删除已停止的选定会话；集合包含活动会话时整次拒绝。</summary>
    /// <param name="sessionIds">待删会话。</param><param name="cancellationToken">取消提交。</param><returns>删除任务。</returns>
    Task DeleteSessionsAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken cancellationToken = default);
    /// <summary>清空已结束历史，保留所有活动会话。</summary><param name="cancellationToken">取消提交。</param><returns>清空任务。</returns>
    Task ClearAsync(CancellationToken cancellationToken = default);
    /// <summary>冻结并格式化选定内容，返回后再显示保存对话框。</summary>
    /// <param name="sessionId">目标会话。</param><param name="format">文本格式。</param><param name="selection">为空导出整场，否则仅导出选中身份。</param>
    /// <param name="cancellationToken">取消读取。</param><returns>不含 BOM 的文本内容。</returns>
    Task<string> ExportAsync(Guid sessionId, HistoryExportFormat format, IReadOnlyCollection<HistoryEntryKey>? selection = null, CancellationToken cancellationToken = default);
    /// <summary>按结束时间清理已停止会话；中断记录优先按最后保存时间，供启动、结束和每日调用。</summary>
    /// <param name="retentionDays">0 表示永久，其余支持 7、30、90 天。</param><param name="cancellationToken">取消清理。</param><returns>清理任务。</returns>
    Task CleanupAsync(int retentionDays, CancellationToken cancellationToken = default);
}
