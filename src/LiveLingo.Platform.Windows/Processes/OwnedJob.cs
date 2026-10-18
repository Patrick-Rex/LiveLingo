using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LiveLingo.Core.Sessions;
using Microsoft.Win32.SafeHandles;

namespace LiveLingo.Platform.Windows.Processes;

/// <summary>记录只有本次成功创建并原子纳入 Job 的子进程。</summary>
/// <param name="Identity">真实实例身份。</param><param name="Role">启动职责。</param>
public sealed record OwnedProcess(TargetIdentity Identity, string Role);

/// <summary>在 CreateProcess 时原子加入 KILL_ON_JOB_CLOSE Job，失败时绝不无监管运行。</summary>
public sealed unsafe partial class OwnedJob : IDisposable
{
    private readonly SafeFileHandle job;
    private readonly List<Process> processes = [];
    private bool disposed;

    /// <summary>建立不可继承的自有 Job 并启用最后句柄关闭时终止子树。</summary>
    /// <exception cref="Win32Exception">Job 建立或配置失败。</exception>
    public OwnedJob()
    {
        job = new SafeFileHandle(CreateJobObjectW(0, null), true);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var limits = new ExtendedLimitInformation { Basic = new() { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(job.DangerousGetHandle(), 9, &limits, (uint)sizeof(ExtendedLimitInformation)))
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error);
        }
    }

    /// <summary>在原子 Job 分配和身份登记完成后恢复新进程主线程。</summary>
    /// <param name="executable">已存在的绝对 EXE 路径。</param><param name="arguments">逐个参数，不经过 shell。</param>
    /// <param name="workingDirectory">绝对工作目录。</param><param name="role">审计职责。</param>
    /// <returns>本次创建的已核实身份。</returns>
    /// <exception cref="ArgumentException">路径或角色无效。</exception>
    /// <exception cref="Win32Exception">创建、Job 属性或恢复线程失败。</exception>
    public OwnedProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory, string role)
        => StartCore(executable, arguments, workingDirectory, role, [], null);

    /// <summary>在同一原子创建路径加入可选标准管道白名单，其他句柄不可继承。</summary>
    /// <param name="executable">绝对 EXE 路径。</param><param name="arguments">独立参数。</param>
    /// <param name="workingDirectory">工作目录。</param><param name="role">审计职责。</param>
    /// <param name="standardHandles">空列表或 stdin/stdout/stderr 三个子端句柄。</param>
    /// <param name="environmentOverrides">仅此子进程的环境，缓存仍强制在 D 盘。</param><returns>已登记实例。</returns>
    private OwnedProcess StartCore(string executable, IReadOnlyList<string> arguments, string workingDirectory, string role,
        nint[] standardHandles, IReadOnlyDictionary<string, string>? environmentOverrides)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new ArgumentException("需要已存在的绝对 EXE 路径。", nameof(executable));
        if (!Path.IsPathFullyQualified(workingDirectory) || !Directory.Exists(workingDirectory)) throw new ArgumentException("需要已存在的绝对工作目录。", nameof(workingDirectory));
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        if (processes.Count >= 64) throw new InvalidOperationException("单个监管器的创建次数达到 64 次上限。");
        nuint attributeBytes = 0;
        var attributeCount = standardHandles.Length == 0 ? 1 : 2;
        InitializeProcThreadAttributeList(0, attributeCount, 0, ref attributeBytes);
        if (attributeBytes == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var command = (Quote(executable) + " " + string.Join(" ", arguments.Select(Quote)) + '\0').ToCharArray();
        var environment = BuildEnvironment(environmentOverrides);
        var attributes = Marshal.AllocHGlobal(checked((int)attributeBytes));
        var initialized = false;
        ProcessInformation process = default;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, attributeCount, 0, ref attributeBytes)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            initialized = true;
            var jobHandle = job.DangerousGetHandle();
            if (!UpdateProcThreadAttribute(attributes, 0, 0x0002000D, &jobHandle, (nuint)sizeof(nint), 0, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var startup = new StartupInfoEx { Startup = new() { Size = (uint)sizeof(StartupInfoEx), Flags = 1, ShowWindow = 0 }, AttributeList = attributes };
            fixed (char* commandPointer = command)
            fixed (char* environmentPointer = environment)
            fixed (nint* inherited = standardHandles)
            {
                if (standardHandles.Length != 0)
                {
                    if (standardHandles.Length != 3) throw new ArgumentException("标准句柄必须为三个。", nameof(standardHandles));
                    if (!UpdateProcThreadAttribute(attributes, 0, 0x00020002, inherited, (nuint)(3 * sizeof(nint)), 0, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                    startup.Startup.Flags |= 0x100;
                    startup.Startup.StandardInput = standardHandles[0];
                    startup.Startup.StandardOutput = standardHandles[1];
                    startup.Startup.StandardError = standardHandles[2];
                }
                // EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED | CREATE_NO_WINDOW.
                if (!CreateProcessW(executable, commandPointer, 0, 0, standardHandles.Length != 0, 0x00080000 | 0x00000400 | 0x00000004 | 0x08000000,
                    environmentPointer, workingDirectory, &startup, out process)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            var identity = WindowsTargets.Query((int)process.ProcessId).Identity ?? throw new InvalidOperationException("无法核实刚创建的自有进程。");
            var managed = Process.GetProcessById((int)process.ProcessId);
            // Open and retain the process handle before it can exit or its PID can be reused.
            _ = managed.Handle;
            processes.Add(managed);
            if (ResumeThread(process.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastPInvokeError());
            return new(identity, role);
        }
        catch
        {
            if (process.Process != 0) TerminateProcess(process.Process, 3);
            throw;
        }
        finally
        {
            if (process.Thread != 0) CloseHandle(process.Thread);
            if (process.Process != 0) CloseHandle(process.Process);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            GC.KeepAlive(job);
        }
    }

    /// <summary>核实完整实例是否属于本监管器，适用于启动器退出后的实际子进程。</summary>
    /// <param name="identity">待核实的实际实例。</param><returns>同一实例仍属于本 Job 时为 true。</returns>
    public bool Contains(TargetIdentity identity)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var process = OpenProcess(0x1000, false, (uint)identity.ProcessId);
        if (process == 0) return false;
        try
        {
            return IsProcessInJob(process, job.DangerousGetHandle(), out var member) && member &&
                WindowsTargets.Query(identity.ProcessId).Identity is { } current && identity.SameInstance(current);
        }
        finally { CloseHandle(process); GC.KeepAlive(job); }
    }

    /// <summary>枚举本 Job 及嵌套子 Job 的当前成员，并以完整实例身份再次核实。</summary>
    /// <returns>至多 512 个当前自有实例；成员变化导致已退出 PID 被忽略。</returns>
    public TargetIdentity[] Members()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        const int capacity = 512;
        var buffer = stackalloc byte[8 + capacity * sizeof(nint)];
        if (!QueryInformationJobObject(job.DangerousGetHandle(), 3, buffer, (uint)(8 + capacity * sizeof(nint)), out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        var assigned = *(uint*)buffer; var count = *(uint*)(buffer + 4);
        if (count > capacity || count < assigned) throw new InvalidOperationException("owned_process_audit_capacity");
        var identities = new List<TargetIdentity>();
        for (var index = 0; index < count; index++)
        {
            var pid = checked((int)(*(nuint*)(buffer + 8 + index * sizeof(nint))));
            var identity = WindowsTargets.Query(pid).Identity;
            if (identity is not null && Contains(identity)) identities.Add(identity);
        }
        GC.KeepAlive(job);
        return identities.ToArray();
    }

    /// <summary>关闭唯一 Job 句柄并在总计五秒内核对登记进程退出。</summary>
    /// <exception cref="TimeoutException">系统未及时终止自有进程。</exception>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        job.Dispose();
        var timer = Stopwatch.StartNew();
        var allExited = true;
        try
        {
            foreach (var process in processes)
            {
                var remaining = Math.Max(0, 5000 - (int)timer.ElapsedMilliseconds);
                allExited &= process.WaitForExit(remaining);
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        if (!allExited) throw new TimeoutException("Job 关闭后仍有登记自有进程未退出。");
    }

    /// <summary>按 Windows CommandLineToArgvW 约定引用单个参数。</summary>
    /// <param name="argument">原始参数。</param><returns>可放入 CreateProcess 命令行的参数。</returns>
    internal static string Quote(string argument)
    {
        if (argument.IndexOf('\0') >= 0) throw new ArgumentException("参数不能包含 NUL。", nameof(argument));
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            result.Append(character);
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    /// <summary>为自有子进程建立继承环境的私有副本，并将新增缓存显式放在 D 盘。</summary>
    /// <param name="overrides">仅对自有子进程生效的显式环境。</param><returns>双 NUL 结尾的 Unicode 环境块。</returns>
    private static char[] BuildEnvironment(IReadOnlyDictionary<string, string>? overrides)
    {
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables()) values[(string)entry.Key] = (string)entry.Value!;
        if (overrides is not null) foreach (var pair in overrides)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Contains('=') || pair.Key.Contains('\0') || pair.Value.Contains('\0'))
                throw new ArgumentException("子进程环境字段无效。", nameof(overrides));
            values[pair.Key] = pair.Value;
        }
        values["TEMP"] = values["TMP"] = @"D:\Soft Ware\LiveLingo\cache\tmp";
        values["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = @"D:\Soft Ware\LiveLingo\cache\dotnet-bundle";
        values["HF_HOME"] = @"D:\Soft Ware\LiveLingo\cache\huggingface";
        values["HF_HUB_CACHE"] = @"D:\Soft Ware\LiveLingo\cache\huggingface\hub";
        values["UV_CACHE_DIR"] = @"D:\Soft Ware\LiveLingo\cache\uv";
        values["PIP_CACHE_DIR"] = @"D:\Soft Ware\LiveLingo\cache\pip";
        return (string.Join('\0', values.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0").ToCharArray();
    }

    /// <summary>Job 的基础限额 ABI。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        internal long ProcessUserTime, JobUserTime;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass, SchedulingClass;
    }
    /// <summary>Job I/O 计数的 ABI。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }
    /// <summary>包含 KILL_ON_JOB_CLOSE 的扩展限额 ABI。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        internal BasicLimitInformation Basic;
        internal IoCounters Io;
        internal nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    /// <summary>标准进程启动结构 ABI。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal uint Size;
        internal nint Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal ushort ShowWindow, ReservedSize;
        internal nint ReservedData, StandardInput, StandardOutput, StandardError;
    }
    /// <summary>包含创建时 Job 列表的扩展启动结构。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        internal StartupInfo Startup;
        internal nint AttributeList;
    }
    /// <summary>保存 CreateProcess 返回的明确自有句柄。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal nint Process, Thread;
        internal uint ProcessId, ThreadId;
    }

    /// <summary>创建 Job。</summary><param name="attributes">不可继承属性。</param><param name="name">可空名称。</param><returns>句柄。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);
    /// <summary>设置 Job 限额。</summary><param name="job">句柄。</param><param name="kind">信息类。</param><param name="information">结构。</param><param name="length">大小。</param><returns>成功标志。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int kind, void* information, uint length);
    /// <summary>查询本 Job 成员，包含嵌套 Job 的子孙。</summary>
    /// <param name="jobHandle">明确自有 Job。</param><param name="kind">信息类。</param><param name="information">有限输出缓冲。</param>
    /// <param name="length">缓冲字节数。</param><param name="returned">实际输出大小。</param><returns>查询成功标志。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(nint jobHandle, int kind, void* information, uint length, out uint returned);
    /// <summary>准备进程扩展属性表。</summary><param name="attributes">表。</param><param name="count">数量。</param><param name="flags">标志。</param><param name="size">容量。</param><returns>成功标志。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint attributes, int count, uint flags, ref nuint size);
    /// <summary>将 Job 列表绑定到创建属性。</summary><param name="attributes">表。</param><param name="flags">标志。</param><param name="attribute">键。</param><param name="value">值。</param><param name="size">值大小。</param><param name="previous">保留。</param><param name="returnSize">保留。</param><returns>成功标志。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint attributes, uint flags, nuint attribute, void* value, nuint size, nint previous, nint returnSize);
    /// <summary>释放初始化属性表。</summary><param name="attributes">表。</param>
    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint attributes);
    /// <summary>按显式可执行路径创建已纳入 Job 的进程。</summary><param name="application">路径。</param><param name="command">可变命令行。</param><param name="processAttributes">属性。</param><param name="threadAttributes">属性。</param><param name="inherit">句柄继承。</param><param name="flags">创建标志。</param><param name="environment">环境块。</param><param name="directory">工作目录。</param><param name="startup">扩展启动结构。</param><param name="process">结果。</param><returns>成功标志。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(string application, char* command, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, char* environment, string directory, StartupInfoEx* startup, out ProcessInformation process);
    /// <summary>恢复已登记进程的主线程。</summary><param name="thread">线程句柄。</param><returns>旧挂起次数。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);
    /// <summary>只终止本次创建失败的进程句柄。</summary><param name="process">句柄。</param><param name="code">退出码。</param><returns>成功标志。</returns>
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint code);
    /// <summary>关闭本次创建返回的句柄。</summary><param name="handle">句柄。</param><returns>成功标志。</returns>
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary>打开用于查询 Job 成员身份的进程句柄。</summary>
    /// <param name="access">查询权限。</param><param name="inherit">是否继承。</param><param name="pid">进程编号。</param><returns>句柄或零。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    /// <summary>核实已打开进程是否属于指定 Job。</summary>
    /// <param name="process">进程句柄。</param><param name="jobHandle">自有 Job。</param><param name="member">是否为成员。</param><returns>查询是否成功。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(nint process, nint jobHandle, [MarshalAs(UnmanagedType.Bool)] out bool member);
}
