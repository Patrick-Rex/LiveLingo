using System.Collections.Immutable;
using LiveLingo.Core.Subtitles;
using LiveLingo.Core.Transcription;

namespace LiveLingo.Runtime.Translation;

/// <summary>将一条待译原文与其所属语义块关联。</summary>
/// <param name="Ticket">本次短句调用的身份，其中音频范围和源版本属于整个语义块。</param><param name="Source">本次提交给模型的完整短句。</param>
/// <param name="Terms">仅包含当前短句命中的用户词条，未配置或预热时为空。</param>
/// <param name="SourceLanguage">与当前识别配置一致的固定源语言。</param>
public sealed record TranslationInput(PipelineRequest Ticket, string Source, ImmutableArray<KeyValuePair<string, string>> Terms = default,
    string SourceLanguage = AsrLanguages.Default);

/// <summary>返回原始译文与可核实终止状态，不等同于语义质量通过。</summary>
/// <param name="Input">完整原始调用。</param><param name="Text">原始模型输出。</param><param name="Complete">正常完整生成。</param>
/// <param name="Reason">终止或失败原因。</param><param name="Elapsed">包含服务开销的单调耗时。</param>
/// <param name="Digest">本地权重摘要或以 api: 前缀标识的服务返回模型；词库直接翻译时为空。</param><param name="LoadNanoseconds">服务报告加载耗时。</param>
/// <param name="EvaluationNanoseconds">服务报告生成耗时。</param><param name="OutputTokens">服务报告输出数量。</param>
/// <param name="PromptEvaluationNanoseconds">服务报告输入计算耗时；未提供保持空值。</param><param name="InputTokens">服务报告输入数量。</param>
public sealed record TranslationResult(TranslationInput Input, string Text, bool Complete, string Reason,
    TimeSpan Elapsed, string? Digest, long? LoadNanoseconds, long? EvaluationNanoseconds, int? OutputTokens,
    long? PromptEvaluationNanoseconds = null, int? InputTokens = null);

/// <summary>隔离可取消的翻译边界以验证工作流失败与生命周期。</summary>
public interface ITranslationService
{
    /// <summary>翻译当前短句并返回实际生成状态。</summary>
    /// <param name="input">短句原文与关联的调用票据。</param><param name="cancellationToken">实际调用取消信号。</param>
    /// <returns>原始响应及完成证据。</returns>
    Task<TranslationResult> TranslateAsync(TranslationInput input, CancellationToken cancellationToken);
}
