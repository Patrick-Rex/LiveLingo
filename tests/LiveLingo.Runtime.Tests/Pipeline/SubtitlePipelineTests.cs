using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LiveLingo.Core.Audio;
using LiveLingo.Core.Subtitles;
using LiveLingo.Runtime.Asr;
using LiveLingo.Runtime.Pipeline;
using LiveLingo.Runtime.Storage;
using LiveLingo.Runtime.Translation;

namespace LiveLingo.Runtime.Tests.Pipeline;

/// <summary>运行完整事件循环与受控模型边界，验证否定修订、慢速合并和显式压力。</summary>
public sealed class SubtitlePipelineTests
{
    /// <summary>旧草稿译文被完整空原文修订后，可按既有稳定无语音规则撤销并继续下一段。</summary>
    /// <returns>无语音撤销及后续音频连续处理验证任务。</returns>
    [Fact]
    public async Task StableNoSpeechRetiresPreviouslyTranslatedDraftAndContinues()
    {
        var models = new ControlledModels { NoSpeechForEmpty = true };
        var input = Channel.CreateBounded<PipelineInput>(8);
        var emptySeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retiredSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = Create(models, snapshot =>
        {
            if (snapshot.RecognizedEnd == 64000) emptySeen.TrySetResult();
            if (snapshot.Subtitles.RetiredIds.Count == 1) retiredSeen.TrySetResult();
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 32000), TestContext.Current.CancellationToken);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("背面");
        await models.Translated.Reader.ReadAsync(timeout.Token);
        await input.Writer.WriteAsync(Audio(32000, 64000), TestContext.Current.CancellationToken);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("");
        await emptySeen.Task.WaitAsync(timeout.Token);
        await input.Writer.WriteAsync(Audio(64000, 96000), TestContext.Current.CancellationToken);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("");
        await Task.WhenAny(retiredSeen.Task, run).WaitAsync(timeout.Token);
        if (run.IsCompleted) Assert.Equal("completed", (await run).Status);
        await retiredSeen.Task.WaitAsync(timeout.Token);
        await input.Writer.WriteAsync(Audio(96000, 128000), TestContext.Current.CancellationToken);
        var next = await models.NextAsync(timeout.Token);
        Assert.Equal(new SampleRange(96000, 128000), next.Request.WindowRange);
        next.Reply.SetResult("次の内容です");
        input.Writer.Complete();
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal("completed", final.Status);
        Assert.Equal(0, final.BacklogSamples);
        Assert.Equal("次の内容です", Assert.Single(final.Subtitles.Segments).Source);
        Assert.Single(final.Subtitles.RetiredIds);
        Assert.Equal(4, final.RecognitionCalls);
        Assert.True(models.Disposed);
    }

