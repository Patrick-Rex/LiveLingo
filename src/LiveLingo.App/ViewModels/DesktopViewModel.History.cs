using CommunityToolkit.Mvvm.ComponentModel;

namespace LiveLingo.App.ViewModels;

/// <summary>将独立的持久档案和保存反馈接入主窗口，不改变实时字幕投影。</summary>
public sealed partial class DesktopViewModel
{
    /// <summary>获取宿主创建的档案页面模型。</summary>
    [ObservableProperty] public partial HistoryViewModel? Archive { get; set; }
    /// <summary>获取独立于翻译状态的历史保存失败提示。</summary>
    [ObservableProperty] public partial string HistorySaveMessage { get; set; } = "";
    /// <summary>获取是否需要持续显示历史保存失败。</summary>
    [ObservableProperty] public partial bool HistorySaveFailed { get; set; }
}
