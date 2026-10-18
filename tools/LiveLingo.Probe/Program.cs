using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using LiveLingo.Core.Sessions;
using LiveLingo.Platform.Windows.Audio;
using LiveLingo.Platform.Windows.Power;
using LiveLingo.Platform.Windows.Processes;
using LiveLingo.Probe.Recording;
using LiveLingo.Runtime.Pipeline;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveLingo.Probe;

/// <summary>承载无窗口采集、恢复与报告入口，外部应用保持非自有。</summary>
internal static class Program
{
    /// <summary>分派显式命令，stdout 仅返回机器可读结果。</summary>
    /// <param name="args">命令参数。</param><returns>契约退出码。</returns>
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new ArgumentException("命令: targets --json | capture --pid N --output ABS (--duration-seconds N | --until-cancelled) | report --input ABS --json | lifecycle-check --output ABS");
            var options = ParseOptions(args[1..]);
            switch (args[0])
            {
                case "pipeline-controlled-capture":
                    ValidateOptions(options, ["repository", "profile", "model", "ollama", "edge", "input", "output", "duration-seconds"]);
                    return await ControlledPipelineCheck.RunAsync(Required(options, "repository"), Required(options, "profile"), Required(options, "model"),
                        Required(options, "ollama"), Required(options, "edge"), Required(options, "input"), Required(options, "output"),
                        checked((int)Number(options, "duration-seconds", 30))).ConfigureAwait(false);
                case "pipeline-capture":
                    ValidateOptions(options, ["repository", "profile", "model", "ollama", "pid", "output", "duration-seconds", "translation-model", "alignment-recovery-seconds", "preserve-observed-repetitions"]);
                    if (!int.TryParse(Required(options, "pid"), out var pipelinePid) || pipelinePid <= 0) throw new ArgumentException("需要正的目标 PID。");
                    return await PipelineReplayCheck.RunAsync(Required(options, "repository"), Required(options, "profile"), Required(options, "model"),
                        Required(options, "ollama"), "", Required(options, "output"), Number(options, "duration-seconds", 180),
                        options.GetValueOrDefault("translation-model") ?? "translategemma:12b", pipelinePid, RecoverySeconds(options), options.ContainsKey("preserve-observed-repetitions")).ConfigureAwait(false);
                case "pipeline-replay":
                    ValidateOptions(options, ["repository", "profile", "model", "ollama", "input", "output", "duration-seconds", "translation-model", "alignment-recovery-seconds", "preserve-observed-repetitions"]);
                    return await PipelineReplayCheck.RunAsync(Required(options, "repository"), Required(options, "profile"), Required(options, "model"),
                        Required(options, "ollama"), Required(options, "input"), Required(options, "output"), Number(options, "duration-seconds", 0),
                        options.GetValueOrDefault("translation-model") ?? "translategemma:12b", alignmentRecoverySeconds: RecoverySeconds(options),
                        preserveObservedRepetitions: options.ContainsKey("preserve-observed-repetitions")).ConfigureAwait(false);
                case "asr-worker-check":
                    ValidateOptions(options, ["repository", "profile", "model", "input", "output"]);
                    return await AsrWorkerCheck.RunAsync(Required(options, "repository"), Required(options, "profile"), Required(options, "model"),
                        Required(options, "input"), Required(options, "output")).ConfigureAwait(false);
                case "model-eval":
                    ValidateOptions(options, ["request", "output"]);
                    return await ModelEvaluationHost.RunAsync(Required(options, "request"), Required(options, "output")).ConfigureAwait(false);
                case "targets":
                    ValidateOptions(options, ["json"]);
                    Console.WriteLine(JsonSerializer.Serialize(WindowsTargets.Enumerate().ToArray(), ProbeJsonContext.Default.TargetQueryArray));
                    return 0;
                case "capture": return await CaptureAsync(options).ConfigureAwait(false);
                case "lifecycle-check":
                    ValidateOptions(options, ["output"]);
                    return await LifecycleRunner.RunAsync(Required(options, "output")).ConfigureAwait(false);
                case "controlled-audio-check":
                    ValidateOptions(options, ["edge", "fixtures", "output", "duration-seconds"]);
                    return await ControlledAudioCheck.RunAsync(Required(options, "edge"), Required(options, "fixtures"), Required(options, "output"),
                        checked((int)Number(options, "duration-seconds", 10))).ConfigureAwait(false);
                case "recovery-check":
                    ValidateOptions(options, ["edge", "fixtures", "output"]);
                    return await RecoveryCheck.RunAsync(Required(options, "edge"), Required(options, "fixtures"), Required(options, "output")).ConfigureAwait(false);
                case "recovery-stop-check":
                    ValidateOptions(options, ["edge", "fixtures", "output"]);
                    return await RecoveryCheck.RunAsync(Required(options, "edge"), Required(options, "fixtures"), Required(options, "output"), true).ConfigureAwait(false);
                case "scope-check":
                    ValidateOptions(options, ["edge", "fixtures", "output"]);
                    return await ScopeCheck.RunAsync(Required(options, "edge"), Required(options, "fixtures"), Required(options, "output")).ConfigureAwait(false);
                case "field-scope-check":
                    ValidateOptions(options, ["pid", "fixtures", "output"]);
                    if (!int.TryParse(Required(options, "pid"), out var fieldPid) || fieldPid <= 0) throw new ArgumentException("需要正的 Edge 根 PID。");
                    return await FieldScopeCheck.RunAsync(fieldPid, Required(options, "fixtures"), Required(options, "output")).ConfigureAwait(false);
                case "report":
                    ValidateOptions(options, ["input", "json"]);
                    var input = Required(options, "input");
                    if (!Path.IsPathFullyQualified(input)) throw new ArgumentException("报告输入必须为绝对目录。");
                    var reportPath = Path.Combine(input, "report.json");
                    var report = File.Exists(reportPath)
                        ? JsonSerializer.Deserialize(File.ReadAllBytes(reportPath), ProbeJsonContext.Default.CaptureRunReport)!
                        : ReportBuilder.Build(input, 5, "宿主未写最终报告；录音头部和清理未确认。", [], 0);
                    Console.WriteLine(JsonSerializer.Serialize(report, ProbeJsonContext.Default.CaptureRunReport));
                    return report.ExitCode;
                default: throw new ArgumentException($"未知命令: {args[0]}");
            }
        }
        catch (ArgumentException exception) { Console.Error.WriteLine(exception.Message); return 2; }
        catch (Exception exception) { Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}"); return 3; }
    }

    /// <summary>接通消费者、状态机与系统通知，并在所有路径清理。</summary>
    /// <param name="options">解析后参数。</param><param name="cancellationToken">夹具或调用者的停止信号。</param>
    /// <returns>采集或清理状态。</returns>
    internal static async Task<int> CaptureAsync(Dictionary<string, string?> options, CancellationToken cancellationToken = default)
    {
        ValidateOptions(options, ["pid", "output", "duration-seconds", "until-cancelled", "queue-seconds", "max-output-bytes", "consumer-delay-ms"]);
        if (!int.TryParse(Required(options, "pid"), out var pid) || pid <= 0) throw new ArgumentException("--pid 必须为正整数。");
        var timed = options.ContainsKey("duration-seconds");
        if (timed == options.ContainsKey("until-cancelled")) throw new ArgumentException("必须且只能选择 --duration-seconds 或 --until-cancelled。");
        var duration = timed ? Number(options, "duration-seconds", 0) : 0;
        if (timed && (duration <= 0 || duration > 86400)) throw new ArgumentException("时长必须在 (0, 86400] 秒。");
        var query = WindowsTargets.Query(pid);
        var target = query.Identity ?? throw new ArgumentException($"无法核实目标: {query.Error}");
        var policy = new CapturePolicy
        {
            QueueAudioSeconds = Number(options, "queue-seconds", 2),
            MaxOutputBytes = checked((long)Number(options, "max-output-bytes", 2L * 1024 * 1024 * 1024)),
            ConsumerDelay = TimeSpan.FromMilliseconds(Number(options, "consumer-delay-ms", 0))
        };
        policy.Validate();
        var directory = OutputDirectory.Prepare(Required(options, "output"));
        var id = Guid.NewGuid();
        var manifest = new RunManifest(1, id, DateTimeOffset.UtcNow, target,
            "包含所选根进程及其子孙进程；浏览器可能包含多个标签页，不能按标签页隔离。", policy,
            WindowsLoopbackCapture.RequestedFormat, Stopwatch.Frequency, RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription, ReportBuilder.SourceFingerprint(), "hardware-capture; content validation is separate");
        using var host = Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders()).Build();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timed) stop.CancelAfter(TimeSpan.FromSeconds(duration));
        ConsoleCancelEventHandler onCancel = (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += onCancel;
        using var ingress = new AudioIngress(policy, WindowsLoopbackCapture.RequestedFormat);
        using var consumerStop = new CancellationTokenSource();
        using var resourceStop = new CancellationTokenSource();
        var recorder = new RunRecorder(directory, manifest);
        var excluded = WindowsTargets.Enumerate().Where(x => x.IsApplicationRoot && x.Identity is not null &&
            !target.SameInstance(x.Identity)).Select(x => x.Identity!).ToHashSet();
        var coordinator = new CaptureCoordinator(new WindowsLoopbackCapture(), policy, ingress,
            identity => WindowsTargets.Query(identity.ProcessId).Identity is { } current && identity.SameInstance(current),
            identity => WindowsTargets.UniqueMatch(identity, WindowsTargets.Enumerate(), excluded),
            diagnostic => recorder.WriteEvent(new("diagnostic", diagnostic)));
        var consumer = recorder.ConsumeAsync(ingress, consumerStop.Token);
        var consumerObserver = ObserveConsumerAsync(consumer, coordinator);
        var resources = SampleResourcesAsync(recorder, ingress, resourceStop.Token);
        var resourceObserver = ObserveConsumerAsync(resources, coordinator);
        int exitCode = 0;
        string? error = null;
        DeviceNotifications? devices = null;
        PowerNotifications? power = null;
        try
        {
            devices = new(detail => coordinator.Post(new(SessionEventKind.DeviceChanged, Detail: detail)));
            power = new(suspended => coordinator.Post(new(suspended ? SessionEventKind.Suspend : SessionEventKind.Resume)));
            exitCode = await coordinator.RunAsync(target, id, stop.Token).ConfigureAwait(false);
        }
        catch (Exception exception) { exitCode = 3; error = $"{exception.GetType().Name}: {exception.Message}"; }
        finally
        {
            stop.Cancel();
            ingress.Complete();
            try { power?.Dispose(); } catch (Exception exception) { exitCode = 5; error = exception.Message; }
            try { devices?.Dispose(); } catch (Exception exception) { exitCode = 5; error = exception.Message; }
            try { await consumer.WaitAsync(policy.StopTimeout).ConfigureAwait(false); }
            catch (TimeoutException exception)
            {
                consumerStop.Cancel(); exitCode = 5; error = exception.Message;
                await consumerObserver.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (exitCode != 5) exitCode = exception is AudioCapacityException || (exception.HResult & 0xFFFF) == 112 ? 4 : 3;
                error = string.Join("; ", new[] { error, exception.Message }.Where(value => value is not null));
            }
            await consumerObserver.ConfigureAwait(false);
            resourceStop.Cancel();
            try { await resources.ConfigureAwait(false); }
            catch (Exception exception) { if (exitCode != 5) exitCode = 3; error = $"{error}; {exception.Message}"; }
            await resourceObserver.ConfigureAwait(false);
            try { recorder.Dispose(); } catch (Exception exception) { exitCode = 5; error = exception.Message; }
            Console.CancelKeyPress -= onCancel;
        }
        var report = ReportBuilder.Build(directory, exitCode, error, recorder.Waves, ingress.PeakFrames);
        File.WriteAllBytes(Path.Combine(directory, "report.json"), JsonSerializer.SerializeToUtf8Bytes(report, ProbeJsonContext.Default.CaptureRunReport));
        Console.WriteLine(JsonSerializer.Serialize(report, ProbeJsonContext.Default.CaptureRunReport));
        return report.ExitCode;
    }

    /// <summary>及时把消费者失败送回状态机。</summary>
    /// <param name="consumer">消费任务。</param><param name="coordinator">事件入口。</param><returns>已观察异常的任务。</returns>
    private static async Task ObserveConsumerAsync(Task consumer, CaptureCoordinator coordinator)
    {
        try { await consumer.ConfigureAwait(false); }
        catch (Exception exception)
        {
            var kind = exception is AudioCapacityException || (exception.HResult & 0xFFFF) == 112 ? "AudioCapacity" : "ConsumerFailure";
            coordinator.Post(new(SessionEventKind.Fatal, Detail: $"{kind}: {exception.Message}"));
        }
    }

    /// <summary>每秒采样 Probe 自身资源。</summary>
    /// <param name="recorder">记录器。</param><param name="ingress">队列。</param><param name="token">停止信号。</param><returns>采样任务。</returns>
    private static async Task SampleResourcesAsync(RunRecorder recorder, AudioIngress ingress, CancellationToken token)
    {
        using var process = Process.GetCurrentProcess();
        try
        {
            while (true)
            {
                process.Refresh();
                recorder.WriteEvent(new("resource", Resource: new(DateTimeOffset.UtcNow, process.PrivateMemorySize64,
                    process.WorkingSet64, process.HandleCount, process.Threads.Count, ingress.QueuedFrames)));
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>解析不重复的长参数。</summary><param name="args">参数。</param><returns>映射。</returns>
    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal) || key.Length == 2) throw new ArgumentException($"无效参数: {key}");
            string? value = null;
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)) value = args[++index];
            if (!values.TryAdd(key[2..], value)) throw new ArgumentException($"重复参数: {key}");
        }
        return values;
    }

    /// <summary>拒绝未知开关及错误值。</summary><param name="options">映射。</param><param name="allowed">允许名称。</param>
    private static void ValidateOptions(Dictionary<string, string?> options, string[] allowed)
    {
        foreach (var (key, value) in options)
        {
            if (!allowed.Contains(key, StringComparer.Ordinal)) throw new ArgumentException($"未知参数: --{key}");
            if (key is "json" or "until-cancelled" or "preserve-observed-repetitions")
            {
                if (value is not null) throw new ArgumentException($"--{key} 不接受值。");
            }
            else if (value is null) throw new ArgumentException($"--{key} 缺少值。");
        }
    }

    /// <summary>读取必填值。</summary><param name="options">映射。</param><param name="name">名称。</param><returns>值。</returns>
    private static string Required(Dictionary<string, string?> options, string name) =>
        options.TryGetValue(name, out var value) && value is not null ? value : throw new ArgumentException($"缺少 --{name}。");

    /// <summary>严格读取扩窗实验的离散预算，不截断小数或默默启用实验。</summary>
    /// <param name="options">已经校验字段名称的命令参数。</param><returns>0、60 或 120 秒。</returns>
    private static int RecoverySeconds(Dictionary<string, string?> options)
    {
        if (!options.TryGetValue("alignment-recovery-seconds", out var value)) return 120;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds is not (0 or 60 or 120))
            throw new ArgumentException("--alignment-recovery-seconds 必须为 0、60 或 120。");
        return seconds;
    }

    /// <summary>按 invariant 格式读取有限数值。</summary>
    /// <param name="options">映射。</param><param name="name">名称。</param><param name="fallback">缺省值。</param><returns>数值。</returns>
    private static double Number(Dictionary<string, string?> options, string name, double fallback)
    {
        if (!options.ContainsKey(name)) return fallback;
        if (!double.TryParse(Required(options, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new ArgumentException($"--{name} 必须为有限数值。");
        return value;
    }
}
