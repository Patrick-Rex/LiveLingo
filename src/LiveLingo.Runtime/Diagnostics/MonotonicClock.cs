using System.Diagnostics;

namespace LiveLingo.Runtime.Diagnostics;

/// <summary>显式换算 WASAPI 100ns 时间与 Stopwatch 时间，拒绝无效测量。</summary>
public static class MonotonicClock
{
    /// <summary>换算同一 QPC 时钟域的采集延迟，不把原始 tick 当成 100ns。</summary>
    /// <param name="qpc100ns">WASAPI 提供的 100ns 时间。</param>
    /// <param name="availableTicks">复制前 Stopwatch 时间。</param>
    /// <param name="timestampValid">原生时间戳是否有效。</param>
    /// <returns>有效毫秒数；时钟异常或无效时间戳返回空。</returns>
    public static double? CaptureLatencyMs(ulong qpc100ns, long availableTicks, bool timestampValid)
    {
        if (!timestampValid || qpc100ns == 0) return null;
        var milliseconds = availableTicks * 1000.0 / Stopwatch.Frequency - qpc100ns / 10000.0;
        return double.IsFinite(milliseconds) && milliseconds is >= 0 and < 60000 ? milliseconds : null;
    }

    /// <summary>换算两个 Stopwatch 时间点之间的毫秒数。</summary>
    /// <param name="startTicks">起点。</param>
    /// <param name="endTicks">终点。</param>
    /// <returns>单调时间差。</returns>
    public static double ElapsedMs(long startTicks, long endTicks) => (endTicks - startTicks) * 1000.0 / Stopwatch.Frequency;
}
