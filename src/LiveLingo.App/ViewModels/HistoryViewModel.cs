using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveLingo.App.Services;
using LiveLingo.Core.History;

namespace LiveLingo.App.ViewModels;

/// <summary>管理分页档案、明确选择和固定内容导出；所有磁盘操作交给历史服务。</summary>
/// <param name="interaction">所属窗口的确认及导出交互。</param>
public sealed partial class HistoryViewModel(IHistoryInteraction interaction) : ObservableObject
{
    private const int SessionPageSize = 20;
    private const int EntryPageSize = 100;
    private readonly CancellationTokenSource lifetime = new();
    private ITranslationHistoryStore? store;
    private IHistoryRecorder? recorder;
    private IReadOnlyList<HistoryEntryItem> allEntries = [];
    private HistoryDocument? document;
    private int sessionOffset;
    private int entryOffset;
    private int totalSessions;
    private bool closed;
    private Task operation = Task.CompletedTask;

    /// <summary>获取当前库的位置。</summary>
    [ObservableProperty] public partial string Location { get; private set; } = "";
    /// <summary>获取一页会话摘要。</summary>
    [ObservableProperty] public partial IReadOnlyList<HistorySessionItem> Sessions { get; private set; } = [];
    /// <summary>获取当前正文页。</summary>
    [ObservableProperty] public partial IReadOnlyList<HistoryEntryItem> Entries { get; private set; } = [];
    /// <summary>获取或设置正在阅读的会话。</summary>
    [ObservableProperty] public partial HistorySessionItem? SelectedSession { get; set; }
    /// <summary>获取或设置应用名搜索输入，点击查找后查询。</summary>
    [ObservableProperty] public partial string Search { get; set; } = "";
    /// <summary>获取或设置当前会话内的双语正文筛选。</summary>
    [ObservableProperty] public partial string TextSearch { get; set; } = "";
    /// <summary>获取是否正在提交档案操作。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Ready)), NotifyPropertyChangedFor(nameof(CanRead)), NotifyPropertyChangedFor(nameof(CanDeleteEntries)), NotifyPropertyChangedFor(nameof(CanDeleteSessions))]
    public partial bool Busy { get; private set; }
    /// <summary>获取最近一次用户操作的反馈。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMessage))] public partial string Message { get; private set; } = "";
    /// <summary>获取持续的保存失败提示。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSaveError))] public partial string SaveMessage { get; private set; } = "";
    /// <summary>获取是否存在可重试的未保存内容。</summary>
    [ObservableProperty] public partial bool CanRetry { get; private set; }
    /// <summary>获取会话分页说明。</summary>
    public string SessionPageText => totalSessions == 0 ? "暂无会话" : $"{sessionOffset + 1}–{Math.Min(sessionOffset + SessionPageSize, totalSessions)} / {totalSessions} 场";
    /// <summary>获取正文分页说明。</summary>
    public string EntryPageText => $"{Entries.Count} 条显示 · 已选 {allEntries.Count(x => x.IsChecked)} 条";
    /// <summary>获取已选会话数。</summary>
    public string SessionSelectionText => $"已选 {Sessions.Count(x => x.IsChecked)} 场";
    /// <summary>获取会话来源及状态。</summary>
    public string DocumentTitle => document is null ? "会话详情" : $"{document.Summary.Info.ApplicationName} · {document.Summary.Info.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
    /// <summary>获取运行配置、状态和错误摘要。</summary>
    public string DocumentDetail => document is null ? "选择左侧会话；记录只保存在本机" :
        $"{document.Summary.Info.SourceLanguage} → 简体中文 · {document.Summary.Info.TranslationModel} · {SelectedSession?.StateText}" +
        (document.Summary.Error is { } error ? " · " + error : "");
    /// <summary>获取是否允许开始操作。</summary>
    public bool Ready => store is not null && !Busy && !closed;
    /// <summary>获取是否可复制或导出当前文档。</summary>
    public bool CanRead => Ready && document is not null;
    /// <summary>获取是否可删除已选择的结束会话条目。</summary>
    public bool CanDeleteEntries => CanRead && document!.Summary.State != HistorySessionState.Recording && allEntries.Any(x => x.IsChecked);
    /// <summary>获取是否可删除选中会话；活动会话不参与删除。</summary>
    public bool CanDeleteSessions => Ready && Sessions.Any(x => x.IsChecked) && Sessions.Where(x => x.IsChecked).All(x => x.CanDelete);
    /// <summary>获取是否显示空记录提示。</summary>
    public bool IsEmpty => Sessions.Count == 0;
    /// <summary>获取是否显示操作反馈。</summary>
    public bool HasMessage => Message.Length != 0;
    /// <summary>获取是否显示持久保存错误。</summary>
    public bool HasSaveError => SaveMessage.Length != 0;
    /// <summary>获取会话列表是否需要翻页。</summary>
    public bool HasSessionPages => totalSessions > SessionPageSize;
    /// <summary>获取筛选后的正文是否需要翻页。</summary>
    public bool HasEntryPages => FilterEntries().Skip(EntryPageSize).Any();