    /// <summary>句尾否定替换整块，只有稳定文本和右侧音频证据后才能定稿。</summary>
    /// <returns>单写入管道正常结束任务。</returns>
    [Fact]
    public async Task NegationReplacesCompleteBlockBeforeFinal()
    {
        var models = new ControlledModels();
        var snapshots = new ConcurrentQueue<PipelineSnapshot>();
        var operations = new ConcurrentQueue<PipelineOperation>();
        var input = Channel.CreateBounded<PipelineInput>(8);
        var pipeline = Create(models, snapshots.Enqueue, publishOperation: operations.Enqueue);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 32000), TestContext.Current.CancellationToken);
        var first = await models.NextAsync(timeout.Token);
        first.Reply.SetResult("今日は行きます。");
        await models.Translated.Reader.ReadAsync(timeout.Token);
        await input.Writer.WriteAsync(Audio(32000, 64000), TestContext.Current.CancellationToken);
        var second = await models.NextAsync(timeout.Token);
        second.Reply.SetResult("今日は行きません。");
        await models.Translated.Reader.ReadAsync(timeout.Token);
        await input.Writer.WriteAsync(Audio(64000, 96000), TestContext.Current.CancellationToken);
        var third = await models.NextAsync(timeout.Token);
        third.Reply.SetResult("今日は行きません。");
        input.Writer.Complete();
        var final = await run.WaitAsync(timeout.Token);
        var segment = Assert.Single(final.Subtitles.Segments);
        Assert.Equal("今日は行きません。", segment.Source);
        Assert.Equal("今日は行きません。", segment.Translation);
        Assert.Equal(SubtitlePhase.Final, segment.Phase);
        Assert.Equal(segment.SourceRevision, segment.TranslatedSourceRevision);
        Assert.True(final.Subtitles.Stopped);
        Assert.Equal(0, final.Pins);
        Assert.True(models.Disposed);
        Assert.Equal(1, models.MaximumConcurrent);
        Assert.Contains(snapshots, x => x.Subtitles.Segments.Any(s => s.Source == "今日は行きます。" && s.Phase != SubtitlePhase.Final));
        Assert.Equal(final.RecognitionCalls + final.TranslationCalls, operations.Count);
        Assert.All(operations, x => { Assert.Equal("accepted", x.Disposition); Assert.True(x.GenerationComplete); Assert.True(x.QueueSeconds >= 0); });
        Assert.All(operations.Where(x => x.Request.Kind == PipelineRequestKind.Recognition), x => Assert.Equal(x.Request.Range, x.Window));
    }

    /// <summary>开放块逐句发布已译前缀，积压识别可修订未译尾句，同文的两次发言各保留一条。</summary>
    /// <returns>真实调度、增量快照及逐次短句输入的验证任务。</returns>
    [Fact]
    public async Task GrowingSourcePublishesPhrasePairsAndReusesTheTranslatedPrefix()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshots = new ConcurrentQueue<PipelineSnapshot>();
        var operations = new ConcurrentQueue<PipelineOperation>();
        var models = new ControlledModels
        {
            TranslationDelayFromCall = 2, TranslationDelay = release.Task,
            TranslateText = source => source == "はい。" ? "是的！对。" : "新的译文"
        };
        var input = Channel.CreateBounded<PipelineInput>(8);
        var pipeline = Create(models, state =>
        {
            snapshots.Enqueue(state);
            if (state.AudioEnd == 64000) buffered.TrySetResult();
        }, publishOperation: operations.Enqueue);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 32000), timeout.Token);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("はい。はい。訂正前。");
        Assert.Equal("はい。", await models.Translated.Reader.ReadAsync(timeout.Token));
        Assert.Equal("はい。", await models.Translated.Reader.ReadAsync(timeout.Token));
        Assert.Contains(snapshots, state => state.Subtitles.Segments.Any(segment =>
            segment.TranslationState == TranslationState.Pending && segment.PhraseTranslations.Length == 1 &&
            segment.PhraseTranslations[0].Translation == "是的！对。"));
        await input.Writer.WriteAsync(Audio(32000, 64000), timeout.Token);
        await buffered.Task.WaitAsync(timeout.Token);
        release.SetResult();
        (await models.NextAsync(timeout.Token)).Reply.SetResult("はい。はい。訂正後。");
        input.Writer.Complete();
        var final = await run.WaitAsync(timeout.Token);

        Assert.Equal("completed", final.Status);
        Assert.Equal("訂正後。", await models.Translated.Reader.ReadAsync(timeout.Token));
        Assert.False(models.Translated.Reader.TryRead(out _));
        var segment = Assert.Single(final.Subtitles.Segments);
        Assert.Equal(["はい。", "はい。", "訂正後。"], segment.PhraseTranslations.Select(phrase => phrase.Source));
        Assert.Equal(["是的！对。", "是的！对。", "新的译文"], segment.PhraseTranslations.Select(phrase => phrase.Translation));
        Assert.Equal(segment.SourceRevision, segment.TranslatedSourceRevision);
        Assert.Equal(3, final.TranslationCalls);
        Assert.Equal(3, operations.Count(operation => operation.Request.Kind == PipelineRequestKind.Translation && operation.CountedAttempt));
        Assert.Equal(1, models.MaximumConcurrent);
    }

    /// <summary>单条翻译截断时保留已经完成的短句，块级状态保持失败，后续正文等待新版本。</summary>
    /// <returns>验证局部失败快照与实际模型调用数的任务。</returns>
    [Fact]
    public async Task IncompletePhraseKeepsEarlierPairsWithoutCompletingTheBlock()
    {
        var models = new ControlledModels { IncompleteTranslationCall = 2 };
        var input = Channel.CreateBounded<PipelineInput>(8);
        var pipeline = Create(models, _ => { });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 32000), timeout.Token);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("一つ。二つ。三つ。");
        input.Writer.Complete();
        var final = await run.WaitAsync(timeout.Token);

        var segment = Assert.Single(final.Subtitles.Segments);
        Assert.Equal("translation_incomplete", final.Status);
        Assert.Equal(TranslationState.Failed, segment.TranslationState);
        Assert.Equal("一つ。", Assert.Single(segment.PhraseTranslations).Source);
        Assert.Null(segment.TranslatedSourceRevision);
        Assert.Equal(2, final.TranslationCalls);
    }

    /// <summary>慢计算期间只留下覆盖全部音频的最新开放快照，不删除真实重复。</summary>
    /// <returns>处理有意积压输入的任务。</returns>
    [Fact]
    public async Task SlowInferenceCoalescesWithoutSkippingAudio()
    {
        var models = new ControlledModels();
        var input = Channel.CreateBounded<PipelineInput>(8);
        var buffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = Create(models, state => { if (state.AudioEnd == 128000) buffered.TrySetResult(); });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 32000), TestContext.Current.CancellationToken);
        var first = await models.NextAsync(timeout.Token);
        await input.Writer.WriteAsync(Audio(32000, 64000), TestContext.Current.CancellationToken);
        await input.Writer.WriteAsync(Audio(64000, 96000), TestContext.Current.CancellationToken);
        await input.Writer.WriteAsync(Audio(96000, 128000), TestContext.Current.CancellationToken);
        input.Writer.Complete();
        await buffered.Task.WaitAsync(timeout.Token);
        first.Reply.SetResult("はい");
        var latest = await models.NextAsync(timeout.Token);
        Assert.Equal(new SampleRange(0, 128000), latest.Request.WindowRange);
        latest.Reply.SetResult("はい、はい。今日は行きません。");
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal(2, final.RecognitionCalls);
        Assert.Equal(0, final.BacklogSamples);
        Assert.Equal("はい、はい。今日は行きません。", Assert.Single(final.Subtitles.Segments).Source);
    }

    /// <summary>翻译阻塞期间继续保留全部新音频，实际计算退出前不能让 ASR 抢占 GPU 配额。</summary>
    /// <returns>翻译压力下的连续覆盖、串行和追赶验证任务。</returns>
    [Fact]
    public async Task SlowTranslationKeepsAudioAndSerialGpuOwnership()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var models = new ControlledModels { TranslationDelay = release.Task };
        var input = Channel.CreateBounded<PipelineInput>(8);
        var buffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = Create(models, state => { if (state.AudioEnd == 128000) buffered.TrySetResult(); });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 32000), TestContext.Current.CancellationToken);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("今日の作業は");
        await models.Translated.Reader.ReadAsync(timeout.Token);
        await input.Writer.WriteAsync(Audio(32000, 64000), TestContext.Current.CancellationToken);
        await input.Writer.WriteAsync(Audio(64000, 96000), TestContext.Current.CancellationToken);
        await input.Writer.WriteAsync(Audio(96000, 128000), TestContext.Current.CancellationToken);
        input.Writer.Complete();
        await buffered.Task.WaitAsync(timeout.Token);
        Assert.Equal(1, models.Concurrent);
        release.SetResult();
        var next = await models.NextAsync(timeout.Token);
        Assert.Equal(new SampleRange(0, 128000), next.Request.WindowRange);
        next.Reply.SetResult("今日の作業はまだ終わりません。");
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal(0, final.BacklogSamples);
        Assert.Equal(1, models.MaximumConcurrent);
        Assert.Equal("今日の作業はまだ終わりません。", Assert.Single(final.Subtitles.Segments).Source);
        Assert.True(models.Disposed);
    }

    /// <summary>直播积压超限后保留原采集段证据，在同一输入流继续，并可再接受真实设备重连。</summary>
    /// <returns>验证未识别区间、样本重定位、迟到结果隔离与模型资源释放。</returns>
    [Fact]
    public async Task LiveBacklogResumesFromCurrentAudioAndPreservesPhysicalResets()
    {
        var models = new ControlledModels();
        var snapshots = new ConcurrentQueue<PipelineSnapshot>();
        var input = Channel.CreateBounded<PipelineInput>(8);
        var options = new PipelineOptions { ResumeLiveAfterBacklog = true, AlignmentRecoverySamples = 0,
            MaximumWindowSamples = 32000, MaximumBacklogSamples = 64000 };
        var pipeline = Create(models, snapshots.Enqueue, options);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(token), token);
        await input.Writer.WriteAsync(Audio(0, 32000), token);
        (await models.NextAsync(token)).Reply.SetResult("最初の発言");
        Assert.Equal("最初の発言", await models.Translated.Reader.ReadAsync(token));
        await input.Writer.WriteAsync(Audio(32000, 64000), token);
        var delayed = await models.NextAsync(token);
        await input.Writer.WriteAsync(Audio(64000, 96000), token);
        await input.Writer.WriteAsync(Audio(96000, 128000), token);
        var resumed = await models.NextAsync(token);
        Assert.Equal(2, resumed.Request.Ticket.Identity.CaptureEpoch);
        Assert.Equal(new SampleRange(0, 32000), resumed.Request.WindowRange);
        delayed.Reply.TrySetResult("遅れた発言");
        resumed.Reply.SetResult("再開した発言");
        Assert.Equal("再開した発言", await models.Translated.Reader.ReadAsync(token));
        await input.Writer.WriteAsync(new PipelineCaptureReset(2, "device_changed"), token);
        await input.Writer.WriteAsync(Audio(0, 32000) with { CaptureEpoch = 2 }, token);
        var afterDeviceReset = await models.NextAsync(token);
        Assert.Equal(3, afterDeviceReset.Request.Ticket.Identity.CaptureEpoch);
        afterDeviceReset.Reply.SetResult("新しい機器");
        input.Writer.Complete();

        var final = await run.WaitAsync(token);
        Assert.Equal("completed", final.Status);
        Assert.Equal(new PipelineGap(1, new(32000, 96000), "audio_backlog_capacity"), final.Gaps[0]);
        Assert.Equal("device_changed", final.Gaps[1].Reason);
        Assert.Contains(snapshots, snapshot => snapshot.Subtitles.Identity.CaptureEpoch == 1 &&
            snapshot.Subtitles.Segments.Any(segment => segment.Source == "最初の発言"));
        Assert.DoesNotContain(snapshots, snapshot => snapshot.Subtitles.Segments.Any(segment => segment.Source == "遅れた発言"));
        Assert.Equal("新しい機器", Assert.Single(final.Subtitles.Segments).Source);
        Assert.Equal(0, final.Pins);
        Assert.Equal(1, models.MaximumConcurrent);
    }

    /// <summary>缓存预算耗尽时明确给出包括被拒绝输入的范围，并等待取消中的模型退出。</summary>
    /// <returns>过载停止及资源清理完成任务。</returns>
    [Fact]
    public async Task PressureIsVisibleAndDoesNotOverwriteInFlightAudio()
    {
        var models = new ControlledModels();
        var input = Channel.CreateBounded<PipelineInput>(32);
        var options = new PipelineOptions { AlignmentRecoverySamples = 0, MemorySamples = 480000, SpillSamples = 0, MaximumBacklogSamples = 480000 };
        var pipeline = Create(models, _ => { }, options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 32000), TestContext.Current.CancellationToken);
        await models.NextAsync(timeout.Token);
        for (var start = 32000; start < 512000; start += 32000) await input.Writer.WriteAsync(Audio(start, start + 32000), TestContext.Current.CancellationToken);
        input.Writer.Complete();
        var result = await run.WaitAsync(timeout.Token);
        Assert.Equal("audio_storage_pressure", result.Status);
        Assert.Equal(512000, Assert.Single(result.Gaps).Range.End);
        Assert.True(models.Disposed);
        Assert.Equal(0, models.Concurrent);
        Assert.Equal(0, result.Pins);
    }

    /// <summary>配置控制先等待旧计算退出，旧结果不得更新新配置字幕。</summary>
    /// <returns>配置代次变化和恢复识别任务。</returns>
    [Fact]
    public async Task ConfigurationInvalidatesRunningResult()
    {
        var models = new ControlledModels();
        var input = Channel.CreateBounded<PipelineInput>(8);
        var pipeline = Create(models, _ => { });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 32000), TestContext.Current.CancellationToken);
        await models.NextAsync(timeout.Token);
        await input.Writer.WriteAsync(new PipelineConfiguration(new("translategemma:4b")), TestContext.Current.CancellationToken);
        var current = await models.NextAsync(timeout.Token);
        Assert.Equal(2, current.Request.Ticket.Identity.ConfigEpoch);
        Assert.Equal(2, current.Request.Ticket.Identity.WorkerEpoch);
        current.Reply.SetResult("新しい結果です。");
        input.Writer.Complete();
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal("新しい結果です。", Assert.Single(final.Subtitles.Segments).Source);
        Assert.Equal(1, models.MaximumConcurrent);
    }

    /// <summary>切换模型后，相同原文的已译前缀也按新配置重译，在途的旧尾句不能进入新字幕。</summary>
    /// <returns>两次后端配置下真实短句调用及最终版本的验证任务。</returns>
    [Fact]
    public async Task ConfigurationChangeRetranslatesAlreadyPublishedPhrases()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var models = new ControlledModels { TranslationDelay = release.Task, TranslationDelayFromCall = 2 };
        var input = Channel.CreateBounded<PipelineInput>(8);
        var operations = new ConcurrentQueue<PipelineOperation>();
        var pipeline = Create(models, _ => { }, publishOperation: operations.Enqueue);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 32000), timeout.Token);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("一つ。二つ。");
        Assert.Equal("一つ。", await models.Translated.Reader.ReadAsync(timeout.Token));
        Assert.Equal("二つ。", await models.Translated.Reader.ReadAsync(timeout.Token));
        await input.Writer.WriteAsync(new PipelineConfiguration(new("translategemma:4b")), timeout.Token);
        var next = await models.NextAsync(timeout.Token);
        Assert.Equal(2, next.Request.Ticket.Identity.ConfigEpoch);
        next.Reply.SetResult("一つ。二つ。");
        release.SetResult();
        input.Writer.Complete();
        var final = await run.WaitAsync(timeout.Token);

        Assert.Equal("一つ。", await models.Translated.Reader.ReadAsync(timeout.Token));
        Assert.Equal("二つ。", await models.Translated.Reader.ReadAsync(timeout.Token));
        Assert.Equal(4, final.TranslationCalls);
        Assert.Equal(2, Assert.Single(final.Subtitles.Segments).PhraseTranslations.Length);
        Assert.Contains(operations, operation => operation.Request.Kind == PipelineRequestKind.Translation &&
            operation.Request.Identity.ConfigEpoch == 1 && operation.Disposition == "control_invalidated");
    }

    /// <summary>配置变化撤销已闭合待翻译块的边界后，该范围必须重新获得识别机会而非永久占住缓存。</summary>
    /// <param name="withOpenTail">配置前是否已经收到新的未识别音频。</param>
    /// <returns>闭合翻译取消、旧音频重识别和旧块撤销验证任务。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationReopensClosedUntranslatedAudio(bool withOpenTail)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var configured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var models = new ControlledModels { IncompleteTranslationCall = 1, TranslationDelay = release.Task, TranslationDelayFromCall = 2 };
        var input = Channel.CreateBounded<PipelineInput>(8);
        var pipeline = Create(models, snapshot => { if (snapshot.Subtitles.Identity.ConfigEpoch == 2) configured.TrySetResult(); });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 32000), TestContext.Current.CancellationToken);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("今日は行きません。");
        await models.Translated.Reader.ReadAsync(timeout.Token);
        await input.Writer.WriteAsync(Audio(32000, 64000), TestContext.Current.CancellationToken);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("今日は行きません。");
        await models.Translated.Reader.ReadAsync(timeout.Token);
        if (withOpenTail) await input.Writer.WriteAsync(Audio(64000, 96000), TestContext.Current.CancellationToken);
        await input.Writer.WriteAsync(new PipelineConfiguration(new("translategemma:4b")), TestContext.Current.CancellationToken);
        await configured.Task.WaitAsync(timeout.Token);
        release.SetResult();
        if (!withOpenTail) await input.Writer.WriteAsync(Audio(64000, 96000), TestContext.Current.CancellationToken);
        input.Writer.Complete();
        var next = await models.NextAsync(timeout.Token);
        var restartedAt = next.Request.WindowRange.Start;
        next.Reply.TrySetResult("今日は行きません。");
        if (next.Request.WindowRange.End < 96000)
            (await models.NextAsync(timeout.Token)).Reply.SetResult("今日は行きません。");
        var result = await run.WaitAsync(timeout.Token);
        Assert.Equal(0, restartedAt);
        Assert.Equal("completed", result.Status);
        Assert.Equal(0, result.BacklogSamples);
        Assert.Equal(withOpenTail ? 2 : 1, result.Subtitles.RetiredIds.Count);
        Assert.True(models.Disposed);
    }

    /// <summary>增长输出只用来确认旧边界，新增语音必须从缓存独立识别且不划入旧块。</summary>
    /// <returns>两个连续语义块及范围守恒检查任务。</returns>
    [Fact]
    public async Task StablePrefixClosesOnlyPreviouslyObservedRange()
    {
        var models = new ControlledModels();
        var input = Channel.CreateBounded<PipelineInput>(8);
        var pipeline = Create(models, _ => { });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 32000), TestContext.Current.CancellationToken);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("今日は行きません。");
        await models.Translated.Reader.ReadAsync(timeout.Token);
        await input.Writer.WriteAsync(Audio(32000, 64000), TestContext.Current.CancellationToken);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("今日は行きません。明日は行きます。");
        var following = await models.NextAsync(timeout.Token);
        Assert.Equal(new SampleRange(32000, 64000), following.Request.WindowRange);
        following.Reply.SetResult("明日は行きます。");
        input.Writer.Complete();
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal(2, final.Subtitles.Segments.Length);
        Assert.Equal(new SampleRange(0, 32000), final.Subtitles.Segments[0].Range);
        Assert.Equal(SubtitlePhase.Final, final.Subtitles.Segments[0].Phase);
        Assert.Equal("今日は行きません。", final.Subtitles.Segments[0].Source);
        Assert.Equal("明日は行きます。", final.Subtitles.Segments[1].Source);
        Assert.Equal(0, final.BacklogSamples);
    }

    /// <summary>翻译占用模型时保留已观察的安静尾部，下一轮增长识别仍能确认真实采样边界。</summary>
    /// <returns>安静候选未被后续连续发声覆盖且新块重识别的验证任务。</returns>
    [Fact]
    public async Task QuietWindowSurvivesCoalescingWithLaterSpeech()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var models = new ControlledModels { TranslationDelay = release.Task };
        var input = Channel.CreateBounded<PipelineInput>(8);
        var pipeline = Create(models, state => { if (state.AudioEnd == 96000) buffered.TrySetResult(); });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 32000), timeout.Token);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("今から");
        await models.Translated.Reader.ReadAsync(timeout.Token);
        await input.Writer.WriteAsync(Audio(32000, 64000), timeout.Token);
        await input.Writer.WriteAsync(Audio(64000, 96000, quietTail: false), timeout.Token);
        await buffered.Task.WaitAsync(timeout.Token);
        release.SetResult();
        var pause = await models.NextAsync(timeout.Token);
        Assert.Equal(new SampleRange(0, 64000), pause.Request.WindowRange);
        pause.Reply.SetResult("今から始めます。");
        (await models.NextAsync(timeout.Token)).Reply.SetResult("今から始めます。次に進みます。");
        var next = await models.NextAsync(timeout.Token);
        Assert.Equal(new SampleRange(64000, 96000), next.Request.WindowRange);
        next.Reply.SetResult("次に進みます。");
        input.Writer.Complete();
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal(SubtitlePhase.Final, final.Subtitles.Segments[0].Phase);
        Assert.Equal(new SampleRange(0, 64000), final.Subtitles.Segments[0].Range);
        Assert.Equal(0, final.BacklogSamples);
    }

    /// <summary>长开放块持续获得识别和翻译时，旧音频不重复占用尚未处理的积压额度。</summary>
    /// <returns>跨越多个完整积压预算后的连续覆盖和逐版翻译验证任务。</returns>
    [Fact]
    public async Task LongOpenBlockOnlyChargesOutstandingAudioToWorkQueues()
    {
        var models = new ControlledModels();
        var input = Channel.CreateBounded<PipelineInput>(8);
        var options = new PipelineOptions { AlignmentRecoverySamples = 0, MaximumWindowSamples = 64000,
            RefreshSamples = 16000, MemorySamples = 192000, MaximumBacklogSamples = 64000 };
        var pipeline = Create(models, _ => { }, options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        for (var index = 1; index <= 5; index++)
        {
            await input.Writer.WriteAsync(Audio((index - 1) * 32000, index * 32000), timeout.Token);
            var text = "今から詳しい説明をするので" + new string('あ', index);
            (await models.NextAsync(timeout.Token)).Reply.SetResult(text);
            Assert.Equal(text, await models.Translated.Reader.ReadAsync(timeout.Token));
        }
        input.Writer.Complete();
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal("completed", final.Status);
        Assert.Equal(160000, final.RecognizedEnd);
        Assert.Equal(5, final.TranslationCalls);
        Assert.Empty(final.Gaps);
        Assert.Single(final.Subtitles.Segments);
    }

    /// <summary>超过窗口的积压按保留重叠的有序窗口追赶，配置切换不回退已接受范围。</summary>
    /// <returns>长块配置切换及完整覆盖验证任务。</returns>
    [Fact]
    public async Task LargeBacklogAndConfigurationKeepContinuousCoverage()
    {
        var models = new ControlledModels();
        var input = Channel.CreateBounded<PipelineInput>(8);
        var covered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = Create(models, state => { if (state.RecognizedEnd == 640000) covered.TrySetResult(); });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 32000), TestContext.Current.CancellationToken);
        var first = await models.NextAsync(timeout.Token);
        await input.Writer.WriteAsync(Audio(32000, 640000), TestContext.Current.CancellationToken);
        first.Reply.SetResult("まだ途中なので");
        var middle = await models.NextAsync(timeout.Token);
        Assert.Equal(new SampleRange(0, 272000), middle.Request.WindowRange);
        middle.Reply.SetResult("まだ途中なので、前半の内容ですけれども");
        var later = await models.NextAsync(timeout.Token);
        Assert.Equal(new SampleRange(32000, 512000), later.Request.WindowRange);
        later.Reply.SetResult("前半の内容ですけれども、後半の内容なので");
        var tail = await models.NextAsync(timeout.Token);
        Assert.Equal(new SampleRange(160000, 640000), tail.Request.WindowRange);
        tail.Reply.SetResult("後半の内容なので、さらに続きますが");
        await covered.Task.WaitAsync(timeout.Token);
        await input.Writer.WriteAsync(new PipelineConfiguration(new("translategemma:4b")), TestContext.Current.CancellationToken);
        var switched = await models.NextAsync(timeout.Token);
        Assert.Equal(new SampleRange(160000, 640000), switched.Request.WindowRange);
        Assert.Equal(2, switched.Request.Ticket.Identity.ConfigEpoch);
        switched.Reply.SetResult("後半の内容なので、さらに続きますが");
        input.Writer.Complete();
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal("completed", final.Status);
        Assert.Equal(640000, final.RecognizedEnd);
        Assert.Equal("まだ途中なので、前半の内容ですけれども、後半の内容なので、さらに続きますが", Assert.Single(final.Subtitles.Segments).Source);
    }

    /// <summary>采集新代次从零开始，取消中的旧结果不能复活撤销块。</summary>
    /// <returns>新旧时间轴隔离与缺口证据验证任务。</returns>
    [Fact]
    public async Task CaptureRecoveryDiscardsOldRunningResult()
    {
        var models = new ControlledModels();
        var input = Channel.CreateBounded<PipelineInput>(8);
        var pipeline = Create(models, _ => { });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 32000), TestContext.Current.CancellationToken);
        var old = await models.NextAsync(timeout.Token);
        await input.Writer.WriteAsync(new PipelineCaptureReset(2, "device_invalidated"), TestContext.Current.CancellationToken);
        await input.Writer.WriteAsync(Audio(0, 32000) with { CaptureEpoch = 2 }, TestContext.Current.CancellationToken);
        var current = await models.NextAsync(timeout.Token);
        old.Reply.TrySetResult("失效结果");
        current.Reply.SetResult("再開しました。");
        input.Writer.Complete();
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal(2, final.Subtitles.Identity.CaptureEpoch);
        Assert.Equal("再開しました。", Assert.Single(final.Subtitles.Segments).Source);
        Assert.Single(final.Subtitles.RetiredIds);
        Assert.Equal("device_invalidated", Assert.Single(final.Gaps).Reason);
        Assert.Equal(1, models.MaximumConcurrent);
    }

    /// <summary>慢速重置期间继续消费有界入口直到 EOF，就绪后仅对最后一个采集代次缓存的音频发起识别。</summary>
    /// <param name="resumeAfterBacklog">首个缺口是否由同一物理输入的积压续接触发。</param>
    /// <param name="resetAgain">是否在重置仍未完成时再切换一次物理采集代次。</param>
    /// <returns>加载、输入消费、EOF 排空与连续窗口身份的验证任务。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaptureResetBuffersNewEpochThroughEofWhileModelsLoad(bool resumeAfterBacklog, bool resetAgain)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buffered = new TaskCompletionSource<PipelineSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var models = new ControlledModels { ResetDelay = release.Task };
        var input = Channel.CreateBounded<PipelineInput>(1);
        var expectedEpoch = resetAgain ? 3 : 2;
        var pipeline = Create(models, state =>
        {
            if (state.Subtitles.Identity.CaptureEpoch == expectedEpoch && state.AudioEnd == 48000) buffered.TrySetResult(state);
        }, new() { ResumeLiveAfterBacklog = true, AlignmentRecoverySamples = 0, MaximumWindowSamples = 64000, MaximumBacklogSamples = 128000 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(ReadThroughEofAsync(input.Reader, eof, timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 32000), timeout.Token);
        await models.NextAsync(timeout.Token);
        var physicalEpoch = 1;
        var origin = 0;
        if (resumeAfterBacklog)
        {
            origin = 128000;
            await input.Writer.WriteAsync(Audio(32000, origin), timeout.Token);
        }
        else await input.Writer.WriteAsync(new PipelineCaptureReset(++physicalEpoch, "device_invalidated"), timeout.Token);
        if (resetAgain)
        {
            await input.Writer.WriteAsync(Audio(origin, origin + 16000) with { CaptureEpoch = physicalEpoch }, timeout.Token);
            await input.Writer.WriteAsync(new PipelineCaptureReset(++physicalEpoch, "device_invalidated"), timeout.Token);
            origin = 0;
        }
        for (var start = origin; start < origin + 48000; start += 16000)
            await input.Writer.WriteAsync(Audio(start, start + 16000) with { CaptureEpoch = physicalEpoch }, timeout.Token);
        input.Writer.Complete();
        var pending = await buffered.Task.WaitAsync(timeout.Token);
        await eof.Task.WaitAsync(timeout.Token);
        Assert.Equal("recovering_models", pending.Status);
        Assert.Equal(1, pending.RecognitionCalls);
        Assert.Equal(48000, pending.BacklogSamples);
        Assert.False(run.IsCompleted);
        release.SetResult();
        var current = await models.NextAsync(timeout.Token);
        Assert.Equal(expectedEpoch, current.Request.Ticket.Identity.CaptureEpoch);
        Assert.Equal(new SampleRange(0, 48000), current.Request.WindowRange);
        current.Reply.SetResult("新しい音声です。");
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal("completed", final.Status);
        Assert.Equal(0, final.BacklogSamples);
        Assert.Equal("新しい音声です。", Assert.Single(final.Subtitles.Segments).Source);
        Assert.Equal(resetAgain ? 2 : 1, final.Gaps.Length);
        Assert.Equal(1, models.MaximumConcurrent);
        Assert.True(models.Disposed);
    }

    /// <summary>停止发生在新代次加载期间时，先取消并等待加载退出，再释放模型和有限音频缓存。</summary>
    /// <returns>取消未释放的加载屏障、实际模型并发和最终停止状态的验证任务。</returns>
    [Fact]
    public async Task StoppingDuringCaptureResetWaitsForLoaderExit()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var models = new ControlledModels { ResetDelay = release.Task };
        var input = Channel.CreateBounded<PipelineInput>(1);
        var pipeline = Create(models, state => { if (state.Status == "recovering_models" && state.AudioEnd == 32000) buffered.TrySetResult(); });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(stop.Token), stop.Token);
        await input.Writer.WriteAsync(new PipelineCaptureReset(2, "device_invalidated"), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 32000) with { CaptureEpoch = 2 }, timeout.Token);
        await buffered.Task.WaitAsync(timeout.Token);
        Assert.Equal(1, models.Concurrent);
        stop.Cancel();
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal("stopped", final.Status);
        Assert.Equal(0, final.RecognitionCalls);
        Assert.Equal(0, final.MemorySamples);
        Assert.Equal(0, models.Concurrent);
        Assert.True(models.Disposed);
    }

    /// <summary>跨窗口无法对齐时保留最后可信原文，不在 EOF 无限制重复同一推理。</summary>
    /// <returns>有限失败与剩余积压可见验证任务。</returns>
    [Fact]
    public async Task AmbiguousWindowStopsRetryingIdenticalEvidenceAtEof()
    {
        var models = new ControlledModels();
        var input = Channel.CreateBounded<PipelineInput>(8);
        var rejections = new List<RecognitionRejection>();
        var pipeline = Create(models, _ => { }, publishRejection: rejections.Add);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 480000), TestContext.Current.CancellationToken);
        var first = await models.NextAsync(timeout.Token);
        first.Reply.SetResult("この長い文章の続きが");
        await models.Translated.Reader.ReadAsync(timeout.Token);
        await input.Writer.WriteAsync(Audio(480000, 960000), TestContext.Current.CancellationToken);
        input.Writer.Complete();
        (await models.NextAsync(timeout.Token)).Reply.SetResult("別の認識結果なので");
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal(2, final.RecognitionCalls);
        Assert.Equal("ambiguous_text_overlap", final.Status);
        Assert.Equal(480000, final.BacklogSamples);
        Assert.Equal("この長い文章の続きが", Assert.Single(final.Subtitles.Segments).Source);
        var rejected = Assert.Single(rejections);
        Assert.Equal("この長い文章の続きが", rejected.Previous!.Text);
        Assert.Equal("別の認識結果なので", rejected.Current.Text);
        Assert.Equal("ambiguous_text_overlap", rejected.Reason);
    }

    /// <summary>到达旧步长上限且识别失败后，新音频允许短连接窗口或不同终点重新对齐可信原文。</summary>
    /// <param name="recoverOnShorterWindow">较短步长能否直接找到唯一重叠。</param><returns>发生停滞后的可验证推进任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NewAudioCanRecoverAfterTheMaximumAdvanceFailed(bool recoverOnShorterWindow)
    {
        var models = new ControlledModels();
        var input = Channel.CreateBounded<PipelineInput>(8);
        var rejected = Channel.CreateBounded<RecognitionRejection>(4);
        var options = new PipelineOptions { AlignmentRecoverySamples = 0, MaximumWindowSamples = 128000, RefreshSamples = 32000 };
        var pipeline = Create(models, _ => { }, options, publishRejection: failure => rejected.Writer.TryWrite(failure));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 128000), timeout.Token);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("最初の内容と窓の重なりを確かめますが");
        await models.Translated.Reader.ReadAsync(timeout.Token);
        await input.Writer.WriteAsync(Audio(128000, 192000), timeout.Token);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("不一致なので");
        Assert.Equal(192000, (await rejected.Reader.ReadAsync(timeout.Token)).Request.Range.End);
        await input.Writer.WriteAsync(Audio(192000, 224000), timeout.Token);
        var bridge = await models.NextAsync(timeout.Token);
        Assert.Equal(new SampleRange(32000, 160000), bridge.Request.WindowRange);
        const string aligned = "窓の重なりを確かめますがここからの説明も続いていますが";
        bridge.Reply.SetResult(recoverOnShorterWindow ? aligned : "まだ不一致なので");
        var lastEnd = 224000;
        if (!recoverOnShorterWindow)
        {
            Assert.Equal(160000, (await rejected.Reader.ReadAsync(timeout.Token)).Request.Range.End);
            lastEnd = 256000;
            await input.Writer.WriteAsync(Audio(224000, lastEnd), timeout.Token);
            var later = await models.NextAsync(timeout.Token);
            Assert.Equal(new SampleRange(96000, 224000), later.Request.WindowRange);
            later.Reply.SetResult(aligned);
        }
        var tail = await models.NextAsync(timeout.Token);
        Assert.Equal(lastEnd, tail.Request.WindowRange.End);
        tail.Reply.SetResult("ここからの説明も続いていますが、さらに話します");
        input.Writer.Complete();
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal("completed", final.Status);
        Assert.Equal(lastEnd, final.RecognizedEnd);
        Assert.Empty(final.Gaps);
        Assert.EndsWith("さらに話します", Assert.Single(final.Subtitles.Segments).Source);
    }

    /// <summary>可信原文接近 120 秒上限时，用短连接的完整扩窗重新取得原始锚点，再处理仍在缓存的新音频。</summary>
    /// <returns>使用真实 30/60/120 秒预算从 117 秒进度继续的验证任务。</returns>
    [Fact]
    public async Task ShorterBridgeRestoresOriginalAnchorAfterLongestRecoveryFailed()
    {
        var models = new ControlledModels();
        var input = Channel.CreateBounded<PipelineInput>(8);
        var coverage = Channel.CreateBounded<long>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        var rejected = Channel.CreateBounded<RecognitionRejection>(8);
        var pipeline = Create(models, state => coverage.Writer.TryWrite(state.RecognizedEnd), new() { AlignmentRecoverySamples = 1920000 },
            publishRejection: failure => rejected.Writer.TryWrite(failure));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await FeedStableWindowsAsync(models, input.Writer, coverage.Reader, 105 * 16000, timeout.Token);
        await input.Writer.WriteAsync(Audio(105 * 16000, 117 * 16000), timeout.Token);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("冒頭の文章が続いていますが");
        while (await coverage.Reader.ReadAsync(timeout.Token) < 117 * 16000) { }
        await input.Writer.WriteAsync(Audio(117 * 16000, 132 * 16000), timeout.Token);
        foreach (var seconds in new[] { 30, 60, 120 })
        {
            var attempt = await models.NextAsync(timeout.Token);
            Assert.Equal(new SampleRange((132 - seconds) * 16000, 132 * 16000), attempt.Request.WindowRange);
            attempt.Reply.SetResult("読み方が違うので");
            await rejected.Reader.ReadAsync(timeout.Token);
        }
        await input.Writer.WriteAsync(Audio(132 * 16000, 134 * 16000), timeout.Token);
        foreach (var seconds in new[] { 30, 60 })
        {
            var attempt = await models.NextAsync(timeout.Token);
            Assert.Equal(new SampleRange((119 - seconds) * 16000, 119 * 16000), attempt.Request.WindowRange);
            attempt.Reply.SetResult("まだ読み方が違うので");
        }
        var anchored = await models.NextAsync(timeout.Token);
        Assert.Equal(new SampleRange(0, 119 * 16000), anchored.Request.WindowRange);
        Assert.Equal(2048, anchored.Request.MaxNewTokens);
        anchored.Reply.SetResult("冒頭の文章が続いていますが、この先の発言を確かめますが");
        var tail = await models.NextAsync(timeout.Token);
        Assert.Equal(new SampleRange(104 * 16000, 134 * 16000), tail.Request.WindowRange);
        tail.Reply.SetResult("この先の発言を確かめますが、あとも続きます");
        input.Writer.Complete();
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal("completed", final.Status);
        Assert.Equal(134 * 16000, final.RecognizedEnd);
        Assert.Equal(0, final.BacklogSamples);
        Assert.Empty(final.Gaps);
    }

    /// <summary>只在失败后扩充同一范围上下文；完整重识别保留原始重复，截断结果仍拒收。</summary>
    /// <param name="complete">扩展调用是否正常生成完整结果。</param><returns>同锚点恢复与失败可见验证任务。</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AlignmentRecoveryReplaysSameRangeWithoutAcceptingTruncation(bool complete)
    {
        var models = new ControlledModels { RecoveryComplete = complete };
        var input = Channel.CreateBounded<PipelineInput>(8);
        var pipeline = Create(models, _ => { }, new() { AlignmentRecoverySamples = 960000 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 480000), TestContext.Current.CancellationToken);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("冒頭の文章が続いていますが");
        await models.Translated.Reader.ReadAsync(timeout.Token);
        await input.Writer.WriteAsync(Audio(480000, 720000), TestContext.Current.CancellationToken);
        input.Writer.Complete();
        var failed = await models.NextAsync(timeout.Token);
        Assert.Equal(new SampleRange(240000, 720000), failed.Request.WindowRange);
        failed.Reply.SetResult("読み方を変えた別の文章なので");
        var expanded = await models.NextAsync(timeout.Token);
        Assert.Equal(failed.Request.Ticket.Range, expanded.Request.Ticket.Range);
        Assert.NotEqual(failed.Request.Ticket.Id, expanded.Request.Ticket.Id);
        Assert.Equal(new SampleRange(0, 720000), expanded.Request.WindowRange);
        Assert.Equal(1024, expanded.Request.MaxNewTokens);
        expanded.Reply.SetResult("中野さん、中野さん、まだ続きがありますが");
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal(3, final.RecognitionCalls);
        Assert.Equal(complete ? "completed" : "recognition_incomplete", final.Status);
        Assert.Equal(complete ? 0 : 240000, final.BacklogSamples);
        Assert.Equal(complete ? "中野さん、中野さん、まだ続きがありますが" : "冒頭の文章が続いていますが", Assert.Single(final.Subtitles.Segments).Source);
        Assert.Equal(1, models.MaximumConcurrent);
        Assert.Equal(0, final.Pins);
    }

    /// <summary>扩窗减少已有重复时按开关拒收，完整来源和积压保留，恢复到原锚点后不能空转重试。</summary>
    /// <param name="enabled">是否启用独立检查。</param><param name="candidate">完整扩窗原文。</param><param name="rejected">是否应拒收。</param>
    /// <returns>真实事件循环中的有限拒收与资源释放验证任务。</returns>
    [Theory]
    [InlineData(true, "中野さん、まだ続きますが", true)]
    [InlineData(false, "中野さん、まだ続きますが", false)]
    [InlineData(true, "中野さん、中野さん、中野さん、まだ続きますが", false)]
    [InlineData(true, "中野さん。中野さん、まだ終わりませんが", false)]
    public async Task RecoveryPreservesObservedRepetitionsWhenEnabled(bool enabled, string candidate, bool rejected)
    {
        const string previous = "中野さん、中野さん、まだ続きますが";
        var models = new ControlledModels();
        var input = Channel.CreateBounded<PipelineInput>(8);
        var operations = new ConcurrentQueue<PipelineOperation>();
        var rejections = new ConcurrentQueue<RecognitionRejection>();
        var pipeline = Create(models, _ => { }, new() { AlignmentRecoverySamples = 1920000, PreserveObservedRepetitions = enabled },
            publishOperation: operations.Enqueue, publishRejection: rejections.Enqueue);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 480000), TestContext.Current.CancellationToken);
        (await models.NextAsync(timeout.Token)).Reply.SetResult(previous);
        await models.Translated.Reader.ReadAsync(timeout.Token);
        await input.Writer.WriteAsync(Audio(480000, 720000), TestContext.Current.CancellationToken);
        input.Writer.Complete();
        (await models.NextAsync(timeout.Token)).Reply.SetResult("別の文章なので");
        var expanded = await models.NextAsync(timeout.Token);
        Assert.Equal(new SampleRange(0, 720000), expanded.Request.WindowRange);
        expanded.Reply.SetResult(candidate);
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal(3, final.RecognitionCalls);
        Assert.Equal(rejected ? "observed_repetition_reduced" : "completed", final.Status);
        Assert.Equal(rejected ? 240000 : 0, final.BacklogSamples);
        var segment = Assert.Single(final.Subtitles.Segments);
        Assert.Equal(rejected ? previous : candidate, segment.Source);
        Assert.Equal(rejected ? 1 : 2, segment.SourceRevision);
        Assert.Equal(new SampleRange(0, rejected ? 480000 : 720000), segment.Range);
        if (rejected)
        {
            var evidence = Assert.Single(rejections, x => x.Reason == "observed_repetition_reduced");
            Assert.Equal(previous, evidence.Previous!.Text);
            Assert.Equal(candidate, evidence.Current.Text);
            Assert.Equal(expanded.Request.Ticket, evidence.Request);
        }
        Assert.NotEqual(SubtitlePhase.Final, segment.Phase);
        Assert.Equal(0, final.Pins);
        Assert.True(models.Disposed);
        Assert.Contains(operations, x => x.RecognitionTokenBudget == 1024 &&
            x.Disposition == (rejected ? "rejected:observed_repetition_reduced" : "accepted"));
    }

    /// <summary>60 秒上下文仍不唯一时最多再试 120 秒，不把失败窗口转为可信原文。</summary>
    /// <param name="budget">显式恢复秒数上限。</param><returns>两级回退与预算耗尽验证任务。</returns>
    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    public async Task RecoveryBudgetIsFiniteAndOnlyAddsEarlierContext(int budget)
    {
        var models = new ControlledModels();
        var input = Channel.CreateBounded<PipelineInput>(8);
        var coverage = Channel.CreateBounded<long>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        var pipeline = Create(models, state => coverage.Writer.TryWrite(state.RecognizedEnd), new() { AlignmentRecoverySamples = budget * 16000 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await FeedStableWindowsAsync(models, input.Writer, coverage.Reader, 960000, timeout.Token);
        await input.Writer.WriteAsync(Audio(960000, 1200000), TestContext.Current.CancellationToken);
        input.Writer.Complete();
        (await models.NextAsync(timeout.Token)).Reply.SetResult("異なる読み方なので");
        var sixty = await models.NextAsync(timeout.Token);
        Assert.Equal(new SampleRange(240000, 1200000), sixty.Request.WindowRange);
        sixty.Reply.SetResult("まだ異なる読み方なので");
        if (budget == 120)
        {
            var maximum = await models.NextAsync(timeout.Token);
            Assert.Equal(new SampleRange(0, 1200000), maximum.Request.WindowRange);
            Assert.Equal(2048, maximum.Request.MaxNewTokens);
            maximum.Reply.SetResult("中野さん、中野さん、まだ続きがありますが");
        }
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal(budget == 120 ? 6 : 5, final.RecognitionCalls);
        Assert.Equal(budget == 120 ? "completed" : "ambiguous_text_overlap", final.Status);
        Assert.Equal(budget == 120 ? 0 : 240000, final.BacklogSamples);
    }

    /// <summary>超过最大恢复窗口的未闭合块仍需唯一保序依据，不能无限重试或用局部文本替换全块。</summary>
    /// <returns>120 秒实验上限耗尽后的原文与积压保留验证任务。</returns>
    [Fact]
    public async Task MaximumRecoveryDoesNotReplaceAnUnanchoredLongBlock()
    {
        var models = new ControlledModels();
        var input = Channel.CreateBounded<PipelineInput>(8);
        var coverage = Channel.CreateBounded<long>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        var pipeline = Create(models, state => coverage.Writer.TryWrite(state.RecognizedEnd), new() { AlignmentRecoverySamples = 1920000 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await FeedStableWindowsAsync(models, input.Writer, coverage.Reader, 1920000, timeout.Token);
        await input.Writer.WriteAsync(Audio(1920000, 2160000), TestContext.Current.CancellationToken);
        input.Writer.Complete();
        foreach (var length in new[] { 480000, 960000, 1920000 })
        {
            var attempt = await models.NextAsync(timeout.Token);
            Assert.Equal(new SampleRange(2160000 - length, 2160000), attempt.Request.WindowRange);
            attempt.Reply.SetResult("まだ別の認識なので");
        }
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal(10, final.RecognitionCalls);
        Assert.Equal("ambiguous_text_overlap", final.Status);
        Assert.Equal(240000, final.BacklogSamples);
        Assert.Equal("冒頭の文章が続いていますが", Assert.Single(final.Subtitles.Segments).Source);
        Assert.Equal(0, final.SpilledSamples);
        Assert.True(models.Disposed);
    }

    /// <summary>扩展调用计算期间新增音频仍保留，旧终点恢复后继续处理完整尾部。</summary>
    /// <returns>恢复与新增输入交错后的完整覆盖验证任务。</returns>
    [Fact]
    public async Task RecoveryKeepsAudioArrivingDuringExpandedInference()
    {
        var models = new ControlledModels();
        var input = Channel.CreateBounded<PipelineInput>(8);
        var pipeline = Create(models, _ => { }, new() { AlignmentRecoverySamples = 960000 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 480000), TestContext.Current.CancellationToken);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("冒頭の文章が続いていますが");
        await models.Translated.Reader.ReadAsync(timeout.Token);
        await input.Writer.WriteAsync(Audio(480000, 720000), TestContext.Current.CancellationToken);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("別の文章なので");
        var expanded = await models.NextAsync(timeout.Token);
        await input.Writer.WriteAsync(Audio(720000, 960000), TestContext.Current.CancellationToken);
        input.Writer.Complete();
        expanded.Reply.SetResult("中野さん、中野さん、まだ続きがありますが");
        var tail = await models.NextAsync(timeout.Token);
        Assert.Equal(new SampleRange(0, 960000), tail.Request.Ticket.Range);
        Assert.Equal(new SampleRange(480000, 960000), tail.Request.WindowRange);
        tail.Reply.SetResult("まだ続きがありますが、今は行きません");
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal("completed", final.Status);
        Assert.Equal(0, final.BacklogSamples);
        Assert.Equal("中野さん、中野さん、まだ続きがありますが、今は行きません", Assert.Single(final.Subtitles.Segments).Source);
        Assert.Equal(1, models.MaximumConcurrent);
    }

    /// <summary>模型实际抛出异常后等待旧调用退出，重建代次并只接受新请求。</summary>
    /// <returns>故障恢复和原文可见性验证任务。</returns>
    [Fact]
    public async Task FailedWorkerRecoversWithNewEpochAndKeepsSource()
    {
        var models = new ControlledModels();
        var snapshots = new ConcurrentQueue<PipelineSnapshot>();
        var operations = new ConcurrentQueue<PipelineOperation>();
        var input = Channel.CreateBounded<PipelineInput>(8);
        var pipeline = Create(models, snapshots.Enqueue, publishOperation: operations.Enqueue);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 32000), TestContext.Current.CancellationToken);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("まだ終わりませんが");
        await models.Translated.Reader.ReadAsync(timeout.Token);
        await input.Writer.WriteAsync(Audio(32000, 64000), TestContext.Current.CancellationToken);
        (await models.NextAsync(timeout.Token)).Reply.SetException(new IOException("worker_eof"));
        var recovered = await models.NextAsync(timeout.Token);
        Assert.Equal(2, recovered.Request.Ticket.Identity.WorkerEpoch);
        Assert.Equal(new SampleRange(0, 64000), recovered.Request.WindowRange);
        recovered.Reply.SetResult("まだ終わりませんが、明日には終わります。");
        input.Writer.Complete();
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal("completed", final.Status);
        Assert.Contains(snapshots, x => x.Subtitles.Segments.Any(s => s.Source == "まだ終わりませんが"));
        Assert.Equal(1, models.MaximumConcurrent);
        Assert.True(models.Disposed);
        var failed = Assert.Single(operations, x => x.Disposition == "failed:IOException");
        Assert.Equal(new SampleRange(0, 64000), failed.Window);
        Assert.Equal(512, failed.RecognitionTokenBudget);
        Assert.True(failed.CountedAttempt);
        Assert.False(failed.GenerationComplete);
        Assert.Null(failed.ModelSeconds);
    }

    /// <summary>翻译服务异常不会清空原文，成功恢复前显式显示失败及重新加载状态。</summary>
    /// <returns>有限恢复后当前版本译文验证任务。</returns>
    [Fact]
    public async Task TranslationFailureKeepsSourceDuringRecovery()
    {
        var models = new ControlledModels { FailTranslations = 1 };
        var snapshots = new ConcurrentQueue<PipelineSnapshot>();
        var operations = new ConcurrentQueue<PipelineOperation>();
        var input = Channel.CreateBounded<PipelineInput>(8);
        var pipeline = Create(models, snapshots.Enqueue, publishOperation: operations.Enqueue);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 32000), TestContext.Current.CancellationToken);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("中野さん、まだ終わりませんが。");
        var restarted = await models.NextAsync(timeout.Token);
        Assert.Equal(2, restarted.Request.Ticket.Identity.WorkerEpoch);
        restarted.Reply.SetResult("中野さん、まだ終わりませんが。");
        input.Writer.Complete();
        var final = await run.WaitAsync(timeout.Token);
        Assert.Equal("completed", final.Status);
        Assert.Equal(TranslationState.Complete, Assert.Single(final.Subtitles.Segments).TranslationState);
        Assert.Contains(snapshots, x => x.Subtitles.Segments.Any(s => s.TranslationState == TranslationState.Failed && s.Source == "中野さん、まだ終わりませんが。"));
        Assert.Contains(snapshots, x => x.Status == "recovering_models");
        Assert.Contains(operations, x => x.Disposition == "failed:IOException" && !x.GenerationComplete);
        Assert.Equal(1, models.MaximumConcurrent);
    }

    /// <summary>清理失败仍发布已停止快照，不能误报完成或漏掉缓存回收。</summary>
    /// <returns>错误可见性与缓存清理验证任务。</returns>
    [Fact]
    public async Task CleanupFailureStillPublishesStoppedSnapshot()
    {
        var models = new ControlledModels { FailCleanup = true };
        var input = Channel.CreateBounded<PipelineInput>(1);
        input.Writer.Complete();
        var result = await Create(models, _ => { }).RunAsync(input.Reader.ReadAllAsync(TestContext.Current.CancellationToken), CancellationToken.None);
        Assert.Equal("model_cleanup_failed:IOException", result.Status);
        Assert.True(result.Subtitles.Stopped);
        Assert.True(models.Disposed);
        Assert.Equal(0, result.MemorySamples);
        Assert.Equal(0, result.Pins);
    }

    /// <summary>诊断介质失败不能绕过模型和音频所有权的退出屏障。</summary>
    /// <returns>证据写入异常后的实际停止任务。</returns>
    [Fact]
    public async Task OperationEvidenceFailureStillDisposesModelsAndLeases()
    {
        var models = new ControlledModels();
        var input = Channel.CreateBounded<PipelineInput>(8);
        var pipeline = Create(models, _ => { }, publishOperation: _ => throw new IOException("evidence_full"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = pipeline.RunAsync(input.Reader.ReadAllAsync(timeout.Token), timeout.Token);
        await input.Writer.WriteAsync(Audio(0, 32000), TestContext.Current.CancellationToken);
        (await models.NextAsync(timeout.Token)).Reply.SetResult("保持原文です。");
        var result = await run.WaitAsync(timeout.Token);
        Assert.Equal("pipeline_failed:IOException", result.Status);
        Assert.Equal("保持原文です。", Assert.Single(result.Subtitles.Segments).Source);
        Assert.True(models.Disposed); Assert.Equal(0, result.Pins);
    }

    /// <summary>在消费方实际读到流末尾时通知测试，区别于生产方关闭输入通道。</summary>
    /// <param name="input">容量受限的音频和控制流。</param><param name="eof">消费方实际到达 EOF 的确认。</param>
    /// <param name="token">本场会话取消信号。</param><returns>保留原始顺序的管道输入。</returns>
    private static async IAsyncEnumerable<PipelineInput> ReadThroughEofAsync(ChannelReader<PipelineInput> input, TaskCompletionSource eof,
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var item in input.ReadAllAsync(token)) yield return item;
        eof.TrySetResult();
    }

    /// <summary>逐窗提交相同原文，并在权威快照覆盖该窗口后才继续送入音频。</summary>
    /// <param name="models">接受受控识别答复的后端。</param><param name="input">连续测试音频入口。</param>
    /// <param name="coverage">最近快照已接受的音频终点。</param><param name="lastEnd">最后一个稳定窗口的采样终点。</param>
    /// <param name="token">本次测试的有限取消信号。</param><returns>所有稳定窗口均已覆盖时完成的任务。</returns>
    private static async Task FeedStableWindowsAsync(ControlledModels models, ChannelWriter<PipelineInput> input, ChannelReader<long> coverage,
        long lastEnd, CancellationToken token)
    {
        for (var end = 480000; end <= lastEnd; end += 240000)
        {
            await input.WriteAsync(Audio(end == 480000 ? 0 : end - 240000, end), token);
            (await models.NextAsync(token)).Reply.SetResult("冒頭の文章が続いていますが");
            long acceptedEnd;
            do { acceptedEnd = await coverage.ReadAsync(token); } while (acceptedEnd < end);
            Assert.Equal(end, acceptedEnd);
        }
    }

    /// <summary>为每个测试建立独占 D 盘临时缓存。</summary>
    /// <param name="models">受控后端。</param><param name="publish">快照观察者。</param><param name="options">可选限额。</param>
    /// <param name="publishOperation">可选逐次操作证据。</param>
    /// <param name="publishRejection">可选显式拒收原文证据。</param>
    /// <returns>尚未运行的独立会话。</returns>
    private static SubtitlePipeline Create(IPipelineModels models, Action<PipelineSnapshot> publish, PipelineOptions? options = null, Action<PipelineOperation>? publishOperation = null,
        Action<RecognitionRejection>? publishRejection = null)
    {
        options ??= new() { AlignmentRecoverySamples = 0 };
        return new(new(Guid.NewGuid(), 1, 1, 1), new(), options,
            () => new ContinuousAudioStore(new FileAudioSpillStore(Path.Combine(@"D:\Soft Ware\LiveLingo\cache\tmp", "pipeline-test-" + Guid.NewGuid().ToString("N"))),
                options.MemorySamples, options.SpillSamples, options.MaximumLeaseSamples), models, publish, publishOperation, publishRejection);
    }

    /// <summary>生成含非零音频和真实静音尾部的有限输入，不把纯数字静音误当讲话。</summary>
    /// <param name="start">连续起点。</param><param name="end">连续终点。</param><param name="quietTail">末尾是否有半秒静音。</param><returns>受控输入。</returns>
    private static PipelineAudio Audio(long start, long end, bool quietTail = true)
    {
        var samples = new float[checked((int)(end - start))];
        Array.Fill(samples, 0.1f, 0, quietTail ? samples.Length - 8000 : samples.Length);
        return new(1, new(new(start, end), samples));
    }

    /// <summary>交给测试控制结束的实际在途请求。</summary><param name="Request">关联身份。</param><param name="Reply">原始文本完成信号。</param>
    private sealed record PendingRecognition(AsrRequest Request, TaskCompletionSource<string> Reply);
    /// <summary>只替代昂贵模型边界，所有管道、缓存、身份与调度均使用生产实现。</summary>
    private sealed class ControlledModels : IPipelineModels
    {
        private readonly Channel<PendingRecognition> calls = Channel.CreateBounded<PendingRecognition>(8);
        private int concurrent;
        private int translationCalls;
        /// <summary>获取已开始的翻译通知。</summary>
        public Channel<string> Translated { get; } = Channel.CreateBounded<string>(16);
        /// <summary>获取模型最大实际并发。</summary>
        public int MaximumConcurrent { get; private set; }
        /// <summary>获取仍在执行的操作数。</summary>
        public int Concurrent => Volatile.Read(ref concurrent);
        /// <summary>获取已完成资源清理状态。</summary>
        public bool Disposed { get; private set; }
        /// <summary>获取或设置需要注入的翻译服务失败次数。</summary>
        public int FailTranslations { get; set; }
        /// <summary>获取扩展窗口生成完整性，用于拒收截断结果。</summary>
        public bool RecoveryComplete { get; init; } = true;
        /// <summary>获取或设置退出阶段故障注入。</summary>
        public bool FailCleanup { get; init; }
        /// <summary>获取可选翻译计算屏障，仅在实际解除或取消后退出调用。</summary>
        public Task? TranslationDelay { get; init; }
        /// <summary>获取首次启动之后的模型加载屏障，可由新控制或会话停止取消。</summary>
        public Task? ResetDelay { get; init; }
        /// <summary>获取从第几次实际翻译开始等待屏障。</summary>
        public int TranslationDelayFromCall { get; init; } = 1;
        /// <summary>获取返回截断响应的翻译调用序号；零表示每次都正常终止。</summary>
        public int IncompleteTranslationCall { get; init; }
        /// <summary>获取输入短句的受控译文，可模拟与原文标点不同的模型输出。</summary>
        public Func<string, string> TranslateText { get; init; } = static source => source;
        /// <summary>获取空结果是否模拟模型完整无语音终止。</summary>
        public bool NoSpeechForEmpty { get; init; }
        /// <summary>取得下一次实际识别调用。</summary><param name="token">有限测试取消。</param><returns>可控请求。</returns>
        public ValueTask<PendingRecognition> NextAsync(CancellationToken token) => calls.Reader.ReadAsync(token);
        /// <inheritdoc />
        public async Task ResetAsync(PipelineIdentity identity, PipelineModelSelection selection, CancellationToken cancellationToken)
        {
            Assert.Equal(0, Concurrent);
            if (identity.WorkerEpoch == 1 || ResetDelay is null) return;
            MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref concurrent));
            try { await ResetDelay.WaitAsync(cancellationToken); }
            finally { Interlocked.Decrement(ref concurrent); }
        }
        /// <inheritdoc />
        public async Task<IpcHeader> RecognizeAsync(AsrRequest request, AudioWindowLease window, CancellationToken cancellationToken)
        {
            MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref concurrent));
            try
            {
                Assert.Equal(request.WindowRange, window.Range);
                var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                await calls.Writer.WriteAsync(new(request, reply), cancellationToken);
                var text = await reply.Task.WaitAsync(cancellationToken);
                Assert.Equal(request.WindowRange.Length, window.Samples.Length);
                return new() { Type = "Transcription", WorkerEpoch = request.Ticket.Identity.WorkerEpoch, Correlation = request,
                    Text = text, Language = "Japanese", Complete = request.MaxNewTokens <= 512 || RecoveryComplete,
                    Reason = request.MaxNewTokens > 512 && !RecoveryComplete ? "incomplete-generation" : NoSpeechForEmpty && text.Length == 0 ? "model-no-speech" : "model-eos", ElapsedSeconds = 0.1 };
            }
            finally { Interlocked.Decrement(ref concurrent); }
        }
        /// <inheritdoc />
        public async Task<TranslationResult> TranslateAsync(TranslationInput input, CancellationToken cancellationToken)
        {
            MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref concurrent));
            try
            {
                Assert.True(Translated.Writer.TryWrite(input.Source));
                if (++translationCalls >= TranslationDelayFromCall && TranslationDelay is not null) await TranslationDelay.WaitAsync(cancellationToken);
                if (FailTranslations > 0) { FailTranslations--; throw new IOException("translation_service_failed"); }
                var complete = translationCalls != IncompleteTranslationCall;
                return new TranslationResult(input, TranslateText(input.Source), complete, complete ? "stop" : "length", TimeSpan.Zero, new('a', 64), 0, 0, 1);
            }
            finally { Interlocked.Decrement(ref concurrent); }
        }
        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Assert.Equal(0, Concurrent); Disposed = true;
            if (FailCleanup) throw new IOException("model_cleanup_failed");
            return ValueTask.CompletedTask;
        }
    }
}
