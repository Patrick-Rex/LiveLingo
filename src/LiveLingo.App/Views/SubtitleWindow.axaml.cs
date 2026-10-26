using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using LiveLingo.Core.Desktop;
using LiveLingo.App.ViewModels;

namespace LiveLingo.App.Views;

/// <summary>按语言模式呈现有效字幕并管理跟随与回看；布局不触碰字幕语义。</summary>
public partial class SubtitleWindow : Window
{
    private bool interactive;
    private readonly ScrollViewer subtitleScroll;
    private StandardCursorType cursorType = StandardCursorType.Arrow;

    /// <summary>获取贴合字幕右上角的锁定按钮窗口，其生命周期从属于字幕窗。</summary>
    public SubtitleLockWindow LayoutLock { get; } = new();

    /// <summary>加载实际透明窗口视图，登记可解除的窗口内事件。</summary>
    public SubtitleWindow()
    {
        AvaloniaXamlLoader.Load(this);
        subtitleScroll = this.FindControl<ScrollViewer>("SubtitleScroll")!;
        ApplyAppearance(new());
        AddHandler(PointerPressedEvent, Drag, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, ZoomText, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, ReviewWithKeyboard, RoutingStrategies.Tunnel);
        subtitleScroll.LayoutUpdated += FollowTail;
        PointerMoved += UpdateCursor;
        this.FindControl<Border>("MoveHandle")!.PointerPressed += Move;
        SetInteractive(false);
    }

    /// <summary>应用经校验的视觉配置；位置由平台桥接钳制。</summary><param name="settings">固定配置。</param>
    public void ApplyAppearance(DesktopSettings settings)
    {
        Resources["TranslationFontSize"] = settings.FontSize;
        Resources["SourceFontSize"] = settings.FontSize * 18 / 28;
        Resources["SourceVisible"] = settings.DisplayMode != SubtitleDisplayMode.TranslationOnly;
        Resources["TranslationVisible"] = settings.DisplayMode != SubtitleDisplayMode.SourceOnly;
        this.FindControl<Border>("Surface")!.Background = new SolidColorBrush(Color.FromArgb((byte)(settings.BackgroundOpacity * 255), 16, 23, 32));
    }

    /// <summary>控制布局事件；原生穿透仍由平台适配器实现。</summary><param name="enabled">是否允许拖动和缩放。</param>
    public void SetInteractive(bool enabled)
    {
        interactive = enabled; CanResize = enabled;
        this.FindControl<Grid>("LayoutToolbar")!.IsVisible = enabled;
        this.FindControl<Grid>("ReadingToolbar")!.IsVisible = enabled;
        this.FindControl<Grid>("ReviewToolbar")!.IsVisible = enabled;
        Grid.SetRow(subtitleScroll, enabled ? 1 : 0);
        Grid.SetRowSpan(subtitleScroll, enabled ? 1 : 2);
        subtitleScroll.Margin = enabled ? default : new Thickness(0, 0, 40, 0);
        this.FindControl<Border>("Surface")!.BorderBrush = new SolidColorBrush(Color.Parse(enabled ? "#9EDFC7" : "#395267"));
        Cursor = Cursor.Default;
        cursorType = StandardCursorType.Arrow;
    }

