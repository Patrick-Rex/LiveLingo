using System.ComponentModel;
using System.Runtime.InteropServices;
using LiveLingo.Core.Sessions;

namespace LiveLingo.Platform.Windows.Processes;

/// <summary>保存进程查询事实，权限或退出失败仍作为明确诊断返回。</summary>
/// <param name="ProcessId">进程编号。</param><param name="ParentProcessId">快照中的父编号。</param>
/// <param name="Name">快照文件名。</param><param name="Identity">核实后的实例身份。</param>
/// <param name="IsApplicationRoot">父实例不属于同一个可执行应用。</param><param name="Error">查询失败信息。</param>
public sealed record TargetQuery(int ProcessId, int ParentProcessId, string Name, TargetIdentity? Identity, bool IsApplicationRoot, string? Error);

/// <summary>使用只读 Win32 查询枚举实际应用根，不以窗口标题替代身份。</summary>
public static partial class WindowsTargets
{
    /// <summary>枚举快照并核实创建时间及可执行路径；保留失败项。</summary>
    /// <returns>全部进程查询结果。</returns>
    public static unsafe IReadOnlyList<TargetQuery> Enumerate()
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var results = new List<TargetQuery>();
        try
        {
            var entry = new ProcessEntry { Size = (uint)sizeof(ProcessEntry) };
            if (!Process32FirstW(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            do
            {
                var query = Query((int)entry.ProcessId);
                results.Add(query with { ParentProcessId = (int)entry.ParentProcessId, Name = new string(entry.ExecutableName) });
            } while (Process32NextW(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        var identities = results.Where(x => x.Identity is not null).ToDictionary(x => x.ProcessId, x => x.Identity!);
        return results.Select(x => x with
        {
            IsApplicationRoot = x.Identity is not null && (!identities.TryGetValue(x.ParentProcessId, out var parent) ||
                parent.CreationTimeUtc > x.Identity.CreationTimeUtc || !x.Identity.SameApplication(parent))
        }).ToArray();
    }

    /// <summary>查询指定实例的真实路径与创建时间；不启动或结束任何进程。</summary>
    /// <param name="processId">目标 PID。</param>
    /// <returns>成功身份或明确错误。</returns>
    public static unsafe TargetQuery Query(int processId)
    {
        var handle = OpenProcess(0x1000, false, (uint)processId);
        if (handle == 0) return new(processId, 0, "", null, false, new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        try
        {
            var path = stackalloc char[32768];
            uint length = 32768;
            if (!QueryFullProcessImageNameW(handle, 0, path, ref length) ||
                !GetProcessTimes(handle, out var creation, out _, out _, out _))
                return new(processId, 0, "", null, false, new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            var executable = new string(path, 0, (int)length);
            var identity = new TargetIdentity(processId, DateTimeOffset.FromFileTime(creation), executable);
            return new(processId, 0, Path.GetFileName(executable), identity, false, null);
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>为固定路径选择唯一应用根；多候选时返回空。</summary>
    /// <param name="original">原选应用。</param><param name="candidates">当前查询结果。</param>
    /// <param name="excludedInstances">会话开始时已存在的其他实例身份。</param>
    /// <returns>唯一候选；不存在或不唯一时为空。</returns>
    public static TargetIdentity? UniqueMatch(TargetIdentity original, IEnumerable<TargetQuery> candidates,
        IReadOnlySet<TargetIdentity> excludedInstances)
    {
        var matches = candidates.Where(x => x.IsApplicationRoot && x.Identity is not null && original.SameApplication(x.Identity))
            .Select(x => x.Identity!).Where(x => !excludedInstances.Contains(x)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>Toolhelp 进程快照的 Unicode 结构。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct ProcessEntry
    {
        /// <summary>结构大小。</summary>
        internal uint Size;
        /// <summary>保留。</summary>
        internal uint Usage;
        /// <summary>PID。</summary>
        internal uint ProcessId;
        /// <summary>保留堆地址。</summary>
        internal nuint DefaultHeapId;
        /// <summary>保留模块编号。</summary>
        internal uint ModuleId;
        /// <summary>线程数。</summary>
        internal uint Threads;
        /// <summary>父 PID。</summary>
        internal uint ParentProcessId;
        /// <summary>基础优先级。</summary>
        internal int BasePriority;
        /// <summary>保留标志。</summary>
        internal uint Flags;
        /// <summary>可执行文件名。</summary>
        internal fixed char ExecutableName[260];
    }

    /// <summary>创建只读进程快照。</summary><param name="flags">快照标志。</param><param name="pid">范围。</param><returns>快照句柄。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint pid);
    /// <summary>读取首个进程。</summary><param name="snapshot">快照。</param><param name="entry">结构。</param><returns>是否成功。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(nint snapshot, ref ProcessEntry entry);
    /// <summary>读取后续进程。</summary><param name="snapshot">快照。</param><param name="entry">结构。</param><returns>是否成功。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(nint snapshot, ref ProcessEntry entry);
    /// <summary>打开查询句柄。</summary><param name="access">权限。</param><param name="inherit">继承标志。</param><param name="pid">PID。</param><returns>句柄。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    /// <summary>查询完整路径。</summary><param name="process">句柄。</param><param name="flags">标志。</param><param name="path">输出。</param><param name="length">字符容量与长度。</param><returns>是否成功。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(nint process, uint flags, char* path, ref uint length);
    /// <summary>查询实例创建时间。</summary><param name="process">句柄。</param><param name="creation">创建 FILETIME。</param><param name="exit">退出时间。</param><param name="kernel">内核时间。</param><param name="user">用户时间。</param><returns>是否成功。</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);
    /// <summary>关闭本次打开的查询句柄。</summary><param name="handle">句柄。</param><returns>是否成功。</returns>
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
