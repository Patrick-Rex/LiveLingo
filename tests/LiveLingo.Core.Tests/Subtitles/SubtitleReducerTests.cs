using LiveLingo.Core.Audio;
using LiveLingo.Core.Subtitles;

namespace LiveLingo.Core.Tests.Subtitles;

/// <summary>用乱序与撤销行为验证字幕权威状态，拒绝结果不得改变快照。</summary>
public sealed class SubtitleReducerTests
{
    /// <summary>完整空原文撤销过期译文显示与关联，旧结果不得恢复被无语音证据撤销的块。</summary>
    [Fact]
    public void EmptySourceClearsStaleTranslationBeforeRetirement()
    {
        var reducer = NewReducer(); var id = Guid.NewGuid();
        reducer.Add(id, new(0, 1));
        Source(reducer, id, 1, 16000, "背面");
        var translation = Request(reducer, id, PipelineRequestKind.Translation, 1);
        Assert.True(reducer.Register(translation));
        Assert.True(reducer.AcceptTranslation(translation, "背面。", true));
        var late = Request(reducer, id, PipelineRequestKind.Translation, 2);
        Assert.True(reducer.Register(late));
        Source(reducer, id, 2, 32000, "");
        var empty = Assert.Single(reducer.Snapshot().Segments);
        Assert.Equal(2, empty.SourceRevision);
        Assert.Equal(TranslationState.None, empty.TranslationState);
        Assert.Null(empty.Translation);
        Assert.Null(empty.TranslatedSourceRevision);
        Assert.False(reducer.AcceptTranslation(late, "旧译文", true));
        reducer.RetireEmpty(id);
        Assert.Empty(reducer.Snapshot().Segments);
        Assert.Contains(id, reducer.Snapshot().RetiredIds);
    }

    /// <summary>合并保留两次真实重复，原身份同时撤销且只提交一个状态版本。</summary>
    [Fact]
    public void MergePreservesRepetitionsAndRetiresBothSources()
    {
        var reducer = NewReducer();
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var merged = Guid.NewGuid();
        reducer.Add(first, new(0, 16000)); reducer.Add(second, new(16000, 32000));
        var late = Request(reducer, second, PipelineRequestKind.Recognition, 1);
        Assert.True(reducer.Register(late));
        var version = reducer.StateSequence;
        reducer.Replace([first, second], [new(merged, new(0, 32000), "はい、はい")]);
        var snapshot = reducer.Snapshot();
        Assert.Equal(version + 1, snapshot.StateSequence);
        Assert.Equal("はい、はい", Assert.Single(snapshot.Segments).Source);
        Assert.Contains(first, snapshot.RetiredIds); Assert.Contains(second, snapshot.RetiredIds);
        Assert.False(reducer.AcceptSource(late, "はい", true, BoundaryDecision.Pending));
    }

    /// <summary>配置变化使既有边界证据失效，不能仅靠新译文重新定稿。</summary>
    [Fact]
    public void ConfigChangeRequiresFreshBoundaryEvidence()
    {
        var reducer = NewReducer(); var id = Guid.NewGuid();
        reducer.Add(id, new(0, 1));
        Source(reducer, id, 1, 16000, "行きません。", new(true, "closed", 2, true));
        reducer.AdvanceIdentity(reducer.Identity with { ConfigEpoch = 2 });
        var translation = Request(reducer, id, PipelineRequestKind.Translation, 1);
        Assert.True(reducer.Register(translation));
        Assert.True(reducer.AcceptTranslation(translation, "不去。", true));
        Assert.NotEqual(SubtitlePhase.Final, Assert.Single(reducer.Snapshot().Segments).Phase);
        Assert.False(Assert.Single(reducer.Snapshot().Segments).Boundary.CanClose);
    }