    /// <summary>仅在布局模式用左键拖动四边或四角；先于子控件命中边缘。</summary><param name="sender">窗口。</param><param name="args">当前指针事件。</param>
    private void Drag(object? sender, PointerPressedEventArgs args)
    {
        if (!interactive || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (args.Source is Visual visual && (visual is ScrollBar || visual.GetVisualAncestors().Any(x => x is ScrollBar)))
            PauseForReview();
        if (ResizeEdge(args.GetPosition(this)) is not { } edge) return;
        args.Handled = true;
        BeginResizeDrag(edge, args);
    }

    /// <summary>只从明确的拖动条移动窗口，保留按钮与正文滚动交互。</summary><param name="sender">拖动区域。</param><param name="args">当前指针事件。</param>
    private void Move(object? sender, PointerPressedEventArgs args)
    {
        if (!interactive || args.Handled || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        args.Handled = true;
        BeginMoveDrag(args);
    }

    /// <summary>以 DIP 识别十二单位宽的尺寸调整边缘，角落同时调整两个方向。</summary><param name="point">窗口内坐标。</param><returns>命中的边缘；正文区域返回空。</returns>
    private WindowEdge? ResizeEdge(Point point)
    {
        var left = point.X < 12; var right = point.X >= Bounds.Width - 12;
        var top = point.Y < 12; var bottom = point.Y >= Bounds.Height - 12;
        if (top) return left ? WindowEdge.NorthWest : right ? WindowEdge.NorthEast : WindowEdge.North;
        if (bottom) return left ? WindowEdge.SouthWest : right ? WindowEdge.SouthEast : WindowEdge.South;
        return left ? WindowEdge.West : right ? WindowEdge.East : null;
    }

    /// <summary>提供与当前边缘一致的尺寸光标，不改变穿透状态。</summary><param name="sender">窗口。</param><param name="args">移动事件。</param>
    private void UpdateCursor(object? sender, PointerEventArgs args)
    {
        var type = interactive ? ResizeEdge(args.GetPosition(this)) switch
        {
            WindowEdge.North or WindowEdge.South => StandardCursorType.SizeNorthSouth,
            WindowEdge.East or WindowEdge.West => StandardCursorType.SizeWestEast,
            WindowEdge.NorthWest or WindowEdge.SouthEast => StandardCursorType.TopLeftCorner,
            WindowEdge.NorthEast or WindowEdge.SouthWest => StandardCursorType.TopRightCorner,
            _ => StandardCursorType.Arrow
        } : StandardCursorType.Arrow;
        if (cursorType != type) { cursorType = type; Cursor = new Cursor(type); }
    }

    /// <summary>布局模式下 Ctrl 加滚轮缩放文字；普通滚轮继续查看完整长句。</summary><param name="sender">窗口。</param><param name="args">滚轮事件。</param>
    private void ZoomText(object? sender, PointerWheelEventArgs args)
    {
        if (!interactive || args.Delta.Y == 0 || DataContext is not DesktopViewModel model) return;
        if (!args.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (args.Source is Visual visual && (visual == subtitleScroll || visual.GetVisualAncestors().Contains(subtitleScroll)) &&
                (args.Delta.Y > 0 && subtitleScroll.Offset.Y > 0 || args.Delta.Y < 0 && subtitleScroll.Offset.Y < subtitleScroll.Extent.Height - subtitleScroll.Viewport.Height))
                PauseForReview();
            return;
        }
        model.AdjustSubtitleFontSize(Math.Sign(args.Delta.Y) * 2);
        args.Handled = true;
    }

    /// <summary>布局完成后跟随当前完整内容的末尾；尺寸与字体改变也重新定位，不排队旧快照回调。</summary><param name="sender">字幕滚动区。</param><param name="args">布局通知。</param>
    private void FollowTail(object? sender, EventArgs args)
    {
        if (DataContext is DesktopViewModel { FollowLatest: true } &&
            subtitleScroll.Extent.Height - subtitleScroll.Viewport.Height - subtitleScroll.Offset.Y > 1)
            subtitleScroll.ScrollToEnd();
    }

    /// <summary>只在有可滚动内容时暂停跟随，保留当前滚动位置与阅读身份。</summary>
    private void PauseForReview()
    {
        if (subtitleScroll.Extent.Height > subtitleScroll.Viewport.Height && DataContext is DesktopViewModel model)
            model.PauseFollowing();
    }

    /// <summary>滚动区键盘回看先暂停跟随，不拦截输入控件或组合快捷键。</summary><param name="sender">窗口。</param><param name="args">键盘事件。</param>
    private void ReviewWithKeyboard(object? sender, KeyEventArgs args)
    {
        if (interactive && args.KeyModifiers == KeyModifiers.None && args.Key is Key.Up or Key.PageUp or Key.Home &&
            args.Source is Visual visual && (visual == subtitleScroll || visual.GetVisualAncestors().Contains(subtitleScroll)))
            PauseForReview();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        subtitleScroll.LayoutUpdated -= FollowTail;
        LayoutLock.Close();
        base.OnClosed(e);
    }
}
