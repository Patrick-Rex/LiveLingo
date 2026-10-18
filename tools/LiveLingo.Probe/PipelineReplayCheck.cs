using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveLingo.Core.Sessions;
using LiveLingo.Core.Subtitles;
using LiveLingo.Platform.Windows.Processes;
using LiveLingo.Probe.Recording;
using LiveLingo.Runtime.Asr;
using LiveLingo.Runtime.Pipeline;
using LiveLingo.Runtime.Storage;

namespace LiveLingo.Probe;

/// <summary>组合真实常驻模型、官方翻译图及有状态 WAV 回放，显式保存评测正文。</summary>
internal static class PipelineReplayCheck
{
    /// <summary>执行墙钟节奏回放，记录有限变更证据与最终完整状态，并核对所有自有身份。</summary>
    /// <param name="repository">项目绝对目录。</param><param name="profilePath">固定 ASR 配置。</param><param name="model">已有 Qwen 模型。</param>
    /// <param name="ollamaExecutable">已有 Ollama EXE。</param><param name="input">本地 PCM WAV。</param><param name="output">独立评测目录。</param>
    /// <param name="durationSeconds">零为文件一遍，正数显式循环至指定墙钟音频时长。</param>
    /// <param name="translationModel">明确选定的翻译模型。</param><param name="targetPid">非零时采集该外部应用进程树，input 不使用。</param>
    /// <param name="alignmentRecoverySeconds">显式扩窗实验上限，零禁用，60 或 120 秒。</param>
    /// <param name="preserveObservedRepetitions">是否启用扩窗已观察重复减少拒收实验。</param>
    /// <returns>运行和清理成功时为零，模型质量另看证据。</returns>
    public static async Task<int> RunAsync(string repository, string profilePath, string model, string ollamaExecutable, string input,
        string output, double durationSeconds, string translationModel, int targetPid = 0, int alignmentRecoverySeconds = 120, bool preserveObservedRepetitions = false)
    {
        foreach (var path in new[] { repository, profilePath, model, ollamaExecutable }.Concat(targetPid == 0 ? [input] : Array.Empty<string>()))
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("需要绝对路径。");
        if (durationSeconds < 0 || durationSeconds > 86400) throw new ArgumentException("回放时长必须位于 0–86400 秒。");
        if (targetPid < 0 || targetPid != 0 && durationSeconds == 0) throw new ArgumentException("采集需要正的目标 PID 和有限时长。");
        var target = targetPid == 0 ? null : WindowsTargets.Query(targetPid).Identity ?? throw new ArgumentException("无法核实采集目标。");
        var selection = new PipelineModelSelection(translationModel); selection.Validate();
        var options = new PipelineOptions { AlignmentRecoverySamples = checked(alignmentRecoverySeconds * 16000), PreserveObservedRepetitions = preserveObservedRepetitions };
        options.Validate();
        using var profile = JsonDocument.Parse(File.ReadAllBytes(profilePath));
        var profileRoot = profile.RootElement;
        var expected = new WorkerInformation(profileRoot.GetProperty("python").GetString()!, profileRoot.GetProperty("torch").GetString()!,
            profileRoot.GetProperty("cuda").GetString()!, profileRoot.GetProperty("modelRevision").GetString()!, "");
        var python = profileRoot.GetProperty("interpreter").GetString()!;
        var directory = OutputDirectory.Prepare(output);
        var sourceFingerprint = ReportBuilder.SourceFingerprint();
        var binaryFingerprint = BinaryFingerprint();
        var provenance = new PipelineProvenance(expected, Hash(profilePath), Hash(Path.Combine(Path.GetDirectoryName(profilePath)!, "requirements.lock")),
            Directory.EnumerateFiles(Path.Combine(repository, "workers", "qwen_asr"), "*.py").Order(StringComparer.Ordinal)
                .ToDictionary(path => Path.GetFileName(path), Hash, StringComparer.Ordinal));
        var owned = new List<OwnedWorkerConnection>();
        var external = WindowsTargets.Enumerate().Where(x => x.IsApplicationRoot && x.Identity is not null &&
            (x.Name.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase) || x.Name.Equals("ollama.exe", StringComparison.OrdinalIgnoreCase)))
            .Select(x => x.Identity!).ToArray();
        var spillDirectories = new List<string>();
        var session = Guid.NewGuid();
        int port;
        using (var reservation = new TcpListener(IPAddress.Loopback, 0)) { reservation.Start(); port = ((IPEndPoint)reservation.LocalEndpoint).Port; }
        var endpoint = new Uri($"http://127.0.0.1:{port}/");
        var started = Stopwatch.GetTimestamp();
        long activeStarted = 0, lastMetricTicks = 0, previousSubtitleState = -1;
        var revisions = new Dictionary<Guid, long>();
        var retired = new HashSet<Guid>();
        var firstTranslations = new HashSet<Guid>();
        var firstReadable = new List<double>();
        var firstFinals = new HashSet<Guid>();
        var finalLatency = new List<double>();
        var finalAfterRangeEnd = new List<double>();
        var status = "starting";
        using var changes = new StreamWriter(new FileStream(Path.Combine(directory, "changes.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false));
        using var metrics = new StreamWriter(new FileStream(Path.Combine(directory, "metrics.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false));
        using var operations = new StreamWriter(new FileStream(Path.Combine(directory, "operations.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false));
        using var rejections = new StreamWriter(new FileStream(Path.Combine(directory, "rejections.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false));
        using var captureEvents = new StreamWriter(new FileStream(Path.Combine(directory, "capture-events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false));
        var capture = target is null ? null : new LivePipelineSource(target, TimeSpan.FromSeconds(durationSeconds),
            diagnostic => WriteBounded(captureEvents, JsonSerializer.Serialize(new RunEvent("diagnostic", diagnostic), ProbeJsonContext.Default.RunEvent)));
        using var currentProcess = Process.GetCurrentProcess();
        PipelineGpuSampler? gpuSampler = null;
        var models = new LocalPipelineModels(epoch =>
        {
            var connection = new OwnedWorkerConnection(python,
                ["-u", "-m", "workers.qwen_asr.service", "--profile", profilePath, "--model", model, "--worker-epoch", epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                repository, "pipeline-qwen", new Dictionary<string, string> { ["LIVELINGO_OWNED_WORKER"] = "1", ["PYTHONUTF8"] = "1" });
            owned.Add(connection); return connection;
        }, () =>
        {
            var connection = new OwnedWorkerConnection(ollamaExecutable, ["serve"], repository, "pipeline-ollama", new Dictionary<string, string>
            {
                ["OLLAMA_HOST"] = $"127.0.0.1:{port}", ["OLLAMA_MODELS"] = @"D:\Soft Ware\Ollama", ["OLLAMA_CONTEXT_LENGTH"] = "4096",
                ["OLLAMA_NUM_PARALLEL"] = "1", ["OLLAMA_MAX_LOADED_MODELS"] = "1"
            });
            owned.Add(connection); return connection;
        }, endpoint, expected, options);
        ContinuousAudioStore? currentStore = null;
        var pipeline = new SubtitlePipeline(new(session, 1, 1, 1), selection, options, () =>
        {
            var path = Path.Combine(@"D:\Soft Ware\LiveLingo\data\pipeline-cache", session.ToString("N") + "-" + spillDirectories.Count);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            spillDirectories.Add(path);
            return currentStore = new ContinuousAudioStore(new FileAudioSpillStore(path), options.MemorySamples, options.SpillSamples, options.MaximumLeaseSamples);
        }, models, snapshot =>
        {
            var now = Stopwatch.GetTimestamp();
            if (activeStarted == 0 && snapshot.Status == "running") activeStarted = now;
            if (snapshot.Subtitles.StateSequence != previousSubtitleState || snapshot.Status != status)
            {
                var changed = snapshot.Subtitles.Segments.Where(x => !revisions.TryGetValue(x.Key.Id, out var old) || old != x.SubtitleRevision).ToArray();
                foreach (var segment in changed)
                {
                    revisions[segment.Key.Id] = segment.SubtitleRevision;
                    if (capture is null && segment.TranslationState == TranslationState.Complete && firstTranslations.Add(segment.Key.Id) && activeStarted != 0)
                        firstReadable.Add(Stopwatch.GetElapsedTime(activeStarted).TotalSeconds - segment.Range.Start / 16000.0);
                    if (capture is null && segment.Phase == SubtitlePhase.Final && firstFinals.Add(segment.Key.Id) && activeStarted != 0)
                    {
                        finalLatency.Add(Stopwatch.GetElapsedTime(activeStarted).TotalSeconds - segment.Range.Start / 16000.0);
                        finalAfterRangeEnd.Add(Stopwatch.GetElapsedTime(activeStarted).TotalSeconds - segment.Range.End / 16000.0);
                    }
                }
                var removed = snapshot.Subtitles.RetiredIds.Where(x => retired.Add(x)).ToArray();
                var change = new PipelineChange(snapshot.StateSequence, DateTimeOffset.UtcNow, snapshot.Status, changed, removed, snapshot.Subtitles.Identity);
                WriteBounded(changes, JsonSerializer.Serialize(change, PipelineCheckJsonContext.Default.PipelineChange));
                previousSubtitleState = snapshot.Subtitles.StateSequence; status = snapshot.Status;
            }
            if (Stopwatch.GetElapsedTime(lastMetricTicks, now) >= TimeSpan.FromSeconds(1) || snapshot.Subtitles.Stopped)
            {
                currentProcess.Refresh();
                foreach (var process in owned) process.ObserveMembers();
                var workerResources = SampleWorkers(owned);
                var metric = new PipelineMetric(DateTimeOffset.UtcNow, activeStarted == 0 ? 0 : Stopwatch.GetElapsedTime(activeStarted).TotalSeconds,
                    snapshot.AudioEnd, snapshot.RecognizedEnd, snapshot.BacklogSamples, snapshot.MemorySamples, snapshot.SpilledSamples, snapshot.Pins,
                    snapshot.ClosedQueueCount, snapshot.RecognitionCalls, snapshot.TranslationCalls, snapshot.LastComputeSeconds,
                    currentProcess.PrivateMemorySize64, currentProcess.WorkingSet64, currentProcess.HandleCount, workerResources);
                WriteBounded(metrics, JsonSerializer.Serialize(metric, PipelineCheckJsonContext.Default.PipelineMetric));
                lastMetricTicks = now;
            }
        }, operation => WriteBounded(operations, JsonSerializer.Serialize(operation, PipelineCheckJsonContext.Default.PipelineOperation)),
            rejection => WriteBounded(rejections, JsonSerializer.Serialize(rejection, PipelineCheckJsonContext.Default.RecognitionRejection)));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds((durationSeconds == 0 ? 86400 : durationSeconds) + 300));
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        using var watchEnd = new CancellationTokenSource();
        var stopWatcher = WatchStopRequestAsync(Path.Combine(directory, "stop.request"), stop, watchEnd.Token);
        PipelineSnapshot? final = null;
        string? error = null;
        try
        {
            var gpuConnection = new OwnedWorkerConnection(Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
                ["--id=0", "--query-gpu=memory.used,memory.total,utilization.gpu", "--format=csv,noheader,nounits", "--loop-ms=1000"], repository, "pipeline-gpu-sampler");
            owned.Add(gpuConnection);
            gpuSampler = new(gpuConnection, Path.Combine(directory, "gpu.jsonl"));
            var source = capture is null ? WaveReplaySource.ReadAsync(input, session, durationSeconds, realtime: true, stop.Token) : capture.ReadAsync(session, stop.Token);
            final = await pipeline.RunAsync(source, stop.Token).ConfigureAwait(false);
        }
        catch (Exception exception) { error = exception.GetType().Name + ": " + exception.Message; }
        finally
        {
            await watchEnd.CancelAsync().ConfigureAwait(false);
            await stopWatcher.ConfigureAwait(false);
            Console.CancelKeyPress -= cancel;
            try { if (gpuSampler is not null) await gpuSampler.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { error = (error ?? "") + "; gpu-cleanup:" + exception.GetType().Name; }
            try { await models.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { error = (error ?? "") + "; cleanup:" + exception.GetType().Name; }
            foreach (var process in owned) try { await process.DisposeAsync().ConfigureAwait(false); } catch (Exception exception) { error = (error ?? "") + "; process-cleanup:" + exception.GetType().Name; }
            try { currentStore?.Dispose(); } catch (Exception exception) { error = (error ?? "") + "; cache-cleanup:" + exception.GetType().Name; }
        }
        var observed = owned.SelectMany(x => x.ObserveMembers()).Distinct().ToArray();
        var remaining = observed.Where(x => WindowsTargets.Query(x.ProcessId).Identity is { } actual && x.SameInstance(actual)).ToArray();
        var temporary = spillDirectories.Where(Directory.Exists).ToArray();
        var passed = error is null && remaining.Length == 0 && temporary.Length == 0 && final is { Status: "completed" } && final.Gaps.Length == 0 &&
            (capture is null || capture.ExitCode == 0) && gpuSampler?.Summary is { Samples: > 0, Error: null };
        var report = new PipelineCheckReport(passed, false, error, selection, options, Stopwatch.GetElapsedTime(started).TotalSeconds,
            activeStarted == 0 ? 0 : Stopwatch.GetElapsedTime(started, activeStarted).TotalSeconds, durationSeconds,
            capture is null ? Hash(input) : null, firstReadable.ToArray(), observed, remaining, temporary,
            external, external.Where(x => WindowsTargets.Query(x.ProcessId).Identity is { } actual && x.SameInstance(actual)).ToArray(), final,
            capture is null ? null : new(capture.Target, capture.Policy, capture.ExitCode, capture.PeakFrames), gpuSampler?.Summary ?? new(0, 0, "not_started"),
            finalLatency.ToArray(), sourceFingerprint, binaryFingerprint, finalAfterRangeEnd.ToArray(), provenance);
        await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, PipelineCheckJsonContext.Default.PipelineCheckReport) + "\n").ConfigureAwait(false);
        Console.WriteLine($"PipelinePassed={passed}; Status={final?.Status}; ASR={final?.RecognitionCalls}; Translation={final?.TranslationCalls}; OwnedRemaining={remaining.Length}; TemporaryRemaining={temporary.Length}; Output={directory}");
        return passed ? 0 : 3;
    }

    /// <summary>为非交互终端提供正常取消入口，不依赖控制台信号传播。</summary>
    /// <param name="path">本次独占评测目录中的停止标记。</param><param name="stop">会话取消源。</param>
    /// <param name="cancellationToken">结束监视器。</param><returns>监视结束任务。</returns>
    internal static async Task WatchStopRequestAsync(string path, CancellationTokenSource stop, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                if (File.Exists(path)) { await stop.CancelAsync().ConfigureAwait(false); return; }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>限制显式证据文件，不能通过日志填满磁盘。</summary><param name="writer">独占日志。</param><param name="json">单条有限记录。</param>
    private static void WriteBounded(StreamWriter writer, string json)
    {
        if (writer.BaseStream.Length + System.Text.Encoding.UTF8.GetByteCount(json) + 1 > 64L * 1024 * 1024) throw new IOException("pipeline_evidence_capacity");
        writer.WriteLine(json); writer.Flush();
    }
    /// <summary>以固定缓冲计算实际回放输入的指纹。</summary><param name="path">本地输入。</param><returns>SHA-256。</returns>
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }

    /// <summary>指纹当前实际运行目录的 EXE/DLL，独立于可能已编辑但未重建的源码快照。</summary>
    /// <returns>按名称及内容计算的 SHA-256。</returns>
    internal static string BinaryFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory).Where(x => Path.GetExtension(x) is ".exe" or ".dll").Order(StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(path) + "\n"));
            using var stream = File.OpenRead(path); hash.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>只统计本次 Job 内仍匹配完整身份的进程，已退出进程不当成零值样本。</summary>
    /// <param name="owned">本次受监管连接。</param><returns>当前可查询自有进程资源。</returns>
    private static PipelineWorkerMetric[] SampleWorkers(List<OwnedWorkerConnection> owned)
    {
        var result = new List<PipelineWorkerMetric>();
        foreach (var identity in owned.SelectMany(x => x.ObserveMembers()).Distinct())
        {
            if (WindowsTargets.Query(identity.ProcessId).Identity is not { } actual || !identity.SameInstance(actual)) continue;
            try
            {
                using var process = Process.GetProcessById(identity.ProcessId);
                if (process.StartTime.ToUniversalTime() != identity.CreationTimeUtc.UtcDateTime) continue;
                result.Add(new(identity, process.PrivateMemorySize64, process.WorkingSet64, process.HandleCount));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return result.ToArray();
    }
}

/// <summary>显式评测的版本变更记录，避免反复保存完整历史造成二次方增长。</summary>
/// <param name="Sequence">全局状态序号。</param><param name="Utc">实际时间。</param><param name="Status">运行状态。</param>
/// <param name="Changed">本次变化块。</param><param name="Retired">新撤销身份。</param>
/// <param name="Identity">本次快照的会话和全部代次。</param>
internal sealed record PipelineChange(long Sequence, DateTimeOffset Utc, string Status, SubtitleSegment[] Changed, Guid[] Retired, PipelineIdentity Identity);
/// <summary>不含正文的持续运行指标。</summary>
/// <param name="Utc">实际时间。</param><param name="ElapsedSeconds">音频阶段墙钟。</param><param name="AudioEnd">输入采样终点。</param>
/// <param name="RecognizedEnd">覆盖终点。</param><param name="BacklogSamples">未覆盖采样。</param><param name="MemorySamples">缓存分配。</param>
/// <param name="SpilledSamples">磁盘采样。</param><param name="Pins">租约数。</param><param name="ClosedQueue">闭合队列。</param>
/// <param name="RecognitionCalls">ASR 次数。</param><param name="TranslationCalls">翻译次数。</param><param name="ComputeSeconds">最后计算耗时。</param>
/// <param name="PrivateBytes">宿主私有内存。</param><param name="WorkingSet">工作集。</param><param name="Handles">句柄。</param>
/// <param name="Workers">仍存活的自有进程资源。</param>
internal sealed record PipelineMetric(DateTimeOffset Utc, double ElapsedSeconds, long AudioEnd, long RecognizedEnd, long BacklogSamples,
    int MemorySamples, long SpilledSamples, int Pins, int ClosedQueue, long RecognitionCalls, long TranslationCalls, double ComputeSeconds,
    long PrivateBytes, long WorkingSet, int Handles, PipelineWorkerMetric[] Workers);
/// <summary>按完整身份记录实际存活 worker 的资源。</summary>
/// <param name="Identity">本次自有实例。</param><param name="PrivateBytes">私有字节。</param><param name="WorkingSet">工作集。</param><param name="Handles">句柄。</param>
internal sealed record PipelineWorkerMetric(TargetIdentity Identity, long PrivateBytes, long WorkingSet, int Handles);
/// <summary>真实回放及清理报告，质量判定与工程运行通过分开。</summary>
/// <param name="Passed">工程运行通过。</param><param name="QualityPassed">本工具不自行宣布质量全对。</param><param name="Error">异常摘要。</param>
/// <param name="Selection">实际模型选择。</param><param name="Options">实际预算。</param><param name="WallSeconds">完整墙钟时间。</param>
/// <param name="StartupSeconds">模型就绪耗时。</param><param name="RequestedDurationSeconds">显式回放时长。</param>
/// <param name="InputSha256">输入指纹。</param><param name="FirstReadableSeconds">各块第一次完整译文相对块起点耗时。</param>
/// <param name="Owned">自有实例。</param><param name="Remaining">仍存在的自有实例。</param><param name="TemporaryRemaining">未清理目录。</param>
/// <param name="ExternalBefore">原先外部应用。</param><param name="ExternalRetained">仍是同一实例的外部应用。</param><param name="Final">最终完整状态。</param>
/// <param name="Capture">实际进程树采集结果，回放为空。</param>
/// <param name="Gpu">独立整卡采样结果。</param>
/// <param name="FinalSeconds">回放中各定稿块从音频起点到首次 Final 的实际秒数。</param><param name="SourceSha256">启动时源码指纹。</param>
/// <param name="BinarySha256">实际宿主 EXE/DLL 指纹，不以当前源码代替已运行二进制。</param>
/// <param name="FinalAfterRangeEndSeconds">从该块已确认音频范围终点到首次定稿的秒数，不冒充词级语义终点。</param>
/// <param name="Provenance">本次实际配置及 worker 源码指纹。</param>
internal sealed record PipelineCheckReport(bool Passed, bool QualityPassed, string? Error, PipelineModelSelection Selection, PipelineOptions Options,
    double WallSeconds, double StartupSeconds, double RequestedDurationSeconds, string? InputSha256, double[] FirstReadableSeconds,
    TargetIdentity[] Owned, TargetIdentity[] Remaining, string[] TemporaryRemaining, TargetIdentity[] ExternalBefore, TargetIdentity[] ExternalRetained, PipelineSnapshot? Final,
    PipelineCaptureReport? Capture, PipelineGpuSummary Gpu, double[] FinalSeconds, string SourceSha256, string BinarySha256,
    double[] FinalAfterRangeEndSeconds, PipelineProvenance Provenance);
/// <summary>实际加载前记录的配置与 Python 文件证据，翻译模型 digest 随操作保存。</summary>
/// <param name="Asr">握手要求的版本与模型 revision。</param><param name="ProfileSha256">本次 profile 内容。</param>
/// <param name="LockSha256">对应隔离依赖锁内容。</param><param name="WorkerFiles">实际仓库 worker 文件名与内容 SHA-256。</param>
internal sealed record PipelineProvenance(WorkerInformation Asr, string ProfileSha256, string LockSha256, Dictionary<string, string> WorkerFiles);
/// <summary>实际采集入口的身份、限额和退出证据。</summary>
/// <param name="Target">外部应用实例。</param><param name="Policy">实际入口预算。</param><param name="ExitCode">协调器退出码。</param><param name="PeakFrames">原始队列峰值。</param>
internal sealed record PipelineCaptureReport(TargetIdentity Target, CapturePolicy Policy, int? ExitCode, long PeakFrames);
/// <summary>有限回放报告类型的编译生成元数据。</summary>
[JsonSerializable(typeof(PipelineChange))]
[JsonSerializable(typeof(PipelineMetric))]
[JsonSerializable(typeof(PipelineCheckReport))]
[JsonSerializable(typeof(ControlledPipelineReport))]
[JsonSerializable(typeof(PipelineOperation))]
[JsonSerializable(typeof(RecognitionRejection))]
internal partial class PipelineCheckJsonContext : JsonSerializerContext;
