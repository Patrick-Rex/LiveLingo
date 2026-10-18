using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using LiveLingo.App.ViewModels;
using LiveLingo.App.Views;

[assembly: AvaloniaTestApplication(typeof(LiveLingo.App.Tests.TestApplication))]

namespace LiveLingo.App.Tests;

/// <summary>为每项 Headless 测试建立真正 Avalonia 控件和编译绑定环境。</summary>
public sealed class TestApplication : Application
{
    /// <inheritdoc />
    public override void Initialize() { RequestedThemeVariant = ThemeVariant.Dark; Styles.Add(new FluentTheme()); }
    /// <summary>构造无原生窗口的测试渲染器；不能用来证明 Windows 穿透。</summary><returns>应用构建器。</returns>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>加载实际 AXAML 与中日文本，验证框架布局和生命周期。</summary>
public sealed class ViewTests
{
    /// <summary>完整长句在三档字号下都保留可滚动尾部，不裁剪源文或译文。</summary><param name="font">中文字号。</param>
    [AvaloniaTheory, InlineData(16.0), InlineData(28.0), InlineData(64.0)]
    public void LongSemanticBlockRemainsScrollable(double font)
    {
        var vm = new DesktopViewModel(new FakeSession(), new FakeStore(), action => action());
        var text = string.Concat(Enumerable.Repeat("最後まで聞かなければ、行くかどうかは分からない。", 80));
        vm.OverlayRows = [new(new(new(Guid.NewGuid(), 1), new(0, 16000), 1, 1, text, text + "尾部", 1,
            Core.Subtitles.SubtitlePhase.Revised, Core.Subtitles.TranslationState.Complete, null, Core.Subtitles.BoundaryDecision.Pending))];
        var overlay = new SubtitleWindow { DataContext = vm, Width = 600, Height = 240 };
        try
        {
            overlay.ApplyAppearance(new() { FontSize = font }); overlay.Show(); overlay.SetInteractive(true); overlay.UpdateLayout();
            var scroll = overlay.FindControl<ScrollViewer>("SubtitleScroll")!;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            scroll.ScrollToEnd(); overlay.UpdateLayout(); Assert.True(scroll.Offset.Y > 0);
            Assert.EndsWith("尾部", vm.OverlayRows[0].Translation);
        }
        finally { overlay.Close(); }
    }

    /// <summary>两个实际视图能完成绑定、布局和视觉配置重应用。</summary>
    [AvaloniaFact]
    public void ViewsLoadAndMeasureLongJapaneseText()
    {
        var vm = new DesktopViewModel(new FakeSession(), new FakeStore(), action => action());
        var control = new ControlWindow { DataContext = vm }; var overlay = new SubtitleWindow { DataContext = vm };
        try
        {
            overlay.ApplyAppearance(new()); control.Show(); overlay.Show();
            overlay.ApplyAppearance(new() { FontSize = 34, BackgroundOpacity = 0.5 });
            vm.Status = string.Concat(Enumerable.Repeat("最後まで聞かなければ否定かどうか分からない。", 20));
            overlay.Measure(new(800, 300)); overlay.Arrange(new(0, 0, 800, 300));
            Assert.True(overlay.Bounds.Width > 0); Assert.Equal(vm, overlay.DataContext);
            Assert.NotNull(overlay.FindControl<Border>("Surface"));
        }
        finally { overlay.Close(); control.Close(); }
    }
}
