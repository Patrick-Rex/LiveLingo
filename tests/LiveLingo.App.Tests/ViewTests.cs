using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
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
    /// <summary>字幕右上角同一按钮完成解锁和重新锁定，控制窗没有重复开关，关闭字幕同时关闭按钮。</summary>
    [AvaloniaFact]
    public void SubtitleLockIsAvailableInBothModesAndClosesWithOverlay()
    {
        var vm = new DesktopViewModel(new FakeSession(), new FakeStore(), action => action());
        var overlay = new SubtitleWindow { DataContext = vm, Width = 320, Height = 260 };
        var control = new ControlWindow { DataContext = vm };
        Action apply = () => overlay.SetInteractive(vm.LayoutMode);
        vm.OverlayChanged += apply;
        try
        {
            overlay.Show(); overlay.LayoutLock.DataContext = vm; overlay.LayoutLock.Show(overlay); overlay.UpdateLayout();
            var button = overlay.LayoutLock.FindControl<Button>("LayoutLockButton")!;
            overlay.LayoutLock.UpdateLayout();
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), overlay.LayoutLock)!.Value;
            Assert.Equal("解锁拖动", button.Content); Assert.False(overlay.CanResize);
            overlay.LayoutLock.MouseDown(point, MouseButton.Left); overlay.LayoutLock.MouseUp(point, MouseButton.Left);
            Assert.True(vm.LayoutMode); Assert.True(overlay.CanResize); Assert.Equal("锁定位置", button.Content);
            overlay.LayoutLock.MouseDown(point, MouseButton.Left); overlay.LayoutLock.MouseUp(point, MouseButton.Left);
            Assert.False(vm.LayoutMode); Assert.False(overlay.CanResize); Assert.True(button.IsEffectivelyVisible);
            control.Show(); control.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 1; control.UpdateLayout();
            Assert.DoesNotContain(control.GetVisualDescendants().OfType<Button>(), x => x.Command == vm.ToggleLayoutCommand);
            var anchor = overlay.FindControl<Border>("LockAnchor")!;
            var edge = anchor.TranslatePoint(new Point(anchor.Bounds.Width, anchor.Bounds.Height), overlay)!.Value;
            Assert.InRange(edge.X, 1, overlay.Bounds.Width); Assert.InRange(edge.Y, 1, overlay.Bounds.Height);
            overlay.Close(); Assert.False(overlay.LayoutLock.IsVisible);
        }
        finally { vm.OverlayChanged -= apply; control.Close(); overlay.Close(); }
    }

    /// <summary>最小控制窗仍保留主操作和保存栏，运行时字幕可调而运行配置禁用。</summary><returns>界面初始化与清理任务。</returns>
    [AvaloniaFact]
    public async Task ControlTabsKeepActionsVisibleAndAppearanceEditableWhileRunning()
    {
        var vm = new DesktopViewModel(new FakeSession(), new FakeStore(), action => action()); await vm.InitializeAsync();
        var control = new ControlWindow { DataContext = vm, Width = 760, Height = 600 };
        try
        {
            control.Show(); await vm.ToggleCommand.ExecuteAsync(null);
            var tabs = control.FindControl<TabControl>("SettingsTabs")!;
            var save = control.FindControl<Button>("SaveSettingsButton")!;
            var start = control.FindControl<Button>("SessionButton")!;
            for (var index = 0; index < 4; index++)
            {
                tabs.SelectedIndex = index; control.UpdateLayout();
                var point = save.TranslatePoint(new Point(0, save.Bounds.Height), control)!.Value;
                Assert.InRange(point.Y, 1, control.Bounds.Height);
                Assert.True(start.IsEffectivelyVisible); Assert.Equal("停止翻译", start.Content);
            }
            tabs.SelectedIndex = 1; control.UpdateLayout();
            var slider = control.FindControl<Slider>("FontSizeSlider")!;
            Assert.True(slider.IsEffectivelyEnabled); slider.Value = 36; Assert.Equal(36, vm.CurrentSettings.FontSize);
            tabs.SelectedIndex = 2; control.UpdateLayout();
            Assert.False(control.FindControl<StackPanel>("RuntimeInputs")!.IsEffectivelyEnabled); Assert.False(save.IsEffectivelyEnabled);
        }
        finally { control.Close(); await vm.CloseAsync(); }
    }

    /// <summary>真实按钮和滚轮即时缩放双语，普通滚轮仍可滚动，退出布局后禁用缩放。</summary>
    [AvaloniaFact]
    public void LayoutControlsZoomBothLanguagesWithoutMovingWindow()
    {
        var vm = new DesktopViewModel(new FakeSession(), new FakeStore(), action => action());
        var text = string.Concat(Enumerable.Repeat("最后まで聞いてください。", 100));
        vm.OverlayRows = [new(new(new(Guid.NewGuid(), 1), new(0, 16000), 1, 1, text, text, 1,
            Core.Subtitles.SubtitlePhase.Revised, Core.Subtitles.TranslationState.Complete, null, Core.Subtitles.BoundaryDecision.Pending))];
        var overlay = new SubtitleWindow { DataContext = vm, Width = 600, Height = 260 };
        vm.AppearanceChanged += overlay.ApplyAppearance;
        try
        {
            overlay.Show(); overlay.SetInteractive(true); overlay.UpdateLayout();
            var initialSize = overlay.Bounds.Size;
            var button = overlay.FindControl<Button>("IncreaseFontButton")!;
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), overlay)!.Value;
            overlay.MouseDown(point, MouseButton.Left); overlay.MouseUp(point, MouseButton.Left);
            Assert.Equal(30, vm.FontSize);
            var blocks = overlay.GetVisualDescendants().OfType<TextBlock>().ToArray();
            Assert.Equal(30, blocks.Single(x => x.Classes.Contains("translation")).FontSize);
            Assert.Equal(30.0 * 18 / 28, blocks.Single(x => x.Classes.Contains("source")).FontSize, 5);
            var scroll = overlay.FindControl<ScrollViewer>("SubtitleScroll")!;
            var body = scroll.TranslatePoint(new Point(80, 60), overlay)!.Value;
            var tail = scroll.Offset.Y;
            overlay.MouseWheel(body, new Vector(0, 1));
            Assert.True(scroll.Offset.Y < tail); Assert.Equal(30, vm.FontSize); Assert.False(vm.FollowLatest);
            overlay.MouseWheel(body, new Vector(0, 1), RawInputModifiers.Control);
            Assert.Equal(32, vm.FontSize);
            Assert.Equal(32, blocks.Single(x => x.Classes.Contains("translation")).FontSize);
            Assert.Equal(initialSize, overlay.Bounds.Size);
            overlay.SetInteractive(false); overlay.UpdateLayout();
            Assert.False(overlay.FindControl<Grid>("LayoutToolbar")!.IsVisible); Assert.False(overlay.CanResize);
            overlay.MouseWheel(new Point(80, 100), new Vector(0, 1), RawInputModifiers.Control);
            Assert.Equal(32, vm.FontSize);
        }
        finally { vm.AppearanceChanged -= overlay.ApplyAppearance; overlay.Close(); }
    }

    /// <summary>长字幕增长和窗口变窄时保持末尾，回看期间不跳动，返回最新按钮恢复跟随。</summary>
    [AvaloniaFact]
    public void AutoFollowPausesForReviewAndResumesFromButton()
    {
        var vm = new DesktopViewModel(new FakeSession(), new FakeStore(), action => action());
        var row = new SubtitleRow(new(new(Guid.NewGuid(), 1), new(0, 16000), 1, 1,
            new string('文', 600), new string('译', 600), 1, Core.Subtitles.SubtitlePhase.Revised,
            Core.Subtitles.TranslationState.Complete, null, Core.Subtitles.BoundaryDecision.Pending));
        vm.History = [row]; vm.OverlayRows = [row];
        var overlay = new SubtitleWindow { DataContext = vm, Width = 600, Height = 360 };
        try
        {
            overlay.Show(); overlay.SetInteractive(true); overlay.UpdateLayout();
            var scroll = overlay.FindControl<ScrollViewer>("SubtitleScroll")!;
            Assert.True(scroll.Offset.Y > 0);
            Assert.Equal(scroll.Extent.Height - scroll.Viewport.Height, scroll.Offset.Y, 1);
            var body = scroll.TranslatePoint(new Point(80, 60), overlay)!.Value;
            overlay.MouseWheel(body, new Vector(0, 3));
            Assert.False(vm.FollowLatest);
            var reviewedOffset = scroll.Offset.Y;
            var updated = row with { Segment = row.Segment with { Translation = new string('新', 1000), SubtitleRevision = 2 } };
            vm.History = [updated]; vm.OverlayRows = [updated]; overlay.UpdateLayout();
            Assert.Equal(reviewedOffset, scroll.Offset.Y, 1);
            overlay.Width = 500; overlay.MouseMove(new Point(40, 40)); overlay.UpdateLayout();
            Assert.False(vm.FollowLatest); Assert.Equal(reviewedOffset, scroll.Offset.Y, 1);
            var button = overlay.FindControl<Button>("ResumeFollowingButton")!;
            Assert.True(button.IsVisible);
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), overlay)!.Value;
            overlay.MouseDown(point, MouseButton.Left); overlay.MouseUp(point, MouseButton.Left); overlay.UpdateLayout();
            Assert.True(vm.FollowLatest);
            Assert.Equal(scroll.Extent.Height - scroll.Viewport.Height, scroll.Offset.Y, 1);
            overlay.Width = 420; overlay.UpdateLayout();
            Assert.Equal(scroll.Extent.Height - scroll.Viewport.Height, scroll.Offset.Y, 1);
        }
        finally { overlay.Close(); }
    }

    /// <summary>控制窗和浮窗语言选择同步更新既有文本可见性，历史与待译提示仍保留。</summary>
    [AvaloniaFact]
    public void DisplayModesChangeOnlyOverlayAndSynchronizeSelectors()
    {
        var vm = new DesktopViewModel(new FakeSession(), new FakeStore(), action => action());
        var row = new SubtitleRow(new(new(Guid.NewGuid(), 1), new(0, 16000), 1, 1, "原文", null, null,
            Core.Subtitles.SubtitlePhase.Draft, Core.Subtitles.TranslationState.None, null, Core.Subtitles.BoundaryDecision.Pending));
        vm.History = [row]; vm.OverlayRows = [row];
        var overlay = new SubtitleWindow { DataContext = vm }; var control = new ControlWindow { DataContext = vm };
        vm.AppearanceChanged += overlay.ApplyAppearance;
        try
        {
            overlay.Show(); control.Show(); overlay.SetInteractive(true); overlay.UpdateLayout();
            var source = overlay.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Classes.Contains("source"));
            var translation = overlay.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Classes.Contains("translation"));
            var selector = control.FindControl<ComboBox>("DisplayModeSelector")!;
            selector.SelectedIndex = 1; overlay.UpdateLayout();
            Assert.False(source.IsVisible); Assert.True(translation.IsVisible); Assert.Equal("等待中文…", translation.Text);
            Assert.Equal(1, overlay.FindControl<ComboBox>("DisplayModeSelector")!.SelectedIndex);
            overlay.FindControl<ComboBox>("DisplayModeSelector")!.SelectedIndex = 2; overlay.UpdateLayout();
            Assert.True(source.IsVisible); Assert.False(translation.IsVisible); Assert.Equal(2, selector.SelectedIndex);
            selector.SelectedIndex = 0; overlay.UpdateLayout();
            Assert.True(source.IsVisible); Assert.True(translation.IsVisible); Assert.Same(row, Assert.Single(vm.History));
        }
        finally { vm.AppearanceChanged -= overlay.ApplyAppearance; overlay.Close(); control.Close(); }
    }

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
