using System.Collections.Immutable;
using System.Diagnostics;
using LiveLingo.Core.Audio;
using LiveLingo.Core.Segmentation;
using LiveLingo.Core.Subtitles;
using LiveLingo.Core.Transcription;
using LiveLingo.Runtime.Asr;
using LiveLingo.Runtime.Storage;
using LiveLingo.Runtime.Translation;

namespace LiveLingo.Runtime.Pipeline;

/// <summary>单写入循环接收的显式输入，音频与控制事件使用同一顺序。</summary>
public abstract record PipelineInput;
/// <summary>已转换且未截断的一段连续采样。</summary><param name="CaptureEpoch">采集代次。</param><param name="Audio">来源范围及音频。</param>
public sealed record PipelineAudio(long CaptureEpoch, ResampledAudio Audio) : PipelineInput;
/// <summary>采集恢复事件，后续时间轴从零开始。</summary><param name="CaptureEpoch">严格递增的新代次。</param><param name="Reason">缺口原因码。</param>
public sealed record PipelineCaptureReset(long CaptureEpoch, string Reason) : PipelineInput;
/// <summary>模型或语言切换事件，旧配置结果立即失效。</summary><param name="Selection">明确的新模型选择。</param>
public sealed record PipelineConfiguration(PipelineModelSelection Selection) : PipelineInput;
/// <summary>明确记录不能继续声称连续覆盖的音频范围。</summary>
/// <param name="CaptureEpoch">发生缺口的代次。</param><param name="Range">已知丢失或停止覆盖范围。</param><param name="Reason">原因码。</param>
public sealed record PipelineGap(long CaptureEpoch, SampleRange Range, string Reason);
/// <summary>仅由显式评测订阅的拒收原文证据，普通运行不保存正文。</summary>
/// <param name="Request">实际请求身份。</param><param name="Previous">仍保留的旧假设。</param>
/// <param name="Current">未接受的新假设。</param><param name="Reason">拒收原因。</param>
public sealed record RecognitionRejection(PipelineRequest Request, TranscriptionHypothesis? Previous, TranscriptionHypothesis Current, string Reason);
/// <summary>完整可投影状态，顶层序号包含字幕、压力和失败更新。</summary>
/// <param name="StateSequence">全局单调快照序号。</param><param name="Subtitles">权威字幕快照。</param>
/// <param name="AudioEnd">当前音频终点。</param><param name="RecognizedEnd">已接受的模型覆盖。</param>
/// <param name="BacklogSamples">尚未覆盖的采样数。</param><param name="ClosedQueueCount">闭合待翻译数。</param>
/// <param name="MemorySamples">缓存分配采样数。</param><param name="SpilledSamples">磁盘采样数。</param>
/// <param name="Pins">活跃窗口数。</param><param name="Status">运行/限制/停止状态。</param>
/// <param name="Gaps">已发生的有限缺口列表。</param><param name="RecognitionCalls">实际识别调用数。</param>
/// <param name="TranslationCalls">实际翻译调用数。</param><param name="LastComputeSeconds">最近模型操作耗时。</param>
public sealed record PipelineSnapshot(long StateSequence, SubtitleSnapshot Subtitles, long AudioEnd, long RecognizedEnd,
    long BacklogSamples, int ClosedQueueCount, int MemorySamples, long SpilledSamples, int Pins, string Status,
    ImmutableArray<PipelineGap> Gaps, long RecognitionCalls, long TranslationCalls, double LastComputeSeconds);

/// <summary>持续消费音频且串行调度模型，全部权威状态只在同一个事件循环修改。</summary>
public sealed class SubtitlePipeline
{
    private readonly PipelineOptions options;
    private readonly Func<ContinuousAudioStore> createStore;
    private readonly IPipelineModels models;
    private readonly Action<PipelineSnapshot> publish;
    private readonly Action<PipelineOperation>? publishOperation;
    private readonly Action<RecognitionRejection>? publishRejection;
    private readonly SubtitleReducer reducer;
    private readonly List<PipelineGap> gaps = [];
    private ContinuousAudioStore store;
    private BoundedWorkQueue recognitionQueue;
    private BoundedWorkQueue translationQueue;
    private SerialGpuDispatcher gpu = new();
    private PipelineModelSelection selection;
    private Guid? openSegment;
    private TranscriptionHypothesis? hypothesis;
    private int stableObservations, recoveries;
    private long nextRequest, snapshotSequence, recognizedEnd, recognitionCalls, translationCalls;
    private long lastOfferedEnd, releaseBefore, observedEnd;
    private long failedRecognitionEnd = -1;
    private string status = "starting";
    private double lastComputeSeconds;
    private bool used;
    private bool previousRightContext;
    private string? recognitionIssue;
    private readonly float[] rightContextSamples = new float[8000];
    private int rightContextPosition, rightContextCount;
    private double rightContextEnergy;