    /// <summary>切换到已应用设置的历史库，旧库由宿主保留以完成尚在进行的写入。</summary><param name="value">历史服务。</param><param name="location">实际历史目录。</param>
    public void Attach(ITranslationHistoryStore value, string location)
    {
        if (closed || ReferenceEquals(store, value)) return;
        store = value; Location = location; sessionOffset = 0; totalSessions = 0;
        Sessions = []; SelectedSession = null; document = null; allEntries = []; Entries = [];
        RefreshProperties();
        if (!Busy) RefreshCommand.Execute(null);
    }

    /// <summary>接收宿主当前记录器的独立错误，回调必须在UI线程执行。</summary><param name="value">可重试记录器。</param><param name="status">最新保存状态。</param>
    public void SetSaveStatus(IHistoryRecorder? value, HistorySaveStatus? status)
    {
        recorder = value; CanRetry = status?.CanRetry == true;
        SaveMessage = status?.Error is { } error ? "历史未完整保存：" + error : "";
    }

    /// <summary>更换阅读目标后异步加载正文。</summary><param name="value">当前目标。</param>
    partial void OnSelectedSessionChanged(HistorySessionItem? value)
    {
        if (value is not null && !Busy && !closed) LoadSelectionCommand.Execute(null);
    }

    /// <summary>正文筛选从第一页开始，不改变条目选择。</summary><param name="value">查找文字。</param>
    partial void OnTextSearchChanged(string value) { entryOffset = 0; UpdateEntryPage(); }

    /// <summary>查询应用名并回到第一页。</summary><returns>查询完成任务。</returns>
    [RelayCommand]
    private Task SearchSessionsAsync() { sessionOffset = 0; return RefreshAsync(); }

    /// <summary>刷新当前页与当前文档，保留选择和未改变的列表对象。</summary><returns>刷新完成任务。</returns>
    [RelayCommand]
    private Task RefreshAsync() => RunAsync(RefreshCoreAsync);

    /// <summary>仅在页面可见且没有交互进行时刷新。</summary><returns>刷新完成任务。</returns>
    public Task PollAsync() => RefreshAsync();

    /// <summary>读取服务中的分页摘要并更新当前正文。</summary><returns>读取任务。</returns>
    private async Task RefreshCoreAsync()
    {
        var expected = store!;
        var page = await expected.ListAsync(sessionOffset, SessionPageSize, Search, lifetime.Token);
        if (!IsCurrent(expected)) return;
        totalSessions = page.TotalCount;
        if (sessionOffset >= totalSessions && sessionOffset > 0)
        {
            sessionOffset = Math.Max(0, (totalSessions - 1) / SessionPageSize * SessionPageSize);
            page = await expected.ListAsync(sessionOffset, SessionPageSize, Search, lifetime.Token);
            if (!IsCurrent(expected)) return;
        }
        var selected = SelectedSession?.Summary.SessionId;
        if (!Sessions.Select(x => x.Summary).SequenceEqual(page.Sessions))
        {
            var checkedIds = Sessions.Where(x => x.IsChecked).Select(x => x.Summary.SessionId).ToHashSet();
            Sessions = page.Sessions.Select(x => new HistorySessionItem(x, RefreshProperties) { IsChecked = checkedIds.Contains(x.SessionId) }).ToArray();
            SelectedSession = Sessions.FirstOrDefault(x => x.Summary.SessionId == selected) ?? Sessions.FirstOrDefault();
        }
        if (page.Warnings.Count != 0) Message = string.Join("；", page.Warnings);
        await LoadSelectionCoreAsync();
        RefreshProperties();
    }

    /// <summary>加载当前会话正文并处理可见错误。</summary><returns>加载任务。</returns>
    [RelayCommand] private Task LoadSelectionAsync() => RunAsync(LoadSelectionCoreAsync);

