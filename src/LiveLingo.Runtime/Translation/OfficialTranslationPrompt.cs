using System.Collections.Immutable;
using System.Text;
using LiveLingo.Core.Transcription;

namespace LiveLingo.Runtime.Translation;

/// <summary>按指定的翻译方向构造单条 TranslateGemma 提示，保留正文前两个空行。</summary>
public static class OfficialTranslationPrompt
{
    /// <summary>固定的 4096 token 上下文为输出留 512、模板留 256，其余按 UTF-8 字节保守估算。</summary>
    internal const int MaximumPromptBytes = 4096 - 512 - 256;

    /// <summary>格式化单条 user 正文，仅在用户词库匹配且预算足够时加入对应词条。</summary>
    /// <param name="source">本次调用的短句原文。</param><param name="terms">按优先级排列的匹配词条。</param>
    /// <param name="sourceLanguage">与当前识别配置一致的源语言。</param><param name="targetLanguage">译文使用的语言。</param>
    /// <returns>保留完整原文的提示，默认日译中且无词条时与 M2 golden 一致。</returns>
    /// <exception cref="ArgumentException">语言不受支持或原文超出输入边界。</exception>
    public static string Format(string source, ImmutableArray<KeyValuePair<string, string>> terms = default,
        string sourceLanguage = AsrLanguages.Default, string targetLanguage = AsrLanguages.DefaultTarget)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 12000) throw new ArgumentException("原文为空或超出已支持长度。", nameof(source));
        var code = AsrLanguages.ToApiCode(sourceLanguage);
        var targetCode = AsrLanguages.ToTargetCode(targetLanguage);
        var instruction = $"You are a professional {sourceLanguage} ({code}) to {targetLanguage} ({targetCode}) translator. " +
            $"Your goal is to accurately convey the meaning and nuances of the original {sourceLanguage} text " +
            $"while adhering to {targetLanguage} grammar, vocabulary, and cultural sensitivities.\n" +
            $"Produce only the {targetLanguage} translation, without any additional explanations or commentary. ";
        if (sourceLanguage == targetLanguage)
            instruction += targetLanguage == "Chinese" ? "Render Traditional Chinese in natural Simplified Chinese while preserving the original meaning. " :
                $"Preserve the original {targetLanguage} wording and meaning. ";
        var lead = $"Please translate the following {sourceLanguage} text into {targetLanguage}:\n\n\n";
        var hint = terms.IsDefaultOrEmpty ? "" : GlossaryPrompt.Format(terms, sourceLanguage, targetLanguage,
            MaximumPromptBytes - Encoding.UTF8.GetByteCount(instruction + lead) - Encoding.UTF8.GetByteCount(source));
        return instruction + hint + lead + source;
    }
}
