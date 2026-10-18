using LiveLingo.Core.Audio;
using LiveLingo.Core.Sessions;

namespace LiveLingo.Runtime.Diagnostics;

/// <summary>传递不含音频正文的会话状态和恢复证据。</summary>
/// <param name="Kind">事件名称。</param><param name="Utc">UTC 时间。</param><param name="Ticks">Stopwatch 时间。</param>
/// <param name="State">当前状态。</param><param name="Epoch">当前代次。</param><param name="StateSequence">状态版本。</param>
/// <param name="Detail">原因或系统通知。</param><param name="Gap">可选显式中断。</param>
/// <param name="Target">归约后当前目标实例，用于核对重连身份。</param>
public sealed record CaptureDiagnostic(string Kind, DateTimeOffset Utc, long Ticks, SessionState State,
    long Epoch, long StateSequence, string? Detail = null, AudioGap? Gap = null, TargetIdentity? Target = null);
