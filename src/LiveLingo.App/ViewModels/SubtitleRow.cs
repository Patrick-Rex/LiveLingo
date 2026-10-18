using LiveLingo.Core.Subtitles;

namespace LiveLingo.App.ViewModels;

/// <summary>只读呈现完整语义块；身份保留真实重复，陈旧译文明确标示。</summary>
/// <param name="Segment">权威字幕版本。</param>
public sealed record SubtitleRow(SubtitleSegment Segment)
{
    /// <summary>获取完整日语原文。</summary>
    public string Source => Segment.Source;
    /// <summary>获取最近译文，空结果显示等待。</summary>
    public string Translation => Segment.Translation ?? "等待中文…";
    /// <summary>获取精确版本和修订状态。</summary>
    public string Detail => $"{Segment.Phase switch { SubtitlePhase.Final => "定稿", SubtitlePhase.Revised => "修订中", _ => "草稿" }} · 源 v{Segment.SourceRevision} / 字幕 v{Segment.SubtitleRevision}" +
        (Segment.TranslatedSourceRevision != Segment.SourceRevision ? " · 中文待更新" : "") + (Segment.Error is { } error ? " · " + error : "");
}
