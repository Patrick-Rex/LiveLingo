using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveLingo.Core.Sessions;
using LiveLingo.Platform.Windows.Processes;
using LiveLingo.Probe.Recording;

namespace LiveLingo.Probe;

/// <summary>描述明确授权的单次模型评测进程，不经过 shell 解释参数。</summary>
/// <param name="Executable">绝对解释器路径。</param><param name="Arguments">逐个命令参数。</param>
/// <param name="WorkingDirectory">绝对工作目录。</param><param name="TimeoutSeconds">总运行限时，1–7200 秒。</param>
internal sealed record ModelEvaluationRequest(string Executable, string[] Arguments, string WorkingDirectory, int TimeoutSeconds);

/// <summary>记录任务退出与自有子树回收，不把进程退出当成模型质量通过。</summary>
/// <param name="ExitCode">评测或监管退出码。</param><param name="Error">失败原因。</param>
/// <param name="OwnedObserved">观察到的自有完整身份。</param><param name="OwnedRemaining">关闭 Job 后仍存活的自有身份。</param>
/// <param name="ExternalBefore">原先存在的 Edge/Ollama 根。</param><param name="ExternalRetained">结束时仍是同一实例的外部根。</param>
internal sealed record ModelEvaluationReport(int ExitCode, string? Error, TargetIdentity[] OwnedObserved,
    TargetIdentity[] OwnedRemaining, TargetIdentity[] ExternalBefore, TargetIdentity[] ExternalRetained);

/// <summary>通过生产 Job 监管评测 Python 和其创建的私有服务，支持取消和运行总限时。</summary>
internal static class ModelEvaluationHost
{
    /// <summary>读取有界请求，等待工作进程，并在所有路径关闭自有 Job。</summary>
    /// <param name="requestPath">绝对 JSON 请求路径。</param><param name="output">独立非 C 盘监管报告目录。</param>
    /// <returns>0 表示工作进程成功并清理完成；3 为失败；5 为清理失败。</returns>
    public static async Task<int> RunAsync(string requestPath, string output)
    {
        if (!Path.IsPathFullyQualified(requestPath) || new FileInfo(requestPath).Length > 65536)
            throw new ArgumentException("评测请求须为不超过 64KiB 的绝对文件。");
        var request = JsonSerializer.Deserialize(File.ReadAllBytes(requestPath), ModelEvaluationJsonContext.Default.ModelEvaluationRequest)
            ?? throw new ArgumentException("评测请求为空。");
        if (request.TimeoutSeconds is < 1 or > 7200 || request.Arguments is null || request.Arguments.Length > 64)
            throw new ArgumentException("评测限时或参数数量无效。");
        var directory = OutputDirectory.Prepare(output);
        var external = WindowsTargets.Enumerate().Where(x => x.IsApplicationRoot && x.Identity is not null &&
            (x.Name.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase) || x.Name.Equals("ollama.exe", StringComparison.OrdinalIgnoreCase)))
            .Select(x => x.Identity!).ToArray();
        var observed = new HashSet<TargetIdentity>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds));
        ConsoleCancelEventHandler onCancel = (_, args) => { args.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += onCancel;
        var job = new OwnedJob();
        var exit = 3;
        string? error = null;
        try
        {
            var previousMarker = Environment.GetEnvironmentVariable("LIVELINGO_OWNED_EVALUATION");
            OwnedProcess owned;
            try
            {
                Environment.SetEnvironmentVariable("LIVELINGO_OWNED_EVALUATION", "1");
                owned = job.Start(request.Executable, request.Arguments, request.WorkingDirectory, "model-evaluation");
            }
            finally { Environment.SetEnvironmentVariable("LIVELINGO_OWNED_EVALUATION", previousMarker); }
            observed.Add(owned.Identity);
            using var process = Process.GetProcessById(owned.Identity.ProcessId);
            // Retain a native handle while the process is alive so ExitCode remains queryable after exit.
            _ = process.Handle;
            while (!process.HasExited)
            {
                stop.Token.ThrowIfCancellationRequested();
                foreach (var candidate in WindowsTargets.Enumerate())
                    if (candidate.Identity is { } identity && job.Contains(identity)) observed.Add(identity);
                await Task.Delay(250, stop.Token).ConfigureAwait(false);
            }
            exit = process.ExitCode;
        }
        catch (Exception exception) { error = $"{exception.GetType().Name}: {exception.Message}"; }
        finally
        {
            try { job.Dispose(); }
            catch (Exception exception) { exit = 5; error = $"{error}; cleanup: {exception.Message}"; }
            Console.CancelKeyPress -= onCancel;
        }
        var deadline = Stopwatch.StartNew();
        while (observed.Any(IsSameInstance) && deadline.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(50).ConfigureAwait(false);
        var remaining = observed.Where(IsSameInstance).ToArray();
        if (remaining.Length > 0) { exit = 5; error = $"{error}; owned processes remain"; }
        var report = new ModelEvaluationReport(exit, error, observed.ToArray(), remaining, external, external.Where(IsSameInstance).ToArray());
        var json = JsonSerializer.Serialize(report, ModelEvaluationJsonContext.Default.ModelEvaluationReport);
        await File.WriteAllTextAsync(Path.Combine(directory, "supervisor.json"), json + "\n").ConfigureAwait(false);
        Console.WriteLine(json);
        return exit;
    }

    /// <summary>按完整身份核对存活，避免 PID 复用造成误判。</summary>
    /// <param name="identity">待核对身份。</param><returns>同一实例仍可查询时为 true。</returns>
    private static bool IsSameInstance(TargetIdentity identity) =>
        WindowsTargets.Query(identity.ProcessId).Identity is { } current && identity.SameInstance(current);
}

/// <summary>生成固定评测监管消息的序列化元数据。</summary>
[JsonSerializable(typeof(ModelEvaluationRequest))]
[JsonSerializable(typeof(ModelEvaluationReport))]
internal partial class ModelEvaluationJsonContext : JsonSerializerContext;