    /// <summary>冻结所选会话，保持仍有效的条目选择。</summary><returns>读取任务。</returns>
    private async Task LoadSelectionCoreAsync()
    {
        var expected = store!;
        if (SelectedSession is not { } selected) { document = null; allEntries = []; Entries = []; RefreshProperties(); return; }
        var next = await expected.ReadAsync(selected.Summary.SessionId, lifetime.Token);
        if (!IsCurrent(expected) || SelectedSession?.Summary.SessionId != next.Summary.SessionId) return;
        var changedSession = document?.Summary.SessionId != next.Summary.SessionId;
        if (changedSession) { entryOffset = 0; TextSearch = ""; }
        if (document is null || changedSession || !document.Entries.SequenceEqual(next.Entries))
        {
            var keys = changedSession ? [] : allEntries.Where(x => x.IsChecked).Select(x => x.Entry.Key).ToHashSet();
            allEntries = next.Entries.Select(x => new HistoryEntryItem(x, RefreshProperties) { IsChecked = keys.Contains(x.Key) }).ToArray();
        }
        document = next; UpdateEntryPage(); RefreshProperties();
    }

    /// <summary>显示前一页会话。</summary><returns>加载任务。</returns>
    [RelayCommand] private Task PreviousSessionsAsync() { if (Ready) sessionOffset = Math.Max(0, sessionOffset - SessionPageSize); return RefreshAsync(); }
    /// <summary>显示后一页会话。</summary><returns>加载任务。</returns>
    [RelayCommand] private Task NextSessionsAsync() { if (Ready && sessionOffset + SessionPageSize < totalSessions) sessionOffset += SessionPageSize; return RefreshAsync(); }
    /// <summary>显示前一页正文。</summary>
    [RelayCommand] private void PreviousEntries() { entryOffset = Math.Max(0, entryOffset - EntryPageSize); UpdateEntryPage(); }
    /// <summary>显示后一页正文。</summary>
    [RelayCommand] private void NextEntries() { if (entryOffset + EntryPageSize < FilterEntries().Count()) entryOffset += EntryPageSize; UpdateEntryPage(); }
    /// <summary>选择当前正文页的全部条目。</summary>
    [RelayCommand] private void SelectPage() { foreach (var item in Entries) item.IsChecked = true; }
    /// <summary>取消当前会话中的所有条目选择。</summary>
    [RelayCommand] private void ClearSelection() { foreach (var item in allEntries) item.IsChecked = false; }

    /// <summary>复制选中条目；没有选择时复制整场，内容在操作时冻结。</summary><returns>复制任务。</returns>
    [RelayCommand]
    private Task CopyAsync() => RunAsync(async () =>
    {
        if (document is null) return;
        var text = await store!.ExportAsync(document.Summary.SessionId, HistoryExportFormat.Text, Selection(), lifetime.Token);
        await interaction.CopyAsync(text, lifetime.Token); Message = "已复制双语记录";
    });

    /// <summary>导出TXT，保存对话框出现前冻结内容。</summary><returns>导出任务。</returns>
    [RelayCommand] private Task ExportTextAsync() => ExportAsync(HistoryExportFormat.Text);
    /// <summary>导出Markdown，保存对话框出现前冻结内容。</summary><returns>导出任务。</returns>
    [RelayCommand] private Task ExportMarkdownAsync() => ExportAsync(HistoryExportFormat.Markdown);

    /// <summary>生成不可变导出内容并交给窗口保存。</summary><param name="format">文本格式。</param><returns>导出任务。</returns>
    private Task ExportAsync(HistoryExportFormat format) => RunAsync(async () =>
    {
        if (document is null) return;
        var summary = document.Summary;
        var text = await store!.ExportAsync(summary.SessionId, format, Selection(), lifetime.Token);
        var name = $"LiveLingo-{summary.Info.StartedAt.ToLocalTime():yyyyMMdd-HHmmss}";
        Message = await interaction.SaveAsync(text, name, format, lifetime.Token) ? "记录已导出" : "已取消导出";
    });

    /// <summary>确认后删除已结束会话的选中条目。</summary><returns>删除任务。</returns>
    [RelayCommand]
    private Task DeleteEntriesAsync()
    {
        if (!CanDeleteEntries) return Task.CompletedTask;
        var id = document!.Summary.SessionId; var keys = Selection()!; var expected = store!;
        return RunAsync(async () =>
        {
            if (!await interaction.ConfirmAsync($"删除当前会话中选中的 {keys.Count} 条双语记录？", lifetime.Token) || !IsCurrent(expected)) return;
            await expected.DeleteEntriesAsync(id, keys, lifetime.Token);
            await RefreshCoreAsync(); Message = $"已删除 {keys.Count} 条记录";
        });
    }

    /// <summary>确认后批量删除已结束会话。</summary><returns>删除任务。</returns>
    [RelayCommand]
    private Task DeleteSessionsAsync()
    {
        if (!CanDeleteSessions) return Task.CompletedTask;
        var ids = Sessions.Where(x => x.IsChecked).Select(x => x.Summary.SessionId).ToArray(); var expected = store!;
        return RunAsync(async () =>
        {
            if (!await interaction.ConfirmAsync($"删除选中的 {ids.Length} 场会话及其全部双语记录？", lifetime.Token) || !IsCurrent(expected)) return;
            await expected.DeleteSessionsAsync(ids, lifetime.Token);
            await RefreshCoreAsync(); Message = $"已删除 {ids.Length} 场会话";
        });
    }

