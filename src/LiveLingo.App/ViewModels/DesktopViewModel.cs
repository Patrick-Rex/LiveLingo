using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveLingo.App.Services;
using LiveLingo.Core.Desktop;

namespace LiveLingo.App.ViewModels;

/// <summary>管理桌面交互和完整字幕投影，模型及原生窗口操作由中立服务和宿主承担。</summary>
public sealed partial class DesktopViewModel : ObservableObject
{
    private readonly IDesktopSession session;
    private readonly IDesktopSettingsStore store;
    private readonly SnapshotPump pump;
    private readonly CancellationTokenSource lifetime = new();
    private DesktopSettings settings = new();
    private bool closing;

    /// <summary>绑定会话、设置及 UI 调度边界。</summary><param name="session">独占会话。</param><param name="store">设置文件服务。</param><param name="post">UI 调度。</param>
    public DesktopViewModel(IDesktopSession session, IDesktopSettingsStore store, Action<Action> post)
    { this.session = session; this.store = store; pump = new(post, Apply); session.Updated += pump.Offer; }

    /// <summary>通知宿主应用显隐或布局策略。</summary>
    public event Action? OverlayChanged;
    /// <summary>通知宿主应用已验证的布局和热键设置。</summary>
    public event Action<DesktopSettings>? SettingsApplied;
    /// <summary>获取用户可选择的明确模型标签。</summary>
    public string[] Models { get; } = ["translategemma:12b", "translategemma:4b"];
    /// <summary>获取已验证的识别语言策略。</summary>
    public string[] Languages { get; } = ["auto", "Japanese"];
    /// <summary>获取可用功能键编号。</summary>
    public int[] FunctionKeys { get; } = Enumerable.Range(1, 11).ToArray();
    /// <summary>获取当前配置的不可变副本。</summary>
    public DesktopSettings CurrentSettings => settings;
    /// <summary>获取有效会话身份，用于显式验证和外部诊断。</summary>
    public Guid SessionId { get; private set; }

    /// <summary>获取根进程选择列表。</summary>
    [ObservableProperty] public partial IReadOnlyList<DesktopTarget> Targets { get; set; } = [];
    /// <summary>获取选定的完整进程身份。</summary>
    [ObservableProperty] public partial DesktopTarget? SelectedTarget { get; set; }
    /// <summary>获取流水线是否仍活动。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit))] public partial bool Active { get; set; }
    /// <summary>获取正在执行的交互操作状态。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit))] public partial bool Busy { get; set; }
    /// <summary>获取设置与目标是否允许改变。</summary>
    public bool CanEdit => !Active && !Busy && !closing;
    /// <summary>获取运行状态，不隐藏实际错误或积压。</summary>
    [ObservableProperty] public partial string Status { get; set; } = "尚未开始 · 请选择应用";
    /// <summary>获取用户可见的交互或依赖错误。</summary>
    [ObservableProperty] public partial string Error { get; set; } = "";
    /// <summary>获取未识别音频的秒数。</summary>
    [ObservableProperty] public partial string Backlog { get; set; } = "积压 0.0 秒";
    /// <summary>获取完整有效字幕历史，不按文本删除真实重复。</summary>
    [ObservableProperty] public partial IReadOnlyList<SubtitleRow> History { get; set; } = [];
    /// <summary>获取字幕窗最近两个完整非空块。</summary>
    [ObservableProperty] public partial IReadOnlyList<SubtitleRow> OverlayRows { get; set; } = [];
    /// <summary>获取字幕窗是否显示。</summary>
    [ObservableProperty] public partial bool OverlayVisible { get; set; } = true;
    /// <summary>获取字幕窗是否允许拖动及滚动。</summary>
    [ObservableProperty] public partial bool LayoutMode { get; set; }
    /// <summary>获取待保存翻译模型。</summary>
    [ObservableProperty] public partial string TranslationModel { get; set; } = "translategemma:12b";
    /// <summary>获取待保存识别语言。</summary>
    [ObservableProperty] public partial string AsrLanguage { get; set; } = "auto";
    /// <summary>获取隔离解释器路径输入。</summary>
    [ObservableProperty] public partial string PythonPath { get; set; } = new DesktopSettings().PythonPath;
    /// <summary>获取 Qwen 权重目录输入。</summary>
    [ObservableProperty] public partial string ModelPath { get; set; } = new DesktopSettings().ModelPath;
    /// <summary>获取 Ollama 程序路径输入。</summary>
    [ObservableProperty] public partial string OllamaPath { get; set; } = new DesktopSettings().OllamaPath;
    /// <summary>获取翻译权重目录输入。</summary>
    [ObservableProperty] public partial string OllamaModelsPath { get; set; } = new DesktopSettings().OllamaModelsPath;
    /// <summary>获取应用数据目录输入。</summary>
    [ObservableProperty] public partial string DataPath { get; set; } = new DesktopSettings().DataPath;
    /// <summary>获取缓存目录输入。</summary>
    [ObservableProperty] public partial string CachePath { get; set; } = new DesktopSettings().CachePath;
    /// <summary>获取待应用中文字号。</summary>
    [ObservableProperty] public partial double FontSize { get; set; } = 28;
    /// <summary>获取待应用背景不透明度。</summary>
    [ObservableProperty] public partial double BackgroundOpacity { get; set; } = 0.75;
    /// <summary>获取启停功能键。</summary>
    [ObservableProperty] public partial int StartKey { get; set; } = 7;
    /// <summary>获取显隐功能键。</summary>
    [ObservableProperty] public partial int VisibilityKey { get; set; } = 8;
    /// <summary>获取布局功能键。</summary>
    [ObservableProperty] public partial int LayoutKey { get; set; } = 9;

