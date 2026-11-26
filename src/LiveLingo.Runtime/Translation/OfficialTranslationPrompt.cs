using System.Collections.Immutable;
using System.Text;
using LiveLingo.Core.Transcription;

namespace LiveLingo.Runtime.Translation;

/// <summary>使用明确的源语言与简体中文目标构造单条 TranslateGemma 提示，保留正文前两个空行。</summary>
public static class OfficialTranslationPrompt
{
    /// <summary>固定的 4096 token 上下文为输出留 512、模板留 256，其余按 UTF-8 字节保守估算。</summary>
    internal const int MaximumPromptBytes = 4096 - 512 - 256;

    /// <summary>格式化单条 user 正文，仅在用户词库匹配且预算足够时加入对应词条。</summary>
    /// <param name="source">本次调用的短句原文。</param><param name="terms">按优先级排列的匹配词条。</param>
    /// <param name="sourceLanguage">与当前识别配置一致的源语言。</param>
    /// <returns>保留完整原文的提示，日语无词条时与 M2 golden 一致。</returns>
    /// <exception cref="ArgumentException">源语言不受支持或原文超出输入边界。</exception>
    public static string Format(string source, ImmutableArray<KeyValuePair<string, string>> terms = default, string sourceLanguage = AsrLanguages.Default)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 12000) throw new ArgumentException("原文为空或超出已支持长度。", nameof(source));
        var code = AsrLanguages.ToApiCode(sourceLanguage);
        var instruction = $"You are a professional {sourceLanguage} ({code}) to Chinese (zh-Hans) translator. " +
            $"Your goal is to accurately convey the meaning and nuances of the original {sourceLanguage} text " +
            "while adhering to Chinese grammar, vocabulary, and cultural sensitivities.\n" +
            "Produce only the Chinese translation, without any additional explanations or commentary. ";
        if (sourceLanguage == "Chinese") instruction += "Render Traditional Chinese in natural Simplified Chinese while preserving the original meaning. ";
        var lead = $"Please translate the following {sourceLanguage} text into Chinese:\n\n\n";
        var hint = terms.IsDefaultOrEmpty ? "" : GlossaryPrompt.Format(terms, sourceLanguage,
            MaximumPromptBytes - Encoding.UTF8.GetByteCount(instruction + lead) - Encoding.UTF8.GetByteCount(source));
        return instruction + hint + lead + source;
    }
}
