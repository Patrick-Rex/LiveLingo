using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LiveLingo.App.Views;

/// <summary>承载应用选择、状态、设置与完整字幕历史。</summary>
public partial class ControlWindow : Window
{
    /// <summary>加载实际编译绑定视图；宿主注入运行时 ViewModel。</summary>
    public ControlWindow() => AvaloniaXamlLoader.Load(this);
}
