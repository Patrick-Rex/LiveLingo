using System.Collections.Immutable;
using LiveLingo.Core.Audio;

namespace LiveLingo.Core.Subtitles;

/// <summary>在单写入者事件循环内维护字幕真相；取消任务不能替代身份校验。</summary>
public sealed class SubtitleReducer
{
    private readonly Dictionary<Guid, SubtitleSegment> segments = [];
    private readonly HashSet<Guid> retired = [];
    private readonly Dictionary<Guid, PipelineRequest> requests = [];
    private readonly Dictionary<(Guid, PipelineRequestKind), long> latestSequences = [];
    private readonly int maximumSegments;
    private readonly int maximumRetired;

    /// <summary>建立具有显式身份和历史容量的归约器。</summary>
    /// <param name="identity">本次有效会话。</param><param name="maximumSegments">有效块总上限。</param>
    /// <param name="maximumRetired">会话内撤销身份上限，达到时必须结束或恢复会话。</param>
    /// <exception cref="ArgumentOutOfRangeException">容量非正。</exception>
    public SubtitleReducer(PipelineIdentity identity, int maximumSegments = 4096, int maximumRetired = 8192)
    {
        identity.Validate();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSegments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRetired);
        Identity = identity;
        this.maximumSegments = maximumSegments;
        this.maximumRetired = maximumRetired;
    }

    /// <summary>获取当前有效身份。</summary>
    public PipelineIdentity Identity { get; private set; }
    /// <summary>获取已提交状态的单调序号。</summary>
    public long StateSequence { get; private set; }
    /// <summary>获取停止后不可逆的接收屏障。</summary>
    public bool Stopped { get; private set; }
    /// <summary>获取最近拒收原因；失败不修改字幕快照。</summary>
    public string? LastRejection { get; private set; }
    /// <summary>获取尚可接收的登记请求数。</summary>
    public int PendingRequests => requests.Count;

    /// <summary>建立空草稿锚点，不复用已撤销身份。</summary>
    /// <param name="id">新块身份。</param><param name="range">起始音频范围。</param>
    /// <returns>新建块的不可变快照。</returns>
    /// <exception cref="InvalidOperationException">停止、重复身份或达到容量。</exception>
    public SubtitleSegment Add(Guid id, SampleRange range)
    {
        if (Stopped || id == Guid.Empty || segments.ContainsKey(id) || retired.Contains(id) || segments.Count >= maximumSegments)
            throw new InvalidOperationException("不能建立该语义块或有效块容量已满。");
        var value = Create(id, range, "");
        segments.Add(id, value);
        Commit();
        return value;
    }

    /// <summary>登记新的调用并替代同块同类的旧待接收调用。</summary>
    /// <param name="request">完整调用关联字段。</param><returns>登记成功时为 true。</returns>
    public bool Register(PipelineRequest request)
    {
        if (!ValidateScope(request, out var segment)) return false;
        if (request.Id == Guid.Empty || request.Sequence <= 0 || request.Range.Length == 0 || !Enum.IsDefined(request.Kind))
            return Reject("invalid_request");
        if (requests.ContainsKey(request.Id)) return Reject("duplicate_request");
        if (request.Kind == PipelineRequestKind.Translation && request.SourceRevision != segment.SourceRevision)
            return Reject("stale_source_revision");
        if (request.Kind == PipelineRequestKind.Translation && (request.Range != segment.Range || string.IsNullOrWhiteSpace(segment.Source)))
            return Reject("invalid_translation_source");
        if (request.Kind == PipelineRequestKind.Recognition &&
            (request.Range.Start != segment.Range.Start || request.Range.End < segment.Range.End))
            return Reject("stale_coverage");
        var key = (request.Segment.Id, request.Kind);
        if (latestSequences.TryGetValue(key, out var previous) && request.Sequence <= previous)
            return Reject("superseded_request");
        foreach (var old in requests.Values.Where(x => x.Segment == request.Segment && x.Kind == request.Kind).ToArray()) requests.Remove(old.Id);
        requests.Add(request.Id, request);
        latestSequences[key] = request.Sequence;
        LastRejection = null;
        return true;
    }

    /// <summary>接收覆盖当前块的新假设，原文或归属改变时递增源版本；空原文同时清除过期译文关联。</summary>
    /// <param name="request">必须与登记一致的调用字段。</param><param name="source">完整原始转写。</param>
    /// <param name="complete">是否正常完整生成，截断结果不可提交。</param><param name="boundary">来源边界证据。</param>
    /// <returns>结果被接收时为 true。</returns>
    public bool AcceptSource(PipelineRequest request, string source, bool complete, BoundaryDecision boundary)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(boundary);
        if (request.Kind != PipelineRequestKind.Recognition) return Reject("wrong_request_kind");
        if (!ValidateResult(request, out var segment)) return false;
        requests.Remove(request.Id);
        if (!complete) return Reject("incomplete_generation");
        if (request.Range.End < segment.Range.End || request.Range.Start > segment.Range.Start) return Reject("stale_coverage");
        if (boundary.CanClose && (!boundary.HasRightContext || boundary.StableObservations < 2)) return Reject("invalid_boundary_evidence");
        var changed = source != segment.Source || request.Range != segment.Range;
        var revision = changed ? checked(segment.SourceRevision + 1) : segment.SourceRevision;
        var translationCurrent = !changed && segment.TranslatedSourceRevision == revision && segment.TranslationState == TranslationState.Complete;
        segments[segment.Key.Id] = segment with
        {
            Range = request.Range, Source = source, SourceRevision = revision,
            SubtitleRevision = checked(segment.SubtitleRevision + 1), Boundary = boundary,
            Phase = boundary.CanClose && translationCurrent ? SubtitlePhase.Final : segment.SourceRevision == 0 ? SubtitlePhase.Draft : SubtitlePhase.Revised,
            TranslationState = changed ? string.IsNullOrWhiteSpace(source) ? TranslationState.None : TranslationState.Pending : segment.TranslationState,
            Translation = string.IsNullOrWhiteSpace(source) ? null : segment.Translation,
            TranslatedSourceRevision = string.IsNullOrWhiteSpace(source) ? null : segment.TranslatedSourceRevision,
            Error = null
        };
        Commit();
        return true;
    }

    /// <summary>仅将当前完整源版本的有效译文提交为可见状态。</summary>
    /// <param name="request">登记翻译的全部关联字段。</param><param name="translation">模型完整原始译文。</param>
    /// <param name="complete">生成是否正常终止。</param><returns>当前译文被接受时为 true。</returns>
    public bool AcceptTranslation(PipelineRequest request, string translation, bool complete)
    {
        if (request.Kind != PipelineRequestKind.Translation) return Reject("wrong_request_kind");
        if (!ValidateResult(request, out var segment)) return false;
        requests.Remove(request.Id);
        if (!complete || string.IsNullOrWhiteSpace(translation))
        {
            SetTranslationFailure(segment, "incomplete_translation");
            return Reject("incomplete_translation");
        }
        segments[segment.Key.Id] = segment with
        {
            Translation = translation, TranslatedSourceRevision = segment.SourceRevision,
            SubtitleRevision = checked(segment.SubtitleRevision + 1), TranslationState = TranslationState.Complete,
            Phase = segment.Boundary.CanClose ? SubtitlePhase.Final : segment.Phase, Error = null
        };
        Commit();
        return true;
    }

    /// <summary>后续增长窗口确认旧完整前缀与既有右侧证据，不把新增语音错误归入旧块。</summary>
    /// <param name="evidence">覆盖旧块且包含新增音频的已登记识别请求。</param>
    /// <param name="stableSource">后续完整输出中逐字保留的旧源文。</param><param name="boundary">两次观察和右侧证据。</param>
    /// <returns>旧块边界已确认时为 true。</returns>
    public bool ConfirmBoundary(PipelineRequest evidence, string stableSource, BoundaryDecision boundary)
    {
        if (evidence.Kind != PipelineRequestKind.Recognition || !ValidateResult(evidence, out var segment)) return false;
        if (!boundary.CanClose || !boundary.HasRightContext || boundary.StableObservations < 2 || stableSource != segment.Source ||
            evidence.Range.Start != segment.Range.Start || evidence.Range.End <= segment.Range.End)
            return Reject("invalid_boundary_confirmation");
        requests.Remove(evidence.Id);
        var translated = segment.TranslationState == TranslationState.Complete && segment.TranslatedSourceRevision == segment.SourceRevision;
        segments[segment.Key.Id] = segment with { Boundary = boundary, Phase = translated ? SubtitlePhase.Final : segment.Phase,
            SubtitleRevision = checked(segment.SubtitleRevision + 1) };
        Commit();
        return true;
    }

    /// <summary>撤销已处理的无文本块；调用方必须保存无语音证据并继续保留修订尾部音频。</summary>
    /// <param name="id">无正文且没有译文的块。</param>
    public void RetireEmpty(Guid id)
    {
        if (Stopped || !segments.TryGetValue(id, out var segment) || segment.Source.Length != 0 || segment.Translation is not null || retired.Count >= maximumRetired)
            throw new InvalidOperationException("只能撤销有容量的空块。");
        CancelSegment(id); segments.Remove(id); retired.Add(id); Commit();
    }

    /// <summary>使当前翻译错误可见，保留源文和旧译文的版本标记。</summary>
    /// <param name="request">失败调用关联字段。</param><param name="reason">不含正文的错误原因码。</param>
    /// <returns>错误属于当前版本时为 true。</returns>
    public bool FailTranslation(PipelineRequest request, string reason)
    {
        if (request.Kind != PipelineRequestKind.Translation) return Reject("wrong_request_kind");
        if (!ValidateResult(request, out var segment)) return false;
        requests.Remove(request.Id);
        SetTranslationFailure(segment, reason);
        return true;
    }

    /// <summary>在音频仍保留时显式重开定稿，旧代次调用立即失效。</summary>
    /// <param name="id">待重开块。</param><param name="retainedAudio">当前确实保留的音频范围。</param>
    /// <returns>新代次的修订块。</returns>
    /// <exception cref="InvalidOperationException">停止、非定稿或音频已释放。</exception>
    public SubtitleSegment Reopen(Guid id, SampleRange retainedAudio)
    {
        if (Stopped || !segments.TryGetValue(id, out var value) || value.Phase != SubtitlePhase.Final || !retainedAudio.Contains(value.Range))
            throw new InvalidOperationException("仅保留音频内的定稿可显式重开。");
        CancelSegment(id);
        value = value with { Key = new(id, checked(value.Key.Generation + 1)), SourceRevision = checked(value.SourceRevision + 1),
            SubtitleRevision = checked(value.SubtitleRevision + 1), Phase = SubtitlePhase.Revised,
            TranslationState = TranslationState.Pending, Boundary = BoundaryDecision.Pending, Error = null };
        segments[id] = value;
        Commit();
        return value;
    }

    /// <summary>一次提交拆分或合并，先完整验证，再撤销旧身份并创建新块。</summary>
    /// <param name="oldIds">本次撤销的全部有效块。</param><param name="replacements">使用全新身份的替换块。</param>
    /// <exception cref="InvalidOperationException">输入不完整、容量不足或替换范围不守恒。</exception>
    public void Replace(IReadOnlyList<Guid> oldIds, IReadOnlyList<SegmentReplacement> replacements)
    {
        if (Stopped || oldIds.Count == 0 || replacements.Count == 0 || oldIds.Distinct().Count() != oldIds.Count ||
            oldIds.Any(x => !segments.ContainsKey(x)) || retired.Count + oldIds.Count > maximumRetired ||
            segments.Count - oldIds.Count + replacements.Count > maximumSegments ||
            replacements.Select(x => x.Id).Distinct().Count() != replacements.Count ||
            replacements.Any(x => x.Id == Guid.Empty || segments.ContainsKey(x.Id) || retired.Contains(x.Id) || x.Source is null))
            throw new InvalidOperationException("原子替换身份或容量无效。");
        var oldRanges = oldIds.Select(x => segments[x].Range).OrderBy(x => x.Start).ToArray();
        var newRanges = replacements.Select(x => x.Range).OrderBy(x => x.Start).ToArray();
        if (!Contiguous(oldRanges) || !Contiguous(newRanges) || oldRanges[0].Start != newRanges[0].Start || oldRanges[^1].End != newRanges[^1].End)
            throw new InvalidOperationException("替换必须保持完整连续音频范围。");
        foreach (var id in oldIds) { CancelSegment(id); segments.Remove(id); retired.Add(id); }
        foreach (var replacement in replacements) segments.Add(replacement.Id, Create(replacement.Id, replacement.Range, replacement.Source));
        Commit();
    }

    /// <summary>同会话递增代次；采集中断撤销旧块，配置变化使旧译文显式失效。</summary>
    /// <param name="identity">新的单调身份。</param>
    /// <exception cref="InvalidOperationException">会话改变、代次回退或撤销容量不足。</exception>
    public void AdvanceIdentity(PipelineIdentity identity)
    {
        identity.Validate();
        if (Stopped || identity.SessionId != Identity.SessionId || identity.CaptureEpoch < Identity.CaptureEpoch ||
            identity.WorkerEpoch < Identity.WorkerEpoch || identity.ConfigEpoch < Identity.ConfigEpoch)
            throw new InvalidOperationException("身份不能回退或跨会话复用。");
        if (identity.CaptureEpoch != Identity.CaptureEpoch)
        {
            if (retired.Count + segments.Count > maximumRetired) throw new InvalidOperationException("撤销历史容量已满。");
            retired.UnionWith(segments.Keys);
            segments.Clear();
            latestSequences.Clear();
        }
        else if (identity.ConfigEpoch != Identity.ConfigEpoch)
        {
            foreach (var value in segments.Values.Where(x => x.Phase != SubtitlePhase.Final).ToArray()) segments[value.Key.Id] = value with
            {
                TranslationState = string.IsNullOrWhiteSpace(value.Source) ? TranslationState.None : TranslationState.Pending,
                Phase = SubtitlePhase.Revised, SubtitleRevision = checked(value.SubtitleRevision + 1),
                Boundary = BoundaryDecision.Pending, Error = null
            };
        }
        requests.Clear();
        Identity = identity;
        Commit();
    }

    /// <summary>建立不可逆停止屏障并清空所有接收许可。</summary>
    public void Stop()
    {
        if (Stopped) return;
        Stopped = true;
        requests.Clear();
        Commit();
    }

    /// <summary>复制当前有效状态，后续归约不能改变已交付快照。</summary>
    /// <returns>按音频范围排序的完整不可变快照。</returns>
    public SubtitleSnapshot Snapshot() => new(Identity, StateSequence, Stopped,
        segments.Values.OrderBy(x => x.Range.Start).ThenBy(x => x.Key.Id).ToImmutableArray(), retired.ToImmutableHashSet());

    /// <summary>校验所有来源失效屏障，返回当前块。</summary>
    /// <param name="request">待校验调用。</param><param name="segment">有效时返回当前块。</param>
    /// <returns>调用仍属于当前有效块时为 true。</returns>
    private bool ValidateScope(PipelineRequest request, out SubtitleSegment segment)
    {
        segment = null!;
        if (Stopped) return Reject("stopped");
        if (request.Identity.SessionId != Identity.SessionId) return Reject("stale_session");
        if (request.Identity.CaptureEpoch != Identity.CaptureEpoch) return Reject("stale_capture_epoch");
        if (request.Identity.ConfigEpoch != Identity.ConfigEpoch) return Reject("stale_config_epoch");
        if (request.Identity.WorkerEpoch != Identity.WorkerEpoch) return Reject("stale_worker_epoch");
        if (retired.Contains(request.Segment.Id)) return Reject("retired_segment");
        if (!segments.TryGetValue(request.Segment.Id, out segment!)) return Reject("unknown_segment");
        if (request.Segment != segment.Key) return Reject("stale_segment_generation");
        if (segment.Phase == SubtitlePhase.Final) return Reject("final_requires_reopen");
        return true;
    }

    /// <summary>验证已登记结果及当前精确源版本。</summary>
    /// <param name="request">返回的全部关联字段。</param><param name="segment">当前块。</param>
    /// <returns>可提交结果时为 true。</returns>
    private bool ValidateResult(PipelineRequest request, out SubtitleSegment segment)
    {
        if (!ValidateScope(request, out segment)) return false;
        if (request.Kind == PipelineRequestKind.Translation && request.SourceRevision != segment.SourceRevision) return Reject("stale_source_revision");
        if (!requests.TryGetValue(request.Id, out var expected)) return Reject("unknown_or_superseded_request");
        if (request != expected) return Reject("request_metadata_mismatch");
        return true;
    }

    /// <summary>保留失败的源版本，并提交明确失败状态。</summary>
    /// <param name="segment">当前块。</param><param name="reason">结构化原因。</param>
    private void SetTranslationFailure(SubtitleSegment segment, string reason)
    {
        segments[segment.Key.Id] = segment with { TranslationState = TranslationState.Failed, Error = reason,
            SubtitleRevision = checked(segment.SubtitleRevision + 1) };
        Commit();
    }

    /// <summary>取消块的接收许可及局部序号，重开后由新代次隔离旧序号。</summary>
    /// <param name="id">需失效的语义块。</param>
    private void CancelSegment(Guid id)
    {
        foreach (var request in requests.Values.Where(x => x.Segment.Id == id).ToArray()) requests.Remove(request.Id);
        latestSequences.Remove((id, PipelineRequestKind.Recognition));
        latestSequences.Remove((id, PipelineRequestKind.Translation));
    }

    /// <summary>记录原因而不改变可见字幕版本。</summary>
    /// <param name="reason">拒收原因码。</param><returns>固定 false 供调用链返回。</returns>
    private bool Reject(string reason) { LastRejection = reason; return false; }

    /// <summary>提交单调状态版本并清除旧拒收诊断。</summary>
    private void Commit() { StateSequence = checked(StateSequence + 1); LastRejection = null; }

    /// <summary>建立一个尚未获得译文的新块。</summary>
    /// <param name="id">新身份。</param><param name="range">来源范围。</param><param name="source">初始原文。</param>
    /// <returns>新的不可变草稿。</returns>
    private static SubtitleSegment Create(Guid id, SampleRange range, string source) =>
        new(new(id, 1), range, string.IsNullOrEmpty(source) ? 0 : 1, 1, source, null, null, SubtitlePhase.Draft,
            string.IsNullOrWhiteSpace(source) ? TranslationState.None : TranslationState.Pending, null, BoundaryDecision.Pending);

    /// <summary>验证排序范围没有空洞、交叠或空片段。</summary>
    /// <param name="ranges">按起点排序的非空列表。</param><returns>完整连续时为 true。</returns>
    private static bool Contiguous(SampleRange[] ranges)
    {
        for (var index = 0; index < ranges.Length; index++)
            if (ranges[index].Length == 0 || index > 0 && ranges[index - 1].End != ranges[index].Start) return false;
        return true;
    }
}