    /// <summary>源版本 8 的译文先到后，版本 7 即使晚到也不能覆盖。</summary>
    [Fact]
    public void OlderTranslationCannotOverwriteNewerSource()
    {
        var reducer = NewReducer();
        var id = Guid.NewGuid();
        reducer.Add(id, new(0, 1));
        for (var index = 1; index <= 7; index++) Source(reducer, id, index, index * 16000, "条件" + index);
        var old = Request(reducer, id, PipelineRequestKind.Translation, 1);
        Assert.True(reducer.Register(old));
        Source(reducer, id, 8, 128000, "今日は行きません。はい、はい。");
        var current = Request(reducer, id, PipelineRequestKind.Translation, 2);
        Assert.True(reducer.Register(current));
        Assert.True(reducer.AcceptTranslation(current, "今天不去。是的，是的。", true));
        var snapshot = reducer.Snapshot();
        Assert.False(reducer.AcceptTranslation(old, "今天去。", true));
        Assert.Equal("stale_source_revision", reducer.LastRejection);
        Assert.Equal(snapshot.StateSequence, reducer.StateSequence);
        Assert.Equal("今天不去。是的，是的。", Assert.Single(reducer.Snapshot().Segments).Translation);
    }

    /// <summary>停止和各类代次不匹配均使结果不可接收。</summary>
    /// <param name="changed">要变更的来源身份分量。</param>
    [Theory]
    [InlineData("session")]
    [InlineData("capture")]
    [InlineData("worker")]
    [InlineData("config")]
    [InlineData("stop")]
    public void InvalidatedIdentityCannotPublish(string changed)
    {
        var reducer = NewReducer();
        var id = Guid.NewGuid();
        reducer.Add(id, new(0, 16000));
        var request = Request(reducer, id, PipelineRequestKind.Recognition, 1);
        Assert.True(reducer.Register(request));
        var identity = request.Identity;
        if (changed == "stop") reducer.Stop();
        else request = request with { Identity = changed switch
        {
            "session" => identity with { SessionId = Guid.NewGuid() },
            "capture" => identity with { CaptureEpoch = 2 },
            "worker" => identity with { WorkerEpoch = 2 },
            _ => identity with { ConfigEpoch = 2 }
        } };
        var version = reducer.StateSequence;
        Assert.False(reducer.AcceptSource(request, "古い結果", true, BoundaryDecision.Pending));
        Assert.Equal(version, reducer.StateSequence);
        Assert.Empty(Assert.Single(reducer.Snapshot().Segments).Source);
    }

    /// <summary>即使原文版本没有变化，重建后的新翻译票据也必须携带当前 worker 代次。</summary>
    [Fact]
    public void TranslationRegistrationRejectsOldWorkerEvenForCurrentSource()
    {
        var reducer = NewReducer(); var id = Guid.NewGuid();
        reducer.Add(id, new(0, 1)); Source(reducer, id, 1, 16000, "変わらない原文です。");
        var stale = Request(reducer, id, PipelineRequestKind.Translation, 1);
        reducer.AdvanceIdentity(reducer.Identity with { WorkerEpoch = 2 });
        Assert.False(reducer.Register(stale));
        Assert.Equal("stale_worker_epoch", reducer.LastRejection);
        var current = stale with { Id = Guid.NewGuid(), Identity = reducer.Identity };
        Assert.True(reducer.Register(current));
        Assert.True(reducer.AcceptTranslation(current, "原文没有改变。", true));
    }

    /// <summary>同范围重试只接受当前登记序号，不接受重复回包或改写范围。</summary>
    [Fact]
    public void RegisteredRangeAndLatestRetryAreAuthoritative()
    {
        var reducer = NewReducer();
        var id = Guid.NewGuid();
        reducer.Add(id, new(0, 16000));
        var first = Request(reducer, id, PipelineRequestKind.Recognition, 1);
        var second = first with { Id = Guid.NewGuid(), Sequence = 2 };
        Assert.True(reducer.Register(first));
        Assert.True(reducer.Register(second));
        Assert.False(reducer.AcceptSource(first, "はい", true, BoundaryDecision.Pending));
        Assert.False(reducer.AcceptSource(second with { Range = new(0, 32000) }, "偽の範囲", true, BoundaryDecision.Pending));
        Assert.True(reducer.AcceptSource(second, "はい、はい", true, BoundaryDecision.Pending));
        Assert.False(reducer.AcceptSource(second, "はい", true, BoundaryDecision.Pending));
        Assert.Equal("はい、はい", Assert.Single(reducer.Snapshot().Segments).Source);
    }

