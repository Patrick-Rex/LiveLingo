using System.Diagnostics;
using System.Threading.Channels;
using LiveLingo.Core.Audio;
using LiveLingo.Core.Sessions;
using LiveLingo.Runtime.Diagnostics;

namespace LiveLingo.Runtime.Pipeline;

/// <summary>将平台回调、目标巡检和恢复副作用串行归约，阻止旧事件重启资源。</summary>
public sealed class CaptureCoordinator
{
    private readonly ICaptureSource source;
    private readonly CapturePolicy policy;
    private readonly AudioIngress ingress;
    private readonly Func<TargetIdentity, bool> isCurrent;
    private readonly Func<TargetIdentity, TargetIdentity?> uniqueMatch;
    private readonly Action<CaptureDiagnostic> trace;
    private readonly TimeProvider clock;
    private readonly Channel<SessionEvent> events = Channel.CreateBounded<SessionEvent>(new BoundedChannelOptions(256)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false
    });
    private readonly CancellationTokenSource eventOverflow = new();
    private int closed;

    /// <summary>建立一个会话的协调器，所有平台行为由边界注入。</summary>
    /// <param name="source">平台采集器。</param><param name="policy">有界配置。</param><param name="ingress">音频入口。</param>
    /// <param name="isCurrent">核实当前实例的只读查询。</param><param name="uniqueMatch">唯一候选查询。</param>
    /// <param name="trace">在回调之外写入的诊断入口。</param><param name="clock">可控时间提供器。</param>
    public CaptureCoordinator(ICaptureSource source, CapturePolicy policy, AudioIngress ingress,
        Func<TargetIdentity, bool> isCurrent, Func<TargetIdentity, TargetIdentity?> uniqueMatch,
        Action<CaptureDiagnostic> trace, TimeProvider? clock = null)
    {
        policy.Validate();
        this.source = source;
        this.policy = policy;
        this.ingress = ingress;
        this.isCurrent = isCurrent;
        this.uniqueMatch = uniqueMatch;
        this.trace = trace;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>非阻塞投递平台事件；事件队列满属于明确失败。</summary>
    /// <param name="message">平台或消费错误事件。</param>
    public void Post(SessionEvent message)
    {
        if (Volatile.Read(ref closed) != 0) return;
        if (!events.Writer.TryWrite(message) && !events.Reader.Completion.IsCompleted)
        {
            try { eventOverflow.Cancel(); }
            catch (ObjectDisposedException) when (Volatile.Read(ref closed) != 0) { }
        }
    }

    /// <summary>运行单写入者事件循环，取消后等待捕获释放并完成音频入口。</summary>
    /// <param name="target">最初选定目标。</param><param name="sessionId">会话身份。</param>
    /// <param name="cancellationToken">用户停止或录制时长信号。</param>
    /// <returns>0 正常、3 恢复失败、4 资源超限、5 清理失败。</returns>
    public async Task<int> RunAsync(TargetIdentity target, Guid sessionId, CancellationToken cancellationToken)
    {
        var reducer = new SessionReducer();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, eventOverflow.Token);
        CancellationTokenSource? activeCancellation = null;
        Task active = Task.CompletedTask;
        Task observer = Task.CompletedTask;
        Task retry = Task.CompletedTask;
        var exitCode = 0;
        var diagnosticExitCode = 0;
        var timer = PumpHealthAsync(lifetime.Token);
        Post(new(SessionEventKind.Start, Target: target));
        try
        {
            await foreach (var incoming in events.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                var message = incoming;
                if (message.Kind == SessionEventKind.DeviceChanged)
                {
                    if (reducer.Target is not null && reducer.State is SessionState.Running or SessionState.Starting or SessionState.Recovering)
                    {
                        if (!isCurrent(reducer.Target)) message = new(SessionEventKind.TargetLost, reducer.CaptureEpoch, Detail: "实例身份消失或无法核实。");
                    }
                    else if (reducer.Target is not null && reducer.State == SessionState.WaitingForTarget)
                    {
                        var match = uniqueMatch(reducer.Target);
                        if (match is not null) message = new(SessionEventKind.TargetFound, Target: match);
                    }
                }
                var previousState = reducer.State;
                var previousEpoch = reducer.CaptureEpoch;
                if (!reducer.Apply(message, policy.RetryBudget))
                {
                    if (message.Kind != SessionEventKind.DeviceChanged) Emit(reducer, lifetime, ref diagnosticExitCode, "rejected-event", message.Detail ?? message.Kind.ToString());
                    continue;
                }
                Emit(reducer, lifetime, ref diagnosticExitCode, message.Kind.ToString(), message.Detail);
                var changedEpoch = reducer.CaptureEpoch != previousEpoch;
                var interrupted = message.Kind is SessionEventKind.CaptureFailed or SessionEventKind.TargetLost or SessionEventKind.Suspend or SessionEventKind.Fatal;
                if (interrupted || changedEpoch)
                {
                    if (previousEpoch > 0 && interrupted)
                    {
                        var gap = new AudioGap(sessionId, previousEpoch, message.Detail ?? message.Kind.ToString(), null, null, Stopwatch.GetTimestamp());
                        WriteTrace(lifetime, ref diagnosticExitCode, new("gap", clock.GetUtcNow(), Stopwatch.GetTimestamp(), reducer.State, previousEpoch, reducer.StateSequence, gap.Reason, gap));
                    }
                    activeCancellation?.Cancel();
                    await ObserveStopAsync(active, observer).ConfigureAwait(false);
                    activeCancellation?.Dispose();
                    activeCancellation = null;
                }
                if (changedEpoch && reducer.State == SessionState.Starting)
                {
                    activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    var epoch = reducer.CaptureEpoch;
                    active = source.RunAsync(reducer.Target!, sessionId, epoch, policy.ActivationTimeout,
                        packet =>
                        {
                            var accepted = ingress.TryPublish(packet);
                            if (!accepted) Post(new(SessionEventKind.Fatal, epoch, Detail: "AudioCapacity: 音频入口超限或关闭。"));
                            return accepted;
                        }, () => Post(new(SessionEventKind.Ready, epoch)), activeCancellation.Token);
                    observer = ObserveCaptureAsync(active, epoch, activeCancellation.Token);
                }
                if (reducer.State == SessionState.Recovering && previousState != SessionState.Recovering)
                    retry = RetryAsync(reducer.CaptureEpoch, reducer.Failures, lifetime.Token);
                if (reducer.State == SessionState.Faulted)
                {
                    exitCode = message.Detail?.StartsWith("AudioCapacity:", StringComparison.Ordinal) == true ? 4 : 3;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            if (eventOverflow.IsCancellationRequested) exitCode = 4;
        }
        catch (TimeoutException exception)
        {
            // A failed stop barrier must not escape as a generic capture failure.
            exitCode = 5;
            Emit(reducer, lifetime, ref diagnosticExitCode, "cleanup-failed", exception.Message);
        }
        finally
        {
            reducer.Apply(new(SessionEventKind.Stop), policy.RetryBudget);
            lifetime.Cancel();
            activeCancellation?.Cancel();
            try { await ObserveStopAsync(active, observer).ConfigureAwait(false); }
            catch (Exception exception) { exitCode = 5; Emit(reducer, lifetime, ref diagnosticExitCode, "cleanup-failed", exception.Message); }
            activeCancellation?.Dispose();
            ingress.Complete();
            await Task.WhenAll(timer, retry).ConfigureAwait(false);
            Volatile.Write(ref closed, 1);
            events.Writer.TryComplete();
            reducer.Apply(new(SessionEventKind.Cleaned), policy.RetryBudget);
            Emit(reducer, lifetime, ref diagnosticExitCode, "cleaned", $"exitCode={exitCode}");
            eventOverflow.Dispose();
        }
        return exitCode == 5 ? 5 : diagnosticExitCode != 0 ? diagnosticExitCode : exitCode;
    }

    /// <summary>将捕获任务的真实失败送回当前事件循环。</summary>
    /// <param name="capture">原生捕获任务。</param><param name="epoch">来源代次。</param><param name="token">代次取消。</param>
    /// <returns>已观察异常的任务。</returns>
    private async Task ObserveCaptureAsync(Task capture, long epoch, CancellationToken token)
    {
        try { await capture.ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested) Post(new(SessionEventKind.CaptureFailed, epoch, Detail: $"{exception.GetType().Name}: {exception.Message}"));
            return;
        }
        if (!token.IsCancellationRequested) Post(new(SessionEventKind.CaptureFailed, epoch, Detail: "采集意外正常结束。"));
    }

    /// <summary>等待真实原生资源退出；异常已由观察者转为状态事件。</summary>
    /// <param name="capture">原生任务。</param><param name="observer">异常观察任务。</param>
    /// <returns>资源停止后完成的任务。</returns>
    private async Task ObserveStopAsync(Task capture, Task observer)
    {
        try { await capture.WaitAsync(policy.StopTimeout, clock).ConfigureAwait(false); }
        catch (Exception) when (capture.IsCompleted) { /* observer preserves the actual capture failure */ }
        await observer.WaitAsync(policy.StopTimeout, clock).ConfigureAwait(false);
    }

    /// <summary>用可控时间进行有限退避，停止后不再投递重试。</summary>
    /// <param name="epoch">旧代次。</param><param name="failures">累计失败次数。</param><param name="token">会话取消。</param>
    /// <returns>延迟完成或取消的任务。</returns>
    private async Task RetryAsync(long epoch, int failures, CancellationToken token)
    {
        try
        {
            await Task.Delay(policy.RetryDelay(failures), clock, token).ConfigureAwait(false);
            Post(new(SessionEventKind.Retry, epoch));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>定期请求身份和客户端任务健康检查，合法静音不触发重启。</summary>
    /// <param name="token">会话取消。</param><returns>停止时完成的周期任务。</returns>
    private async Task PumpHealthAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(policy.TargetPollInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) Post(new(SessionEventKind.DeviceChanged, Detail: "periodic-health"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>记录归约后的不可变诊断。</summary>
    /// <param name="kind">诊断类型。</param><param name="detail">原因。</param>
    /// <param name="reducer">当前会话归约状态。</param>
    /// <param name="lifetime">诊断写入失败时取消的会话。</param>
    /// <param name="diagnosticExitCode">保留首次诊断失败的退出码。</param>
    private void Emit(SessionReducer reducer, CancellationTokenSource lifetime, ref int diagnosticExitCode, string kind, string? detail) => WriteTrace(lifetime, ref diagnosticExitCode, new(kind, clock.GetUtcNow(), Stopwatch.GetTimestamp(),
        reducer.State, reducer.CaptureEpoch, reducer.StateSequence, detail, Target: reducer.Target));

    /// <summary>诊断介质失败时停止会话，仍允许执行完整的资源清理。</summary>
    /// <param name="diagnostic">待写诊断。</param>
    /// <param name="lifetime">诊断写入失败时取消的会话。</param>
    /// <param name="diagnosticExitCode">保留首次诊断失败的退出码。</param>
    private void WriteTrace(CancellationTokenSource lifetime, ref int diagnosticExitCode, CaptureDiagnostic diagnostic)
    {
        if (diagnosticExitCode != 0) return;
        try { trace(diagnostic); }
        catch (Exception exception)
        {
            diagnosticExitCode = exception is AudioCapacityException || (exception.HResult & 0xFFFF) == 112 ? 4 : 3;
            lifetime.Cancel();
        }
    }
}
