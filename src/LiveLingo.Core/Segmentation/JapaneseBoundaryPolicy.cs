using LiveLingo.Core.Subtitles;

namespace LiveLingo.Core.Segmentation;

/// <summary>以可解释保守规则评估日语闭合，不以句号、静音或 EOS 单独定稿。</summary>
public static class JapaneseBoundaryPolicy
{
    private static readonly string[] Dependencies = ["けれども", "けれど", "けど", "ですが", "ますが", "ので", "から", "なら", "たら", "ながら", "ため", "のに", "では", "とか", "など", "そして", "それで", "例えば", "が", "と", "て", "し", "や", "ば"];
    private static readonly string[] Predicates = ["ませんでした", "ありません", "ありませんでした", "ました", "ません", "ます", "でした", "です", "だった", "ではない", "わけではない", "なかった", "できない", "ない", "ください", "である", "ましょう", "でしょう", "だろう", "だ"];

    /// <summary>要求完整生成、重复稳定观察、闭合结构、谓语及右侧证据同时成立。</summary>
    /// <param name="text">完整原始日语块。</param><param name="stableObservations">全文一致的连续观察次数。</param>
    /// <param name="hasRightContext">是否存在独立右侧音频闭合证据。</param><param name="complete">是否非截断完整生成。</param>
    /// <returns>带可诊断原因的边界判断；不支持的句式保持可修订。</returns>
    public static BoundaryDecision Evaluate(string text, int stableObservations, bool hasRightContext, bool complete) =>
        SubtitleBoundaryPolicy.Evaluate("Japanese", text, stableObservations, hasRightContext, complete);

    /// <summary>检查日语块末尾的从属结构与谓语，供共用证据门槛判断是否闭合。</summary>
    /// <param name="text">已确认非空且结构平衡的识别块。</param><returns>阻止定稿的原因，谓语闭合时为空。</returns>
    internal static string? CheckEnding(string text)
    {
        var body = text.Trim().TrimEnd('。', '！', '？', '!', '?', '.', ' ', '　');
        if (body.EndsWith('、') || Dependencies.Any(x => body.EndsWith(x, StringComparison.Ordinal)))
            return "open_dependency_or_enumeration";
        if (body.EndsWith("よね", StringComparison.Ordinal)) body = body[..^2];
        else if (body.EndsWith('よ') || body.EndsWith('ね')) body = body[..^1];
        if (body.EndsWith('か')) body = body[..^1];
        return Predicates.Any(x => body.EndsWith(x, StringComparison.Ordinal)) ? null : "predicate_not_confirmed";
    }
}
