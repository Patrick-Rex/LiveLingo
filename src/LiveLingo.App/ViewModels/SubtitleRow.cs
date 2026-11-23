using CommunityToolkit.Mvvm.ComponentModel;
using LiveLingo.Core.Subtitles;

namespace LiveLingo.App.ViewModels;

/// <summary>只读呈现完整语义块；身份保留真实重复，陈旧译文明确标示。</summary>
public sealed record SubtitleRow
{
    /// <summary>为单个权威语义块建立显示投影。</summary><param name="segment">当前字幕版本。</param>
    public SubtitleRow(SubtitleSegment segment) => Segment = segment;

    /// <summary>获取权威字幕版本，替换版本时重新配对该块的短句。</summary>
    public SubtitleSegment Segment
    {
        get;
        init
        {
            field = value;
            Phrases = CreatePhrases();
        }
    }

    /// <summary>获取按识别顺序排列的原译短句，尚无当前译文的句子保留其位置。</summary>
    public IReadOnlyList<SubtitlePhraseRow> Phrases { get; private set; } = [];

    /// <summary>获取完整日语原文。</summary>
    public string Source => Segment.Source;
    /// <summary>获取最近译文，空结果显示等待。</summary>
    public string Translation => Segment.Translation ?? "等待中文…";
    /// <summary>获取面向阅读的简短状态；陈旧或失败译文不会显示为已定稿。</summary>
    public string StatusText => GetStatusText(Segment);

    /// <summary>读取实时与历史共用的块状态，无需创建短句投影。</summary>
    /// <param name="segment">当前语义块。</param><returns>面向阅读的状态说明。</returns>
    internal static string GetStatusText(SubtitleSegment segment) => segment.TranslationState == TranslationState.Failed ? "翻译未完成" :
        segment.Translation is null ? (segment.Phase == SubtitlePhase.Draft ? "草稿 · 等待中文" : "更新中 · 等待中文") :
        segment.TranslatedSourceRevision != segment.SourceRevision ? "更新中 · 中文待更新" :
        segment.Phase switch
        {
            SubtitlePhase.Final when segment.TranslationState == TranslationState.Complete => "已定稿",
            _ when segment.SourceRangeSealed && segment.TranslationState == TranslationState.Complete => "已翻译",
            SubtitlePhase.Draft => "草稿",
            _ => "更新中"
        };
    /// <summary>获取精确版本和修订状态。</summary>
    public string Detail => $"{(Segment.SourceRangeSealed ? "实时分段 · 原文已固定" : Segment.Phase switch { SubtitlePhase.Final => "定稿", SubtitlePhase.Revised => "修订中", _ => "草稿" })} · 源 v{Segment.SourceRevision} / 字幕 v{Segment.SubtitleRevision}" +
        (Segment.TranslatedSourceRevision != Segment.SourceRevision ? " · 中文待更新" : "") + (Segment.Error is { } error ? " · " + error : "");

    /// <summary>按原句位置绑定已确认的译文，兼容只保存当前整块译文的旧字幕。</summary>
    /// <returns>当前原文对应的双语阅读单元。</returns>
    private IReadOnlyList<SubtitlePhraseRow> CreatePhrases()
    {
        var translations = Segment.PhraseTranslations;
        var final = Segment.Phase == SubtitlePhase.Final && Segment.TranslationState == TranslationState.Complete &&
            Segment.TranslatedSourceRevision == Segment.SourceRevision;
        if (translations.IsDefaultOrEmpty && Segment.TranslatedSourceRevision == Segment.SourceRevision && Segment.Translation is { Length: > 0 } whole)
            return [new(Segment.Key, 0, Source, whole, StatusText, Detail, final)];

        var sources = SubtitlePhrases.Split(Source);
        var previous = Segment.PreviousPhraseTranslations;
        var count = Math.Max(sources.Length, previous.Length);
        var result = new List<SubtitlePhraseRow>(count);
        var failed = Segment.TranslationState == TranslationState.Failed;
        var detail = Detail;
        for (var index = 0; index < count; index++)
        {
            var source = index < sources.Length ? sources[index] : null;
            if (!translations.IsDefaultOrEmpty && index < translations.Length && translations[index] is { } current && current.Source == source)
            {
                result.Add(new(Segment.Key, index, current.Source, current.Translation, final ? "已定稿" : "已翻译", detail, final));
            }
            else if (index < previous.Length && previous[index] is { } old)
            {
                var status = source is null ? "上一版 · 本次已移除" : failed ? "上一版 · 重译未完成" : "上一版 · 待更新";
                var revision = source is null ? "当前识别已缩短" : $"当前识别：{source}";
                result.Add(new(Segment.Key, index, old.Source, old.Translation, status, $"{detail} · {revision}", false, true)
                    { CurrentSource = source });
            }
            else if (source is not null)
            {
                result.Add(new(Segment.Key, index, source, null, failed ? "翻译未完成" : "等待中文", detail, false));
            }
        }
        return result;
    }
}

/// <summary>呈现一个识别短句和归属于该位置的中文，保留重复原句的独立位置。</summary>
/// <param name="Key">语义块身份。</param><param name="Index">块内短句序号。</param>
/// <param name="Source">识别原句。</param><param name="Chinese">当前原句的译文，等待时为空。</param>
/// <param name="StatusText">阅读状态。</param><param name="Detail">所属块的版本详情。</param><param name="IsFinal">该块已按当前译文定稿。</param>
/// <param name="IsPreviousTranslation">正在显示上一版原译对，等待此位置的新译文。</param>
public sealed record SubtitlePhraseRow(SegmentKey Key, int Index, string Source, string? Chinese, string StatusText, string Detail, bool IsFinal, bool IsPreviousTranslation = false)
{
    /// <summary>获取此位置的最新识别原文，供仅原文模式跟随。修订已删除该位置时为空。</summary>
    public string? CurrentSource { get; init; } = Source;

    /// <summary>获取译文或当前等待提示。</summary>
    public string Translation => Chinese ?? "等待中文…";
}

/// <summary>保持短句的显示项身份，原文、译文和版本修订时在现有控件上更新绑定。</summary>
/// <param name="phrase">首次显示的短句内容。</param>
public sealed partial class LivePhraseItem(SubtitlePhraseRow phrase) : ObservableObject
{
    /// <summary>获取此位置最新的原译文及状态。</summary>
    [NotifyPropertyChangedFor(nameof(StatusText)), NotifyPropertyChangedFor(nameof(Translation))]
    [ObservableProperty] public partial SubtitlePhraseRow Phrase { get; internal set; } = phrase;
    /// <summary>获取实时会话是否已经停止接收翻译。</summary>
    [NotifyPropertyChangedFor(nameof(StatusText)), NotifyPropertyChangedFor(nameof(Translation))]
    [ObservableProperty] public partial bool Stopped { get; internal set; }
    /// <summary>获取当前的阅读状态，停止后区分定稿、已译未定稿和缺少可用译文。</summary>
    public string StatusText => Stopped && !Phrase.IsFinal
        ? Phrase.IsPreviousTranslation ? "已停止 · 上一版译文" : Phrase.Chinese is null ? "已停止 · 翻译未完成" : "已停止 · 未定稿"
        : Phrase.StatusText;
    /// <summary>获取对应译文，尚未收到的译文在会话停止后结束等待。</summary>
    public string Translation => Stopped && Phrase.Chinese is null ? "尚无译文" : Phrase.Translation;
}
