using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LiveLingo.App.Views;

/// <summary>承载字幕右上角唯一锁定入口，使正文穿透时按钮仍可接收点击；位置及原生样式由宿主管理。</summary>
public partial class SubtitleLockWindow : Window
{
    /// <summary>加载紧凑锁定按钮；不单独显示或激活窗口。</summary>
    public SubtitleLockWindow() => AvaloniaXamlLoader.Load(this);
}
