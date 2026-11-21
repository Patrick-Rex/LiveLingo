using System.Collections.Immutable;

namespace LiveLingo.Core.Subtitles;

/// <summary>保存一次短句翻译实际使用的原文和返回的完整译文。</summary>
/// <param name="Source">本次请求的原文短句。</param><param name="Translation">与该原文对应的完整译文。</param>
public sealed record SubtitlePhraseTranslation(string Source, string Translation);

/// <summary>按识别文本中的句末和换行划分翻译输入，保留开放句尾与真实重复。</summary>
public static class SubtitlePhrases
{
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
        if (!text.IsEmpty) phrases.Add(text.ToString());
    }
}
