using LiveLingo.Core.Processes;
using LiveLingo.Core.Sessions;
using System.Diagnostics;

namespace LiveLingo.Platform.Windows.Processes;

/// <summary>为每个 worker 实例提供独立 Job，失败恢复不会终止其他服务或用户进程。</summary>
public sealed class OwnedWorkerConnection : IWorkerConnection
{
    private readonly OwnedJob job;
    private readonly OwnedPipes pipes;
    private readonly object sync = new();
    private Task? termination;
    private readonly HashSet<TargetIdentity> observed = [];

    /// <summary>创建原子受监管的 worker 及私有标准管道。</summary>
    /// <param name="executable">既有解释器或工具路径。</param><param name="arguments">独立参数。</param>
    /// <param name="directory">工作目录。</param><param name="role">审计职责。</param><param name="environment">子进程私有环境。</param>
    public OwnedWorkerConnection(string executable, IReadOnlyList<string> arguments, string directory, string role,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        job = new();
        try { pipes = job.StartRedirected(executable, arguments, directory, role, environment); }
        catch { job.Dispose(); throw; }
        observed.Add(pipes.Process.Identity);
    }
    /// <summary>获取用于清理后完整身份核对的登记记录。</summary>
    public OwnedProcess Process => pipes.Process;
    /// <inheritdoc />
    public Stream Input => pipes.Input;
    /// <inheritdoc />
    public Stream Output => pipes.Output;
    /// <inheritdoc />
    public Stream Error => pipes.Error;

    /// <summary>保存当前已观察子孙的完整身份，关闭后仍可用于 PID 复用安全核对。</summary>
    /// <returns>有界且去重的本次自有身份。</returns>
    public TargetIdentity[] ObserveMembers()
    {
        lock (sync)
        {
            if (termination is null)
                foreach (var identity in job.Members())
                {
                    if (observed.Count >= 1024 && !observed.Contains(identity)) throw new InvalidOperationException("owned_identity_history_capacity");
                    observed.Add(identity);
                }
            return observed.ToArray();
        }
    }

    /// <inheritdoc />
    public Task TerminateAsync()
    {
        lock (sync)
        {
            if (termination is not null) return termination;
            Exception? auditFailure = null;
            try { ObserveMembers(); } catch (Exception error) { auditFailure = error; }
            return termination = Task.Run(() =>
            {
                try { job.Dispose(); }
                finally { pipes.Dispose(); }
                var deadline = Stopwatch.StartNew();
                while (observed.Any(identity => WindowsTargets.Query(identity.ProcessId).Identity is { } actual && identity.SameInstance(actual)))
                {
                    if (deadline.Elapsed >= TimeSpan.FromSeconds(5)) throw new InvalidOperationException("owned_descendant_remained");
                    Thread.Sleep(10);
                }
                if (auditFailure is not null) throw new InvalidOperationException("owned_process_audit_failed", auditFailure);
            });
        }
    }
    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await TerminateAsync().ConfigureAwait(false);
}