    /// <summary>确认后清空当前库的已结束记录，保留活动会话。</summary><returns>清空任务。</returns>
    [RelayCommand]
    private Task ClearHistoryAsync() => RunAsync(async () =>
    {
        var expected = store!;
        if (!await interaction.ConfirmAsync("清空当前历史目录内所有已结束会话？正在翻译的会话将保留。", lifetime.Token) || !IsCurrent(expected)) return;
        await expected.ClearAsync(lifetime.Token); await RefreshCoreAsync(); Message = "已清空已结束记录";
    });

    /// <summary>重试宿主仍保留的未保存内容。</summary><returns>重试任务。</returns>
    [RelayCommand]
    private Task RetrySaveAsync() => RunAsync(async () =>
    {
        if (recorder is not { } current) return;
        await current.RetryAsync(lifetime.Token); SetSaveStatus(current, current.Status);
        await RefreshCoreAsync(); Message = current.Status.Error is null ? "历史保存已重试" : "仍有未保存内容，请导出备份";
    });

    /// <summary>统一互斥交互，捕获错误并保持窗口可继续操作。</summary><param name="action">当前操作。</param><returns>可观察任务。</returns>
    private Task RunAsync(Func<Task> action)
    {
        if (!Ready) return Task.CompletedTask;
        operation = ExecuteAsync(action); return operation;
    }

    /// <summary>运行一次受生命周期约束的操作并呈现错误。</summary><param name="action">已验证的操作。</param><returns>操作任务。</returns>
    private async Task ExecuteAsync(Func<Task> action)
    {
        Busy = true;
        try { await action(); }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception error) { if (!closed) Message = "操作未完成：" + error.Message; }
        finally { Busy = false; RefreshProperties(); }
    }

    /// <summary>检查异步结果是否仍属于当前库与窗口。</summary><param name="expected">操作捕获的库。</param><returns>是否可应用结果。</returns>
    private bool IsCurrent(ITranslationHistoryStore expected) => !closed && ReferenceEquals(store, expected);

    /// <summary>获取条目选择；没有选择时返回空以表示整场。</summary><returns>固定选择或空。</returns>
    private IReadOnlyCollection<HistoryEntryKey>? Selection()
    {
        var selected = allEntries.Where(x => x.IsChecked).Select(x => x.Entry.Key).ToArray();
        return selected.Length == 0 ? null : selected;
    }

    /// <summary>按原文和译文筛选当前会话，不修改底层正文。</summary><returns>筛选后的条目。</returns>
    private IEnumerable<HistoryEntryItem> FilterEntries() => string.IsNullOrWhiteSpace(TextSearch) ? allEntries : allEntries.Where(x =>
        x.Source.Contains(TextSearch, StringComparison.OrdinalIgnoreCase) || x.Translation.Contains(TextSearch, StringComparison.OrdinalIgnoreCase));

    /// <summary>限制显示条目数并保持有效页边界。</summary>
    private void UpdateEntryPage()
    {
        var filtered = FilterEntries().ToArray();
        if (entryOffset >= filtered.Length) entryOffset = Math.Max(0, (filtered.Length - 1) / EntryPageSize * EntryPageSize);
        var page = filtered.Skip(entryOffset).Take(EntryPageSize).ToArray();
        if (!Entries.SequenceEqual(page)) Entries = page;
        OnPropertyChanged(nameof(EntryPageText));
        OnPropertyChanged(nameof(HasEntryPages));
    }

    /// <summary>刷新依赖选择、文档与页边界的可见属性。</summary>
    private void RefreshProperties()
    {
        foreach (var name in new[] { nameof(Ready), nameof(CanRead), nameof(CanDeleteEntries), nameof(CanDeleteSessions), nameof(SessionPageText), nameof(EntryPageText), nameof(SessionSelectionText), nameof(DocumentTitle), nameof(DocumentDetail), nameof(IsEmpty), nameof(HasSessionPages), nameof(HasEntryPages) })
            OnPropertyChanged(name);
    }

    /// <summary>取消并等待当前窗口交互，不负责销毁宿主拥有的存储服务。</summary><returns>关闭任务。</returns>
    public async Task CloseAsync()
    {
        if (closed) return;
        closed = true; await lifetime.CancelAsync(); await operation; lifetime.Dispose(); RefreshProperties();
    }
}
