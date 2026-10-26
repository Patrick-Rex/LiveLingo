using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LiveLingo.Core.Audio;
using LiveLingo.Core.Desktop;
using LiveLingo.Core.History;
using LiveLingo.Core.Subtitles;

namespace LiveLingo.Runtime.Storage;

/// <summary>在数据根下按会话和采集代次保存字幕，所有文件操作串行且不阻塞采集回调。</summary>
/// <param name="dataPath">已经提交的非 C 盘应用数据根。</param><param name="timeProvider">保留期和结束时间来源。</param>
public sealed class JsonTranslationHistoryStore(string dataPath, TimeProvider? timeProvider = null) : ITranslationHistoryStore
{
    private readonly string root = GetRoot(dataPath);
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim files = new(1, 1);
    private readonly object registryGate = new();
    private readonly Dictionary<Guid, HistoryRecorder> recorders = [];
    private bool disposed;

    /// <summary>获取当前实例固定的历史根，不随后续设置编辑改变。</summary>
    public string RootPath => root;

    /// <inheritdoc />
    public IHistoryRecorder BeginSession(HistorySessionInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(info.ApplicationName)) throw new ArgumentException("历史会话需要有效身份和应用名称。", nameof(info));
        lock (registryGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (recorders.ContainsKey(info.SessionId)) throw new InvalidOperationException("历史会话身份不能重复。");
            if (recorders.Count >= 8) throw new InvalidOperationException("有过多会话尚未保存，请先重试历史保存或删除这些会话。");
            var recorder = new HistoryRecorder(this, info, time);
            recorders.Add(info.SessionId, recorder);
            return recorder;
        }
    }

    /// <inheritdoc />
    public async Task<HistoryPage> ListAsync(int offset = 0, int limit = 50, string? search = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        await files.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ListCoreAsync(offset, limit, search, cancellationToken).ConfigureAwait(false); }
        finally { files.Release(); }
    }

    /// <inheritdoc />
    public async Task<HistoryDocument> ReadAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await files.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadCoreAsync(sessionId, cancellationToken).ConfigureAwait(false); }
        finally { files.Release(); }
    }

    /// <inheritdoc />
    public async Task DeleteEntriesAsync(Guid sessionId, IReadOnlyCollection<HistoryEntryKey> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return;
        await files.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStopped([sessionId]);
            var recorder = FindRecorder(sessionId);
            if (recorder is not null && (recorder.Status.IsSaving || recorder.Status.CanRetry))
                throw new InvalidOperationException("这场会话尚未完整保存，请先重试保存，或直接删除整场会话。");
            var document = await ReadCoreAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var selected = entries.ToHashSet();
            var remaining = document.Entries.Where(x => !selected.Contains(x.Key)).ToArray();
            foreach (var group in document.Entries.GroupBy(x => x.CaptureEpoch))
            {
                if (!group.Any(x => selected.Contains(x.Key))) continue;
                var segment = new HistoryEpochFile(1, sessionId, group.Key, remaining.Where(x => x.CaptureEpoch == group.Key).Select(x => x.Segment).ToImmutableArray());
                await WriteAtomicAsync(EpochPath(sessionId, group.Key), segment, HistoryJsonContext.Default.HistoryEpochFile, cancellationToken).ConfigureAwait(false);
            }
            var savedAt = await WriteSummaryAsync(document.Summary with { EntryCount = remaining.Length }, cancellationToken).ConfigureAwait(false);
            recorder?.ApplyPersistedDeletion(selected, savedAt);
        }
        finally { files.Release(); }
    }

    /// <inheritdoc />
    public async Task DeleteSessionsAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        var deleted = new List<HistoryRecorder>();
        await files.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStopped(sessionIds);
            foreach (var id in sessionIds.Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DeleteSessionCore(id) is { } recorder) deleted.Add(recorder);
            }
        }
        finally { files.Release(); foreach (var recorder in deleted) recorder.NotifyDeleted(); }
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var deleted = new List<HistoryRecorder>();
        await files.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Guid[] ids;
            lock (registryGate) ids = recorders.Keys.ToArray();
            var all = EnumerateSessions().Select(x => x.Id).Concat(ids).Distinct().ToArray();
            foreach (var id in all)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (FindRecorder(id)?.IsActive == true) continue;
                if (DeleteSessionCore(id) is { } recorder) deleted.Add(recorder);
            }
        }
        finally { files.Release(); foreach (var recorder in deleted) recorder.NotifyDeleted(); }
    }

    /// <inheritdoc />
    public async Task<string> ExportAsync(Guid sessionId, HistoryExportFormat format, IReadOnlyCollection<HistoryEntryKey>? selection = null, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        var document = await ReadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var selected = selection?.ToHashSet();
        var entries = selected is null ? document.Entries : document.Entries.Where(x => selected.Contains(x.Key)).ToArray();
        var markdown = format == HistoryExportFormat.Markdown;
        var text = new StringBuilder();
        var title = Clean(document.Summary.Info.ApplicationName, markdown);
        text.Append(markdown ? "# " : "").Append(title).Append('\n');
        text.Append("会话：").Append(document.Summary.SessionId).Append('\n');
        text.Append("开始：").Append(FormatTime(document.Summary.Info.StartedAt)).Append('\n');
        text.Append("结束：").Append(document.Summary.EndedAt is { } ended ? FormatTime(ended) : "尚未结束").Append('\n');
        text.Append("状态：").Append(StateText(document.Summary.State)).Append('\n');
        text.Append("识别语言：").Append(Clean(document.Summary.Info.SourceLanguage, markdown)).Append('\n');
        text.Append("翻译模型：").Append(Clean(document.Summary.Info.TranslationModel, markdown)).Append('\n');
        if (document.Summary.Error is { } error) text.Append("记录提示：").Append(Clean(error, markdown)).Append('\n');
        text.Append('\n');
        long previous = -1;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.CaptureEpoch != previous)
            {
                previous = entry.CaptureEpoch;
                text.Append(markdown ? "## " : "").Append("采集片段 ").Append(previous).Append("（时间从本片段起算）\n\n");
            }
            var segment = entry.Segment;
            var state = segment.Phase == SubtitlePhase.Final ? "定稿" : segment.Phase == SubtitlePhase.Revised ? "修订中" : "草稿";
            if (segment.TranslatedSourceRevision != segment.SourceRevision) state += " · 译文待更新";
            if (segment.TranslationState == TranslationState.Failed) state += " · 翻译失败";
            text.Append('[').Append(state).Append("]\n原文：").Append(Clean(segment.Source, markdown)).Append('\n');
            text.Append("译文：").Append(Clean(segment.Translation ?? "（暂无译文）", markdown)).Append("\n\n");
        }
        return text.ToString();
    }

    /// <inheritdoc />
    public async Task CleanupAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        if (retentionDays is not (0 or 7 or 30 or 90)) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        if (retentionDays == 0) return;
        var deleted = new List<HistoryRecorder>();
        await files.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var summaries = await ListCoreAsync(0, int.MaxValue, null, cancellationToken).ConfigureAwait(false);
            var before = time.GetUtcNow().AddDays(-retentionDays);
            foreach (var summary in summaries.Sessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var retentionOrigin = summary.State == HistorySessionState.Interrupted
                    ? summary.LastSavedAt ?? summary.EndedAt ?? summary.Info.StartedAt
                    : summary.EndedAt ?? summary.LastSavedAt ?? summary.Info.StartedAt;
                if (summary.State == HistorySessionState.Recording || retentionOrigin >= before) continue;
                if (DeleteSessionCore(summary.SessionId) is { } recorder) deleted.Add(recorder);
            }
        }
        finally { files.Release(); foreach (var recorder in deleted) recorder.NotifyDeleted(); }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        HistoryRecorder[] active;
        lock (registryGate)
        {
            if (disposed) return;
            disposed = true; active = recorders.Values.ToArray();
        }
        foreach (var recorder in active) await recorder.CompleteAsync(recorder.IsActive ? "应用退出时会话尚未停止" : null).ConfigureAwait(false);
    }

    /// <summary>串行提交一组独立代次快照，摘要在正文之后替换。</summary>
    /// <param name="recorder">拥有这些快照的记录器。</param><param name="epochs">本次固定的待写版本。</param>
    /// <param name="summary">与本批快照对应的摘要。</param><returns>最后摘要成功提交的时间。</returns>
    internal async Task<DateTimeOffset> SaveAsync(HistoryRecorder recorder, SubtitleSnapshot[] epochs, HistorySessionSummary summary)
    {
        await files.WaitAsync().ConfigureAwait(false);
        try
        {
            if (recorder.IsDeleted) return time.GetUtcNow();
            var directory = SessionPath(recorder.SessionId);
            EnsureDirectory(directory);
            var summaryPath = Path.Combine(directory, "summary.json");
            if (!File.Exists(summaryPath)) await WriteSummaryAsync(summary with { State = HistorySessionState.Recording, EndedAt = null, EntryCount = 0 }, CancellationToken.None).ConfigureAwait(false);
            foreach (var snapshot in epochs)
            {
                var epoch = new HistoryEpochFile(1, recorder.SessionId, snapshot.Identity.CaptureEpoch, ValidSegments(snapshot));
                await WriteAtomicAsync(EpochPath(recorder.SessionId, epoch.CaptureEpoch), epoch, HistoryJsonContext.Default.HistoryEpochFile, CancellationToken.None).ConfigureAwait(false);
            }
            return await WriteSummaryAsync(summary, CancellationToken.None).ConfigureAwait(false);
        }
        finally { files.Release(); }
    }

    /// <summary>已停止且保存成功后释放完整内存正文，列表改为按需读取文件。</summary><param name="recorder">完成记录器。</param>
    internal void Release(HistoryRecorder recorder)
    {
        lock (registryGate)
            if (recorders.TryGetValue(recorder.SessionId, out var current) && ReferenceEquals(current, recorder)) recorders.Remove(recorder.SessionId);
    }

    /// <summary>提取当前代次有效非空条目，保留同文不同身份的真实重复。</summary><param name="snapshot">完整权威快照。</param><returns>有效段。</returns>
    internal static ImmutableArray<SubtitleSegment> ValidSegments(SubtitleSnapshot snapshot) => snapshot.Segments
        .Where(x => !snapshot.RetiredIds.Contains(x.Key.Id) && !string.IsNullOrWhiteSpace(x.Source))
        .OrderBy(x => x.Range.Start).ThenBy(x => x.Key.Id).ToImmutableArray();

    /// <summary>读取摘要并叠加本进程尚未完成的记录，不载入正文。</summary>
    /// <param name="offset">跳过条数。</param><param name="limit">页大小。</param><param name="search">应用名搜索。</param><param name="token">取消读取。</param><returns>摘要页。</returns>
    private async Task<HistoryPage> ListCoreAsync(int offset, int limit, string? search, CancellationToken token)
    {
        var values = new Dictionary<Guid, HistorySessionSummary>(); var warnings = new List<string>();
        foreach (var item in EnumerateSessions())
        {
            token.ThrowIfCancellationRequested();
            try { values[item.Id] = Recover(await ReadSummaryAsync(item.Id, token).ConfigureAwait(false)); }
            catch (Exception error) when (IsStorageError(error)) { warnings.Add($"历史 {item.Id:N} 未加载，原文件保留：{error.Message}"); }
        }
        lock (registryGate)
            foreach (var recorder in recorders.Values) values[recorder.SessionId] = recorder.GetSummary();
        var matching = values.Values.Where(x => string.IsNullOrWhiteSpace(search) || x.Info.ApplicationName.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Info.StartedAt).ThenBy(x => x.SessionId).ToArray();
        return new(matching.Skip(offset).Take(limit).ToArray(), matching.Length, warnings);
    }

    /// <summary>读取活动固定快照或所选会话的代次文件，不伪造跨代次时间。</summary>
    /// <param name="id">会话身份。</param><param name="token">取消读取。</param><returns>正文快照。</returns>
    private async Task<HistoryDocument> ReadCoreAsync(Guid id, CancellationToken token)
    {
        if (FindRecorder(id) is { } recorder) return recorder.Freeze();
        var summary = Recover(await ReadSummaryAsync(id, token).ConfigureAwait(false));
        var entries = new List<HistoryEntry>();
        var epochs = new HashSet<long>();
        foreach (var path in Directory.EnumerateFiles(SessionPath(id), "epoch-*.json").Order(StringComparer.Ordinal))
        {
            var epoch = await ReadFileAsync(path, HistoryJsonContext.Default.HistoryEpochFile, 64 * 1024 * 1024, token).ConfigureAwait(false);
            if (epoch.SchemaVersion != 1 || epoch.SessionId != id || epoch.CaptureEpoch <= 0 || epoch.Segments.IsDefault || epoch.Segments.Length > 4096 ||
                !epochs.Add(epoch.CaptureEpoch) || epochs.Count > 257 || epoch.Segments.Any(x => x is null || x.Key.Id == Guid.Empty || x.Key.Generation <= 0 ||
                    x.Source is null || !Enum.IsDefined(x.Phase) || !Enum.IsDefined(x.TranslationState)) || epoch.Segments.Select(x => x.Key.Id).Distinct().Count() != epoch.Segments.Length)
                throw new InvalidDataException("历史正文版本、身份或容量不受支持。");
            entries.AddRange(epoch.Segments.Select(x => new HistoryEntry(epoch.CaptureEpoch, x)));
            if (entries.Count > 12288) throw new InvalidDataException("历史会话超出正文容量。");
        }
        var ordered = entries.OrderBy(x => x.CaptureEpoch).ThenBy(x => x.Segment.Range.Start).ThenBy(x => x.Segment.Key.Id).ToArray();
        return new(summary with { EntryCount = ordered.Length }, ordered);
    }

    /// <summary>读取并校验一份轻量会话摘要。</summary><param name="id">会话身份。</param><param name="token">取消读取。</param><returns>磁盘摘要。</returns>
    private async Task<HistorySessionSummary> ReadSummaryAsync(Guid id, CancellationToken token)
    {
        if (Directory.Exists(root)) RejectLink(root);
        if (Directory.Exists(SessionPath(id))) RejectLink(SessionPath(id));
        var file = await ReadFileAsync(Path.Combine(SessionPath(id), "summary.json"), HistoryJsonContext.Default.HistorySummaryFile, 65536, token).ConfigureAwait(false);
        if (file.SchemaVersion != 1 || file.Summary is null || file.Summary.Info is null || file.Summary.Info.SessionId != id ||
            string.IsNullOrWhiteSpace(file.Summary.Info.ApplicationName) || !Enum.IsDefined(file.Summary.State)) throw new InvalidDataException("历史摘要版本或身份不受支持。");
        return file.Summary.LastSavedAt is not null ? file.Summary : file.Summary with
        { LastSavedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(SessionPath(id), "summary.json")), TimeSpan.Zero) };
    }

    /// <summary>将失去本进程记录器的未结束会话显示为中断，不启动任何模型。</summary><param name="summary">磁盘摘要。</param><returns>恢复状态。</returns>
    private static HistorySessionSummary Recover(HistorySessionSummary summary) => summary.State == HistorySessionState.Recording
        ? summary with { State = HistorySessionState.Interrupted, Error = summary.Error ?? "上次运行未正常结束，显示最后成功保存的内容。" } : summary;

    /// <summary>写入与正文对应的轻量摘要并记录本次保存时间。</summary><param name="summary">摘要。</param><param name="token">取消提交。</param><returns>本次成功提交的保存时间。</returns>
    private async Task<DateTimeOffset> WriteSummaryAsync(HistorySessionSummary summary, CancellationToken token)
    {
        var savedAt = time.GetUtcNow();
        await WriteAtomicAsync(Path.Combine(SessionPath(summary.SessionId), "summary.json"), new HistorySummaryFile(1, summary with { LastSavedAt = savedAt }), HistoryJsonContext.Default.HistorySummaryFile, token).ConfigureAwait(false);
        return savedAt;
    }

    /// <summary>拒绝对仍在录制的会话进行修改，避免删除后被流水线复活。</summary><param name="ids">目标身份。</param>
    private void EnsureStopped(IEnumerable<Guid> ids)
    {
        lock (registryGate)
            if (ids.Any(id => recorders.TryGetValue(id, out var recorder) && recorder.IsActive)) throw new InvalidOperationException("请先停止翻译，再删除历史。");
    }

    /// <summary>在串行文件入口内删除会话并撤销重试，返回等待锁外通知的记录器。</summary><param name="id">目标身份。</param><returns>被撤销的记录器或空。</returns>
    private HistoryRecorder? DeleteSessionCore(Guid id)
    {
        var path = SessionPath(id);
        if (Directory.Exists(root)) RejectLink(root);
        if (Directory.Exists(path)) { RejectLink(path); Directory.Delete(path, recursive: true); }
        HistoryRecorder? recorder;
        lock (registryGate) recorders.Remove(id, out recorder);
        recorder?.MarkDeleted();
        return recorder;
    }

    /// <summary>查找当前进程持有的未完成记录器。</summary><param name="id">会话身份。</param><returns>记录器或空。</returns>
    private HistoryRecorder? FindRecorder(Guid id) { lock (registryGate) return recorders.GetValueOrDefault(id); }

    /// <summary>仅枚举以本服务身份规则命名的会话目录。</summary><returns>已知目录身份。</returns>
    private IEnumerable<(Guid Id, string Path)> EnumerateSessions()
    {
        if (!Directory.Exists(root)) yield break;
        RejectLink(root);
        foreach (var path in Directory.EnumerateDirectories(root))
            if (Guid.TryParseExact(Path.GetFileName(path), "N", out var id)) { RejectLink(path); yield return (id, path); }
    }

    /// <summary>获取只由可信 GUID 构成的会话路径。</summary><param name="id">会话身份。</param><returns>绝对路径。</returns>
    private string SessionPath(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("历史会话身份不能为空。", nameof(id));
        return Path.Combine(root, id.ToString("N"));
    }

    /// <summary>获取独立采集代次文件名。</summary><param name="id">会话身份。</param><param name="epoch">采集代次。</param><returns>绝对文件路径。</returns>
    private string EpochPath(Guid id, long epoch) => Path.Combine(SessionPath(id), $"epoch-{epoch:D12}.json");

    /// <summary>验证资源盘约束并固定历史根。</summary><param name="path">应用数据根。</param><returns>规范化历史根。</returns>
    private static string GetRoot(string path) { DesktopSettings.ValidateDataPath(path); return Path.Combine(Path.GetFullPath(path), "history"); }

    /// <summary>创建写入目录并拒绝通过目录链接写到历史根以外。</summary><param name="path">会话目录。</param>
    private void EnsureDirectory(string path) { Directory.CreateDirectory(root); RejectLink(root); Directory.CreateDirectory(path); RejectLink(path); }

    /// <summary>拒绝跟随用户替换的目录链接。</summary><param name="path">待访问路径。</param>
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("历史目录不能是符号链接或目录联接。");
    }

    /// <summary>限制文件大小并使用生成的元数据读取正文。</summary>
    /// <typeparam name="T">文件类型。</typeparam><param name="path">文件路径。</param><param name="type">生成元数据。</param><param name="maximum">最大字节数。</param><param name="token">取消读取。</param><returns>文件内容。</returns>
    private static async Task<T> ReadFileAsync<T>(string path, JsonTypeInfo<T> type, long maximum, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("历史文件不存在。", path);
        RejectLink(path);
        if (info.Length > maximum) throw new InvalidDataException("历史文件超出大小限制。");
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(stream, type, token).ConfigureAwait(false) ?? throw new InvalidDataException("历史文件内容为空。");
    }

    /// <summary>在同目录提交临时文件，取消或失败保留旧文件并清理临时文件。</summary>
    /// <typeparam name="T">文件类型。</typeparam><param name="path">正式路径。</param><param name="value">固定内容。</param><param name="type">生成元数据。</param><param name="token">提交前取消。</param><returns>原子替换任务。</returns>
    private static async Task WriteAtomicAsync<T>(string path, T value, JsonTypeInfo<T> type, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, type, token).ConfigureAwait(false);
                await stream.WriteAsync("\n"u8.ToArray(), token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false); stream.Flush(true);
            }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>识别可显示并保留原文件的本地存储错误。</summary><param name="error">异常。</param><returns>是否属于可报告存储错误。</returns>
    private static bool IsStorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException;

    /// <summary>将状态转换为导出文案。</summary><param name="state">会话状态。</param><returns>可读状态。</returns>
    private static string StateText(HistorySessionState state) => state switch
    {
        HistorySessionState.Recording => "翻译中（导出时快照）", HistorySessionState.Completed => "已结束",
        HistorySessionState.PartiallySaved => "部分保存", _ => "已中断"
    };

    /// <summary>格式化可追溯时区的会话时间，不依赖机器区域设置。</summary><param name="time">会话时间。</param><returns>导出时间。</returns>
    private static string FormatTime(DateTimeOffset time) => time.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>统一换行并转义 Markdown 内容，避免模型正文变成活动标记。</summary><param name="value">正文。</param><param name="markdown">是否导出 Markdown。</param><returns>安全可读文本。</returns>
    private static string Clean(string value, bool markdown)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!markdown) return normalized;
        foreach (var character in new[] { "\\", "`", "*", "_", "{", "}", "[", "]", "<", ">", "#", "|" }) normalized = normalized.Replace(character, "\\" + character, StringComparison.Ordinal);
        return normalized;
    }
}

