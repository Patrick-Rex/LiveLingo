using System.Collections.Immutable;
using LiveLingo.Core.Audio;

namespace LiveLingo.Core.Subtitles;

/// <summary>将结果绑定到会话及三个独立失效代次。</summary>
/// <param name="SessionId">不复用的会话身份。</param><param name="CaptureEpoch">连续采集代次。</param>
/// <param name="WorkerEpoch">识别进程代次。</param><param name="ConfigEpoch">模型及语言配置代次。</param>
public sealed record PipelineIdentity(Guid SessionId, long CaptureEpoch, long WorkerEpoch, long ConfigEpoch)
{
    /// <summary>拒绝缺失会话或非正代次。</summary>
    /// <exception cref="ArgumentException">会话身份为空。</exception>
    /// <exception cref="ArgumentOutOfRangeException">代次非正。</exception>
    public void Validate()
    {
        if (SessionId == Guid.Empty) throw new ArgumentException("会话身份不能为空。");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CaptureEpoch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(WorkerEpoch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ConfigEpoch);
    }
}

/// <summary>标识同一个语义块的可撤销代次。</summary>
/// <param name="Id">语义块稳定身份。</param><param name="Generation">显式重开产生的新代次。</param>
public readonly record struct SegmentKey(Guid Id, long Generation);

/// <summary>区分识别和翻译的独立请求序列。</summary>
public enum PipelineRequestKind
{
    /// <summary>带音频来源的识别假设。</summary>
    Recognition,
    /// <summary>完整源版本的翻译。</summary>
    Translation
}

/// <summary>保存登记调用的不可变关联字段，返回结果必须逐项匹配。</summary>
/// <param name="Id">一次调用的身份。</param><param name="Sequence">同一块同类调用的递增序号。</param>
/// <param name="Kind">调用种类。</param><param name="Identity">会话和代次。</param>
/// <param name="Segment">语义块及代次。</param><param name="Range">完整登记音频范围。</param>
/// <param name="SourceRevision">翻译所需的精确源版本；识别仅记录发起时源版本。</param>
public sealed record PipelineRequest(Guid Id, long Sequence, PipelineRequestKind Kind,
    PipelineIdentity Identity, SegmentKey Segment, SampleRange Range, long SourceRevision);

/// <summary>描述字幕的可修订阶段，不表示模型绝对正确。</summary>
public enum SubtitlePhase
{
    /// <summary>第一次可见假设。</summary>
    Draft,
    /// <summary>已有假设正在被修订。</summary>
    Revised,
    /// <summary>边界与对应翻译满足本次提交条件。</summary>
    Final
}

/// <summary>使原文与译文的更新状态可见。</summary>
public enum TranslationState
{
    /// <summary>暂无需要翻译的源文。</summary>
    None,
    /// <summary>当前源版本等待译文。</summary>
    Pending,
    /// <summary>当前源版本已有完整译文。</summary>
    Complete,
    /// <summary>当前源版本翻译失败，原因另列。</summary>
    Failed
}

/// <summary>保存一次边界判断的证据，正常生成终止本身不构成闭合。</summary>
/// <param name="CanClose">语义、稳定性及右侧证据是否共同成立。</param>
/// <param name="Reason">结构化原因码。</param><param name="StableObservations">一致观察次数。</param>
/// <param name="HasRightContext">是否具有右侧音频证据。</param>
public sealed record BoundaryDecision(bool CanClose, string Reason, int StableObservations, bool HasRightContext)
{
    /// <summary>获取尚未获得充分证据的默认判断。</summary>
    public static BoundaryDecision Pending { get; } = new(false, "insufficient_evidence", 0, false);
}

/// <summary>保存一个完整语义块的源文、译文及精确版本关系。</summary>
/// <param name="Key">可撤销身份。</param><param name="Range">本块音频范围。</param>
/// <param name="SourceRevision">当前源版本。</param><param name="SubtitleRevision">可见内容版本。</param>
/// <param name="Source">完整源文，保留真实重复。</param><param name="Translation">最近译文。</param>
/// <param name="TranslatedSourceRevision">最近译文对应源版本。</param><param name="Phase">修订阶段。</param>
/// <param name="TranslationState">当前源版本的翻译状态。</param><param name="Error">当前失败原因。</param>
/// <param name="Boundary">本次来源边界证据。</param>
public sealed record SubtitleSegment(SegmentKey Key, SampleRange Range, long SourceRevision,
    long SubtitleRevision, string Source, string? Translation, long? TranslatedSourceRevision,
    SubtitlePhase Phase, TranslationState TranslationState, string? Error, BoundaryDecision Boundary);

/// <summary>供消费者按单调版本替换的完整权威快照。</summary>
/// <param name="Identity">当前身份。</param><param name="StateSequence">单调状态序号。</param>
/// <param name="Stopped">全局停止屏障。</param><param name="Segments">全部有效块的不可变列表。</param>
/// <param name="RetiredIds">已经撤销且不得复活的身份。</param>
public sealed record SubtitleSnapshot(PipelineIdentity Identity, long StateSequence, bool Stopped,
    ImmutableArray<SubtitleSegment> Segments, ImmutableHashSet<Guid> RetiredIds);

/// <summary>为原子拆分或合并提供新块的完整源内容。</summary>
/// <param name="Id">从未使用过的新块身份。</param><param name="Range">新块来源范围。</param>
/// <param name="Source">新块完整原文。</param>
public sealed record SegmentReplacement(Guid Id, SampleRange Range, string Source);
