using System.Collections.Immutable;
using System.Globalization;

namespace LiveLingo.Core.Subtitles;

/// <summary>保存一次短句翻译实际使用的原文和返回的完整译文。</summary>
/// <param name="Source">本次请求的原文短句。</param><param name="Translation">与该原文对应的完整译文。</param>
public sealed record SubtitlePhraseTranslation(string Source, string Translation);

/// <summary>按句末、停顿和阅读长度划分字幕及翻译输入，保留开放句尾与真实重复。</summary>
public static class SubtitlePhrases
{
    // 预算按半角列近似估算，同一份识别输入在主窗、浮窗和模型中始终具有相同的边界。
    private const int ReadingColumns = 56;
    private const int MinimumClauseColumns = 20;

    /// <summary>按出现顺序提取短句，保留标点、数字小数点和引述归属。短句共用原语义块的音频范围。</summary>
    /// <param name="source">当前版本的完整识别原文。</param>
    /// <returns>去除首尾空白的非空原文短句，包括最后尚未说完的一句。</returns>
    public static ImmutableArray<string> Split(string source)
    {
        var phrases = ImmutableArray.CreateBuilder<string>();
        var closers = new Stack<char>();
        var start = 0;
        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            if (character is '\r' or '\n')
            {
                Add(phrases, source.AsSpan(start, index - start));
                start = index + 1;
                continue;
            }
            if (closers.TryPeek(out var expected) && character == expected)
            {
                closers.Pop();
                continue;
            }
            var closer = character switch
            {
                '「' => '」', '『' => '』', '“' => '”', '"' => '"', '（' => '）', '(' => ')',
                '【' => '】', '[' => ']', _ => '\0'
            };
            if (closer != '\0') { closers.Push(closer); continue; }
            if (!IsSentenceEnd(source, index)) continue;

            var end = index + 1;
            while (end < source.Length && IsSentenceEnd(source, end)) end++;
            var quoted = closers.Count > 0;
            while (end < source.Length && closers.TryPeek(out expected) && source[end] == expected)
            {
                closers.Pop();
                end++;
            }
            // 「行く？」と聞いた。中的引语属于外层句子；相邻的独立引语则分别翻译。
            var continuesOutsideQuote = quoted && end < source.Length &&
                (char.IsLetterOrDigit(source[end]) || IsSentenceEnd(source, end) || source[end] is '、' or '，' or ',');
            if (closers.Count == 0 && !continuesOutsideQuote)
            {
                Add(phrases, source.AsSpan(start, end - start));
                start = end;
            }
            index = end - 1;
        }
        Add(phrases, source.AsSpan(start));
        return phrases.ToImmutable();
    }

    /// <summary>识别中日文及英文句末，保留小数、域名和缩写中的内部点号。</summary>
    /// <param name="source">完整识别文本。</param><param name="index">待检查字符位置。</param>
    /// <returns>该位置可作为句末标点时为 true。</returns>
    private static bool IsSentenceEnd(string source, int index) => source[index] switch
    {
        '。' or '！' or '？' or '!' or '?' => true,
        '.' => !(index > 0 && index + 1 < source.Length && char.IsAsciiLetterOrDigit(source[index - 1]) &&
            char.IsAsciiLetterOrDigit(source[index + 1])),
        _ => false
    };

    /// <summary>按原顺序保留非空短句，单一空行不产生译文请求。</summary>
    /// <param name="phrases">本次识别的短句集合。</param><param name="text">分隔位置之间的原文。</param>
    private static void Add(ImmutableArray<string>.Builder phrases, ReadOnlySpan<char> text)
    {
        text = text.Trim();
        while (!text.IsEmpty)
        {
            var end = ReadingEnd(text);
            phrases.Add(text[..end].TrimEnd().ToString());
            text = text[end..].TrimStart();
        }
    }

    /// <summary>从固定起点向前读取一条字幕，优先使用停顿、日语谓语和词界，连续无边界的文字在完整字素后换句。</summary>
    /// <param name="text">由句末和换行初次划分、去除首尾空白的非空原文。</param>
    /// <returns>当前阅读短句在输入内的独占终点。</returns>
    private static int ReadingEnd(ReadOnlySpan<char> text)
    {
        var columns = 0;
        var predicateEnd = 0;
        var wordEnd = 0;
        for (var index = 0; index < text.Length;)
        {
            var length = StringInfo.GetNextTextElementLength(text[index..]);
            var current = text[index];
            var end = index + length;
            if (end == text.Length) return end;
            var next = text[end];
            columns += current <= '~' ? 1 : 2;

            if (columns >= MinimumClauseColumns)
            {
                if (current is '、' or '，' or ',' or '；' or ';' or '：' or ':' && !IsNumericSeparator(text, index))
                    return IncludeTrailingPunctuation(text, end);

                if (HasSpokenEnding(text[..end]) && next is not ('か' or 'ね' or 'よ' or 'が'))
                    predicateEnd = end;
                else if (current is 'は' or 'を' or 'に' or 'で' or 'が' or 'と' or 'も' && IsIdeograph(next))
                    wordEnd = end;
            }
            if (char.IsWhiteSpace(next)) wordEnd = end;

            if (columns >= ReadingColumns)
            {
                if (IsTrailingPunctuation(next) && !IsNumericSeparator(text, end))
                    return IncludeTrailingPunctuation(text, end);
                if (predicateEnd > 0) return predicateEnd;
                if (wordEnd > 0) return wordEnd;
                // 没有更早断点的长拉丁单词或数字保持完整。
                if (!(char.IsAsciiLetterOrDigit(current) && char.IsAsciiLetterOrDigit(next)) &&
                    !IsNumericSeparator(text, index) && !IsNumericSeparator(text, end)) return end;
            }
            index = end;
        }
        return text.Length;
    }

    /// <summary>识别适合阅读停顿的常见口语句尾，仅用于较长字幕的断行。</summary>
    /// <param name="prefix">从本句开头至候选边界的原文。</param><returns>候选处是否具有口语句尾。</returns>
    private static bool HasSpokenEnding(ReadOnlySpan<char> prefix) =>
        prefix.EndsWith("です", StringComparison.Ordinal) || prefix.EndsWith("ます", StringComparison.Ordinal) ||
        prefix.EndsWith("ました", StringComparison.Ordinal) || prefix.EndsWith("でした", StringComparison.Ordinal) ||
        prefix.EndsWith("ません", StringComparison.Ordinal) || prefix.EndsWith("ください", StringComparison.Ordinal) ||
        prefix.EndsWith("ですね", StringComparison.Ordinal) || prefix.EndsWith("ですよ", StringComparison.Ordinal) ||
        prefix.EndsWith("ですか", StringComparison.Ordinal) || prefix.EndsWith("ますか", StringComparison.Ordinal);

    /// <summary>判断助词之后是否开始常见的汉字词。</summary>
    /// <param name="value">下一个字符。</param><returns>是否为基本或扩展 A 区汉字。</returns>
    private static bool IsIdeograph(char value) => value is >= '\u3400' and <= '\u9fff';

    /// <summary>判断停顿符号是否位于小数、分组数值或时间内部。</summary>
    /// <param name="text">本次阅读范围。</param><param name="index">候选符号的位置。</param>
    /// <returns>符号是否必须与两侧的数字一起保留。</returns>
    private static bool IsNumericSeparator(ReadOnlySpan<char> text, int index) =>
        index > 0 && index + 1 < text.Length && text[index] is ',' or '，' or '.' or ':' or '：' &&
        char.IsDigit(text[index - 1]) && char.IsDigit(text[index + 1]);

    /// <summary>将边界后紧邻的标点和结束引号留给前句。</summary>
    /// <param name="text">当前阅读范围。</param><param name="end">紧邻标点之前的独占终点。</param>
    /// <returns>附带完整句尾标点后的独占终点。</returns>
    private static int IncludeTrailingPunctuation(ReadOnlySpan<char> text, int end)
    {
        while (end < text.Length && IsTrailingPunctuation(text[end])) end += StringInfo.GetNextTextElementLength(text[end..]);
        return end;
    }

    /// <summary>判断字符是否应随前面的词一起显示。</summary>
    /// <param name="value">边界后的字符。</param><returns>是否为停顿、终止或闭合符号。</returns>
    private static bool IsTrailingPunctuation(char value) => value is '。' or '！' or '？' or '!' or '?' or '.' or
        '、' or '，' or ',' or '；' or ';' or '：' or ':' or '」' or '』' or '”' or '）' or ')' or '】' or ']';
}
