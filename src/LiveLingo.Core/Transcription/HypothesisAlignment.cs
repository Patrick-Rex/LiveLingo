using LiveLingo.Core.Audio;

namespace LiveLingo.Core.Transcription;

/// <summary>携带完整音频来源的原始识别假设，不提供伪造词级时间。</summary>
/// <param name="Range">识别音频范围。</param><param name="Text">未去重的原文。</param>
public sealed record TranscriptionHypothesis(SampleRange Range, string Text);

/// <summary>描述同一逻辑块的假设更新或需要更多证据的对齐失败。</summary>
/// <param name="Accepted">是否有充分顺序和音频重叠依据。</param><param name="Hypothesis">可接受的完整块，否则为空。</param>
/// <param name="StablePrefixLength">原始字符串内一致前缀的 UTF16 长度，不跨代理项。</param>
/// <param name="Reason">不含正文的对齐理由。</param>
public sealed record AlignmentDecision(bool Accepted, TranscriptionHypothesis? Hypothesis, int StablePrefixLength, string Reason);

/// <summary>只在来源重叠且文本归属唯一时合并，歧义时保留旧块等待更多证据。</summary>
public static class HypothesisAlignment
{
    /// <summary>比较增长窗口或唯一后缀/前缀重叠，不按全局字串删除真实重复。</summary>
    /// <param name="previous">此前已接受的完整块。</param><param name="current">新工作窗口的原始结果。</param>
    /// <param name="minimumOverlapCharacters">跨锚点合并所需最短匹配长度。</param>
    /// <returns>完整替换假设或显式不确定原因。</returns>
    /// <exception cref="ArgumentOutOfRangeException">匹配长度不足以形成有效约束。</exception>
    public static AlignmentDecision Compare(TranscriptionHypothesis previous, TranscriptionHypothesis current, int minimumOverlapCharacters = 8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumOverlapCharacters, 2);
        if (current.Range.End < previous.Range.End || current.Range.Start < previous.Range.Start)
            return new(false, null, 0, "older_or_unanchored_window");
        if (current.Range.Start == previous.Range.Start)
            return new(true, current, CommonPrefix(previous.Text, current.Text), "same_anchor_replacement");
        if (!previous.Range.Overlaps(current.Range)) return new(false, null, 0, "audio_overlap_required");
        var (left, positions) = ComparisonText(previous.Text);
        var (right, _) = ComparisonText(current.Text);
        var overlaps = new List<int>();
        for (var length = 1; length <= Math.Min(left.Length, right.Length); length++)
            if (!char.IsLowSurrogate(left[left.Length - length]) &&
                (left.Length == length || positions[left.Length - length] != positions[left.Length - length - 1]) &&
                left.AsSpan(left.Length - length).SequenceEqual(right.AsSpan(0, length))) overlaps.Add(length);
        if (overlaps.Count != 1 || overlaps[0] < minimumOverlapCharacters)
            return new(false, null, 0, "ambiguous_text_overlap");
        var overlap = right[..overlaps[0]];
        if (left.IndexOf(overlap, StringComparison.Ordinal) != left.LastIndexOf(overlap, StringComparison.Ordinal) ||
            right.IndexOf(overlap, StringComparison.Ordinal) != right.LastIndexOf(overlap, StringComparison.Ordinal))
            return new(false, null, 0, "repeated_overlap_ambiguous");
        var text = previous.Text[..positions[left.Length - overlaps[0]]] + current.Text;
        return new(true, new(new(previous.Range.Start, current.Range.End), text), CommonPrefix(previous.Text, text), "unique_ordered_audio_overlap");
    }

    /// <summary>保守检查已观察重复的减少；只比较文本，不推断新音频包含多少次重复，也不改写候选。</summary>
    /// <param name="previous">此前已接受的完整逻辑块。</param><param name="candidate">已对齐的完整扩窗候选。</param>
    /// <returns>长重复片段或有分隔符的相邻短重复计数减少时为 true。</returns>
    /// <remarks>忽略已有比较规则允许的标点和计数写法；重复片段的正确词汇修订也可能被保守拒收。</remarks>
    public static bool HasReducedObservedRepetition(string previous, string candidate)
    {
        var (left, positions) = ComparisonText(previous);
        var (right, _) = ComparisonText(candidate);
        var fragments = new Dictionary<string, (int Count, int End)>(StringComparer.Ordinal);
        for (var start = 0; start < left.Length; start++)
        {
            if (char.IsLowSurrogate(left[start])) continue;
            // 长片段不要求相邻；按固定长度统计非重叠出现，避免把重叠字符误计为多次发言。
            const int longLength = 16;
            if (start + longLength <= left.Length && !char.IsHighSurrogate(left[start + longLength - 1]))
            {
                var fragment = left.Substring(start, longLength);
                if (!fragments.TryGetValue(fragment, out var observed)) fragments.Add(fragment, (1, start + longLength));
                else if (start >= observed.End) fragments[fragment] = (observed.Count + 1, start + longLength);
            }
        }
        var shortFragments = new HashSet<string>(StringComparer.Ordinal);
        for (var start = 0; start < left.Length; start++)
        {
            if (char.IsLowSurrogate(left[start])) continue;
            for (var length = 2; length <= 12 && start + length * 2 <= left.Length; length++)
            {
                var boundary = start + length;
                if (char.IsHighSurrogate(left[boundary - 1]) || char.IsHighSurrogate(left[boundary + length - 1]) ||
                    positions[boundary] <= positions[boundary - 1] + 1) continue;
                if (left.AsSpan(start, length).SequenceEqual(left.AsSpan(boundary, length)))
                    shortFragments.Add(left.Substring(start, length));
            }
        }
        foreach (var (fragment, observed) in fragments)
            if (observed.Count >= 2 && CountOccurrences(right, fragment) < observed.Count) return true;
        foreach (var fragment in shortFragments)
            if (CountOccurrences(right, fragment) < CountOccurrences(left, fragment)) return true;
        return false;
    }

    /// <summary>按原顺序统计互不重叠的完整片段，不以删除或拼补实现计数。</summary>
    /// <param name="text">仅用于比较的文本。</param><param name="fragment">非空比较片段。</param><returns>出现次数。</returns>
    private static int CountOccurrences(string text, string fragment)
    {
        var count = 0; var start = 0;
        while ((start = text.IndexOf(fragment, start, StringComparison.Ordinal)) >= 0)
        { count++; start += fragment.Length; }
        return count;
    }

    /// <summary>构建仅用于匹配的字词序列及原始 UTF16 位置，合并时仍使用原始文本。</summary>
    /// <param name="text">原始假设。</param><returns>字词序列和每个字符在原文中的位置。</returns>
    private static (string Text, int[] Positions) ComparisonText(string text)
    {
        var characters = new System.Text.StringBuilder(text.Length);
        var positions = new List<int>(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (TryCounterNumber(text, index, out var end, out var number))
            {
                characters.Append(number);
                for (var digit = 0; digit < number.Length; digit++) positions.Add(index);
                index = end - 1;
            }
            else if (!IsBoundaryPunctuation(text, index)) { characters.Append(text[index]); positions.Add(index); }
        }
        return (characters.ToString(), positions.ToArray());
    }

    /// <summary>仅在明确计数后缀前比较等值小整数写法，不处理人名、分数、负号或任意汉字串。</summary>
    /// <param name="text">原始文本。</param><param name="start">候选整数起点。</param>
    /// <param name="end">完整数字记号的终点。</param><param name="number">只用于比较的十进制写法。</param>
    /// <returns>0–9999 的无歧义计数记号成功解析时为 true。</returns>
    private static bool TryCounterNumber(string text, int start, out int end, out string number)
    {
        end = start; number = "";
        const string symbols = "零〇一二三四五六七八九十百千0123456789０１２３４５６７８９";
        if (start > 0 && symbols.Contains(text[start - 1])) return false;
        while (end < text.Length && symbols.Contains(text[end])) end++;
        if (end == start || end - start > 8) return false;
        var suffix = text.AsSpan(end);
        if (!(suffix.StartsWith("位以内", StringComparison.Ordinal) || suffix.StartsWith("位以下", StringComparison.Ordinal) ||
            suffix.StartsWith("位以上", StringComparison.Ordinal) || suffix.StartsWith("種類", StringComparison.Ordinal) ||
            suffix.StartsWith("枚", StringComparison.Ordinal) || suffix.StartsWith("回", StringComparison.Ordinal) || suffix.StartsWith("個", StringComparison.Ordinal))) return false;
        var value = 0; var pending = -1; var previousUnit = 10000; var hadUnit = false;
        var digits = new System.Text.StringBuilder();
        foreach (var character in text.AsSpan(start, end - start))
        {
            var unit = character switch { '十' => 10, '百' => 100, '千' => 1000, _ => 0 };
            if (unit != 0)
            {
                if (unit >= previousUnit || pending == 0 || digits.Length > 1) return false;
                value += (pending < 0 ? 1 : pending) * unit;
                pending = -1; previousUnit = unit; hadUnit = true; digits.Clear();
            }
            else
            {
                var digit = character is '零' or '〇' ? 0 : "一二三四五六七八九".IndexOf(character) + 1;
                if (digit == 0 && character is not ('零' or '〇')) digit = character <= '9' ? character - '0' : character - '０';
                if (hadUnit && pending >= 0) return false;
                pending = digit; digits.Append((char)('0' + digit));
            }
        }
        if (!hadUnit) { number = digits.ToString(); return number.Length <= 4; }
        if (pending == 0) return false;
        value += Math.Max(0, pending);
        number = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>寻找未规范化文本的一致前缀，不改变输出字形或重复。</summary>
    /// <param name="left">先前文本。</param><param name="right">新文本。</param><returns>不拆分 UTF16 代理项的前缀长度。</returns>
    public static int CommonPrefix(string left, string right)
    {
        var length = 0;
        while (length < Math.Min(left.Length, right.Length) && left[length] == right[length]) length++;
        if (length > 0 && char.IsHighSurrogate(left[length - 1])) length--;
        return length;
    }

    /// <summary>仅为稳定性证据比较字词序列，忽略标点和空白变化，不修改或去重任何输出文本。</summary>
    /// <param name="previous">此前完整原文。</param><param name="current">新完整原文。</param><param name="allowAdditionalContent">是否允许新原文继续增长。</param>
    /// <returns>此前每个非标点字符按原顺序逐字保留时为 true。</returns>
    public static bool HasStableContent(string previous, string current, bool allowAdditionalContent)
    {
        var left = 0; var right = 0; var compared = 0; var previousRight = -1;
        while (true)
        {
            while (left < previous.Length && IsBoundaryPunctuation(previous, left)) left++;
            while (right < current.Length && IsBoundaryPunctuation(current, right)) right++;
            if (left == previous.Length)
            {
                if (compared == 0) return false;
                if (right == current.Length) return true;
                if (!allowAdditionalContent) return false;
                // 新增文字必须仍由句末符号分开；「行かない→行かないわけではない」不能提前闭合。
                for (var index = previousRight + 1; index < right; index++)
                    if (current[index] is '。' or '！' or '？' or '.' or '!' or '?') return true;
                return false;
            }
            if (right == current.Length || previous[left++] != current[right++]) return false;
            compared++; previousRight = right - 1;
        }
    }

    /// <summary>只忽略文本表层断句符号；引号和括号仍参与逐字比较及语义配对。</summary>
    /// <param name="text">完整原文。</param><param name="index">待比较字符位置。</param><returns>不属于数字内部的空白或断句符号时为 true。</returns>
    private static bool IsBoundaryPunctuation(string text, int index) =>
        !(index > 0 && index + 1 < text.Length && char.IsDigit(text[index - 1]) && char.IsDigit(text[index + 1])) &&
        (char.IsWhiteSpace(text[index]) || text[index] is '。' or '、' or '，' or '！' or '？' or '.' or ',' or '!' or '?');
}
