using LiveLingo.App.Services;
using LiveLingo.App.ViewModels;
using LiveLingo.Core.History;
using LiveLingo.Core.Subtitles;

namespace LiveLingo.App.Tests;

/// <summary>验证历史页面的选择、确认、固定导出与生命周期，不依赖磁盘或原生对话框。</summary>
public sealed class HistoryViewModelTests
{
    /// <summary>运行中的记录可阅读复制导出，但任何选择均不能绕过删除限制。</summary><returns>交互验证任务。</returns>
    [Fact]
    public async Task RecordingCanBeCopiedAndExportedButCannotBeDeleted()
    {
        var document = CreateDocument("player.exe", HistorySessionState.Recording, 2);
        var store = new MemoryHistoryStore(document); var interaction = new MemoryHistoryInteraction();
        var model = await CreateModelAsync(store, interaction);
        try
        {
            Assert.True(model.CanRead); Assert.False(Assert.Single(model.Sessions).CanDelete);
            model.Entries[0].IsChecked = true; model.Sessions[0].IsChecked = true;
            Assert.False(model.CanDeleteEntries); Assert.False(model.CanDeleteSessions);
            await model.DeleteEntriesCommand.ExecuteAsync(null); await model.DeleteSessionsCommand.ExecuteAsync(null);
            Assert.Empty(interaction.Confirmations); Assert.Empty(store.DeletedEntries); Assert.Empty(store.DeletedSessions);
            await model.CopyCommand.ExecuteAsync(null); await model.ExportTextCommand.ExecuteAsync(null);
            Assert.Equal("原文 0\n译文 0", Assert.Single(interaction.Copies));
            Assert.Equal(Assert.Single(interaction.Copies), Assert.Single(interaction.Saves).Text);
            Assert.Equal(HistoryExportFormat.Text, interaction.Saves[0].Format);
        }
        finally { await model.CloseAsync(); }
    }

    /// <summary>取消删除不写存储，确认只提交已冻结的条目或会话身份。</summary><returns>确认范围验证任务。</returns>
    [Fact]
    public async Task EndedEntriesAndSessionsRequireConfirmationAndRespectCancellation()
    {
        var first = CreateDocument("first.exe", HistorySessionState.Completed, 3);
        var second = CreateDocument("second.exe", HistorySessionState.Interrupted, 1);
        var store = new MemoryHistoryStore(first, second); var interaction = new MemoryHistoryInteraction { ConfirmResult = false };
        var model = await CreateModelAsync(store, interaction);
        try
        {
            model.Entries[0].IsChecked = true; model.Entries[2].IsChecked = true;
            await model.DeleteEntriesCommand.ExecuteAsync(null);
            Assert.Contains("2 条", Assert.Single(interaction.Confirmations)); Assert.Empty(store.DeletedEntries); Assert.Equal(3, model.Entries.Count);
            interaction.ConfirmResult = true;
            await model.DeleteEntriesCommand.ExecuteAsync(null);
            var deleted = Assert.Single(store.DeletedEntries);
            Assert.Equal(first.Summary.SessionId, deleted.SessionId);
            Assert.Equal(new[] { first.Entries[0].Key, first.Entries[2].Key }, deleted.Keys);
            Assert.Equal(first.Entries[1].Key, Assert.Single(model.Entries).Entry.Key);
            foreach (var item in model.Sessions) item.IsChecked = true;
            interaction.ConfirmResult = false; await model.DeleteSessionsCommand.ExecuteAsync(null);
            Assert.Empty(store.DeletedSessions); Assert.Equal(2, model.Sessions.Count);
            Assert.Contains("2 场", interaction.Confirmations.Last());
            interaction.ConfirmResult = true; await model.DeleteSessionsCommand.ExecuteAsync(null);
            Assert.Equal(new[] { first.Summary.SessionId, second.Summary.SessionId }, Assert.Single(store.DeletedSessions));
            Assert.Empty(model.Sessions); Assert.Empty(model.Entries);
        }
        finally { await model.CloseAsync(); }
    }

