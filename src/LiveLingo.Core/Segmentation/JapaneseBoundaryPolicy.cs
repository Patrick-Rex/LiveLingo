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
    public static BoundaryDecision Evaluate(string text, int stableObservations, bool hasRightContext, bool complete)
    {
        if (!complete) return new(false, "incomplete_generation", stableObservations, hasRightContext);
        if (string.IsNullOrWhiteSpace(text)) return new(false, "empty_source", stableObservations, hasRightContext);
        if (!Balanced(text)) return new(false, "open_quote_or_parenthesis", stableObservations, hasRightContext);
        var body = text.Trim().TrimEnd('。', '！', '？', '!', '?', '.', ' ', '　');
        if (body.EndsWith('、') || Dependencies.Any(x => body.EndsWith(x, StringComparison.Ordinal)))
            return new(false, "open_dependency_or_enumeration", stableObservations, hasRightContext);
        if (body.EndsWith("よね", StringComparison.Ordinal)) body = body[..^2];
        else if (body.EndsWith('よ') || body.EndsWith('ね')) body = body[..^1];
        if (body.EndsWith('か')) body = body[..^1];
        if (!Predicates.Any(x => body.EndsWith(x, StringComparison.Ordinal)))
            return new(false, "predicate_not_confirmed", stableObservations, hasRightContext);
        if (stableObservations < 2) return new(false, "source_not_stable", stableObservations, hasRightContext);
        if (!hasRightContext) return new(false, "right_context_missing", stableObservations, false);
        return new(true, "stable_complete_predicate_with_right_context", stableObservations, true);
    }

    /// <summary>检查成对引述及括号的嵌套，不把内部标点当外层句尾。</summary>
    /// <param name="text">原始文本。</param><returns>所有已打开结构均按顺序闭合时为 true。</returns>
    private static bool Balanced(string text)
    {
        var stack = new Stack<char>();
        foreach (var character in text)
        {
            var closing = character switch { '「' => '」', '『' => '』', '（' => '）', '(' => ')', '[' => ']', '【' => '】', _ => '\0' };
            if (closing != '\0') stack.Push(closing);
            else if (character is '」' or '』' or '）' or ')' or ']' or '】')
            {
                if (!stack.TryPop(out var expected) || character != expected) return false;
            }
        }
        return stack.Count == 0;
    }
}
