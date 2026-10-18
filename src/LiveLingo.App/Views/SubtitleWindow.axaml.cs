using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using LiveLingo.Core.Desktop;

namespace LiveLingo.App.Views;

/// <summary>呈现最近完整双语块；交互模式仅改变布局，不触碰字幕语义。</summary>
public partial class SubtitleWindow : Window
{
    private bool interactive;
    private readonly Style translationStyle = new(x => x.OfType<TextBlock>().Class("translation"));

    /// <summary>加载实际透明窗口视图，登记可解除的窗口内事件。</summary>
    public SubtitleWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Styles.Add(translationStyle);
        PointerPressed += Drag;
    }

    /// <summary>应用经校验的视觉配置；位置由平台桥接钳制。</summary><param name="settings">固定配置。</param>
    public void ApplyAppearance(DesktopSettings settings)
    {
        translationStyle.Setters.Clear(); translationStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, settings.FontSize));
        this.FindControl<Border>("Surface")!.Background = new SolidColorBrush(Color.FromArgb((byte)(settings.BackgroundOpacity * 255), 16, 23, 32));
    }

    /// <summary>控制布局事件；原生穿透仍由平台适配器实现。</summary><param name="enabled">是否允许拖动和缩放。</param>
    public void SetInteractive(bool enabled) { interactive = enabled; CanResize = enabled; }

    /// <summary>仅在布局模式用左键拖动；边缘八像素用于尺寸调整。</summary><param name="sender">窗口。</param><param name="args">当前指针事件。</param>
    private void Drag(object? sender, PointerPressedEventArgs args)
    {
        if (!interactive || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = args.GetPosition(this);
        if (p.X > Bounds.Width - 12 && p.Y > Bounds.Height - 12) BeginResizeDrag(WindowEdge.SouthEast, args);
        else if (p.Y < 36) BeginMoveDrag(args);
    }
}
