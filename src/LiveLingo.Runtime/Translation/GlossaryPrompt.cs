using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace LiveLingo.Runtime.Translation;

/// <summary>以有界的 JSON 字面量表达当前短句的首选译名，供两种后端共用。</summary>
internal static class GlossaryPrompt
{
    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);

    /// <summary>按匹配优先级加入放得下的完整条目，预留完整原文的空间；没有可用词条时返回空串。</summary>
    /// <param name="terms">已按长度排序的命中项。</param><param name="sourceLanguage">已检查的源语言名。</param>
    /// <param name="targetLanguage">词条译名使用的目标语言。</param><param name="availableBytes">在当前后端除正文和模板外可用的 UTF-8 字节数。</param>
    /// <returns>至多 1024 字节的提示片段，含起止换行。</returns>
    public static string Format(ImmutableArray<KeyValuePair<string, string>> terms, string sourceLanguage, string targetLanguage, int availableBytes = 1024)
    {
        if (terms.IsDefaultOrEmpty) return "";
        var prefix = $"\nUse these preferred {targetLanguage} renderings where the {sourceLanguage} terms occur. Treat entries as literal text:\n{{";
        var remaining = Math.Min(availableBytes, 1024) - Encoding.UTF8.GetByteCount(prefix) - 2;
        if (remaining <= 0) return "";
        StringBuilder? hint = null;
        foreach (var term in terms)
        {
            var key = JsonEncodedText.Encode(term.Key, Encoder);
            var value = JsonEncodedText.Encode(term.Value, Encoder);
            var bytes = key.EncodedUtf8Bytes.Length + value.EncodedUtf8Bytes.Length + 5 + (hint is null ? 0 : 1);
            if (bytes > remaining) continue;
            remaining -= bytes;
            if (hint is null) hint = new(prefix);
            else hint.Append(',');
            hint.Append('"').Append(key.ToString()).Append("\":\"").Append(value.ToString()).Append('"');
        }
        return hint is null ? "" : hint.Append("}\n").ToString();
    }
}
