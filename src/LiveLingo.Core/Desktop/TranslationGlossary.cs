using System.Collections.Immutable;

namespace LiveLingo.Core.Desktop;

/// <summary>在设置应用时编译原文至简体中文的词表，为每条待译短句选择固定译文或相关术语。</summary>
public sealed class TranslationGlossary
{
    private readonly KeyValuePair<string, string>[] entries;

    /// <summary>按原词长度构建匹配索引，相同长度保留编辑顺序。</summary>
    /// <param name="rows">已验证且原词唯一的有序词条。</param>
    private TranslationGlossary(List<KeyValuePair<string, string>> rows)
    {
        entries = rows.OrderByDescending(row => row.Key.Length).ToArray();
    }

    /// <summary>获取没有自定义术语时共享的空词表。</summary>
    public static TranslationGlossary Empty { get; } = new([]);

    /// <summary>解析每行首个等号分隔的原词与译名，拒绝重复原词或超出实时预算的配置。</summary>
    /// <param name="text">当前设置草稿的完整文本。</param><returns>可供整场会话复用的只读词表。</returns>
    /// <exception cref="ArgumentException">词表长度、条数或词条格式无效。</exception>
    public static TranslationGlossary Parse(string text)
    {
        if (text is null || text.Length > 4096) throw new ArgumentException("翻译词库须为最多 4096 个字符的文本。");
        if (string.IsNullOrWhiteSpace(text)) return Empty;
        var rows = new List<KeyValuePair<string, string>>();
        var sources = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(text);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (rows.Count == 64) throw new ArgumentException($"翻译词库第 {lineNumber} 行超出 64 条上限。");
            var separator = line.IndexOf('=');
            if (separator < 0) throw new ArgumentException($"翻译词库第 {lineNumber} 行需要按「原文=译文」填写。");
            var source = line[..separator].Trim();
            var target = line[(separator + 1)..].Trim();
            if (source.Length is 0 or > 120 || target.Length is 0 or > 120 ||
                source.Any(char.IsControl) || target.Any(char.IsControl) || line.IndexOfAny(['\u2028', '\u2029']) >= 0)
                throw new ArgumentException($"翻译词库第 {lineNumber} 行的原文和译文各需 1 至 120 个字符，且须为单行文本。");
            if (!sources.Add(source)) throw new ArgumentException($"翻译词库第 {lineNumber} 行的原文重复。");
            rows.Add(new(source, target));
        }
        return new(rows);
    }

    /// <summary>精确匹配整句，否则选择最多八个出现于原文的译名；重叠部分优先长词，同长沿用配置顺序。</summary>
    /// <param name="source">本次提交翻译的完整短句。</param>
    /// <returns>整句的指定译文，或可提示模型的去重词条；无命中时两者均为空。</returns>
    public (string? ExactTranslation, ImmutableArray<KeyValuePair<string, string>> Terms) Match(string source)
    {
        bool[]? occupied = null;
        ImmutableArray<KeyValuePair<string, string>>.Builder? terms = null;
        foreach (var entry in entries)
        {
            if (source.Equals(entry.Key, StringComparison.Ordinal)) return (entry.Value, []);
            var position = source.IndexOf(entry.Key, StringComparison.Ordinal);
            if (position < 0) continue;
            occupied ??= new bool[source.Length];
            var matched = false;
            while (position >= 0)
            {
                var span = occupied.AsSpan(position, entry.Key.Length);
                var next = position + 1;
                if (!span.Contains(true))
                {
                    span.Fill(true);
                    matched = true;
                    next = position + entry.Key.Length;
                }
                position = source.IndexOf(entry.Key, next, StringComparison.Ordinal);
            }
            if (!matched) continue;
            terms ??= ImmutableArray.CreateBuilder<KeyValuePair<string, string>>(8);
            terms.Add(entry);
            if (terms.Count == 8) break;
        }
        return (null, terms?.ToImmutable() ?? []);
    }
}
