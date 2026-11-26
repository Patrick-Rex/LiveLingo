using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using LiveLingo.App.ViewModels;

namespace LiveLingo.App.Views;

/// <summary>承载应用选择、状态、设置与完整字幕历史。</summary>
public partial class ControlWindow : Window
{
    private readonly ListBox historyList;

    /// <summary>加载实时页并登记只作用于当前会话列表的跟随和回看操作。</summary>
    public ControlWindow()
    {
        AvaloniaXamlLoader.Load(this);
        historyList = this.FindControl<ListBox>("HistoryList")!;
        historyList.LayoutUpdated += FollowTranslation;
        historyList.AddHandler(PointerWheelChangedEvent, ReviewWithWheel, RoutingStrategies.Tunnel);
        historyList.AddHandler(PointerPressedEvent, ReviewWithScrollBar, RoutingStrategies.Tunnel);
        historyList.AddHandler(KeyDownEvent, ReviewWithKeyboard, RoutingStrategies.Tunnel);
    }

    /// <summary>在当前短句出现或其译文回填后滚到同一个列表项，保留手动回看的位置。</summary>
    /// <param name="sender">实时列表。</param><param name="args">布局通知。</param>
    private void FollowTranslation(object? sender, EventArgs args)
    {
        if (DataContext is DesktopViewModel { ReviewingTranscript: false } model &&
            historyList.IsEffectivelyVisible && historyList.Scroll is { } scroll)
            SubtitleScrollPosition.Align(historyList, scroll, model.LivePhraseIndex);
    }

    /// <summary>仅在有实际滚动空间且用户沿该方向滚动时暂停主窗跟随。</summary>
    /// <param name="sender">实时列表。</param><param name="args">滚轮事件。</param>
    private void ReviewWithWheel(object? sender, PointerWheelEventArgs args)
    {
        if (historyList.Scroll is { } scroll && (args.Delta.Y > 0 && scroll.Offset.Y > 0 ||
            args.Delta.Y < 0 && scroll.Offset.Y < scroll.Extent.Height - scroll.Viewport.Height)) PauseTranscript();
    }

    /// <summary>拖动滚动条前暂停跟随，允许用户选择自己的阅读位置。</summary>
    /// <param name="sender">实时列表。</param><param name="args">指针事件。</param>
    private void ReviewWithScrollBar(object? sender, PointerPressedEventArgs args)
    {
        if (args.Source is Avalonia.Visual visual && (visual is ScrollBar || visual.GetVisualAncestors().Any(x => x is ScrollBar)))
            PauseTranscript();
    }

    /// <summary>键盘浏览实时列表时暂停自动跟随。</summary>
    /// <param name="sender">实时列表。</param><param name="args">按键事件。</param>
    private void ReviewWithKeyboard(object? sender, KeyEventArgs args)
    {
        if (args.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown) PauseTranscript();
    }

    /// <summary>保留主窗当前阅读位置，浮窗仍可独立跟随译文。</summary>
    private void PauseTranscript()
    {
        if (DataContext is DesktopViewModel model && historyList.Scroll is { } scroll && scroll.Extent.Height > scroll.Viewport.Height)
            model.ReviewingTranscript = true;
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        historyList.LayoutUpdated -= FollowTranslation;
        base.OnClosed(e);
    }
}
