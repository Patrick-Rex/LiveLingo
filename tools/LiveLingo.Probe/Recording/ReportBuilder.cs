using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiveLingo.Runtime.Diagnostics;

namespace LiveLingo.Probe.Recording;

/// <summary>流式重放元数据，以固定内存统计分位和资源趋势。</summary>
internal static class ReportBuilder
{
    /// <summary>从实际事件生成报告，强杀未完成运行保持清理失败。</summary>
    /// <param name="directory">运行目录。</param><param name="exitCode">退出码。</param>
    /// <param name="error">错误。</param><param name="waves">录音证据。</param><param name="peakFrames">入口最大帧数。</param>
    /// <returns>可重放报告。</returns>
    public static CaptureRunReport Build(string directory, int exitCode, string? error, WaveEvidence[] waves, long peakFrames)
    {
        var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(directory, "manifest.json")), ProbeJsonContext.Default.RunManifest)!;
        if (manifest.StopwatchFrequency <= 0) throw new InvalidDataException("录制清单中的单调时钟频率必须为正数。");
        var capture = new Histogram();
        var queue = new Histogram();
        var write = new Histogram();
        var recovery = new Histogram();
        long? interruptedAt = null;
        var initialized = false;
        long packets = 0, frames = 0, invalid = 0, gaps = 0, maxMemory = 0;
        int maxHandles = 0;
        ResourceEvidence? first = null, last = null;
        var cleaned = false;
        using var input = File.OpenText(Path.Combine(directory, "events.jsonl"));
        while (input.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            RunEvent? item;
            try { item = JsonSerializer.Deserialize(line, ProbeJsonContext.Default.RunEvent); }
            catch (JsonException) { error = $"{error} JSONL 含不完整记录。"; exitCode = 5; break; }
            if (item?.Packet is { } packet)
            {
                packets++;
                frames += packet.FrameCount;
                if (packet.CaptureLatencyMs is { } valid) capture.Add(valid); else invalid++;
                queue.Add(ElapsedMs(manifest.StopwatchFrequency, packet.EnqueuedTicks, packet.DequeuedTicks));
                write.Add(ElapsedMs(manifest.StopwatchFrequency, packet.DequeuedTicks, packet.WrittenTicks));
                peakFrames = Math.Max(peakFrames, packet.QueuedFrames);
            }
            if (item?.Diagnostic?.Gap is not null) gaps++;
            if (item?.Diagnostic?.Kind == "cleaned") cleaned = true;
            if (item?.Diagnostic is { } diagnostic)
            {
                if (diagnostic.Kind is "CaptureFailed" or "TargetLost" or "Suspend") interruptedAt ??= diagnostic.Ticks;
                if (diagnostic.Kind == "Ready")
                {
                    initialized = true;
                    if (interruptedAt is { } start) recovery.Add(ElapsedMs(manifest.StopwatchFrequency, start, diagnostic.Ticks));
                    interruptedAt = null;
                }
                if (diagnostic.State == LiveLingo.Core.Sessions.SessionState.Faulted && diagnostic.Detail is not null)
                    error ??= diagnostic.Detail;
            }
            if (item?.Resource is { } resource)
            {
                first ??= resource;
                last = resource;
                maxMemory = Math.Max(maxMemory, resource.PrivateBytes);
                maxHandles = Math.Max(maxHandles, resource.HandleCount);
                peakFrames = Math.Max(peakFrames, resource.QueuedFrames);
            }
        }
        if (exitCode != 0) error ??= "运行失败；请结合退出码与 events.jsonl 检查，诊断介质可能不可写。";
        return new(1, manifest.RunId, DateTimeOffset.UtcNow, exitCode, error, packets, frames, capture.Count, invalid,
            capture.Quantile(0.50), capture.Quantile(0.95), queue.Quantile(0.95), write.Quantile(0.95), 1,
            peakFrames, gaps, first, last, maxMemory, maxHandles, waves, cleaned && exitCode != 5,
            ["内容核验需独立 spectrum.json 或现场记录", "多标签页与子进程范围", "真实设备切换/拔插", "真实 Edge 重启", "真实睡眠恢复", "两小时代表性播放"])
        {
            InitializedFormat = initialized ? manifest.RequestedFormat : null,
            RecoverySamples = recovery.Count,
            RecoveryP50Ms = recovery.Quantile(0.5),
            RecoveryP95Ms = recovery.Quantile(0.95),
            IncompleteRecoveries = interruptedAt.HasValue ? 1 : 0,
            HistogramOverflowSamples = capture.Overflow + queue.Overflow + write.Overflow + recovery.Overflow
        };
    }

    /// <summary>计算当前源码及锁文件指纹，发布目录无源码时使用 Probe 二进制。</summary>
    /// <returns>带范围标签的 SHA-256。</returns>
    public static string SourceFingerprint()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "LiveLingo.slnx"))) root = root.Parent;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (root is null)
        {
            foreach (var binary in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Order(StringComparer.Ordinal))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(binary) + "\n"));
                using var input = File.OpenRead(binary);
                hash.AppendData(SHA256.HashData(input));
            }
            return "published-assemblies:" + Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        var paths = new[] { "src", "tools", "tests" }.SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root.FullName, folder), "*", SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj") &&
                (path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".csproj", StringComparison.Ordinal) || path.EndsWith("packages.lock.json", StringComparison.Ordinal)))
            .Concat(new[] { "global.json", "Directory.Build.props", "Directory.Packages.props", "nuget.config", "LiveLingo.slnx" }.Select(path => Path.Combine(root.FullName, path)))
            .Order(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root.FullName, path).Replace('\\', '/') + "\n"));
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(path)));
        }
        return "source-tree:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>固定 1ms 桶精度的有界统计。</summary>
    private sealed class Histogram
    {
        private readonly long[] buckets = new long[60001];
        /// <summary>获取有效样本数。</summary>
        internal long Count { get; private set; }
        /// <summary>获取超出有限统计桶的样本数。</summary>
        internal long Overflow { get; private set; }
        /// <summary>计入非负测量。</summary><param name="milliseconds">测量值。</param>
        internal void Add(double milliseconds)
        {
            if (!double.IsFinite(milliseconds) || milliseconds < 0) return;
            if (milliseconds > 60000) Overflow++;
            else buckets[(int)Math.Ceiling(milliseconds)]++;
            Count++;
        }
        /// <summary>返回向上取整分位桶，零样本为空。</summary><param name="fraction">分位。</param><returns>桶上界。</returns>
        internal double? Quantile(double fraction)
        {
            if (Count == 0) return null;
            var rank = (long)Math.Ceiling(Count * fraction);
            long cumulative = 0;
            for (var index = 0; index < buckets.Length; index++)
            {
                cumulative += buckets[index];
                if (cumulative >= rank) return index;
            }
            return null;
        }
    }

    /// <summary>以录制机器的频率解释持久化 tick，避免换机重放改变报告。</summary>
    /// <param name="start">起点 tick。</param><param name="end">终点 tick。</param><returns>录制时钟的毫秒差。</returns>
    /// <param name="frequency">录制清单中的单调时钟频率。</param>
    private static double ElapsedMs(long frequency, long start, long end) => ((double)end - start) * 1000 / frequency;
}