    /// <summary>导出先固定点击时选择与正文，再打开保存交互；等待期间的修订和选择不会改变内容。</summary><returns>导出竞态验证任务。</returns>
    [Fact]
    public async Task ExportFreezesSelectedContentBeforeOpeningSaveInteraction()
    {
        var original = CreateDocument("player.exe", HistorySessionState.Recording, 3);
        var store = new MemoryHistoryStore(original) { ExportGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var interaction = new MemoryHistoryInteraction { SaveResult = false, SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var model = await CreateModelAsync(store, interaction);
        try
        {
            model.Entries[1].IsChecked = true;
            var export = model.ExportMarkdownCommand.ExecuteAsync(null);
            await store.ExportEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Empty(interaction.Saves); Assert.True(model.Busy);
            model.ClearSelectionCommand.Execute(null); model.Entries[2].IsChecked = true;
            store.Documents[original.Summary.SessionId] = original with
            {
                Entries = original.Entries.Select(x => x with { Segment = x.Segment with { Translation = "后来修订的译文" } }).ToArray()
            };
            store.ExportGate.SetResult();
            await interaction.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var saved = Assert.Single(interaction.Saves);
            Assert.Equal("原文 1\n译文 1", saved.Text);
            Assert.Equal(HistoryExportFormat.Markdown, saved.Format);
            Assert.StartsWith("LiveLingo-", saved.SuggestedName, StringComparison.Ordinal);
            Assert.Equal(new[] { original.Entries[1].Key }, Assert.Single(store.Exports).Selection);
            interaction.SaveGate.SetResult(); await export;
            Assert.Equal("已取消导出", model.Message); Assert.True(model.CanRead);
        }
        finally { await model.CloseAsync(); }
    }

    /// <summary>切换库后旧正文的迟到读取不会污染新页面，后续刷新只读取新库。</summary><returns>库切换竞态验证任务。</returns>
    [Fact]
    public async Task SwitchingLibrariesRejectsLateReadResults()
    {
        var oldStore = new MemoryHistoryStore(CreateDocument("old.exe", HistorySessionState.Completed, 2))
        { ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var newDocument = CreateDocument("new.exe", HistorySessionState.Completed, 1);
        var newStore = new MemoryHistoryStore(newDocument);
        var model = new HistoryViewModel(new MemoryHistoryInteraction());
        try
        {
            model.Attach(oldStore, @"D:\old\history");
            var oldRefresh = model.RefreshCommand.ExecutionTask!;
            await oldStore.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            model.Attach(newStore, @"D:\new\history");
            Assert.Empty(model.Entries); Assert.Empty(model.Sessions);
            oldStore.ReadGate.SetResult(); await oldRefresh;
            Assert.Empty(model.Entries); Assert.DoesNotContain("old.exe", model.DocumentTitle, StringComparison.Ordinal);
            await model.PollAsync();
            Assert.Equal(newDocument.Summary.SessionId, Assert.Single(model.Sessions).Summary.SessionId);
            Assert.Equal(newDocument.Entries[0].Key, Assert.Single(model.Entries).Entry.Key);
            Assert.Equal(@"D:\new\history", model.Location);
        }
        finally { await model.CloseAsync(); }
    }

    /// <summary>会话和正文分页各自有界，正文搜索匹配原译文且不清除跨页选择。</summary><returns>分页与搜索验证任务。</returns>
    [Fact]
    public async Task PaginationAndTextSearchPreserveSelections()
    {
        var documents = Enumerable.Range(0, 23).Select(index => CreateDocument($"app{index:D2}.exe", HistorySessionState.Completed, index == 0 ? 205 : 1)).ToArray();
        var store = new MemoryHistoryStore(documents); var interaction = new MemoryHistoryInteraction();
        var model = await CreateModelAsync(store, interaction);
        try
        {
            Assert.Equal(20, model.Sessions.Count); Assert.Equal(100, model.Entries.Count);
            model.Entries[0].IsChecked = true;
            model.NextEntriesCommand.Execute(null); Assert.Equal(100, model.Entries.Count); Assert.Equal("原文 100", model.Entries[0].Source);
            model.Entries[0].IsChecked = true;
            model.NextEntriesCommand.Execute(null); Assert.Equal(5, model.Entries.Count);
            model.TextSearch = "译文 204"; Assert.Equal("原文 204", Assert.Single(model.Entries).Source);
            model.TextSearch = "原文 203"; Assert.Equal("译文 203", Assert.Single(model.Entries).Translation);
            await model.CopyCommand.ExecuteAsync(null);
            Assert.Equal(new[] { documents[0].Entries[0].Key, documents[0].Entries[100].Key }, Assert.Single(store.Exports).Selection);
            model.TextSearch = ""; Assert.Equal(100, model.Entries.Count); Assert.True(model.Entries[0].IsChecked);
            await model.NextSessionsCommand.ExecuteAsync(null); Assert.Equal(3, model.Sessions.Count); Assert.Equal("21–23 / 23 场", model.SessionPageText);
            model.Search = "app01"; await model.SearchSessionsCommand.ExecuteAsync(null);
            Assert.Equal("app01.exe", Assert.Single(model.Sessions).Title); Assert.Equal("1–1 / 1 场", model.SessionPageText);
            Assert.Contains(store.Queries, x => x.Offset == 20 && x.Limit == 20);
            Assert.Equal("app01", store.Queries.Last().Search);
        }
        finally { await model.CloseAsync(); }
    }

    /// <summary>关闭取消尚未确认的删除并等待交互退出，关闭后不再读取或写入存储。</summary><returns>关闭屏障验证任务。</returns>
    [Fact]
    public async Task CloseCancelsPendingConfirmationAndPreventsFurtherOperations()
    {
        var store = new MemoryHistoryStore(CreateDocument("player.exe", HistorySessionState.Completed, 1));
        var interaction = new MemoryHistoryInteraction { ConfirmGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var model = await CreateModelAsync(store, interaction);
        model.Entries[0].IsChecked = true;
        var deletion = model.DeleteEntriesCommand.ExecuteAsync(null);
        await interaction.ConfirmEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await model.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); await deletion;
        Assert.True(interaction.ConfirmationCancelled); Assert.Empty(store.DeletedEntries); Assert.False(model.Ready);
        var reads = store.Queries.Count;
        await model.RefreshCommand.ExecuteAsync(null); await model.CopyCommand.ExecuteAsync(null);
        Assert.Equal(reads, store.Queries.Count); Assert.Empty(interaction.Copies); Assert.False(store.Disposed);
    }

    /// <summary>构造并等待初次档案加载，避免把未等待的命令当作测试完成。</summary>
    /// <param name="store">内存档案服务。</param><param name="interaction">可控制的窗口交互。</param><returns>已完成初次加载的视图模型。</returns>
    private static async Task<HistoryViewModel> CreateModelAsync(MemoryHistoryStore store, MemoryHistoryInteraction interaction)
    {
        var model = new HistoryViewModel(interaction); model.Attach(store, @"D:\test\history");
        await model.RefreshCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        return model;
    }

    /// <summary>创建具有不同稳定身份和可搜索双语内容的合成会话。</summary>
    /// <param name="application">应用名称。</param><param name="state">会话状态。</param><param name="count">字幕数量。</param><returns>固定测试文档。</returns>
    private static HistoryDocument CreateDocument(string application, HistorySessionState state, int count)
    {
        var started = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        var info = new HistorySessionInfo(Guid.NewGuid(), application, "Japanese", "translategemma:12b", started);
        var summary = new HistorySessionSummary(info, state == HistorySessionState.Recording ? null : started.AddMinutes(1), state, count, null);
        var entries = Enumerable.Range(0, count).Select(index => new HistoryEntry(1,
            new(new(Guid.NewGuid(), 1), new(index * 16000, (index + 1) * 16000), 1, 1, $"原文 {index}", $"译文 {index}", 1,
                SubtitlePhase.Final, TranslationState.Complete, null, BoundaryDecision.Pending))).ToArray();
        return new(summary, entries);
    }
}

/// <summary>提供可控读取和导出闸门的中立存储，记录视图模型实际提交的操作。</summary>
internal sealed class MemoryHistoryStore : ITranslationHistoryStore
{
    /// <summary>以固定文档初始化内存存储。</summary><param name="documents">按列表顺序提供的文档。</param>
    public MemoryHistoryStore(params HistoryDocument[] documents) => Documents = documents.ToDictionary(x => x.Summary.SessionId);
    /// <summary>获取可以模拟外部修订的内存文档。</summary>
    public Dictionary<Guid, HistoryDocument> Documents { get; }
    /// <summary>获取可选正文读取闸门。</summary>
    public TaskCompletionSource? ReadGate { get; init; }
    /// <summary>获取可选导出内容释放闸门。</summary>
    public TaskCompletionSource? ExportGate { get; init; }
    /// <summary>获取读取已捕获文档的信号。</summary>
    public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>获取导出已冻结内容的信号。</summary>
    public TaskCompletionSource ExportEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>获取实际分页查询。</summary>
    public List<(int Offset, int Limit, string? Search)> Queries { get; } = [];
    /// <summary>获取提交删除的条目身份。</summary>
    public List<(Guid SessionId, IReadOnlyCollection<HistoryEntryKey> Keys)> DeletedEntries { get; } = [];
    /// <summary>获取提交删除的会话身份。</summary>
    public List<IReadOnlyCollection<Guid>> DeletedSessions { get; } = [];
    /// <summary>获取固定导出的选择和格式。</summary>
    public List<(Guid SessionId, HistoryExportFormat Format, IReadOnlyCollection<HistoryEntryKey>? Selection)> Exports { get; } = [];
    /// <summary>获取存储是否由调用者释放。</summary>
    public bool Disposed { get; private set; }
    /// <inheritdoc />
    public IHistoryRecorder BeginSession(HistorySessionInfo info) => throw new NotSupportedException("页面测试不启动记录器。");
    /// <inheritdoc />
    public Task<HistoryPage> ListAsync(int offset = 0, int limit = 50, string? search = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Queries.Add((offset, limit, search));
        var values = Documents.Values.Where(x => string.IsNullOrEmpty(search) || x.Summary.Info.ApplicationName.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        return Task.FromResult(new HistoryPage(values.Skip(offset).Take(limit).Select(x => x.Summary).ToArray(), values.Length, []));
    }
    /// <inheritdoc />
    public async Task<HistoryDocument> ReadAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var snapshot = Documents[sessionId]; ReadEntered.TrySetResult();
        if (ReadGate is not null) await ReadGate.Task.WaitAsync(cancellationToken);
        return snapshot;
    }
    /// <inheritdoc />
    public Task DeleteEntriesAsync(Guid sessionId, IReadOnlyCollection<HistoryEntryKey> entries, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); DeletedEntries.Add((sessionId, entries.ToArray()));
        var document = Documents[sessionId]; var remaining = document.Entries.Where(x => !entries.Contains(x.Key)).ToArray();
        Documents[sessionId] = document with { Entries = remaining, Summary = document.Summary with { EntryCount = remaining.Length } };
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public Task DeleteSessionsAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); DeletedSessions.Add(sessionIds.ToArray());
        foreach (var id in sessionIds) Documents.Remove(id);
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var id in Documents.Where(x => x.Value.Summary.State != HistorySessionState.Recording).Select(x => x.Key).ToArray()) Documents.Remove(id);
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public async Task<string> ExportAsync(Guid sessionId, HistoryExportFormat format, IReadOnlyCollection<HistoryEntryKey>? selection = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Exports.Add((sessionId, format, selection?.ToArray()));
        var snapshot = string.Join("\n", Documents[sessionId].Entries.Where(x => selection is null || selection.Contains(x.Key)).Select(x => x.Segment.Source + "\n" + x.Segment.Translation));
        ExportEntered.TrySetResult();
        if (ExportGate is not null) await ExportGate.Task.WaitAsync(cancellationToken);
        return snapshot;
    }
    /// <inheritdoc />
    public Task CleanupAsync(int retentionDays, CancellationToken cancellationToken = default) => Task.CompletedTask;
    /// <inheritdoc />
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

/// <summary>记录固定文本和确认范围，允许测试取消尚未关闭的对话框。</summary>
internal sealed class MemoryHistoryInteraction : IHistoryInteraction
{
    /// <summary>获取或设置默认确认结果。</summary>
    public bool ConfirmResult { get; set; } = true;
    /// <summary>获取默认保存结果。</summary>
    public bool SaveResult { get; init; } = true;
    /// <summary>获取可选确认闸门。</summary>
    public TaskCompletionSource? ConfirmGate { get; init; }
    /// <summary>获取可选保存交互闸门。</summary>
    public TaskCompletionSource? SaveGate { get; init; }
    /// <summary>获取确认对话框已显示的信号。</summary>
    public TaskCompletionSource ConfirmEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>获取保存对话框已接收固定文本的信号。</summary>
    public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>获取所有确认范围。</summary>
    public List<string> Confirmations { get; } = [];
    /// <summary>获取交给剪贴板的文本。</summary>
    public List<string> Copies { get; } = [];
    /// <summary>获取交给保存对话框的文本、文件名及格式。</summary>
    public List<(string Text, string SuggestedName, HistoryExportFormat Format)> Saves { get; } = [];
    /// <summary>获取确认是否收到生命周期取消。</summary>
    public bool ConfirmationCancelled { get; private set; }
    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken)
    {
        Confirmations.Add(message); ConfirmEntered.TrySetResult();
        try { if (ConfirmGate is not null) await ConfirmGate.Task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { ConfirmationCancelled = true; throw; }
        return ConfirmResult;
    }
    /// <inheritdoc />
    public Task CopyAsync(string text, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); Copies.Add(text); return Task.CompletedTask; }
    /// <inheritdoc />
    public async Task<bool> SaveAsync(string text, string suggestedName, HistoryExportFormat format, CancellationToken cancellationToken)
    {
        Saves.Add((text, suggestedName, format)); SaveEntered.TrySetResult();
        if (SaveGate is not null) await SaveGate.Task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); return SaveResult;
    }
}