    /// <summary>组合显式有界缓存及模型边界，不在采集回调中运行模型。</summary>
    /// <param name="identity">本次不可复用会话身份。</param><param name="selection">已确认模型选择。</param>
    /// <param name="options">资源与刷新预算。</param><param name="createStore">为每个采集代次创建独占缓存。</param>
    /// <param name="models">受监管模型环境。</param><param name="publish">快速消费完整快照，不能阻塞音频。</param>
    /// <param name="publishOperation">可选无正文操作证据消费者；普通运行可以省略。</param>
    /// <param name="publishRejection">显式评测可订阅原始拒收文本；默认不记录。</param>
    public SubtitlePipeline(PipelineIdentity identity, PipelineModelSelection selection, PipelineOptions options,
        Func<ContinuousAudioStore> createStore, IPipelineModels models, Action<PipelineSnapshot> publish, Action<PipelineOperation>? publishOperation = null,
        Action<RecognitionRejection>? publishRejection = null)
    {
        identity.Validate(); selection.Validate(); options.Validate();
        this.options = options; this.createStore = createStore; this.models = models; this.publish = publish; this.selection = selection;
        this.publishOperation = publishOperation;
        this.publishRejection = publishRejection;
        reducer = new(identity); store = createStore();
        recognitionQueue = Queue(PipelineRequestKind.Recognition); translationQueue = Queue(PipelineRequestKind.Translation);
    }

