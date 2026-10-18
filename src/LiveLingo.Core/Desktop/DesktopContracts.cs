using LiveLingo.Core.Sessions;
using LiveLingo.Core.Subtitles;

namespace LiveLingo.Core.Desktop;

/// <summary>表示经身份核实的应用根，不承诺隔离浏览器标签页。</summary>
/// <param name="Name">用户可读名称。</param><param name="Identity">PID、创建时间及可执行路径。</param>
public sealed record DesktopTarget(string Name, TargetIdentity Identity)
{
    /// <summary>获取同时包含进程和采集范围的选择器标签。</summary>
    public string Label => $"{Name} · PID {Identity.ProcessId} · 全部子孙/标签页";
}

/// <summary>承载全部字幕和当前运行状态，可安全合并掉更早的完整快照。</summary>
/// <param name="SessionId">调用者分配的会话。</param><param name="Sequence">会话内递增序号。</param>
/// <param name="Subtitles">完整有效字幕及撤销集合。</param><param name="Status">运行状态或可见错误。</param>
/// <param name="BacklogSeconds">尚待识别的音频秒数。</param><param name="Active">是否仍持有会话资源。</param>
public sealed record DesktopUpdate(Guid SessionId, long Sequence, SubtitleSnapshot? Subtitles, string Status, double BacklogSeconds, bool Active);

/// <summary>隔离共享界面与实际采集、模型及窗口宿主。</summary>
public interface IDesktopSession
{
    /// <summary>发布完整快照；订阅者负责切换到 UI 线程。</summary>
    event Action<DesktopUpdate>? Updated;
    /// <summary>获取当前可核实的应用根。</summary><param name="cancellationToken">取消枚举。</param><returns>可选择的应用。</returns>
    Task<IReadOnlyList<DesktopTarget>> GetTargetsAsync(CancellationToken cancellationToken);
    /// <summary>启动一个新身份的独占会话；失败不得保留模型进程。</summary>
    /// <param name="sessionId">预先分配的会话。</param><param name="target">选定外部应用。</param><param name="settings">本次固定配置。</param>
    /// <param name="cancellationToken">取消启动。</param><returns>启动已登记的任务，实际就绪由状态报告。</returns>
    Task StartAsync(Guid sessionId, DesktopTarget target, DesktopSettings settings, CancellationToken cancellationToken);
    /// <summary>取消并等待本程序拥有的资源回收；重复请求等候同一次结束。</summary><returns>完整停止任务。</returns>
    Task StopAsync();
}

/// <summary>保存读取结果并明确区分缺失与损坏，损坏不触发自动覆盖。</summary>
/// <param name="Settings">已验证设置或安全默认值。</param><param name="Error">用户需要看到的读取错误。</param>
public sealed record SettingsLoadResult(DesktopSettings Settings, string? Error);

/// <summary>将持久化细节留在运行层。</summary>
public interface IDesktopSettingsStore
{
    /// <summary>读取本地设置，损坏时保留原文件。</summary><param name="cancellationToken">取消读取。</param><returns>值及可见错误。</returns>
    Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default);
    /// <summary>仅在明确保存请求时原子替换设置。</summary><param name="settings">完整新设置。</param><param name="cancellationToken">提交前可取消。</param><returns>提交完成任务。</returns>
    Task SaveAsync(DesktopSettings settings, CancellationToken cancellationToken = default);
}
