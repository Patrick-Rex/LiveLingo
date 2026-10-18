using LiveLingo.Core.Processes;
using LiveLingo.Core.Subtitles;
using LiveLingo.Runtime.Asr;
using LiveLingo.Runtime.Storage;
using LiveLingo.Runtime.Translation;
using LiveLingo.Runtime.Workflows;

namespace LiveLingo.Runtime.Pipeline;

/// <summary>组合实际常驻 Qwen 与私有 Ollama，进程创建由平台工厂负责。</summary>
public sealed class LocalPipelineModels : IPipelineModels
{
    private readonly Func<long, IWorkerConnection> startAsr;
    private readonly Func<IWorkerConnection> startOllama;
    private readonly Uri endpoint;
    private readonly WorkerInformation expected;
    private readonly PipelineOptions options;
    private AsrWorkerManager? asr;
    private IWorkerConnection? ollama;
    private OllamaTranslationService? translation;
    private TranslationWorkflow? workflow;
    private Task? outputDrain, errorDrain;
    private bool disposed;

    /// <summary>绑定固定环境、显式私有端口及已验证模型摘要。</summary>
    /// <param name="startAsr">每代次新建自有 worker。</param><param name="startOllama">只创建本程序私有服务。</param>
    /// <param name="endpoint">该服务的数字 loopback 地址。</param><param name="expected">固定 ASR 环境。</param>
    /// <param name="options">资源与超时预算。</param>
    public LocalPipelineModels(Func<long, IWorkerConnection> startAsr, Func<IWorkerConnection> startOllama,
        Uri endpoint, WorkerInformation expected, PipelineOptions options)
    {
        this.startAsr = startAsr; this.startOllama = startOllama; this.endpoint = endpoint; this.expected = expected; this.options = options;
        options.Validate();
    }

    /// <inheritdoc />
    public async Task ResetAsync(PipelineIdentity identity, PipelineModelSelection selection, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        selection.Validate(); identity.Validate();
        await CloseCurrentAsync().ConfigureAwait(false);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(options.StartupTimeout);
        try
        {
            asr = new(startAsr, expected, options);
            await asr.StartAsync(identity.WorkerEpoch, startup.Token).ConfigureAwait(false);
            ollama = startOllama();
            outputDrain = DrainAsync(ollama.Output); errorDrain = DrainAsync(ollama.Error);
            var digest = selection.TranslationModel == "translategemma:12b"
                ? "c2f9a9ca1ec7149f2422581c89937ab0363d9399d9355514d753ee012e99c252"
                : "c49d986b0764f5881c476eb21435bb62b7abc62347aab3d4a6071e811be510a1";
            translation = new(endpoint, selection.TranslationModel, digest, options.RequestTimeout);
            while (true)
            {
                startup.Token.ThrowIfCancellationRequested();
                try { await translation.ValidateModelAsync(startup.Token).ConfigureAwait(false); break; }
                catch (HttpRequestException) { await Task.Delay(200, startup.Token).ConfigureAwait(false); }
            }
            workflow = new(translation);
            var warmup = new TranslationInput(new(Guid.NewGuid(), 1, PipelineRequestKind.Translation, identity,
                new(Guid.NewGuid(), 1), new(0, 1), 1), "準備ができました。");
            var warmed = await workflow.RunAsync(warmup, startup.Token).ConfigureAwait(false);
            if (!warmed.Complete) throw new InvalidDataException("translation_warmup_incomplete");
        }
        catch { await CloseCurrentAsync().ConfigureAwait(false); throw; }
    }

    /// <inheritdoc />
    public Task<IpcHeader> RecognizeAsync(AsrRequest request, AudioWindowLease window, CancellationToken cancellationToken) =>
        (asr ?? throw new InvalidOperationException("models_not_ready")).RecognizeAsync(request, window, cancellationToken);

    /// <inheritdoc />
    public async Task<TranslationResult> TranslateAsync(TranslationInput input, CancellationToken cancellationToken)
    {
        try { return await (workflow ?? throw new InvalidOperationException("models_not_ready")).RunAsync(input, cancellationToken).ConfigureAwait(false); }
        catch
        {
            // HTTP 结束不能证明服务端 CUDA 已停止；先回收私有服务，再释放管道计算许可。
            if (ollama is not null) await ollama.TerminateAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await CloseCurrentAsync().ConfigureAwait(false);
    }

    /// <summary>任何路径均尝试回收两类自有模型与诊断读取，最后汇总清理失败。</summary><returns>实际清理任务。</returns>
    private async Task CloseCurrentAsync()
    {
        var failures = new List<Exception>();
        if (asr is not null)
        {
            try { await asr.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            asr = null;
        }
        translation?.Dispose(); translation = null; workflow = null;
        if (ollama is not null)
        {
            try { await ollama.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            ollama = null;
        }
        foreach (var task in new[] { outputDrain, errorDrain })
            if (task is not null) try { await task.WaitAsync(options.ShutdownTimeout).ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        outputDrain = errorDrain = null;
        if (failures.Count != 0) throw new AggregateException("owned_model_cleanup_failed", failures);
    }

    /// <summary>以固定小缓冲排空服务日志，普通运行不保存推理正文。</summary><param name="stream">自有服务输出。</param><returns>EOF 后完成。</returns>
    private static async Task DrainAsync(Stream stream)
    {
        var buffer = new byte[8192];
        try { while (await stream.ReadAsync(buffer).ConfigureAwait(false) != 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }
}