/// <summary>保存摘要文件的独立兼容版本。</summary><param name="SchemaVersion">文件版本。</param><param name="Summary">会话摘要。</param>
internal sealed record HistorySummaryFile(int SchemaVersion, HistorySessionSummary Summary);

/// <summary>保存单个采集代次的最后权威正文，不保存音频。</summary>
/// <param name="SchemaVersion">文件版本。</param><param name="SessionId">会话身份。</param><param name="CaptureEpoch">独立音频代次。</param><param name="Segments">有效字幕。</param>
internal sealed record HistoryEpochFile(int SchemaVersion, Guid SessionId, long CaptureEpoch, ImmutableArray<SubtitleSegment> Segments);

/// <summary>为本地历史生成受控 JSON 元数据。</summary>
[JsonSourceGenerationOptions(WriteIndented = true, NewLine = "\n", UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = new[] { typeof(HistorySampleRangeConverter) })]
[JsonSerializable(typeof(HistorySummaryFile))]
[JsonSerializable(typeof(HistoryEpochFile))]
internal partial class HistoryJsonContext : JsonSerializerContext;

/// <summary>恢复只读采样范围的真实边界，避免结构体默认构造把范围静默归零。</summary>
internal sealed class HistorySampleRangeConverter : JsonConverter<SampleRange>
{
    /// <inheritdoc />
    public override SampleRange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("Start", out var start) || !value.TryGetProperty("End", out var end) ||
            !start.TryGetInt64(out var first) || !end.TryGetInt64(out var last) || first < 0 || last < first)
            throw new JsonException("历史音频范围无效。");
        return new(first, last);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, SampleRange value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteNumber("Start", value.Start); writer.WriteNumber("End", value.End); writer.WriteEndObject();
    }
}
