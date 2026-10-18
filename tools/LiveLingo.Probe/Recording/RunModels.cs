using System.Text.Json.Serialization;
using LiveLingo.Core.Audio;
using LiveLingo.Core.Sessions;
using LiveLingo.Platform.Windows.Processes;
using LiveLingo.Runtime.Diagnostics;
using LiveLingo.Runtime.Pipeline;

namespace LiveLingo.Probe.Recording;

/// <summary>记录启动时的实际环境和配置，不含进程命令行或音频正文。</summary>
/// <param name="SchemaVersion">记录版本。</param><param name="RunId">会话身份。</param><param name="StartedUtc">开始时间。</param>
/// <param name="Target">所选实例。</param><param name="Scope">范围说明。</param><param name="Policy">实际限额。</param>
/// <param name="RequestedFormat">已请求格式。</param><param name="StopwatchFrequency">时钟频率。</param>
/// <param name="OperatingSystem">操作系统。</param><param name="Runtime">实际运行时。</param>
/// <param name="SourceSha256">源码树指纹。</param><param name="EvidenceKind">真实采集或模拟分类。</param>
internal sealed record RunManifest(int SchemaVersion, Guid RunId, DateTimeOffset StartedUtc, TargetIdentity Target,
    string Scope, CapturePolicy Policy, AudioFormat RequestedFormat, long StopwatchFrequency,
    string OperatingSystem, string Runtime, string SourceSha256, string EvidenceKind);

/// <summary>记录一个 PCM 包从复制至落盘的时间和采样范围。</summary>
/// <param name="Epoch">代次。</param><param name="Sequence">包序号。</param><param name="FrameStart">起点。</param>
/// <param name="FrameCount">帧数。</param><param name="Flags">原生标志。</param><param name="DevicePositionFrames">设备位置。</param>
/// <param name="QpcPosition100ns">原生时间。</param><param name="AvailableTicks">可用时间。</param>
/// <param name="EnqueuedTicks">入队时间。</param><param name="DequeuedTicks">出队时间。</param>
/// <param name="WrittenTicks">落盘调用完成时间。</param><param name="CaptureLatencyMs">有效采集时延。</param>
/// <param name="QueuedFrames">观察到的排队帧数。</param>
internal sealed record PacketEvidence(long Epoch, long Sequence, long FrameStart, int FrameCount, PacketFlags Flags,
    ulong DevicePositionFrames, ulong QpcPosition100ns, long AvailableTicks, long EnqueuedTicks,
    long DequeuedTicks, long WrittenTicks, double? CaptureLatencyMs, long QueuedFrames);

/// <summary>记录本 Probe 的资源，不把外部应用内存记成本程序资源。</summary>
/// <param name="Utc">采样时间。</param><param name="PrivateBytes">私有字节。</param><param name="WorkingSetBytes">工作集。</param>
/// <param name="HandleCount">句柄数。</param><param name="Threads">线程数。</param><param name="QueuedFrames">排队帧数。</param>
internal sealed record ResourceEvidence(DateTimeOffset Utc, long PrivateBytes, long WorkingSetBytes, int HandleCount, int Threads, long QueuedFrames);

/// <summary>定义 JSONL 的有限形状，避免序列化 PCM 或原生对象。</summary>
/// <param name="Kind">记录类型。</param><param name="Diagnostic">会话诊断。</param>
/// <param name="Packet">音频包元数据。</param><param name="Resource">资源采样。</param>
internal sealed record RunEvent(string Kind, CaptureDiagnostic? Diagnostic = null, PacketEvidence? Packet = null, ResourceEvidence? Resource = null);

/// <summary>记录 WAV 完成状态和实际文件指纹。</summary>
/// <param name="FileName">文件名。</param><param name="Epoch">代次。</param><param name="Frames">帧数。</param>
/// <param name="Bytes">PCM 长度。</param><param name="Sha256">完整文件哈希。</param><param name="HeaderFinalized">头部是否完成。</param>
internal sealed record WaveEvidence(string FileName, long Epoch, long Frames, long Bytes, string Sha256, bool HeaderFinalized);

/// <summary>有界统计后的报告，未操作的现场场景保持未执行。</summary>
/// <param name="SchemaVersion">版本。</param><param name="RunId">身份。</param><param name="CompletedUtc">结束时间。</param>
/// <param name="ExitCode">退出状态。</param><param name="Error">失败详情。</param><param name="Packets">包数。</param>
/// <param name="Frames">帧数。</param><param name="ValidTimestampSamples">有效时间戳数。</param>
/// <param name="InvalidTimestampSamples">无效时间戳数。</param><param name="CaptureP50Ms">采集 P50。</param>
/// <param name="CaptureP95Ms">采集 P95。</param><param name="QueueP95Ms">排队 P95。</param>
/// <param name="WriteP95Ms">写入 P95。</param><param name="QuantileResolutionMs">分位桶精度。</param>
/// <param name="MaxQueuedFrames">最大排队量。</param><param name="GapCount">明确中断数。</param>
/// <param name="ResourcesFirst">首次资源。</param><param name="ResourcesLast">末次资源。</param>
/// <param name="ResourcesMaxPrivateBytes">最大私有内存。</param><param name="ResourcesMaxHandles">最大句柄。</param>
/// <param name="Waves">文件证据。</param><param name="CleanupCompleted">正常清理完成。</param>
/// <param name="UnexecutedScenarios">未执行的现场验证。</param>
internal sealed record CaptureRunReport(int SchemaVersion, Guid RunId, DateTimeOffset CompletedUtc, int ExitCode, string? Error,
    long Packets, long Frames, long ValidTimestampSamples, long InvalidTimestampSamples,
    double? CaptureP50Ms, double? CaptureP95Ms, double? QueueP95Ms, double? WriteP95Ms, double QuantileResolutionMs,
    long MaxQueuedFrames, long GapCount, ResourceEvidence? ResourcesFirst, ResourceEvidence? ResourcesLast,
    long ResourcesMaxPrivateBytes, int ResourcesMaxHandles, WaveEvidence[] Waves, bool CleanupCompleted, string[] UnexecutedScenarios)
{
    /// <summary>获取成功 Initialize 后的固定 PCM 格式；从未就绪时为空。</summary>
    public AudioFormat? InitializedFormat { get; init; }
    /// <summary>获取中断至下一次 Ready 的完整恢复样本数。</summary>
    public long RecoverySamples { get; init; }
    /// <summary>获取恢复耗时 P50；没有样本或分位超过 60 秒时为空。</summary>
    public double? RecoveryP50Ms { get; init; }
    /// <summary>获取恢复耗时 P95；没有样本或分位超过 60 秒时为空。</summary>
    public double? RecoveryP95Ms { get; init; }
    /// <summary>获取运行结束时仍未恢复的中断次数。</summary>
    public int IncompleteRecoveries { get; init; }
    /// <summary>获取超过 60 秒统计桶上限的样本总数，避免把超限值压成 60 秒。</summary>
    public long HistogramOverflowSamples { get; init; }
}

/// <summary>在编译期生成固定诊断协议的 JSON 元数据。</summary>
[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(RunManifest))]
[JsonSerializable(typeof(RunEvent))]
[JsonSerializable(typeof(CaptureRunReport))]
[JsonSerializable(typeof(TargetQuery[]))]
[JsonSerializable(typeof(LifecycleReport))]
internal partial class ProbeJsonContext : JsonSerializerContext;
