using LiveLingo.Core.Sessions;

namespace LiveLingo.Core.Audio;

/// <summary>隔离平台采集实现，运行至取消或真实失败；所有包只转移一次。</summary>
public interface ICaptureSource
{
    /// <summary>采集指定身份的进程树；发布失败必须停止当前代次。</summary>
    /// <param name="target">已核实的进程实例身份。</param>
    /// <param name="sessionId">会话身份。</param>
    /// <param name="epoch">采集代次。</param>
    /// <param name="activationTimeout">有界激活等待时间。</param>
    /// <param name="publish">接管包所有权并报告是否接受的入口。</param>
    /// <param name="ready">客户端成功启动后的非阻塞通知。</param>
    /// <param name="cancellationToken">停止采集的信号。</param>
    /// <returns>释放易失资源后完成的任务。</returns>
    Task RunAsync(TargetIdentity target, Guid sessionId, long epoch, TimeSpan activationTimeout,
        Func<AudioPacket, bool> publish, Action ready, CancellationToken cancellationToken);
}