    /// <summary>新源到达后旧译文保持明确的旧版本，截断不会伪造成当前译文。</summary>
    [Fact]
    public void PendingAndFailedTranslationsKeepTheirSourceVersionVisible()
    {
        var reducer = NewReducer();
        var id = Guid.NewGuid();
        reducer.Add(id, new(0, 1));
        Source(reducer, id, 1, 16000, "行きます");
        var first = Request(reducer, id, PipelineRequestKind.Translation, 1);
        Assert.True(reducer.Register(first));
        Assert.True(reducer.AcceptTranslation(first, "去", true));
        Source(reducer, id, 2, 32000, "行きません");
        var pending = Assert.Single(reducer.Snapshot().Segments);
        Assert.Equal(TranslationState.Pending, pending.TranslationState);
        Assert.NotEqual(pending.SourceRevision, pending.TranslatedSourceRevision);
        var second = Request(reducer, id, PipelineRequestKind.Translation, 2);
        Assert.True(reducer.Register(second));
        Assert.False(reducer.AcceptTranslation(second, "不", false));
        var failed = Assert.Single(reducer.Snapshot().Segments);
        Assert.Equal(TranslationState.Failed, failed.TranslationState);
        Assert.Equal("去", failed.Translation);
        Assert.NotEqual(SubtitlePhase.Final, failed.Phase);
    }

    /// <summary>定稿不能被普通结果修改，重开必须证明音频仍然存在。</summary>
    [Fact]
    public void FinalRequiresExplicitRetainedAudioReopen()
    {
        var reducer = NewReducer();
        var id = Guid.NewGuid();
        reducer.Add(id, new(0, 1));
        Source(reducer, id, 1, 16000, "行きません。", new(true, "complete_with_right_context", 2, true));
        var translation = Request(reducer, id, PipelineRequestKind.Translation, 1);
        Assert.True(reducer.Register(translation));
        Assert.True(reducer.AcceptTranslation(translation, "不去。", true));
        Assert.Equal(SubtitlePhase.Final, Assert.Single(reducer.Snapshot().Segments).Phase);
        Assert.False(reducer.Register(Request(reducer, id, PipelineRequestKind.Recognition, 2)));
        Assert.Throws<InvalidOperationException>(() => reducer.Reopen(id, new(16000, 32000)));
        var reopened = reducer.Reopen(id, new(0, 32000));
        Assert.Equal(2, reopened.Key.Generation);
        Assert.Equal(SubtitlePhase.Revised, reopened.Phase);
        Assert.False(reducer.AcceptTranslation(translation, "旧译文", true));
        Assert.Equal("stale_segment_generation", reducer.LastRejection);
    }

    /// <summary>拆分完整原子提交；非法范围完全不改状态，撤销身份不复活。</summary>
    [Fact]
    public void SplitIsAtomicAndOldIdentityCannotReturn()
    {
        var reducer = NewReducer();
        var id = Guid.NewGuid();
        reducer.Add(id, new(0, 32000));
        var late = Request(reducer, id, PipelineRequestKind.Recognition, 1);
        Assert.True(reducer.Register(late));
        var version = reducer.StateSequence;
        Assert.Throws<InvalidOperationException>(() => reducer.Replace([id], [new(Guid.NewGuid(), new(1, 32000), "坏范围")]));
        Assert.Equal(version, reducer.StateSequence);
        reducer.Replace([id], [new(Guid.NewGuid(), new(0, 16000), "はい"), new(Guid.NewGuid(), new(16000, 32000), "はい")]);
        var snapshot = reducer.Snapshot();
        Assert.Equal(version + 1, snapshot.StateSequence);
        Assert.Equal(2, snapshot.Segments.Length);
        Assert.All(snapshot.Segments, x => Assert.Equal("はい", x.Source));
        Assert.Contains(id, snapshot.RetiredIds);
        Assert.False(reducer.AcceptSource(late, "はい", true, BoundaryDecision.Pending));
        Assert.Equal("retired_segment", reducer.LastRejection);
    }

    /// <summary>容量不足必须在原子变更前失败，不能丢失当前字幕。</summary>
    [Fact]
    public void RetiredCapacityDoesNotPartiallyApplyReplacement()
    {
        var reducer = new SubtitleReducer(new(Guid.NewGuid(), 1, 1, 1), maximumRetired: 1);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        reducer.Add(first, new(0, 16000));
        reducer.Replace([first], [new(second, new(0, 16000), "はい")]);
        var version = reducer.StateSequence;
        Assert.Throws<InvalidOperationException>(() => reducer.Replace([second], [new(Guid.NewGuid(), new(0, 16000), "はい")]));
        Assert.Equal(version, reducer.StateSequence);
        Assert.Equal(second, Assert.Single(reducer.Snapshot().Segments).Key.Id);
    }

