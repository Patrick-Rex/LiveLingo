using System.Runtime.CompilerServices;
using LiveLingo.Core.Sessions;
using LiveLingo.Platform.Windows.Audio;
using LiveLingo.Platform.Windows.Power;
using LiveLingo.Platform.Windows.Processes;
using LiveLingo.Runtime.Diagnostics;
using LiveLingo.Runtime.Pipeline;

namespace LiveLingo.Probe;

/// <summary>在模型就绪后启动 M1 实际进程树采集，目标应用始终为外部所有。</summary>
internal sealed class LivePipelineSource
{
    private readonly TargetIdentity target;
    private readonly TimeSpan duration;
    private readonly Action<CaptureDiagnostic> trace;

    /// <summary>固定用户选定的完整进程身份与有限采集时长。</summary>
    /// <param name="target">已核实的外部进程。</param><param name="duration">实际采集时间。</param><param name="trace">有界不含正文的诊断输出。</param>
    public LivePipelineSource(TargetIdentity target, TimeSpan duration, Action<CaptureDiagnostic> trace)
    { this.target = target; this.duration = duration; this.trace = trace; }

    /// <summary>获取本次实际入口策略；六十秒缓冲用于重建模型期间保留原始音频。</summary>
    public CapturePolicy Policy { get; } = new() { QueueAudioSeconds = 60, QueuePackets = 12000 };
    /// <summary>获取实际协调器退出码；未启动或尚未结束时为空。</summary>
    public int? ExitCode { get; private set; }
    /// <summary>获取已观察入口峰值帧数。</summary>
    public long PeakFrames { get; private set; }
    /// <summary>获取本次外部目标身份。</summary>
    public TargetIdentity Target => target;

    /// <summary>运行原生捕获与转换，任何提前结束均取消并等待原生资源退出。</summary>
    /// <param name="session">管道会话。</param><param name="cancellationToken">会话停止。</param><returns>连续采样和真实重置事件。</returns>
    public async IAsyncEnumerable<PipelineInput> ReadAsync(Guid session, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(duration);
        using var ingress = new AudioIngress(Policy, WindowsLoopbackCapture.RequestedFormat);
        var excluded = WindowsTargets.Enumerate().Where(x => x.IsApplicationRoot && x.Identity is not null && !target.SameInstance(x.Identity))
            .Select(x => x.Identity!).ToHashSet();
        var coordinator = new CaptureCoordinator(new WindowsLoopbackCapture(), Policy, ingress,
            identity => WindowsTargets.Query(identity.ProcessId).Identity is { } actual && identity.SameInstance(actual),
            identity => WindowsTargets.UniqueMatch(identity, WindowsTargets.Enumerate(), excluded), trace);
        using var devices = new DeviceNotifications(detail => coordinator.Post(new(SessionEventKind.DeviceChanged, Detail: detail)));
        using var power = new PowerNotifications(suspended => coordinator.Post(new(suspended ? SessionEventKind.Suspend : SessionEventKind.Resume)));
        var capturing = coordinator.RunAsync(target, session, stop.Token);
        try
        {
            await foreach (var item in CapturePipelineInput.ReadAsync(ingress.ReadAllAsync(cancellationToken), session, cancellationToken).ConfigureAwait(false))
                yield return item;
            ExitCode = await capturing.ConfigureAwait(false);
            if (ExitCode != 0) throw new IOException("capture_exit_" + ExitCode);
        }
        finally
        {
            stop.Cancel();
            ExitCode = await capturing.ConfigureAwait(false);
            PeakFrames = ingress.PeakFrames;
        }
    }
}
