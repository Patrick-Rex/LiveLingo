namespace LiveLingo.Core.Processes;

/// <summary>平台所有的 worker 连接；终止必须等待实际进程树退出。</summary>
public interface IWorkerConnection : IAsyncDisposable
{
    /// <summary>获取宿主唯一写入的 stdin。</summary>
    Stream Input { get; }
    /// <summary>获取只含协议帧的 stdout。</summary>
    Stream Output { get; }
    /// <summary>获取必须持续排空的 stderr。</summary>
    Stream Error { get; }
    /// <summary>终止且有界等待本连接的自有进程树，并使管道读写退出；不得影响用户进程。</summary>
    /// <returns>确认实际计算及资源已退出的任务。</returns>
    Task TerminateAsync();
}