    /// <summary>增长窗口只确认旧范围，配置切换不会暗中重开已经确认的定稿。</summary>
    [Fact]
    public void ConfirmedPrefixKeepsRangeAndFinalAcrossConfiguration()
    {
        var reducer = NewReducer(); var id = Guid.NewGuid();
        reducer.Add(id, new(0, 1));
        Source(reducer, id, 1, 16000, "行きません。");
        var translation = Request(reducer, id, PipelineRequestKind.Translation, 1);
        Assert.True(reducer.Register(translation));
        Assert.True(reducer.AcceptTranslation(translation, "不去。", true));
        var evidence = Request(reducer, id, PipelineRequestKind.Recognition, 2) with { Range = new(0, 32000) };
        Assert.True(reducer.Register(evidence));
        Assert.True(reducer.ConfirmBoundary(evidence, "行きません。", new(true, "stable_complete_prefix", 2, true)));
        var final = Assert.Single(reducer.Snapshot().Segments);
        Assert.Equal(new SampleRange(0, 16000), final.Range);
        Assert.Equal(1, final.SourceRevision);
        Assert.Equal(SubtitlePhase.Final, final.Phase);
        reducer.AdvanceIdentity(reducer.Identity with { ConfigEpoch = 2, WorkerEpoch = 2 });
        Assert.Equal(final, Assert.Single(reducer.Snapshot().Segments));
        Assert.False(reducer.AcceptSource(evidence, "迟到内容", true, BoundaryDecision.Pending));
    }

    /// <summary>无文本身份撤销后不能复活，含正文或译文的块不能静默撤销。</summary>
    [Fact]
    public void RetireEmptyPreservesMeaningfulSegmentsAndTombstones()
    {
        var reducer = NewReducer(); var id = Guid.NewGuid();
        reducer.Add(id, new(0, 16000));
        var request = Request(reducer, id, PipelineRequestKind.Recognition, 1);
        Assert.True(reducer.Register(request));
        reducer.RetireEmpty(id);
        Assert.False(reducer.AcceptSource(request, "迟到", true, BoundaryDecision.Pending));
        Assert.Equal("retired_segment", reducer.LastRejection);
        var spoken = Guid.NewGuid(); reducer.Add(spoken, new(0, 16000));
        Source(reducer, spoken, 2, 16000, "はい");
        Assert.Throws<InvalidOperationException>(() => reducer.RetireEmpty(spoken));
    }

    /// <summary>创建独立会话避免不同测试之间共享状态。</summary>
    /// <returns>使用首代身份的归约器。</returns>
    private static SubtitleReducer NewReducer() => new(new(Guid.NewGuid(), 1, 1, 1));

    /// <summary>从当前块快照构造完整调用票据。</summary>
    /// <param name="reducer">当前归约器。</param><param name="id">目标块。</param><param name="kind">调用种类。</param>
    /// <param name="sequence">同块调用序号。</param><returns>尚未登记的调用。</returns>
    private static PipelineRequest Request(SubtitleReducer reducer, Guid id, PipelineRequestKind kind, long sequence)
    {
        var segment = reducer.Snapshot().Segments.Single(x => x.Key.Id == id);
        return new(Guid.NewGuid(), sequence, kind, reducer.Identity, segment.Key, segment.Range, segment.SourceRevision);
    }

    /// <summary>提交一个完整增长假设作为场景准备。</summary>
    /// <param name="reducer">当前归约器。</param><param name="id">目标块。</param><param name="sequence">调用序号。</param>
    /// <param name="end">增长窗口终点。</param><param name="text">原始源文。</param><param name="boundary">可选边界证据。</param>
    private static void Source(SubtitleReducer reducer, Guid id, long sequence, long end, string text, BoundaryDecision? boundary = null)
    {
        var request = Request(reducer, id, PipelineRequestKind.Recognition, sequence) with { Range = new(0, end) };
        Assert.True(reducer.Register(request));
        Assert.True(reducer.AcceptSource(request, text, true, boundary ?? BoundaryDecision.Pending));
    }
}
