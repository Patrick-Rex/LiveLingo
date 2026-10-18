using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LiveLingo.Core.Desktop;
using LiveLingo.Core.Sessions;
using LiveLingo.Platform.Windows.Audio;
using LiveLingo.Platform.Windows.Power;
using LiveLingo.Platform.Windows.Processes;
using LiveLingo.Runtime.Asr;
using LiveLingo.Runtime.Pipeline;
using LiveLingo.Runtime.Storage;

namespace LiveLingo.Desktop.Windows;

/// <summary>组合已验证的进程树采集及字幕流水线；只终止本会话创建的模型 Job。</summary>
/// <param name="contentRoot">包含固定 Python 代码和 profile 的发布根。</param>
public sealed class DesktopSession(string contentRoot) : IDesktopSession
{
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly object updates = new();
    private CancellationTokenSource? stop;
    private Task running = Task.CompletedTask;
    private long sequence;
    private string captureState = "等待模型";
    private PipelineSnapshot? latest;

    /// <inheritdoc />
    public event Action<DesktopUpdate>? Updated;
    /// <summary>获取显式验证时的原始完整快照；普通模式不写入正文。</summary>
    public event Action<PipelineSnapshot>? PipelineUpdated;
    /// <summary>获取上次运行登记的自有进程，用于结束后核验。</summary>
    public TargetIdentity[] LastOwned { get; private set; } = [];
    /// <summary>获取结束时仍然存活的自有身份。</summary>
    public TargetIdentity[] RemainingOwned { get; private set; } = [];
    /// <summary>获取结束后仍存在的本会话音频溢写目录，不包括工具或外部缓存。</summary>
    public string[] RemainingTemporary { get; private set; } = [];
    /// <summary>获取本次结束错误；空值才表示运行与清理均没有抛出异常。</summary>
    public string? LastError { get; private set; }
    /// <summary>获取显式验证专用输入；正常产品入口不设置它。</summary>
    public Func<Guid, CancellationToken, IAsyncEnumerable<PipelineInput>>? VerificationInput { get; set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<DesktopTarget>> GetTargetsAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<DesktopTarget>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return WindowsTargets.Enumerate().Where(x => x.IsApplicationRoot && x.Identity is not null && x.ProcessId != Environment.ProcessId)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(x => new DesktopTarget(x.Name, x.Identity!)).ToArray();
    }, cancellationToken);

    /// <inheritdoc />
    public async Task StartAsync(Guid sessionId, DesktopTarget target, DesktopSettings settings, CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!running.IsCompleted) throw new InvalidOperationException("上一会话尚未停止。");
            settings.Validate();
            if (sessionId == Guid.Empty) throw new ArgumentException("需要新会话身份。");
            if (VerificationInput is null && (WindowsTargets.Query(target.Identity.ProcessId).Identity is not { } actual || !actual.SameInstance(target.Identity)))
                throw new InvalidOperationException("目标已退出或 PID 已被复用，请刷新选择。");
            foreach (var path in new[] { settings.PythonPath, settings.OllamaPath, Path.Combine(contentRoot, "runtime-profiles", "win-x64-cu130", "profile.json"), Path.Combine(contentRoot, "workers", "qwen_asr", "service.py"), Path.Combine(contentRoot, "tools", "model_eval", "common.py"), Path.Combine(contentRoot, "tools", "model_eval", "prepare_model.py"), Path.Combine(contentRoot, "tools", "model_eval", "download.py") })
                if (!File.Exists(path)) throw new FileNotFoundException("运行依赖缺失：" + path, path);
            foreach (var path in new[] { settings.ModelPath, settings.OllamaModelsPath })
                if (!Directory.Exists(path)) throw new DirectoryNotFoundException("权重目录缺失：" + path);
            stop?.Dispose(); stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sequence = 0; latest = null; captureState = VerificationInput is null ? "等待模型" : "显式 WAV 回放"; LastError = null; LastOwned = []; RemainingOwned = [];
            var token = stop.Token;
            running = Task.Run(() => RunAsync(sessionId, target, settings, token), CancellationToken.None);
        }
        finally { lifecycle.Release(); }
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { if (stop is not null) await stop.CancelAsync().ConfigureAwait(false); await running.ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }

    /// <summary>运行模型和采集，初始化、运行和关闭任意失败均继续清理其他自有资源。</summary>
    /// <param name="id">会话身份。</param><param name="target">外部目标。</param><param name="settings">固定设置。</param><param name="token">会话取消。</param><returns>全部资源收敛任务。</returns>
    private async Task RunAsync(Guid id, DesktopTarget target, DesktopSettings settings, CancellationToken token)
    {
        var owned = new List<OwnedWorkerConnection>();
        var temporary = new List<string>();
        LocalPipelineModels? models = null;
        ContinuousAudioStore? store = null;
        try
        {
            var environment = PrepareEnvironment(settings);
            var profilePath = Path.Combine(contentRoot, "runtime-profiles", "win-x64-cu130", "profile.json");
            using var profile = JsonDocument.Parse(await File.ReadAllBytesAsync(profilePath, token).ConfigureAwait(false));
            var root = profile.RootElement;
            var expected = new WorkerInformation(root.GetProperty("python").GetString()!, root.GetProperty("torch").GetString()!, root.GetProperty("cuda").GetString()!, root.GetProperty("modelRevision").GetString()!, "");
            int port;
            using (var reservation = new TcpListener(IPAddress.Loopback, 0)) { reservation.Start(); port = ((IPEndPoint)reservation.LocalEndpoint).Port; }
            var options = new PipelineOptions();
            models = new(epoch =>
            {
                var connection = new OwnedWorkerConnection(settings.PythonPath, ["-u", "-m", "workers.qwen_asr.service", "--profile", profilePath,
                    "--model", settings.ModelPath, "--worker-epoch", epoch.ToString(CultureInfo.InvariantCulture)], contentRoot, "desktop-qwen", environment);
                owned.Add(connection); return connection;
            }, () =>
            {
                var ollamaEnvironment = new Dictionary<string, string>(environment)
                { ["OLLAMA_HOST"] = $"127.0.0.1:{port}", ["OLLAMA_MODELS"] = settings.OllamaModelsPath, ["OLLAMA_CONTEXT_LENGTH"] = "4096", ["OLLAMA_NUM_PARALLEL"] = "1", ["OLLAMA_MAX_LOADED_MODELS"] = "1" };
                var connection = new OwnedWorkerConnection(settings.OllamaPath, ["serve"], contentRoot, "desktop-ollama", ollamaEnvironment);
                owned.Add(connection); return connection;
            }, new Uri($"http://127.0.0.1:{port}/"), expected, options);
            var generation = 0;
            var pipeline = new SubtitlePipeline(new(id, 1, 1, 1), new(settings.TranslationModel, settings.AsrLanguage), options, () =>
            {
                var parent = Path.Combine(settings.DataPath, "pipeline-cache"); Directory.CreateDirectory(parent);
                var path = Path.Combine(parent, id.ToString("N") + "-" + generation++); temporary.Add(path);
                return store = new ContinuousAudioStore(new FileAudioSpillStore(path), options.MemorySamples, options.SpillSamples, options.MaximumLeaseSamples);
            }, models, snapshot =>
            {
                lock (updates) { latest = snapshot; Publish(id, true); }
                foreach (var process in owned) process.ObserveMembers();
                PipelineUpdated?.Invoke(snapshot);
            });
            var input = VerificationInput is { } verification ? verification(id, token) : CaptureAsync(target.Identity, id, token);
            await pipeline.RunAsync(input, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { LastError = error.GetType().Name + ": " + error.Message; }
        finally
        {
            try { if (models is not null) await models.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { LastError = "模型清理失败：" + error.Message; }
            foreach (var process in owned)
                try { await process.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { LastError = "进程清理失败：" + error.Message; }
            try { store?.Dispose(); } catch (Exception error) { LastError = "缓存清理失败：" + error.Message; }
            LastOwned = owned.SelectMany(x => x.ObserveMembers()).Distinct().ToArray();
            RemainingOwned = LastOwned.Where(x => WindowsTargets.Query(x.ProcessId).Identity is { } actual && actual.SameInstance(x)).ToArray();
            if (RemainingOwned.Length != 0) LastError = "仍有自有进程未退出。";
            RemainingTemporary = temporary.Where(Directory.Exists).ToArray();
            if (RemainingTemporary.Length != 0) LastError = "本会话音频临时目录未清理。";
            lock (updates) Publish(id, false);
        }
    }

    /// <summary>发布串行完整快照；最终停止发布发生在所有清理之后。</summary><param name="id">会话。</param><param name="active">是否仍拥有运行资源。</param>
    private void Publish(Guid id, bool active)
    {
        var state = active ? $"{latest?.Status ?? "starting"} · 采集 {captureState}" : LastError ?? $"已停止 · {latest?.Status ?? "cancelled"}";
        Updated?.Invoke(new(id, ++sequence, latest?.Subtitles, state, (latest?.BacklogSamples ?? 0) / 16000.0, active));
    }

    /// <summary>复用 M1 原生采集与恢复，将真实代次和缺口交给 M3 转换器。</summary>
    /// <param name="target">外部应用身份。</param><param name="id">会话。</param><param name="token">取消捕获。</param><returns>连续音频和重置输入。</returns>
    private async IAsyncEnumerable<PipelineInput> CaptureAsync(TargetIdentity target, Guid id, [EnumeratorCancellation] CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var policy = new CapturePolicy { QueueAudioSeconds = 60, QueuePackets = 12000 };
        using var ingress = new AudioIngress(policy, WindowsLoopbackCapture.RequestedFormat);
        var excluded = WindowsTargets.Enumerate().Where(x => x.IsApplicationRoot && x.Identity is not null && !target.SameInstance(x.Identity)).Select(x => x.Identity!).ToHashSet();
        var coordinator = new CaptureCoordinator(new WindowsLoopbackCapture(), policy, ingress,
            identity => WindowsTargets.Query(identity.ProcessId).Identity is { } actual && identity.SameInstance(actual),
            identity => WindowsTargets.UniqueMatch(identity, WindowsTargets.Enumerate(), excluded),
            diagnostic => { lock (updates) { captureState = diagnostic.State.ToString(); Publish(id, true); } });
        using var devices = new DeviceNotifications(detail => coordinator.Post(new(SessionEventKind.DeviceChanged, Detail: detail)));
        using var power = new PowerNotifications(suspended => coordinator.Post(new(suspended ? SessionEventKind.Suspend : SessionEventKind.Resume)));
        var capturing = coordinator.RunAsync(target, id, cancellation.Token);
        try
        {
            await foreach (var input in CapturePipelineInput.ReadAsync(ingress.ReadAllAsync(token), id, token).ConfigureAwait(false)) yield return input;
            var result = await capturing.ConfigureAwait(false);
            if (result != 0) throw new IOException("capture_exit_" + result);
        }
        finally { await cancellation.CancelAsync().ConfigureAwait(false); await capturing.ConfigureAwait(false); }
    }

    /// <summary>建立子进程私有缓存环境并检查数据卷余量，不修改用户全局环境。</summary><param name="settings">固定资源根。</param><returns>私有环境覆盖。</returns>
    private static Dictionary<string, string> PrepareEnvironment(DesktopSettings settings)
    {
        foreach (var path in new[] { settings.DataPath, settings.CachePath })
        {
            Directory.CreateDirectory(path);
            if (new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace < 512L * 1024 * 1024) throw new IOException("资源卷可用空间不足 512MiB：" + path);
        }
        var environment = new Dictionary<string, string> { ["LIVELINGO_OWNED_WORKER"] = "1", ["LIVELINGO_CACHE_ROOT"] = settings.CachePath,
            ["LIVELINGO_PROFILE_INTERPRETER"] = settings.PythonPath, ["PYTHONUTF8"] = "1", ["PYTHONIOENCODING"] = "utf-8", ["HF_HUB_OFFLINE"] = "1", ["TRANSFORMERS_OFFLINE"] = "1" };
        foreach (var (key, folder) in new[] { ("TEMP", "tmp"), ("TMP", "tmp"), ("DOTNET_BUNDLE_EXTRACT_BASE_DIR", "dotnet-bundle"), ("HF_HOME", "huggingface"), ("HF_HUB_CACHE", "huggingface/hub"),
            ("UV_CACHE_DIR", "uv"), ("PIP_CACHE_DIR", "pip"), ("TORCH_HOME", "torch"), ("TORCHINDUCTOR_CACHE_DIR", "torchinductor"), ("CUDA_CACHE_PATH", "cuda"), ("PYTHONPYCACHEPREFIX", "pycache") })
        { var path = Path.Combine(settings.CachePath, folder); Directory.CreateDirectory(path); environment[key] = path; }
        return environment;
    }
}
