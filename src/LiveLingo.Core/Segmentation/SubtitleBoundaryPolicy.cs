using LiveLingo.Core.Subtitles;
using LiveLingo.Core.Transcription;

namespace LiveLingo.Core.Segmentation;

/// <summary>按选定源语言判断字幕是否具备足够的独立闭合证据。</summary>
public static class SubtitleBoundaryPolicy
{
    /// <summary>日语校验原有谓语规则，中文、英文及韩文要求明确句末。各语言均要求完整、稳定、右侧证据及闭合结构。</summary>
    /// <param name="language">当前会话明确选择的识别语言。</param><param name="text">完整识别块。</param>
    /// <param name="stableObservations">连续一致观察次数。</param><param name="hasRightContext">是否有独立右侧音频证据。</param>
    /// <param name="complete">是否完整生成。</param><returns>供字幕归约器核对的边界及依据。</returns>
    /// <exception cref="ArgumentException">源语言未受支持。</exception>
    public static BoundaryDecision Evaluate(string language, string text, int stableObservations, bool hasRightContext, bool complete)
    {
        if (!AsrLanguages.IsSupported(language)) throw new ArgumentException("字幕源语言不受支持。", nameof(language));
        if (!complete) return new(false, "incomplete_generation", stableObservations, hasRightContext);
        if (string.IsNullOrWhiteSpace(text)) return new(false, "empty_source", stableObservations, hasRightContext);
        if (!Balanced(text)) return new(false, "open_quote_or_parenthesis", stableObservations, hasRightContext);
        var rejection = language == "Japanese" ? JapaneseBoundaryPolicy.CheckEnding(text) :
            HasTerminalPunctuation(text) ? null : "sentence_end_not_confirmed";
        if (rejection is not null) return new(false, rejection, stableObservations, hasRightContext);
        if (stableObservations < 2) return new(false, "source_not_stable", stableObservations, hasRightContext);
        if (!hasRightContext) return new(false, "right_context_missing", stableObservations, false);
        return new(true, language == "Japanese" ? "stable_complete_predicate_with_right_context" : "stable_sentence_end_with_right_context", stableObservations, true);
    }

    /// <summary>接受有实际文字且带明确句末的陈述、疑问或感叹，省略号末尾保持可修订。</summary>
    /// <param name="text">结构平衡的识别原文。</param><returns>是否具有明确的文字句末。</returns>
    private static bool HasTerminalPunctuation(string text)
    {
        var body = text.AsSpan().TrimEnd().TrimEnd("」』）)】]”\"".AsSpan()).TrimEnd();
        return !body.IsEmpty && body[^1] is ('。' or '！' or '？' or '!' or '?' or '.') &&
            !body.EndsWith("...", StringComparison.Ordinal) && text.Any(char.IsLetterOrDigit);
    }

    /// <summary>检查成对引述及括号的嵌套，不把未闭合结构内的标点当作外层句尾。</summary>
    /// <param name="text">原始文本。</param><returns>所有已打开结构均按顺序闭合时为 true。</returns>
    private static bool Balanced(string text)
    {
        var stack = new Stack<char>();
        foreach (var character in text)
        {
            if (character == '"' && stack.TryPeek(out var quote) && quote == '"') { stack.Pop(); continue; }
            var closing = character switch { '「' => '」', '『' => '』', '（' => '）', '(' => ')', '[' => ']', '【' => '】', '“' => '”', '"' => '"', _ => '\0' };
            if (closing != '\0') stack.Push(closing);
            else if (character is '」' or '』' or '）' or ')' or ']' or '】' or '”')
            {
                if (!stack.TryPop(out var expected) || character != expected) return false;
            }
        }
        return stack.Count == 0;
    }
}
