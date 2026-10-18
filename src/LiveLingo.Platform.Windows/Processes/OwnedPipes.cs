using System.IO.Pipes;

namespace LiveLingo.Platform.Windows.Processes;

/// <summary>本次自有进程的三个父端流；Job 负责进程终止，流负责管道资源。</summary>
public sealed class OwnedPipes : IDisposable
{
    /// <summary>创建明确拥有的父端流组合。</summary>
    /// <param name="process">原子监管的身份。</param><param name="input">向子进程写入。</param>
    /// <param name="output">读取子进程输出。</param><param name="error">持续读取诊断。</param>
    internal OwnedPipes(OwnedProcess process, AnonymousPipeServerStream input, AnonymousPipeServerStream output, AnonymousPipeServerStream error)
    { Process = process; Input = input; Output = output; Error = error; }
    /// <summary>获取已登记身份。</summary>
    public OwnedProcess Process { get; }
    /// <summary>获取只写标准输入。</summary>
    public Stream Input { get; }
    /// <summary>获取只读二进制标准输出。</summary>
    public Stream Output { get; }
    /// <summary>获取只读诊断标准错误。</summary>
    public Stream Error { get; }
    /// <inheritdoc />
    public void Dispose() { Input.Dispose(); Output.Dispose(); Error.Dispose(); }
}

/// <summary>为常驻 worker 添加原子 Job 与限定继承句柄的标准管道。</summary>
public sealed unsafe partial class OwnedJob
{
    /// <summary>建立匿名管道并在进程创建时加入 Job；失败释放所有父子端句柄。</summary>
    /// <param name="executable">绝对 EXE 路径。</param><param name="arguments">独立参数。</param>
    /// <param name="workingDirectory">工作目录。</param><param name="role">审计职责。</param>
    /// <param name="environment">子进程私有环境；D 盘缓存设置具有优先级。</param>
    /// <returns>由调用方释放、且须在 Job 生命周期内使用的三个父端流。</returns>
    public OwnedPipes StartRedirected(string executable, IReadOnlyList<string> arguments, string workingDirectory, string role,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        AnonymousPipeServerStream? input = null, output = null, error = null;
        try
        {
            input = new(PipeDirection.Out, HandleInheritability.Inheritable);
            output = new(PipeDirection.In, HandleInheritability.Inheritable);
            error = new(PipeDirection.In, HandleInheritability.Inheritable);
            var handles = new[] { input.ClientSafePipeHandle.DangerousGetHandle(), output.ClientSafePipeHandle.DangerousGetHandle(), error.ClientSafePipeHandle.DangerousGetHandle() };
            var process = StartCore(executable, arguments, workingDirectory, role, handles, environment);
            input.DisposeLocalCopyOfClientHandle(); output.DisposeLocalCopyOfClientHandle(); error.DisposeLocalCopyOfClientHandle();
            return new(process, input, output, error);
        }
        catch { input?.Dispose(); output?.Dispose(); error?.Dispose(); throw; }
    }
}
