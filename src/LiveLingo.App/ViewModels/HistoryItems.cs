using CommunityToolkit.Mvvm.ComponentModel;
using LiveLingo.Core.History;

namespace LiveLingo.App.ViewModels;

/// <summary>提供会话摘要和独立于阅读焦点的批量选择状态。</summary>
/// <param name="summary">固定摘要。</param><param name="selectionChanged">刷新选择命令状态。</param>
public sealed partial class HistorySessionItem(HistorySessionSummary summary, Action selectionChanged) : ObservableObject
{
    /// <summary>获取固定摘要。</summary>
    public HistorySessionSummary Summary { get; } = summary;
    /// <summary>获取应用名称。</summary>
    public string Title => Summary.Info.ApplicationName;
    /// <summary>获取本地时间与条数。</summary>
    public string Detail => $"{Summary.Info.StartedAt.ToLocalTime():MM-dd HH:mm} · {Summary.EntryCount} 条 · {StateText}";
    /// <summary>获取可理解的会话状态。</summary>
    public string StateText => Summary.State switch { HistorySessionState.Recording => "翻译中", HistorySessionState.Completed => "已结束", HistorySessionState.PartiallySaved => "部分保存", _ => "中断" };
    /// <summary>获取会话是否允许删除。</summary>
    public bool CanDelete => Summary.State != HistorySessionState.Recording;
    /// <summary>获取或设置批量选择。</summary>
    [ObservableProperty] public partial bool IsChecked { get; set; }
    /// <summary>通知上层更新选择计数。</summary><param name="value">当前选择。</param>
    partial void OnIsCheckedChanged(bool value) => selectionChanged();
}

/// <summary>承载一条双语档案以及批量选择，不按正文去重。</summary>
/// <param name="entry">固定历史条目。</param><param name="selectionChanged">刷新选择计数。</param>
public sealed partial class HistoryEntryItem(HistoryEntry entry, Action selectionChanged) : ObservableObject
{
    private readonly SubtitleRow row = new(entry.Segment);
    /// <summary>获取固定历史条目。</summary>
    public HistoryEntry Entry { get; } = entry;
    /// <summary>获取原文。</summary>
    public string Source => Entry.Segment.Source;
    /// <summary>获取译文或明确的未完成提示。</summary>
    public string Translation => Entry.Segment.Translation ?? "尚无译文";
    /// <summary>获取不隐藏版本不匹配的简短状态。</summary>
    public string StatusText => row.StatusText;
    /// <summary>获取版本与采集代次诊断。</summary>
    public string Detail => $"采集段 {Entry.CaptureEpoch} · 原文 v{Entry.Segment.SourceRevision} / 字幕 v{Entry.Segment.SubtitleRevision}" +
        (Entry.Segment.Error is { } error ? " · " + error : "");
    /// <summary>获取或设置本条选择状态。</summary>
    [ObservableProperty] public partial bool IsChecked { get; set; }
    /// <summary>通知上层更新选择计数。</summary><param name="value">当前选择。</param>
    partial void OnIsCheckedChanged(bool value) => selectionChanged();
}
