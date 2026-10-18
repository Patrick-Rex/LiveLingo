using LiveLingo.Core.Subtitles;
using LiveLingo.Runtime.Asr;
using LiveLingo.Runtime.Storage;
using LiveLingo.Runtime.Translation;

namespace LiveLingo.Runtime.Pipeline;

/// <summary>经过 M2 评测的显式模型选择，切换必须递增配置代次。</summary>
/// <param name="TranslationModel">TranslateGemma 4B 或 12B 标签。</param><param name="AsrLanguage">auto 或 Japanese。</param>
public sealed record PipelineModelSelection(string TranslationModel = "translategemma:12b", string AsrLanguage = "auto")
{
    /// <summary>拒绝未经本轮集成的模型与语言，不自动降级。</summary>
    public void Validate()
    {
        if (TranslationModel is not ("translategemma:4b" or "translategemma:12b") || AsrLanguage is not ("auto" or "Japanese"))
            throw new ArgumentException("unsupported_model_selection");
    }
}

/// <summary>隔离真实模型进程与故障夹具；所有方法必须代表实际计算生命周期。</summary>
public interface IPipelineModels : IAsyncDisposable
{
    /// <summary>实际结束旧计算并建立匹配身份的模型环境，初始化失败不得继续执行。</summary>
    /// <param name="identity">新会话及代次。</param><param name="selection">显式模型选择。</param><param name="cancellationToken">启动取消。</param>
    /// <returns>实际 Ready 时完成。</returns>
    Task ResetAsync(PipelineIdentity identity, PipelineModelSelection selection, CancellationToken cancellationToken);
    /// <summary>只识别有效音频租约，并回显实际窗口与全部版本。</summary>
    /// <param name="request">完整请求。</param><param name="window">当前窗口租约。</param><param name="cancellationToken">推理取消。</param>
    /// <returns>原始模型输出。</returns>
    Task<IpcHeader> RecognizeAsync(AsrRequest request, AudioWindowLease window, CancellationToken cancellationToken);
    /// <summary>经有限 MAF 图翻译完整当前块。</summary>
    /// <param name="input">当前原文版本。</param><param name="cancellationToken">推理取消。</param><returns>完整终止证据。</returns>
    Task<TranslationResult> TranslateAsync(TranslationInput input, CancellationToken cancellationToken);
}
