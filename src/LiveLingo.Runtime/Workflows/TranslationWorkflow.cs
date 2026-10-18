using LiveLingo.Core.Subtitles;
using LiveLingo.Runtime.Translation;
using Microsoft.Agents.AI.Workflows;

namespace LiveLingo.Runtime.Workflows;

/// <summary>每块一个有限强类型图；工作流输出仍须由字幕归约器校验身份。</summary>
public sealed class TranslationWorkflow
{
    private readonly ITranslationService service;

    /// <summary>组合本地翻译边界，不引入 agent 对话或无限运行图。</summary>
    /// <param name="service">具有真实取消与失败语义的翻译服务。</param>
    public TranslationWorkflow(ITranslationService service) => this.service = service;

    /// <summary>执行校验、翻译和结果校验三步，消费全部有限事件并确保运行已释放。</summary>
    /// <param name="input">当前完整原文及身份。</param><param name="cancellationToken">取消整个运行的信号。</param>
    /// <returns>唯一匹配本次请求的结果，不包含自动重试。</returns>
    public async Task<TranslationResult> RunAsync(TranslationInput input, CancellationToken cancellationToken)
    {
        var validate = new ValidateInput();
        var translate = new Translate(service);
        var response = new ValidateResponse(input);
        var workflow = new WorkflowBuilder(validate).AddEdge(validate, translate).AddEdge(translate, response).WithOutputFrom(response).Build();
        await using var run = await InProcessExecution.OpenStreamingAsync(workflow,
            sessionId: input.Ticket.Id.ToString(), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!await run.TrySendMessageAsync(input).ConfigureAwait(false)) throw new InvalidOperationException("工作流未接受输入。");
        TranslationResult? result = null;
        await foreach (var item in run.WatchStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            if (item is WorkflowErrorEvent failure) throw new InvalidOperationException("翻译工作流失败。", failure.Exception);
            if (item is not WorkflowOutputEvent output) continue;
            if (result is not null || output.Data is not TranslationResult value) throw new InvalidDataException("工作流输出数量或类型不符合契约。");
            result = value;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result ?? throw new InvalidDataException("工作流结束但没有完整输出。");
    }

    /// <summary>验证输入只包含一个完整有效语义块。</summary>
    private sealed class ValidateInput : Executor<TranslationInput, TranslationInput>
    {
        /// <summary>建立固定图节点。</summary>
        public ValidateInput() : base("validate_input") { }
        /// <inheritdoc />
        public override ValueTask<TranslationInput> HandleAsync(TranslationInput message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            message.Ticket.Identity.Validate();
            if (message.Ticket.Kind != PipelineRequestKind.Translation || message.Ticket.Id == Guid.Empty ||
                message.Ticket.Sequence <= 0 || message.Ticket.Segment.Id == Guid.Empty || message.Ticket.Segment.Generation <= 0 ||
                message.Ticket.SourceRevision <= 0 || message.Ticket.Range.Length == 0)
                throw new InvalidDataException("翻译关联身份无效。");
            _ = OfficialTranslationPrompt.Format(message.Source);
            return ValueTask.FromResult(message);
        }
    }

    /// <summary>将框架运行取消传递到唯一服务调用，不重复发送节点输出。</summary>
    private sealed class Translate : Executor<TranslationInput, TranslationResult>
    {
        private readonly ITranslationService service;
        /// <summary>绑定服务边界。</summary><param name="service">本地翻译服务。</param>
        public Translate(ITranslationService service) : base("translate") => this.service = service;
        /// <inheritdoc />
        public override async ValueTask<TranslationResult> HandleAsync(TranslationInput message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
            await service.TranslateAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>拒绝错误关联的服务响应，完整性状态原样交给归约器。</summary>
    private sealed class ValidateResponse : Executor<TranslationResult, TranslationResult>
    {
        private readonly TranslationInput expected;
        /// <summary>绑定本次精确输入。</summary><param name="expected">当前源文及身份。</param>
        public ValidateResponse(TranslationInput expected) : base("validate_response") => this.expected = expected;
        /// <inheritdoc />
        public override ValueTask<TranslationResult> HandleAsync(TranslationResult message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message.Input != expected) throw new InvalidDataException("工作流结果不属于当前完整原文版本。");
            if (message.Complete && string.IsNullOrWhiteSpace(message.Text)) throw new InvalidDataException("空译文不得声明成功。");
            return ValueTask.FromResult(message);
        }
    }
}
