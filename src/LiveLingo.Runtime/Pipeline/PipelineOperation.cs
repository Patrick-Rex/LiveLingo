using LiveLingo.Core.Audio;
using LiveLingo.Core.Subtitles;

namespace LiveLingo.Runtime.Pipeline;

/// <summary>一次实际模型操作的无正文诊断；生成正常结束不等于结果已被字幕归约接收。</summary>
/// <param name="Utc">观察操作完成的时间。</param><param name="Request">完整调用关联身份。</param>
/// <param name="Window">实际识别窗口，翻译为空。</param><param name="QueueSeconds">本快照待执行时间。</param>
/// <param name="HostSeconds">宿主完整模型操作耗时。</param><param name="ModelSeconds">worker 报告推理耗时，翻译另列 eval。</param>
/// <param name="GenerationComplete">模型是否完整终止。</param><param name="Termination">模型终止或传输失败原因。</param>
/// <param name="Disposition">权威事件循环接收、拒绝或取消原因。</param><param name="LoadNanoseconds">Ollama 模型加载耗时。</param>
/// <param name="EvaluationNanoseconds">Ollama 输出计算耗时。</param><param name="OutputTokens">Ollama 输出 token 数。</param>
/// <param name="ModelDigest">翻译模型摘要。</param>
/// <param name="PromptEvaluationNanoseconds">Ollama 输入计算耗时。</param><param name="InputTokens">Ollama 输入数量。</param>
/// <param name="CountedAttempt">是否已计入宿主模型尝试数，不能等同于 GPU 一定开始计算。</param>
/// <param name="RecognitionTokenBudget">实际识别请求的最大输出 token；没有构造识别请求时为空。</param>
public sealed record PipelineOperation(DateTimeOffset Utc, PipelineRequest Request, SampleRange? Window, double? QueueSeconds,
    double HostSeconds, double? ModelSeconds, bool GenerationComplete, string? Termination, string Disposition,
    long? LoadNanoseconds, long? EvaluationNanoseconds, int? OutputTokens, string? ModelDigest, long? PromptEvaluationNanoseconds, int? InputTokens, bool CountedAttempt,
    int? RecognitionTokenBudget = null);
