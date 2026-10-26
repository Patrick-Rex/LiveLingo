using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
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
    private DesktopSettings persistedSettings = new();
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private Task appearanceSaveTask = Task.CompletedTask;
    private long appearanceVersion;
    private bool initialized;
    private bool loadingSettings;
    private bool allowAutoSave;
    private bool closing;

    /// <summary>绑定会话、设置及 UI 调度边界。</summary><param name="session">独占会话。</param><param name="store">设置文件服务。</param><param name="post">UI 调度。</param>
    public DesktopViewModel(IDesktopSession session, IDesktopSettingsStore store, Action<Action> post)
    { this.session = session; this.store = store; pump = new(post, Apply); session.Updated += pump.Offer; }

    /// <summary>通知宿主应用显隐或布局策略。</summary>
    public event Action? OverlayChanged;
    /// <summary>通知宿主应用已验证的布局和热键设置。</summary>
    public event Action<DesktopSettings>? SettingsApplied;
    /// <summary>通知宿主即时更新字幕视觉，不重设窗口位置或模型会话。</summary>
    public event Action<DesktopSettings>? AppearanceChanged;
    /// <summary>获取用户可选择的明确模型标签。</summary>
    public string[] Models { get; } = ["translategemma:12b", "translategemma:4b"];
    /// <summary>获取已验证的识别语言策略。</summary>
    public string[] Languages { get; } = ["auto", "Japanese"];
    /// <summary>获取与字幕显示模式数值顺序一致的本地化选项。</summary>
    public string[] DisplayModes { get; } = ["双语", "仅中文", "仅原文"];
    /// <summary>获取与桌面主题数值顺序一致的显示名称。</summary>
    public string[] Themes { get; } = ["跟随系统", "浅色", "深色"];
    /// <summary>获取可选历史保留天数，零表示永久保留。</summary>
    public int[] HistoryRetentionOptions { get; } = [7, 30, 90, 0];
    /// <summary>获取保留期选择器的人类可读名称。</summary>
    public string[] HistoryRetentionLabels { get; } = ["7 天", "30 天", "90 天", "永久"];
    /// <summary>将保留期显示选项映射到待应用的天数，零仅作为持久化值。</summary>
    public int HistoryRetentionIndex
    {
        get => Array.IndexOf(HistoryRetentionOptions, HistoryRetentionDays);
        set { if (!closing && value >= 0 && value < HistoryRetentionOptions.Length) HistoryRetentionDays = HistoryRetentionOptions[value]; }
    }
    /// <summary>获取或设置即时生效并自动保存的桌面主题。</summary>
    public int ThemeIndex
    {
        get => (int)settings.Theme;
        set
        {
            if (closing || !Enum.IsDefined((DesktopTheme)value) || value == ThemeIndex) return;
            settings = settings with { Theme = (DesktopTheme)value };
            OnPropertyChanged(); PreviewAppearance();
        }
    }
    /// <summary>获取或设置当前一级页面；页面切换保留设置草稿。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsLivePage)), NotifyPropertyChangedFor(nameof(IsHistoryPage)), NotifyPropertyChangedFor(nameof(IsSettingsPage))]
    public partial int SelectedPageIndex { get; set; }
    /// <summary>获取实时翻译页是否可见。</summary>
    public bool IsLivePage => SelectedPageIndex == 0;
    /// <summary>获取历史记录页是否可见。</summary>
    public bool IsHistoryPage => SelectedPageIndex == 1;
    /// <summary>获取设置页是否可见。</summary>
    public bool IsSettingsPage => SelectedPageIndex == 2;
    /// <summary>获取或设置设置页当前分类。</summary>
    [ObservableProperty] public partial int SelectedSettingsIndex { get; set; }
    /// <summary>获取已生效的模型和识别策略摘要。</summary>
    public string RuntimeSummary => $"{settings.TranslationModel} · {(settings.AsrLanguage == "auto" ? "自动识别语言" : "日语")}";
    /// <summary>获取或设置浮窗显示模式；忽略选择控件初始化的空选项。</summary>
    public int DisplayModeIndex
    {
        get => (int)settings.DisplayMode;
        set
        {
            if (closing || !Enum.IsDefined((SubtitleDisplayMode)value) || value == DisplayModeIndex) return;
            settings = settings with { DisplayMode = (SubtitleDisplayMode)value };
            OnPropertyChanged(); OnPropertyChanged(nameof(PreviewSourceVisible)); OnPropertyChanged(nameof(PreviewTranslationVisible)); PreviewAppearance();
        }
    }
    /// <summary>获取可用功能键编号。</summary>
    public int[] FunctionKeys { get; } = Enumerable.Range(1, 11).ToArray();
    /// <summary>获取当前配置的不可变副本。</summary>
    public DesktopSettings CurrentSettings => settings;
    /// <summary>获取有效会话身份，用于显式验证和外部诊断。</summary>
    public Guid SessionId { get; private set; }

    /// <summary>获取根进程选择列表。</summary>
    [ObservableProperty] public partial IReadOnlyList<DesktopTarget> Targets { get; set; } = [];
    /// <summary>获取选定的完整进程身份。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanToggle))] public partial DesktopTarget? SelectedTarget { get; set; }
    /// <summary>获取流水线是否仍活动。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit)), NotifyPropertyChangedFor(nameof(CanToggle)), NotifyPropertyChangedFor(nameof(SessionButtonText))] public partial bool Active { get; set; }
    /// <summary>获取正在执行的交互操作状态。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit)), NotifyPropertyChangedFor(nameof(CanToggle)), NotifyPropertyChangedFor(nameof(SessionButtonText))] public partial bool Busy { get; set; }
    /// <summary>获取设置与目标是否允许改变。</summary>
    public bool CanEdit => !Active && !Busy && !closing;
    /// <summary>获取主操作是否可执行；未保存运行设置必须先保存或撤销。</summary>
    public bool CanToggle => initialized && !Busy && !closing && (Active || SelectedTarget is not null && !HasPendingSettings);
    /// <summary>获取符合当前操作状态的主按钮文案。</summary>
    public string SessionButtonText => Busy ? "正在处理…" : Active ? "停止翻译" : "开始翻译";
    /// <summary>获取模型、资源路径或快捷键是否尚未手动提交。</summary>
    public bool HasPendingSettings => ReadSettings() != settings;
    /// <summary>获取运行配置的待保存提示。</summary>
    public string PendingSettingsText => HasPendingSettings ? "有待应用的设置，请应用或撤销后开始" : "设置已同步";
    /// <summary>获取独立于会话状态的手动保存结果。</summary>
    [ObservableProperty] public partial string SettingsSaveMessage { get; set; } = "";
    /// <summary>获取字幕即时预览与持久化结果。</summary>
    [ObservableProperty] public partial string AppearanceSaveMessage { get; set; } = "主题和字幕外观自动保存";
    /// <summary>获取字幕保存是否需要用户重试。</summary>
    [ObservableProperty] public partial bool AppearanceSaveFailed { get; set; }
    /// <summary>获取运行状态，不隐藏实际错误或积压。</summary>
    [ObservableProperty] public partial string Status { get; set; } = "尚未开始 · 请选择应用";
    /// <summary>获取用户可见的交互或依赖错误。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] public partial string Error { get; set; } = "";
    /// <summary>获取是否需要显示错误区域，空状态不占用控制窗空间。</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);
    /// <summary>获取未识别音频的秒数。</summary>
    [ObservableProperty] public partial string Backlog { get; set; } = "积压 0.0 秒";
    /// <summary>获取完整有效字幕历史，不按文本删除真实重复。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLiveHistory))] public partial IReadOnlyList<SubtitleRow> History { get; set; } = [];
    /// <summary>获取实时页是否已收到有效字幕。</summary>
    public bool HasLiveHistory => History.Count > 0;
    /// <summary>获取合成字幕预览是否显示原文。</summary>
    public bool PreviewSourceVisible => settings.DisplayMode != SubtitleDisplayMode.TranslationOnly;
    /// <summary>获取合成字幕预览是否显示译文。</summary>
    public bool PreviewTranslationVisible => settings.DisplayMode != SubtitleDisplayMode.SourceOnly;
    /// <summary>获取与实际浮窗一致的原文字号。</summary>
    public double PreviewSourceFontSize => FontSize * 18 / 28;
    /// <summary>获取字幕窗最近两块或回看锁定身份的当前有效版本。</summary>
    [ObservableProperty] public partial IReadOnlyList<SubtitleRow> OverlayRows { get; set; } = [];
    /// <summary>获取是否跟随最新字幕；暂停时只更新当前阅读身份的权威版本。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsReviewing))]
    public partial bool FollowLatest { get; private set; } = true;
    /// <summary>获取是否显示回看提示及返回最新入口。</summary>
    public bool IsReviewing => !FollowLatest;
    /// <summary>获取字幕窗是否显示。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OverlayToggleText))] public partial bool OverlayVisible { get; set; } = true;
    /// <summary>获取当前可执行的字幕显隐操作。</summary>
    public string OverlayToggleText => OverlayVisible ? "隐藏字幕" : "显示字幕";
    /// <summary>获取字幕窗是否允许拖动及滚动。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LayoutActionText))] public partial bool LayoutMode { get; set; }
    /// <summary>获取字幕右上角锁定按钮的下一步操作。</summary>
    public string LayoutActionText => LayoutMode ? "锁定位置" : "解锁拖动";
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
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PreviewSourceFontSize))] public partial double FontSize { get; set; } = 28;
    /// <summary>获取待应用背景不透明度。</summary>
    [ObservableProperty] public partial double BackgroundOpacity { get; set; } = 0.75;
    /// <summary>获取启停功能键。</summary>
    [ObservableProperty] public partial int StartKey { get; set; } = 7;
    /// <summary>获取显隐功能键。</summary>
    [ObservableProperty] public partial int VisibilityKey { get; set; } = 8;
    /// <summary>获取布局功能键。</summary>
    [ObservableProperty] public partial int LayoutKey { get; set; } = 9;
    /// <summary>获取或设置待明确应用的历史保留天数。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HistoryRetentionIndex))] public partial int HistoryRetentionDays { get; set; } = 30;

    /// <summary>读取设置并枚举目标；损坏文件不会在初始化时被覆盖。</summary><returns>初始化完成任务。</returns>
    public async Task InitializeAsync()
    {
        var result = await store.LoadAsync(lifetime.Token);
        settings = result.Settings; persistedSettings = settings; loadingSettings = true;
        OnPropertyChanged(nameof(DisplayModeIndex));
        OnPropertyChanged(nameof(PreviewSourceVisible)); OnPropertyChanged(nameof(PreviewTranslationVisible));
        OnPropertyChanged(nameof(ThemeIndex)); OnPropertyChanged(nameof(RuntimeSummary));
        LoadRuntimeInputs();
        FontSize = settings.FontSize; BackgroundOpacity = settings.BackgroundOpacity;
        loadingSettings = false; initialized = true; allowAutoSave = result.Error is null;
        OnPropertyChanged(nameof(CanToggle));
        Error = result.Error ?? "";
        if (!allowAutoSave) AppearanceSaveMessage = "原设置加载失败，自动保存已暂停；停止后手动保存可恢复";
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
        if (!CanToggle) return;
        Busy = true;
        try
        {
            Error = "";
            if (Active) { Status = "正在停止并回收资源…"; await session.StopAsync(); pump.EndSession(); Active = false; return; }
            if (SelectedTarget is null) throw new InvalidOperationException("请先选择可核实的应用根进程。");
            settings.Validate();
            SessionId = Guid.NewGuid(); pump.SwitchSession(SessionId);
            History = []; OverlayRows = []; FollowLatest = true; Active = true; Status = "正在加载模型…";
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
        try
        {
            await saveGate.WaitAsync(lifetime.Token);
            try
            {
                var next = ReadSettings(); next.Validate();
                await store.SaveAsync(next, lifetime.Token);
                persistedSettings = next; settings = CopyAppearance(next, settings); allowAutoSave = true;
                SettingsSaveMessage = "运行设置已保存"; Error = "";
                NotifyPendingSettings(); SettingsApplied?.Invoke(settings);
                OnPropertyChanged(nameof(RuntimeSummary));
            }
            finally { saveGate.Release(); }
            ScheduleAppearanceSave();
        }
        catch (Exception error) { SettingsSaveMessage = "设置未保存：" + error.Message; Error = SettingsSaveMessage; }
        finally { Busy = false; }
    }

    /// <summary>撤销尚未提交的运行设置，保留即时生效的字幕外观和位置。</summary>
    [RelayCommand]
    private void RevertSettings()
    {
        if (!CanEdit) return;
        LoadRuntimeInputs(); SettingsSaveMessage = "已撤销运行设置修改"; NotifyPendingSettings();
    }

    /// <summary>将已提交运行设置回填到编辑控件，不覆盖字幕视觉输入。</summary>
    private void LoadRuntimeInputs()
    {
        TranslationModel = settings.TranslationModel; AsrLanguage = settings.AsrLanguage;
        PythonPath = settings.PythonPath; ModelPath = settings.ModelPath; OllamaPath = settings.OllamaPath;
        OllamaModelsPath = settings.OllamaModelsPath; DataPath = settings.DataPath; CachePath = settings.CachePath;
        StartKey = settings.StartKey; VisibilityKey = settings.VisibilityKey; LayoutKey = settings.LayoutKey;
        HistoryRetentionDays = settings.HistoryRetentionDays;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(TranslationModel) or nameof(AsrLanguage) or nameof(PythonPath) or nameof(ModelPath) or
            nameof(OllamaPath) or nameof(OllamaModelsPath) or nameof(DataPath) or nameof(CachePath) or nameof(StartKey) or nameof(VisibilityKey) or nameof(LayoutKey) or nameof(HistoryRetentionDays))
        {
            SettingsSaveMessage = ""; NotifyPendingSettings();
        }
    }

    /// <summary>刷新手动保存提示及启动按钮的可执行状态。</summary>
    private void NotifyPendingSettings()
    {
        OnPropertyChanged(nameof(HasPendingSettings)); OnPropertyChanged(nameof(PendingSettingsText)); OnPropertyChanged(nameof(CanToggle));
    }

    /// <summary>字号滑块和浮窗按钮共用即时预览与延迟保存。</summary><param name="value">中文 DIP 字号。</param>
    partial void OnFontSizeChanged(double value)
    {
        if (loadingSettings || closing || !double.IsFinite(value) || value is < 16 or > 64) return;
        settings = settings with { FontSize = value }; PreviewAppearance();
    }

    /// <summary>背景滑块运行中即时生效并自动保存。</summary><param name="value">背景不透明度。</param>
    partial void OnBackgroundOpacityChanged(double value)
    {
        if (loadingSettings || closing || !double.IsFinite(value) || value is < 0.1 or > 1) return;
        settings = settings with { BackgroundOpacity = value }; PreviewAppearance();
    }

    /// <summary>仅向浮窗推送视觉改变，保存使用独立串行路径。</summary>
    private void PreviewAppearance() { AppearanceChanged?.Invoke(settings); ScheduleAppearanceSave(); }

    /// <summary>合并连续拖动和滑块变化，只保持一个待完成的自动保存任务。</summary>
    private void ScheduleAppearanceSave()
    {
        if (!initialized || closing || !allowAutoSave) return;
        appearanceVersion++;
        AppearanceSaveFailed = false; AppearanceSaveMessage = "外观已生效，等待自动保存…";
        if (appearanceSaveTask.IsCompleted) appearanceSaveTask = AutoSaveAppearanceAsync();
    }

    /// <summary>等待连续调整稳定后串行写入；失败可见且不无限重试。</summary><returns>有界于本次调整与窗口生命周期的任务。</returns>
    private async Task AutoSaveAppearanceAsync()
    {
        try
        {
            while (!closing)
            {
                var version = appearanceVersion;
                await Task.Delay(TimeSpan.FromMilliseconds(400), lifetime.Token);
                if (version != appearanceVersion) continue;
                await PersistAppearanceAsync(lifetime.Token);
                if (version == appearanceVersion) return;
            }
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception error) { AppearanceSaveFailed = true; AppearanceSaveMessage = "外观保存失败：" + error.Message; }
    }

    /// <summary>重新安排一次字幕保存；不覆盖加载失败的原始配置。</summary>
    [RelayCommand] private void RetryAppearanceSave() => ScheduleAppearanceSave();

    /// <summary>把当前字幕字段合入最后成功保存的运行设置，防止保存未确认的模型或路径。</summary><param name="cancellationToken">生命周期取消。</param><returns>原子保存任务。</returns>
    private async Task PersistAppearanceAsync(CancellationToken cancellationToken)
    {
        await saveGate.WaitAsync(cancellationToken);
        try
        {
            var next = CopyAppearance(persistedSettings, settings);
            if (next != persistedSettings) { await store.SaveAsync(next, cancellationToken); persistedSettings = next; }
            AppearanceSaveFailed = false; AppearanceSaveMessage = "外观已自动保存";
        }
        finally { saveGate.Release(); }
    }

    /// <summary>仅复制字幕字段，不改变运行配置或快捷键。</summary><param name="target">保留运行字段的配置。</param><param name="source">字幕外观与布局来源。</param><returns>合并后的不可变配置。</returns>
    private static DesktopSettings CopyAppearance(DesktopSettings target, DesktopSettings source) => target with
    {
        Theme = source.Theme, FontSize = source.FontSize, BackgroundOpacity = source.BackgroundOpacity, DisplayMode = source.DisplayMode,
        OverlayX = source.OverlayX, OverlayY = source.OverlayY, OverlayWidth = source.OverlayWidth, OverlayHeight = source.OverlayHeight
    };

    /// <summary>请求宿主无激活地显示或隐藏字幕。</summary>
    [RelayCommand] private void ToggleOverlay() { if (closing) return; OverlayVisible = !OverlayVisible; OverlayChanged?.Invoke(); }
    /// <summary>请求临时交互模式；常规模式恢复原生穿透。</summary>
    [RelayCommand] private void ToggleLayout() { if (closing) return; LayoutMode = !LayoutMode; OverlayVisible = true; OverlayChanged?.Invoke(); }

    /// <summary>从待应用提示直接进入设置，保留所有编辑输入。</summary>
    [RelayCommand] private void OpenSettings() { if (!closing) SelectedPageIndex = 2; }

    /// <summary>从实时页的运行摘要进入运行环境分类。</summary>
    [RelayCommand] private void OpenRuntimeSettings() { if (closing) return; SelectedSettingsIndex = 2; SelectedPageIndex = 2; }

    /// <summary>手动回看时保留当前阅读身份，不让新块替换正在阅读的内容。</summary>
    public void PauseFollowing() { if (!closing) FollowLatest = false; }

    /// <summary>回到最近两块有效字幕并恢复自动滚动，运行中的推理不受影响。</summary>
    [RelayCommand]
    private void ResumeFollowing()
    {
        if (closing) return;
        FollowLatest = true; UpdateOverlayRows();
    }

    /// <summary>把双语字幕放大一档，运行中也可操作。</summary>
    [RelayCommand] private void IncreaseSubtitleFont() => AdjustSubtitleFontSize(2);

    /// <summary>把双语字幕缩小一档，保持最小可读字号。</summary>
    [RelayCommand] private void DecreaseSubtitleFont() => AdjustSubtitleFontSize(-2);

    /// <summary>调整中文字号并按比例缩放原文，保留未保存的模型设置与窗口布局。</summary><param name="delta">中文字号的 DIP 增量；非有限值忽略。</param>
    public void AdjustSubtitleFontSize(double delta)
    {
        if (closing || !double.IsFinite(delta)) return;
        FontSize = Math.Clamp(FontSize + delta, 16, 64);
    }

    /// <summary>记录布局并合并自动保存请求，不修改语义输入。</summary>
    /// <param name="x">物理像素横坐标。</param><param name="y">物理像素纵坐标。</param><param name="width">DIP 宽度。</param><param name="height">DIP 高度。</param>
    public void RememberBounds(int x, int y, double width, double height)
    {
        if (closing || !double.IsFinite(width) || !double.IsFinite(height) || width < 320 || height < 120) return;
        var next = settings with { OverlayX = x, OverlayY = y, OverlayWidth = width, OverlayHeight = height };
        if (next == settings) return;
        settings = next; ScheduleAppearanceSave();
    }

    /// <summary>从 UI 线程关闭快照入口，然后等待自有会话完整停止。</summary><returns>资源清理任务。</returns>
    public async Task CloseAsync()
    {
        if (closing) return;
        closing = true; OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanToggle)); pump.Close(); session.Updated -= pump.Offer;
        await lifetime.CancelAsync();
        try
        {
            if (SaveCommand.ExecutionTask is { } manualSave) await manualSave;
            await appearanceSaveTask;
            if (initialized && allowAutoSave) await PersistAppearanceAsync(CancellationToken.None);
        }
        finally { await session.StopAsync(); Active = false; lifetime.Dispose(); }
    }

    /// <summary>在 UI 线程投影全部有效身份，任何撤销和空块都不再可见。</summary><param name="value">通过屏障的完整快照。</param>
    private void Apply(DesktopUpdate value)
    {
        if (!value.Active) pump.SwitchSession(Guid.Empty);
        Active = value.Active; Status = value.Status; Backlog = $"积压 {value.BacklogSeconds:F1} 秒";
        if (value.Subtitles is not { } subtitles) return;
        History = subtitles.Segments.Where(x => !subtitles.RetiredIds.Contains(x.Key.Id) && !string.IsNullOrWhiteSpace(x.Source)).Select(x => new SubtitleRow(x)).ToArray();
        UpdateOverlayRows();
    }

    /// <summary>跟随时取最近两块；回看时按完整身份更新修订，撤销或空块立即移除。</summary>
    private void UpdateOverlayRows()
    {
        if (FollowLatest) { OverlayRows = History.TakeLast(2).ToArray(); return; }
        var reading = OverlayRows.Select(x => x.Segment.Key).ToHashSet();
        OverlayRows = History.Where(x => reading.Contains(x.Segment.Key)).ToArray();
    }

    /// <summary>将编辑输入和最近布局组合为本次不可变设置。</summary><returns>完整设置。</returns>
    private DesktopSettings ReadSettings() => settings with
    {
        TranslationModel = TranslationModel, AsrLanguage = AsrLanguage, PythonPath = PythonPath, ModelPath = ModelPath,
        OllamaPath = OllamaPath, OllamaModelsPath = OllamaModelsPath, DataPath = DataPath, CachePath = CachePath,
        StartKey = StartKey, VisibilityKey = VisibilityKey, LayoutKey = LayoutKey, HistoryRetentionDays = HistoryRetentionDays
    };
}
