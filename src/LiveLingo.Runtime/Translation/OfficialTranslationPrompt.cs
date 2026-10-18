namespace LiveLingo.Runtime.Translation;

/// <summary>唯一的已接受日语至简体中文官方提示；保留原文及规定的两个空行。</summary>
public static class OfficialTranslationPrompt
{
    /// <summary>格式化单条 user 正文，不附加未经采用的实验规则。</summary>
    /// <param name="source">完整原文。</param><returns>与 M2 golden 一致的提示正文。</returns>
    /// <exception cref="ArgumentException">原文为空或超出有界上下文输入长度。</exception>
    public static string Format(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 12000) throw new ArgumentException("原文为空或超出已支持长度。", nameof(source));
        return "You are a professional Japanese (ja) to Chinese (zh-Hans) translator. " +
            "Your goal is to accurately convey the meaning and nuances of the original Japanese text " +
            "while adhering to Chinese grammar, vocabulary, and cultural sensitivities.\n" +
            "Produce only the Chinese translation, without any additional explanations or commentary. " +
            "Please translate the following Japanese text into Chinese:\n\n\n" + source;
    }
}
