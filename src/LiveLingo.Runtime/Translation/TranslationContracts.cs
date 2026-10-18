using LiveLingo.Core.Subtitles;

namespace LiveLingo.Runtime.Translation;

/// <summary>携带精确源版本的完整块翻译输入，原文始终作为数据。</summary>
/// <param name="Ticket">完整调用关联字段。</param><param name="Source">未经去重或修补的全部源文。</param>
public sealed record TranslationInput(PipelineRequest Ticket, string Source);

/// <summary>返回原始译文与可核实终止状态，不等同于语义质量通过。</summary>
/// <param name="Input">完整原始调用。</param><param name="Text">原始模型输出。</param><param name="Complete">正常完整生成。</param>
/// <param name="Reason">终止或失败原因。</param><param name="Elapsed">包含服务开销的单调耗时。</param>
/// <param name="Digest">启动时校验的权重身份。</param><param name="LoadNanoseconds">服务报告加载耗时。</param>
/// <param name="EvaluationNanoseconds">服务报告生成耗时。</param><param name="OutputTokens">服务报告输出数量。</param>
/// <param name="PromptEvaluationNanoseconds">服务报告输入计算耗时；未提供保持空值。</param><param name="InputTokens">服务报告输入数量。</param>
public sealed record TranslationResult(TranslationInput Input, string Text, bool Complete, string Reason,
    TimeSpan Elapsed, string Digest, long? LoadNanoseconds, long? EvaluationNanoseconds, int? OutputTokens,
    long? PromptEvaluationNanoseconds = null, int? InputTokens = null);

/// <summary>隔离可取消的翻译边界以验证工作流失败与生命周期。</summary>
public interface ITranslationService
{
    /// <summary>翻译完整块，截断和服务失败不得被包装成成功。</summary>
    /// <param name="input">当前完整块和版本。</param><param name="cancellationToken">实际调用取消信号。</param>
    /// <returns>原始响应及完成证据。</returns>
    Task<TranslationResult> TranslateAsync(TranslationInput input, CancellationToken cancellationToken);
}