    /// <summary>读取设置并枚举目标；损坏文件不会在初始化时被覆盖。</summary><returns>初始化完成任务。</returns>
    public async Task InitializeAsync()
    {
        var result = await store.LoadAsync(lifetime.Token);
        settings = result.Settings;
        TranslationModel = settings.TranslationModel; AsrLanguage = settings.AsrLanguage;
        PythonPath = settings.PythonPath; ModelPath = settings.ModelPath; OllamaPath = settings.OllamaPath;
        OllamaModelsPath = settings.OllamaModelsPath; DataPath = settings.DataPath; CachePath = settings.CachePath;
        FontSize = settings.FontSize; BackgroundOpacity = settings.BackgroundOpacity;
        StartKey = settings.StartKey; VisibilityKey = settings.VisibilityKey; LayoutKey = settings.LayoutKey;
        Error = result.Error ?? "";
        SettingsApplied?.Invoke(settings);
        await RefreshAsync();
    }

    /// <summary>刷新可核实应用，尽可能保持当前选定身份。</summary><returns>刷新完成任务。</returns>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!CanEdit) return;
        Busy = true;
        try
        {
            var previous = SelectedTarget?.Identity;
            Targets = await session.GetTargetsAsync(lifetime.Token);
            SelectedTarget = Targets.FirstOrDefault(x => previous?.SameInstance(x.Identity) == true) ??
                Targets.FirstOrDefault(x => x.Name.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase)) ?? Targets.FirstOrDefault();
        }
        catch (Exception error) when (!closing) { Error = error.Message; }
        finally { Busy = false; }
    }

    /// <summary>互斥切换启动和停止；开始前显式建立新会话屏障。</summary><returns>交互完成任务。</returns>
    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (closing || Busy) return;
        Busy = true;
        try
        {
            Error = "";
            if (Active) { Status = "正在停止并回收资源…"; await session.StopAsync(); pump.EndSession(); Active = false; return; }
            if (SelectedTarget is null) throw new InvalidOperationException("请先选择可核实的应用根进程。");
            var next = ReadSettings(); next.Validate();
            settings = next; SettingsApplied?.Invoke(settings);
            SessionId = Guid.NewGuid(); pump.SwitchSession(SessionId);
            History = []; OverlayRows = []; Active = true; Status = "正在加载模型…";
            await session.StartAsync(SessionId, SelectedTarget, settings, lifetime.Token);
        }
        catch (Exception error) { Active = false; Error = error.Message; Status = "未启动或启动失败"; }
        finally { Busy = false; }
    }

    /// <summary>显式保存完整设置；失败保留磁盘旧值并显示原因。</summary><returns>保存完成任务。</returns>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanEdit) return;
        Busy = true;
        try { var next = ReadSettings(); await store.SaveAsync(next, lifetime.Token); settings = next; Error = ""; SettingsApplied?.Invoke(settings); Status = "设置已保存"; }
        catch (Exception error) { Error = "设置未保存：" + error.Message; }
        finally { Busy = false; }
    }

    /// <summary>请求宿主无激活地显示或隐藏字幕。</summary>
    [RelayCommand] private void ToggleOverlay() { if (closing) return; OverlayVisible = !OverlayVisible; OverlayChanged?.Invoke(); }
    /// <summary>请求临时交互模式；常规模式恢复原生穿透。</summary>
    [RelayCommand] private void ToggleLayout() { if (closing) return; LayoutMode = !LayoutMode; OverlayVisible = true; OverlayChanged?.Invoke(); }

    /// <summary>保存当前窗口位置用于下一次显式保存，布局不修改语义输入。</summary>
    /// <param name="x">物理像素横坐标。</param><param name="y">物理像素纵坐标。</param><param name="width">DIP 宽度。</param><param name="height">DIP 高度。</param>
    public void RememberBounds(int x, int y, double width, double height)
        => settings = settings with { OverlayX = x, OverlayY = y, OverlayWidth = width, OverlayHeight = height };

    /// <summary>从 UI 线程关闭快照入口，然后等待自有会话完整停止。</summary><returns>资源清理任务。</returns>
    public async Task CloseAsync()
    {
        if (closing) return;
        closing = true; OnPropertyChanged(nameof(CanEdit)); pump.Close(); session.Updated -= pump.Offer;
        await lifetime.CancelAsync();
        await session.StopAsync(); Active = false;
        lifetime.Dispose();
    }

    /// <summary>在 UI 线程投影全部有效身份，任何撤销和空块都不再可见。</summary><param name="value">通过屏障的完整快照。</param>
    private void Apply(DesktopUpdate value)
    {
        if (!value.Active) pump.SwitchSession(Guid.Empty);
        Active = value.Active; Status = value.Status; Backlog = $"积压 {value.BacklogSeconds:F1} 秒";
        if (value.Subtitles is not { } subtitles) return;
        History = subtitles.Segments.Where(x => !subtitles.RetiredIds.Contains(x.Key.Id) && !string.IsNullOrWhiteSpace(x.Source)).Select(x => new SubtitleRow(x)).ToArray();
        OverlayRows = History.TakeLast(2).ToArray();
    }

    /// <summary>将编辑输入和最近布局组合为本次不可变设置。</summary><returns>完整设置。</returns>
    private DesktopSettings ReadSettings() => settings with
    {
        TranslationModel = TranslationModel, AsrLanguage = AsrLanguage, PythonPath = PythonPath, ModelPath = ModelPath,
        OllamaPath = OllamaPath, OllamaModelsPath = OllamaModelsPath, DataPath = DataPath, CachePath = CachePath,
        FontSize = FontSize, BackgroundOpacity = BackgroundOpacity, StartKey = StartKey, VisibilityKey = VisibilityKey, LayoutKey = LayoutKey
    };
}
