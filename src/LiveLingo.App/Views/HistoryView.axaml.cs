using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LiveLingo.App.ViewModels;

namespace LiveLingo.App.Views;

/// <summary>仅在档案页面可见时刷新活动记录，离开视觉树后停止计时器。</summary>
public partial class HistoryView : UserControl
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>加载编译绑定页面并登记可清理的刷新事件。</summary>
    public HistoryView() { AvaloniaXamlLoader.Load(this); timer.Tick += OnTick; }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); timer.Start(); }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { timer.Stop(); base.OnDetachedFromVisualTree(e); }

    /// <summary>刷新可见页面，错误由视图模型呈现。</summary><param name="sender">窗口计时器。</param><param name="args">节拍。</param>
    private async void OnTick(object? sender, EventArgs args)
    {
        if (IsEffectivelyVisible && DataContext is HistoryViewModel model) await model.PollAsync();
    }
}
