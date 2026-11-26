using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LiveLingo.App.Views;

/// <summary>在字幕正文穿透时为返回最新字幕提供独立的非激活命中区。</summary>
public partial class SubtitleResumeWindow : Window
{
    /// <summary>加载回看操作，窗口位置与显隐由字幕宿主同步。</summary>
    public SubtitleResumeWindow() => AvaloniaXamlLoader.Load(this);
}