    /// <summary>处理流直到正常 EOF、取消或显式资源失败；所有路径等待计算并释放模型与缓存。</summary>
    /// <param name="input">通过有界入口按顺序提供的音频与控制。</param><param name="cancellationToken">会话停止信号。</param>
    /// <returns>停止后的完整状态，失败状态仍保留已有源文。</returns>
    public async Task<PipelineSnapshot> RunAsync(IAsyncEnumerable<PipelineInput> input, CancellationToken cancellationToken)
    {
        if (used) throw new InvalidOperationException("pipeline_session_cannot_be_reused");
        used = true;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var enumerator = input.GetAsyncEnumerator(lifetime.Token);
        Task<bool>? incoming = null;
        Task<ModelCompletion>? computing = null;
        CancellationTokenSource? computationStop = null;
        var eof = false;
        try
        {
            await models.ResetAsync(reducer.Identity, selection, lifetime.Token).ConfigureAwait(false);
            status = "running"; Emit();
            while (true)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (!eof && incoming is null) incoming = enumerator.MoveNextAsync().AsTask();
                if (computing?.IsCompleted == true)
                {
                    var completion = await computing.ConfigureAwait(false);
                    computing = null; computationStop!.Dispose(); computationStop = null;
                    await ApplyCompletionAsync(completion, lifetime.Token).ConfigureAwait(false);
                    OfferRecognition(force: eof);
                }
                if (incoming?.IsCompleted == true)
                {
                    if (await incoming.ConfigureAwait(false))
                    {
                        var item = enumerator.Current;
                        if (item is PipelineAudio audio) Append(audio);
                        else
                        {
                            computationStop?.Cancel();
                            if (computing is not null) { RecordOperation(await computing.ConfigureAwait(false), "control_invalidated"); computing = null; }
                            computationStop?.Dispose(); computationStop = null;
                            await ApplyControlAsync(item, lifetime.Token).ConfigureAwait(false);
                        }
                    }
                    else { eof = true; OfferRecognition(force: true); }
                    incoming = null;
                }
                if (computing is null)
                {
                    var work = TakeNext();
                    if (work is not null)
                    {
                        computationStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        computing = ComputeAsync(work, computationStop.Token);
                    }
                }
                if (eof && computing is null) break;
                if (incoming is null && !eof) continue;
                var waits = new List<Task>(2);
                if (incoming is not null) waits.Add(incoming);
                if (computing is not null) waits.Add(computing);
                if (waits.Count != 0) await Task.WhenAny(waits).WaitAsync(lifetime.Token).ConfigureAwait(false);
            }
            status = recognitionIssue ?? (reducer.Snapshot().Segments.Any(x => x.TranslationState == TranslationState.Failed)
                ? "translation_incomplete" : "completed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { status = "stopped"; }
        catch (Exception error)
        {
            status = error is PipelinePressureException pressure ? pressure.Reason : "pipeline_failed:" + error.GetType().Name;
            if (gaps.Count < 256) gaps.Add(new(reducer.Identity.CaptureEpoch, new(recognizedEnd, Math.Max(observedEnd, Math.Max(recognizedEnd, store.End))), status));
        }
        finally
        {
            lifetime.Cancel(); computationStop?.Cancel();
            try
            {
                if (computing is not null)
                {
                    var stoppedResult = await computing.ConfigureAwait(false);
                    try { RecordOperation(stoppedResult, "session_stopped"); }
                    catch (Exception error) { status = "diagnostic_failed:" + error.GetType().Name; }
                }
                if (incoming is not null) try { await incoming.ConfigureAwait(false); } catch (Exception) { }
            }
            finally
            {
                computationStop?.Dispose(); recognitionQueue.Stop(); translationQueue.Stop(); reducer.Stop();
                try { await models.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { status = "model_cleanup_failed:" + error.GetType().Name; }
                try { store.Dispose(); }
                catch (Exception error) { status = "cache_cleanup_failed:" + error.GetType().Name; }
            }
        }
        return Emit();
    }

    /// <summary>追加完整音频，保护当前未闭合块和闭合待翻译块，超限明确停止。</summary>
    /// <param name="input">连续来源采样。</param>
    private void Append(PipelineAudio input)
    {
        if (input.CaptureEpoch != reducer.Identity.CaptureEpoch || input.Audio.Range.Start != store.End || input.Audio.Range.Length != input.Audio.Samples.Length)
            throw new InvalidDataException("capture_identity_or_continuity");
        if (input.Audio.Samples.Length == 0) return;
        observedEnd = input.Audio.Range.End;
        var quietTail = ObserveRightContext(input.Audio.Samples);
        try
        {
            for (var offset = 0; offset < input.Audio.Samples.Length; offset += 16000)
                store.Append(input.Audio.Range.Start + offset, input.Audio.Samples.AsSpan(offset, Math.Min(16000, input.Audio.Samples.Length - offset)));
        }
        catch (InvalidOperationException) { throw new PipelinePressureException("audio_storage_pressure"); }
        if (store.End - recognizedEnd > options.MaximumBacklogSamples) throw new PipelinePressureException("audio_backlog_capacity");
        if (openSegment is null)
        {
            if (input.Audio.Samples.All(x => x == 0)) { recognizedEnd = store.End; Reclaim(); Emit(); return; }
            openSegment = Guid.NewGuid(); reducer.Add(openSegment.Value, new(input.Audio.Range.Start, input.Audio.Range.Start));
            hypothesis = null; stableObservations = 0; lastOfferedEnd = input.Audio.Range.Start;
        }
        OfferRecognition(quietTail && store.End - lastOfferedEnd >= 8000);
        Reclaim(); Emit();
    }

    /// <summary>为开放块保留包含未处理锚点的最新快照；模型窗口长度不改变逻辑块。</summary>
    /// <param name="force">EOF 时允许不足刷新间隔的最后完整输入。</param>
    private void OfferRecognition(bool force)
    {
        if (recognitionQueue.Pending?.RecoveryWindowSamples > 0 || recognitionQueue.Running?.RecoveryWindowSamples > 0) return;
        if (openSegment is null || store.End <= lastOfferedEnd || !force && store.End - lastOfferedEnd < options.RefreshSamples) return;
        var segment = Segment(openSegment.Value);
        var reachableEnd = Math.Min(store.End, (hypothesis?.Range.End ?? segment.Range.Start) +
            (hypothesis is null ? options.MaximumWindowSamples : Math.Max(options.RefreshSamples, options.MaximumWindowSamples / 2)));
        if (reachableEnd <= failedRecognitionEnd) return;
        var request = new PipelineRequest(Guid.NewGuid(), ++nextRequest, PipelineRequestKind.Recognition, reducer.Identity,
            segment.Key, new(segment.Range.Start, store.End), segment.SourceRevision);
        if (!recognitionQueue.Offer(new(request, request.Range, false, Stopwatch.GetTimestamp()))) throw new PipelinePressureException(recognitionQueue.Rejection!);
        lastOfferedEnd = store.End;
    }

    /// <summary>闭合译文优先，其次最新完整块译文，之后进行下一次识别。</summary>
    /// <returns>尚未登记到归约器的工作。</returns>
    private ScheduledWork? TakeNext()
    {
        var translation = translationQueue.Take();
        if (translation is not null) return translation;
        return recognitionQueue.Take();
    }

    /// <summary>执行一次模型操作，窗口租约在底层计算实际结束后释放。</summary>
    /// <param name="work">固定在途工作。</param><param name="cancellationToken">本次计算取消。</param>
    /// <returns>仅包含结果的消息，不在后台修改权威状态。</returns>
    private async Task<ModelCompletion> ComputeAsync(ScheduledWork work, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var queued = work.OfferedTicks == 0 ? (double?)null : Stopwatch.GetElapsedTime(work.OfferedTicks, started).TotalSeconds;
        AsrRequest? recognitionRequest = null;
        var counted = false;
        try
        {
            if (work.Request.Kind == PipelineRequestKind.Recognition && work.RecoveryWindowSamples == 0)
            {
                var previousEnd = hypothesis?.Range.End ?? work.Request.Range.Start;
                var advance = hypothesis is null ? options.MaximumWindowSamples : Math.Max(options.RefreshSamples, options.MaximumWindowSamples / 2);
                var actualEnd = Math.Min(work.Request.Range.End, previousEnd + advance);
                if (actualEnd < work.Request.Range.End)
                {
                    work = work with { Request = work.Request with { Range = new(work.Request.Range.Start, actualEnd) } };
                    lastOfferedEnd = actualEnd;
                }
            }
            if (!reducer.Register(work.Request)) return new(work, null, null, "stale_before_dispatch", 0, false, queued);
            if (work.Request.Kind == PipelineRequestKind.Translation)
            {
                var input = new TranslationInput(work.Request, Segment(work.Request.Segment.Id).Source);
                translationCalls++; counted = true;
                var result = await gpu.RunAsync(token => models.TranslateAsync(input, token), cancellationToken).ConfigureAwait(false);
                return new(work, null, result, null, Stopwatch.GetElapsedTime(started).TotalSeconds, false, queued, counted);
            }
            var end = work.Request.Range.End;
            var windowSamples = work.RecoveryWindowSamples == 0 ? options.MaximumWindowSamples : work.RecoveryWindowSamples;
            var windowRange = new SampleRange(Math.Max(work.Request.Range.Start, end - windowSamples), end);
            using var window = store.Acquire(windowRange);
            var rightContext = HasQuietRightContext(window.Samples.Span);
            var request = new AsrRequest(work.Request, windowRange, windowRange.Start, selection.AsrLanguage,
                work.RecoveryWindowSamples == 0 ? 512 : 512 * (windowSamples / 480000));
            recognitionRequest = request;
            recognitionCalls++; counted = true;
            var response = await gpu.RunAsync(token => models.RecognizeAsync(request, window, token), cancellationToken).ConfigureAwait(false);
            if (response.Correlation != request) throw new InvalidDataException("recognition_correlation_mismatch");
            return new(work, response, null, null, Stopwatch.GetElapsedTime(started).TotalSeconds, rightContext, queued, counted, recognitionRequest);
        }
        catch (Exception error) { return new(work, null, null, error.GetType().Name, Stopwatch.GetElapsedTime(started).TotalSeconds, false, queued, counted, recognitionRequest); }
    }

    /// <summary>在事件循环提交模型结果；旧身份始终由归约器再检查。</summary>
    /// <param name="result">已退出的计算。</param><param name="cancellationToken">恢复取消。</param><returns>必要恢复完成后的任务。</returns>
    private async Task ApplyCompletionAsync(ModelCompletion result, CancellationToken cancellationToken)
    {
        var ticket = result.Work.Request;
        var queue = ticket.Kind == PipelineRequestKind.Recognition ? recognitionQueue : translationQueue;
        queue.Complete(ticket.Id); lastComputeSeconds = result.Elapsed;
        var disposition = "not_applied";
        try
        {
            if (result.Error is not null)
            {
                disposition = "failed:" + result.Error;
                if (ticket.Kind == PipelineRequestKind.Translation) reducer.FailTranslation(ticket, "translation_failed:" + result.Error);
                if (result.Error != "stale_before_dispatch")
                {
                    Emit();
                    if (++recoveries > 3) throw new PipelinePressureException("model_recovery_budget");
                    await ResetModelsAsync(reducer.Identity with { WorkerEpoch = reducer.Identity.WorkerEpoch + 1 }, selection, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (result.Translation is { } translated)
            {
                if (translated.Input.Ticket != ticket) throw new InvalidDataException("translation_correlation_mismatch");
                disposition = reducer.AcceptTranslation(ticket, translated.Text, translated.Complete) ? "accepted" : "rejected:" + reducer.LastRejection;
            }
            else if (result.Recognition is { } recognized)
            {
                var current = new TranscriptionHypothesis(recognized.Correlation!.WindowRange, recognized.Text!);
                var alignment = hypothesis is null ? new AlignmentDecision(true, current, 0, "initial") : HypothesisAlignment.Compare(hypothesis, current);
                if (recognized.Complete == true && alignment.Accepted && hypothesis is not null &&
                    options.PreserveObservedRepetitions && result.Work.RecoveryWindowSamples > 0 &&
                    HypothesisAlignment.HasReducedObservedRepetition(hypothesis.Text, alignment.Hypothesis!.Text))
                    alignment = new(false, null, 0, "observed_repetition_reduced");
                if (recognized.Complete != true || !alignment.Accepted)
                {
                    recognitionIssue = recognized.Complete != true ? "recognition_incomplete" : alignment.Reason;
                    disposition = "rejected:" + recognitionIssue;
                    publishRejection?.Invoke(new(ticket, hypothesis, current, recognitionIssue));
                    failedRecognitionEnd = ticket.Range.End;
                    recognitionQueue.InvalidatePending();
                    if (recognized.Complete == true) OfferAlignmentRecovery(result.Work, current.Range);
                }
                else
                {
                    recognitionIssue = null; failedRecognitionEnd = -1;
                    var previous = hypothesis;
                    var previousBoundary = previous is null ? BoundaryDecision.Pending : JapaneseBoundaryPolicy.Evaluate(previous.Text, 2, previousRightContext, true);
                    if (previous is not null && previousBoundary.CanClose && current.Range.End > previous.Range.End &&
                        HypothesisAlignment.HasStableContent(previous.Text, alignment.Hypothesis!.Text, true) &&
                        !HypothesisAlignment.HasStableContent(previous.Text, alignment.Hypothesis.Text, false) &&
                        reducer.ConfirmBoundary(ticket, previous.Text, previousBoundary))
                    {
                        disposition = "boundary_confirmation";
                        QueueTranslation(Segment(ticket.Segment.Id), true);
                        CloseOpen(previous.Range.End);
                        Reclaim(); Emit(); return;
                    }
                    hypothesis = alignment.Hypothesis!;
                    previousRightContext = result.RightContext;
                    stableObservations = previous is not null && (previous.Text.Length == 0 && hypothesis.Text.Length == 0 ||
                        HypothesisAlignment.HasStableContent(previous.Text, hypothesis.Text, false)) &&
                        hypothesis.Range.End > previous.Range.End ? stableObservations + 1 : 1;
                    var boundary = JapaneseBoundaryPolicy.Evaluate(hypothesis.Text, stableObservations, result.RightContext, true);
                    if (reducer.AcceptSource(ticket, hypothesis.Text, true, boundary))
                    {
                        disposition = "accepted";
                        recognizedEnd = Math.Max(recognizedEnd, ticket.Range.End);
                        var segment = Segment(ticket.Segment.Id);
                        if (segment.Source.Length == 0 && stableObservations >= 2 && result.RightContext &&
                            recognized.Reason is "exact-digital-silence" or "model-no-speech")
                        {
                            reducer.RetireEmpty(segment.Key.Id);
                            CloseOpen(ticket.Range.End);
                        }
                        else
                        {
                            QueueTranslation(segment, boundary.CanClose);
                            if (boundary.CanClose) CloseOpen(ticket.Range.End);
                        }
                    }
                    else disposition = "rejected:" + reducer.LastRejection;
                }
            }
            Reclaim(); Emit();
        }
        finally { RecordOperation(result, disposition); }
    }

    /// <summary>仅在对齐失败后重试同一逻辑范围，扩大旧上下文而不跳过任何待处理音频。</summary>
    /// <param name="failed">实际失败的工作。</param><param name="window">失败调用的真实窗口。</param>
    private void OfferAlignmentRecovery(ScheduledWork failed, SampleRange window)
    {
        var next = failed.RecoveryWindowSamples == 0 ? 960000 : 1920000;
        if (options.AlignmentRecoverySamples < next || failed.RecoveryWindowSamples >= next ||
            Math.Max(failed.Request.Range.Start, window.End - next) >= window.Start) return;
        var request = failed.Request with { Id = Guid.NewGuid(), Sequence = ++nextRequest };
        if (!recognitionQueue.Offer(new(request, request.Range, false, Stopwatch.GetTimestamp(), next)))
            throw new PipelinePressureException(recognitionQueue.Rejection!);
        lastOfferedEnd = request.Range.End;
    }

    /// <summary>登记当前完整块的翻译，已具备当前译文的定稿不重复调用。</summary>
    /// <param name="segment">当前有效块。</param><param name="closed">边界是否已确认。</param>
    private void QueueTranslation(SubtitleSegment segment, bool closed)
    {
        if (string.IsNullOrWhiteSpace(segment.Source) || segment.Phase == SubtitlePhase.Final) return;
        var request = new PipelineRequest(Guid.NewGuid(), ++nextRequest, PipelineRequestKind.Translation, reducer.Identity,
            segment.Key, segment.Range, segment.SourceRevision);
        if (!translationQueue.Offer(new(request, request.Range, closed, Stopwatch.GetTimestamp()))) throw new PipelinePressureException(translationQueue.Rejection!);
    }

    /// <summary>在已证实的音频边界建立新锚点，缓存在边界之后的语音重新识别且不丢失。</summary>
    /// <param name="end">旧块实际完整范围终点。</param>
    private void CloseOpen(long end)
    {
        recognitionQueue.InvalidatePending(); openSegment = null; hypothesis = null; stableObservations = 0; previousRightContext = false; failedRecognitionEnd = -1;
        lastOfferedEnd = end;
        if (store.End > end)
        {
            openSegment = Guid.NewGuid(); reducer.Add(openSegment.Value, new(end, end));
        }
    }

    /// <summary>处理已等待计算退出的采集或模型控制，旧许可全部撤销。</summary>
    /// <param name="input">控制事件。</param><param name="cancellationToken">恢复取消。</param><returns>模型重新就绪任务。</returns>
    private async Task ApplyControlAsync(PipelineInput input, CancellationToken cancellationToken)
    {
        var identity = reducer.Identity;
        if (input is PipelineCaptureReset reset)
        {
            if (reset.CaptureEpoch <= identity.CaptureEpoch || gaps.Count >= 256) throw new InvalidDataException("capture_reset_identity_or_capacity");
            gaps.Add(new(identity.CaptureEpoch, new(recognizedEnd, Math.Max(recognizedEnd, store.End)), reset.Reason));
            identity = identity with { CaptureEpoch = reset.CaptureEpoch, WorkerEpoch = identity.WorkerEpoch + 1 };
            store.Dispose(); store = createStore(); openSegment = null; hypothesis = null;
            recognizedEnd = lastOfferedEnd = releaseBefore = observedEnd = 0; stableObservations = 0;
            recognitionIssue = null; previousRightContext = false;
            Array.Clear(rightContextSamples); rightContextPosition = rightContextCount = 0; rightContextEnergy = 0;
        }
        else if (input is PipelineConfiguration configuration)
        {
            configuration.Selection.Validate(); selection = configuration.Selection;
            ReopenUntranslatedRanges();
            identity = identity with { ConfigEpoch = identity.ConfigEpoch + 1, WorkerEpoch = identity.WorkerEpoch + 1 };
            stableObservations = 0; previousRightContext = false;
        }
        else throw new InvalidDataException("unknown_pipeline_control");
        await ResetModelsAsync(identity, selection, cancellationToken).ConfigureAwait(false);
        Emit();
    }

    /// <summary>将边界即将失效的闭合待翻译范围与开放尾部原子重建为可重新识别的完整范围。</summary>
    /// <exception cref="InvalidOperationException">待恢复范围不连续或已不在缓存中，不能假装仍可重识别。</exception>
    private void ReopenUntranslatedRanges()
    {
        var pending = reducer.Snapshot().Segments.Where(x => x.Phase != SubtitlePhase.Final).OrderBy(x => x.Range.Start).ToArray();
        if (!pending.Any(x => x.Key.Id != openSegment)) return;
        // 新音频可能只有尚未识别的空锚点；先撤销该身份，后续完整窗口仍从旧范围起点覆盖到 store.End。
        foreach (var empty in pending.Where(x => x.Range.Length == 0)) reducer.RetireEmpty(empty.Key.Id);
        pending = pending.Where(x => x.Range.Length != 0).ToArray();
        if (pending.Length == 0) { openSegment = null; hypothesis = null; return; }
        var range = new SampleRange(pending[0].Range.Start, pending[^1].Range.End);
        if (!store.Contains(range)) throw new InvalidOperationException("configuration_source_audio_unavailable");
        var source = string.Concat(pending.Select(x => x.Source));
        var id = Guid.NewGuid();
        reducer.Replace(pending.Select(x => x.Key.Id).ToArray(), [new(id, range, source)]);
        openSegment = id;
        hypothesis = new(range, source);
        stableObservations = 0; previousRightContext = false; failedRecognitionEnd = -1;
    }

    /// <summary>确认旧模型实际退出后重建配额，清空旧调用并重排当前有效开放范围。</summary>
    /// <param name="identity">新的失效身份。</param><param name="configuration">当前模型选择。</param><param name="cancellationToken">取消。</param>
    /// <returns>模型就绪任务。</returns>
    private async Task ResetModelsAsync(PipelineIdentity identity, PipelineModelSelection configuration, CancellationToken cancellationToken)
    {
        reducer.AdvanceIdentity(identity);
        stableObservations = 0; failedRecognitionEnd = -1;
        recognitionQueue = Queue(PipelineRequestKind.Recognition); translationQueue = Queue(PipelineRequestKind.Translation);
        status = "recovering_models"; Emit();
        await models.ResetAsync(identity, configuration, cancellationToken).ConfigureAwait(false);
        status = "running";
        gpu = new();
        if (openSegment is not null) { lastOfferedEnd = Segment(openSegment.Value).Range.Start; OfferRecognition(true); }
        foreach (var segment in reducer.Snapshot().Segments.Where(x => x.Key.Id != openSegment && x.Phase != SubtitlePhase.Final && !string.IsNullOrWhiteSpace(x.Source)))
        {
            var request = new PipelineRequest(Guid.NewGuid(), ++nextRequest, PipelineRequestKind.Translation, identity, segment.Key, segment.Range, segment.SourceRevision);
            if (!translationQueue.Offer(new(request, request.Range, true, Stopwatch.GetTimestamp()))) throw new PipelinePressureException(translationQueue.Rejection!);
        }
    }

    /// <summary>仅释放已完成且超过修订尾部的音频，闭合待翻译与开放块仍保留。</summary>
    private void Reclaim()
    {
        var protectedStart = reducer.Snapshot().Segments.Where(x => x.Phase != SubtitlePhase.Final).Select(x => x.Range.Start).DefaultIfEmpty(store.End).Min();
        var before = Math.Min(protectedStart, Math.Max(0, store.End - options.MaximumWindowSamples));
        if (before >= releaseBefore) { store.ReleaseBefore(before); releaseBefore = before; }
    }

    /// <summary>右侧低能量仅是边界佐证，不删采样或独自触发定稿。</summary>
    /// <param name="samples">真实完整窗口。</param><returns>最后 0.5 秒均方根低于 -54dB 时为 true。</returns>
    private static bool HasQuietRightContext(ReadOnlySpan<float> samples)
    {
        if (samples.Length < 8000) return false;
        double sum = 0;
        foreach (var sample in samples[^8000..]) sum += sample * sample;
        return sum / 8000 < 0.002 * 0.002;
    }

    /// <summary>持续观察最后半秒的真实音频，安静尾部可提前请求稳定性复核但不能独自定稿。</summary>
    /// <param name="samples">本次新输入，不删除、不修改。</param><returns>半秒低能量证据成立时为 true。</returns>
    private bool ObserveRightContext(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            var old = rightContextSamples[rightContextPosition];
            rightContextEnergy += (double)sample * sample - (double)old * old;
            rightContextSamples[rightContextPosition] = sample;
            rightContextPosition = (rightContextPosition + 1) % rightContextSamples.Length;
            rightContextCount = Math.Min(rightContextCount + 1, rightContextSamples.Length);
        }
        return rightContextCount == rightContextSamples.Length && rightContextEnergy / rightContextSamples.Length < 0.002 * 0.002;
    }

    /// <summary>读取当前身份的不可变块。</summary><param name="id">有效块身份。</param><returns>当前块。</returns>
    private SubtitleSegment Segment(Guid id) => reducer.Snapshot().Segments.Single(x => x.Key.Id == id);
    /// <summary>使用统一预算建立一种模型的有限调度队列。</summary><param name="kind">操作种类。</param><returns>空队列。</returns>
    private BoundedWorkQueue Queue(PipelineRequestKind kind) => new(kind, options.ClosedQueueCapacity, options.MaximumBacklogSamples);
    /// <summary>发布完整状态，旧快照不会再被修改。</summary><returns>本次提交快照。</returns>
    private PipelineSnapshot Emit()
    {
        var snapshot = new PipelineSnapshot(++snapshotSequence, reducer.Snapshot(), store.End, recognizedEnd,
            Math.Max(0, store.End - recognizedEnd), translationQueue.ClosedCount, store.MemorySamples, store.SpilledSamples, store.PinCount,
            status == "running" ? recognitionIssue ?? status : status, gaps.ToImmutableArray(), recognitionCalls, translationCalls, lastComputeSeconds);
        publish(snapshot); return snapshot;
    }

    /// <summary>提交无正文逐次模型证据，单独区分完整生成和当前版本接收。</summary>
    /// <param name="result">实际完成操作。</param><param name="disposition">事件循环接收或拒绝原因。</param>
    private void RecordOperation(ModelCompletion result, string disposition)
    {
        var translated = result.Translation; var recognized = result.Recognition;
        publishOperation?.Invoke(new(DateTimeOffset.UtcNow, result.Work.Request, result.RecognitionRequest?.WindowRange,
            result.QueueSeconds, result.Elapsed, recognized?.ElapsedSeconds, recognized?.Complete ?? translated?.Complete ?? false,
            result.Error ?? recognized?.Reason ?? translated?.Reason, disposition, translated?.LoadNanoseconds,
            translated?.EvaluationNanoseconds, translated?.OutputTokens, translated?.Digest, translated?.PromptEvaluationNanoseconds, translated?.InputTokens,
            result.CountedAttempt, result.RecognitionRequest?.MaxNewTokens));
    }
    /// <summary>后端任务只携带结果，不直接写字幕状态。</summary>
    /// <param name="Work">已固定工作。</param><param name="Recognition">识别结果。</param><param name="Translation">翻译结果。</param>
    /// <param name="Error">错误码。</param><param name="Elapsed">实际操作耗时。</param><param name="RightContext">实际音频右侧证据。</param>
    /// <param name="QueueSeconds">当前快照实际待执行秒数。</param>
    /// <param name="CountedAttempt">是否已计入模型尝试数。</param>
    /// <param name="RecognitionRequest">实际构造的识别请求，失败时仍保留其窗口及预算。</param>
    private sealed record ModelCompletion(ScheduledWork Work, IpcHeader? Recognition, TranslationResult? Translation, string? Error, double Elapsed, bool RightContext,
        double? QueueSeconds, bool CountedAttempt = false, AsrRequest? RecognitionRequest = null);
}

/// <summary>显式容量或恢复预算失败，不静默跳过未处理音频。</summary>
public sealed class PipelinePressureException : Exception
{
    /// <summary>记录不含正文的压力原因。</summary><param name="reason">结构化原因。</param>
    public PipelinePressureException(string reason) : base(reason) => Reason = reason;
    /// <summary>获取明确压力或恢复预算原因。</summary>
    public string Reason { get; }
}
