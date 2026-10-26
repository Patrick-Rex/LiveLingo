using LiveLingo.Core.Desktop;
using LiveLingo.Core.History;
using LiveLingo.Runtime.Storage;

namespace LiveLingo.Desktop.Windows;

/// <summary>管理已提交数据目录的历史库、每日清理和独立保存状态；验证入口使用隔离根。</summary>
/// <param name="verificationDataPath">显式验证的数据根，普通运行为空。</param>
public sealed class DesktopHistory(string? verificationDataPath = null) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, JsonTranslationHistoryStore> stores = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, IHistoryRecorder> failures = [];
    private readonly CancellationTokenSource lifetime = new();
    private JsonTranslationHistoryStore? current;
    private int retentionDays = 30;
    private Task dailyTask = Task.CompletedTask;
    private Task cleanupTask = Task.CompletedTask;
    private bool cleanupRequested;
    private bool cleanupRunning;
    private bool closed;
    private string? maintenanceError;
    private string? recordingError;

    /// <summary>通知UI重新读取库位置或持久化状态，调用者负责UI调度。</summary>
    public event Action? Changed;
    /// <summary>获取当前已提交设置对应的库。</summary>
    public ITranslationHistoryStore? Store { get { lock (gate) return current; } }
    /// <summary>获取当前库的实际路径。</summary>
    public string Location { get { lock (gate) return current?.RootPath ?? ""; } }
    /// <summary>获取最早尚未恢复的记录器，供用户重试。</summary>
    public IHistoryRecorder? FailedRecorder
    {
        get
        {
            lock (gate)
            {
                foreach (var id in failures.Where(x => !x.Value.Status.IsSaving && x.Value.Status.Error is null).Select(x => x.Key).ToArray()) failures.Remove(id);
                return failures.Values.FirstOrDefault(x => x.Status.Error is not null);
            }
        }
    }
    /// <summary>获取独立维护错误，避免自动清理失败被隐藏。</summary>
    public string? MaintenanceError { get { lock (gate) return recordingError ?? maintenanceError; } }

    /// <summary>切换已确认的数据目录及保留期，复用旧库以保护迟到写入。</summary><param name="settings">已应用设置。</param>
    public void Configure(DesktopSettings settings)
    {
        lock (gate)
        {
            if (closed) return;
            var path = Path.GetFullPath(verificationDataPath ?? settings.DataPath);
            if (!stores.TryGetValue(path, out current)) stores[path] = current = new JsonTranslationHistoryStore(path);
            retentionDays = settings.HistoryRetentionDays;
            if (dailyTask.IsCompleted) dailyTask = DailyAsync();
        }
        Changed?.Invoke(); RequestCleanup();
    }

    /// <summary>在新会话启动时固定历史库并监听保存失败，不执行磁盘写入。</summary>
    /// <param name="id">新会话身份。</param><param name="target">已核实目标。</param><param name="settings">本次固定配置。</param><returns>本次非阻塞记录器。</returns>
    public IHistoryRecorder? Begin(Guid id, DesktopTarget target, DesktopSettings settings)
    {
        try
        {
            Configure(settings);
            IHistoryRecorder recorder;
            lock (gate) recorder = current!.BeginSession(new(id, target.Name, settings.AsrLanguage, settings.TranslationModel, DateTimeOffset.UtcNow));
            recorder.StatusChanged += status => OnStatus(recorder, status);
            Changed?.Invoke(); return recorder;
        }
        catch (Exception error)
        {
            lock (gate) recordingError = "本场历史无法开始保存：" + error.Message;
            Changed?.Invoke(); return null;
        }
    }

    /// <summary>等待本场历史冲刷；失败保留提示但不妨碍模型资源清理。</summary><param name="recorder">本次记录器。</param><param name="error">会话错误。</param><returns>有界结束任务。</returns>
    public async Task CompleteAsync(IHistoryRecorder recorder, string? error)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await recorder.CompleteAsync(error, budget.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { lock (gate) recordingError = "历史退出冲刷超时，可能仅保存了部分记录。"; }
        catch (Exception failure) { lock (gate) recordingError = "历史未完整保存：" + failure.Message; }
        OnStatus(recorder, recorder.Status); RequestCleanup();
    }

    /// <summary>聚合仍未解决的记录失败，不让后续成功会话遮蔽旧失败。</summary><param name="recorder">状态来源。</param><param name="status">当前保存状态。</param>
    private void OnStatus(IHistoryRecorder recorder, HistorySaveStatus status)
    {
        lock (gate)
        {
            if (closed) return;
            if (status.Error is not null) failures[status.SessionId] = recorder;
            else if (!status.IsSaving) failures.Remove(status.SessionId);
        }
        Changed?.Invoke();
    }

    /// <summary>合并清理请求，最多保留一个后台维护任务。</summary>
    private void RequestCleanup()
    {
        lock (gate)
        {
            if (closed) return;
            cleanupRequested = true;
            if (!cleanupRunning) { cleanupRunning = true; cleanupTask = Task.Run(CleanupPendingAsync); }
        }
    }

    /// <summary>在启动、设置应用和会话结束后清理当前库，错误可见。</summary><returns>维护任务。</returns>
    private async Task CleanupPendingAsync()
    {
        while (true)
        {
            JsonTranslationHistoryStore? target; int days;
            lock (gate)
            {
                if (closed || !cleanupRequested) { cleanupRunning = false; return; }
                cleanupRequested = false; target = current; days = retentionDays;
            }
            if (target is null) return;
            try
            {
                await target.CleanupAsync(days, lifetime.Token).ConfigureAwait(false);
                lock (gate) maintenanceError = null;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch (Exception error) { lock (gate) maintenanceError = "历史清理未完成：" + error.Message; }
            Changed?.Invoke();
        }
    }

    /// <summary>每日触发一次保留期维护，退出时取消等待。</summary><returns>生命周期任务。</returns>
    private async Task DailyAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false)) RequestCleanup();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisposeCoreAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    /// <summary>完成所有已拥有的库和维护任务，由外层统一约束总退出预算。</summary><returns>完整收敛任务。</returns>
    private async Task DisposeCoreAsync()
    {
        JsonTranslationHistoryStore[] owned;
        lock (gate) { if (closed) return; closed = true; owned = stores.Values.ToArray(); }
        await lifetime.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(dailyTask, cleanupTask).ConfigureAwait(false);
        try { foreach (var item in owned) await item.DisposeAsync().ConfigureAwait(false); }
        finally { lifetime.Dispose(); }
    }
}
