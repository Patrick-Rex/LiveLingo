using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using System.Collections.Immutable;
using LiveLingo.App.ViewModels;
using LiveLingo.App.Views;
using LiveLingo.Core.Subtitles;

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
    /// <summary>锁定时正文避开独立锁按钮且无诊断噪声，解锁工具在最小宽度内保持独立命中。</summary>
    /// <param name="width">以 DIP 表示的字幕窗口宽度。</param>
    [AvaloniaTheory, InlineData(320.0), InlineData(420.0), InlineData(1000.0)]
    public void CompactOverlayKeepsBodyAndControlsSeparate(double width)
    {
        var vm = new DesktopViewModel(new FakeSession(), new FakeStore(), action => action())
        {
            Status = "运行状态不应进入字幕正文",
            OverlayRows = [new(new(new(Guid.NewGuid(), 1), new(0, 16000), 1, 1, "長い原文。", "完整译文。", 1,
                SubtitlePhase.Final, TranslationState.Complete, null, BoundaryDecision.Pending))]
        };
        var overlay = new SubtitleWindow { DataContext = vm, Width = width, Height = 260 };
        try
        {
            overlay.Show(); overlay.UpdateLayout();
            var scroll = overlay.FindControl<ScrollViewer>("SubtitleScroll")!;
            var anchor = overlay.FindControl<Border>("LockAnchor")!;
            var scrollTop = scroll.TranslatePoint(default, overlay)!.Value;
            var anchorTop = anchor.TranslatePoint(default, overlay)!.Value;
            Assert.Equal(scrollTop.Y, anchorTop.Y, 1);
            Assert.True(scrollTop.X + scroll.Bounds.Width <= anchorTop.X);
            Assert.Equal(40, anchor.Bounds.Width); Assert.Equal(40, anchor.Bounds.Height);
            Assert.False(overlay.FindControl<Grid>("LayoutToolbar")!.IsEffectivelyVisible);
            Assert.False(overlay.FindControl<Button>("ResumeFollowingButton")!.IsEffectivelyVisible);
            Assert.Equal(ThemeVariant.Dark, overlay.RequestedThemeVariant);
            var text = overlay.GetVisualDescendants().OfType<TextBlock>().Where(x => x.IsEffectivelyVisible).Select(x => x.Text).ToArray();
            Assert.Contains("完整译文。", text);
            Assert.Contains("已定稿", text);
            Assert.DoesNotContain(text, x => x is not null && (x.Contains("源 v", StringComparison.Ordinal) || x.Contains(vm.Status, StringComparison.Ordinal)));

            overlay.SetInteractive(true); overlay.UpdateLayout();
            var handle = overlay.FindControl<Border>("MoveHandle")!;
            var selector = overlay.FindControl<ComboBox>("DisplayModeSelector")!;
            var increase = overlay.FindControl<Button>("IncreaseFontButton")!;
            var handleStart = handle.TranslatePoint(default, overlay)!.Value;
            var selectorStart = selector.TranslatePoint(default, overlay)!.Value;
            var increaseStart = increase.TranslatePoint(default, overlay)!.Value;
            Assert.True(handle.Bounds.Width >= 32);
            Assert.True(handleStart.X + handle.Bounds.Width <= selectorStart.X);
            Assert.True(increaseStart.X + increase.Bounds.Width <= anchorTop.X);
            Assert.True(scroll.TranslatePoint(default, overlay)!.Value.Y >= anchorTop.Y + anchor.Bounds.Height);
            Assert.True(increase.Bounds.Width >= 32 && increase.Bounds.Height >= 32);
            Assert.False(overlay.FindControl<Button>("ResumeFollowingButton")!.IsEffectivelyVisible);
        }
        finally { overlay.Close(); }
    }

    /// <summary>回看操作在紧凑与常规尺寸下都保留独立命中区，脱离边缘并能从主窗键盘执行。</summary>
    /// <param name="width">字幕窗宽度（DIP）。</param>
    /// <param name="height">字幕窗高度（DIP）。</param>
    [AvaloniaTheory, InlineData(320, 120), InlineData(600, 260)]
    public void ReviewButtonsStayClearOfSubtitlesAndResizeEdges(int width, int height)
    {
        var vm = new DesktopViewModel(new FakeSession(), new FakeStore(), action => action());
        var overlay = new SubtitleWindow { DataContext = vm, Width = width, Height = height };
        var control = new ControlWindow { DataContext = vm, Width = 760, Height = 600 };
        try
        {
            overlay.Show(); control.Show();
            vm.PauseFollowing();
            var anchor = overlay.FindControl<Grid>("ReviewAnchor")!;
            var lockAnchor = overlay.FindControl<Border>("LockAnchor")!;
            var scroll = overlay.FindControl<ScrollViewer>("SubtitleScroll")!;
            var button = overlay.FindControl<Button>("ResumeFollowingButton")!;
            foreach (var unlocked in new[] { false, true })
            {
                overlay.SetInteractive(unlocked);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                overlay.UpdateLayout();
                Assert.True(anchor.IsEffectivelyVisible);
                Assert.Equal(unlocked, button.IsEffectivelyVisible);
                var bounds = new Rect(anchor.TranslatePoint(default, overlay)!.Value, anchor.Bounds.Size);
                var body = new Rect(scroll.TranslatePoint(default, overlay)!.Value, scroll.Bounds.Size);
                var padlock = new Rect(lockAnchor.TranslatePoint(default, overlay)!.Value, lockAnchor.Bounds.Size);
                Assert.Equal(new Size(128, 40), bounds.Size);
                Assert.False(bounds.Intersects(body));
                Assert.False(bounds.Intersects(padlock));
                Assert.True(body.Width >= 120 && body.Height >= 32, $"Subtitle viewport {body}, unlocked={unlocked}");
                Assert.True(bounds.X >= 12 && bounds.Right <= width - 12 && bounds.Bottom <= height - 12,
                    $"Review anchor {bounds} in {overlay.ClientSize}, unlocked={unlocked}");
            }
            var mainButton = control.FindControl<Button>("ResumeReadingButton")!;
            control.RequestedThemeVariant = height == 120 ? ThemeVariant.Dark : ThemeVariant.Light;
            control.UpdateLayout();
            Assert.True(mainButton.IsEffectivelyVisible);
            Assert.Equal(AutomationProperties.GetName(button), AutomationProperties.GetName(mainButton));
            Assert.Equal("回到最新字幕并恢复跟随", AutomationProperties.GetName(mainButton));
            Assert.True(mainButton.Bounds.Width >= 120 && mainButton.Bounds.Height >= 32);
            Assert.True(mainButton.Focus(NavigationMethod.Tab));
            control.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            control.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.True(vm.FollowLatest);
        }
        finally { overlay.Close(); control.Close(); }
    }

    /// <summary>读取状态以对应翻译版本为准，失败、待译和陈旧译文不能冒充完整定稿。</summary>
    /// <param name="phase">权威字幕阶段。</param><param name="translationState">权威翻译状态。</param>
    /// <param name="translation">可能缺失的译文。</param><param name="translatedRevision">译文对应源版本。</param><param name="expected">预期短状态。</param>
    [Theory]
    [InlineData(SubtitlePhase.Draft, TranslationState.None, null, null, "草稿 · 等待中文")]
    [InlineData(SubtitlePhase.Revised, TranslationState.Pending, null, null, "更新中 · 等待中文")]
    [InlineData(SubtitlePhase.Revised, TranslationState.Complete, "译文", 2L, "更新中")]
    [InlineData(SubtitlePhase.Final, TranslationState.Complete, "译文", 2L, "已定稿")]
    [InlineData(SubtitlePhase.Final, TranslationState.Pending, "译文", 1L, "更新中 · 中文待更新")]
    [InlineData(SubtitlePhase.Final, TranslationState.Pending, "译文", 2L, "更新中")]
    [InlineData(SubtitlePhase.Final, TranslationState.Failed, "旧译文", 1L, "翻译未完成")]
    public void SubtitleStatusDoesNotHideIncompleteTranslation(SubtitlePhase phase, TranslationState translationState, string? translation,
        long? translatedRevision, string expected)
    {
        var row = new SubtitleRow(new(new(Guid.NewGuid(), 1), new(0, 16000), 2, 3, "原文", translation, translatedRevision,
            phase, translationState, null, BoundaryDecision.Pending));
        Assert.Equal(expected, row.StatusText);
        var history = new HistoryEntryItem(new(1, row.Segment), () => { });
        Assert.Equal(expected, history.StatusText);
        Assert.Contains("源 v2 / 字幕 v3", row.Detail);
    }

    /// <summary>档案的常规已译条目只显示原译文，存在失败时可在同一行读到状态和具体原因。</summary>
    [AvaloniaFact]
    public void ArchiveEntriesKeepBilingualTextAndExposeFailures()
    {
        var translated = new SubtitleSegment(new(Guid.NewGuid(), 1), new(0, 16000), 1, 2, "原句。", "译文。", 1,
            SubtitlePhase.Revised, TranslationState.Complete, null, BoundaryDecision.Pending) { SourceRangeSealed = true };
        var failed = translated with { Key = new(Guid.NewGuid(), 1), Translation = null, TranslationState = TranslationState.Failed, Error = "服务超时" };
        var vm = new DesktopViewModel(new FakeSession(), new FakeStore(), action => action()) { SelectedPageIndex = 1 };
        var control = new ControlWindow { DataContext = vm };
        try
        {
            var list = control.FindControl<HistoryView>("ArchivePage")!.FindControl<ListBox>("ArchiveEntries")!;
            list.ItemsSource = new HistoryEntryItem[] { new(new(1, translated), () => { }), new(new(1, failed), () => { }) };
            control.Show(); control.UpdateLayout();
            var first = list.ContainerFromIndex(0)!;
            Assert.Empty(first.GetVisualDescendants().OfType<Expander>());
            var text = first.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible).Select(block => block.Text);
            Assert.Equal(["原句。", "译文。"], text);
            var second = list.ContainerFromIndex(1)!.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible).ToArray();
            Assert.Contains(second, block => block.Text == "翻译未完成");
            Assert.Contains("服务超时", AutomationProperties.GetHelpText(second.Single(block => block.Text == "尚无译文")));
        }
        finally { control.Close(); }
    }

    /// <summary>主窗口明暗主题切换会更新实际画刷，字幕窗口始终使用独立的深色阅读背景。</summary>
    [AvaloniaFact]
    public void MainThemeChangesWithoutChangingSubtitleAppearance()
    {
        var vm = new DesktopViewModel(new FakeSession(), new FakeStore(), action => action());
        var control = new ControlWindow { DataContext = vm };
        var overlay = new SubtitleWindow { DataContext = vm };
        try
        {
            control.RequestedThemeVariant = ThemeVariant.Light; control.Show(); overlay.Show(); control.UpdateLayout();
            var lightBackground = Assert.IsType<Avalonia.Media.SolidColorBrush>(control.Background).Color;
            var overlayBackground = Assert.IsType<Avalonia.Media.SolidColorBrush>(overlay.FindControl<Border>("Surface")!.Background).Color;
            control.RequestedThemeVariant = ThemeVariant.Dark; control.UpdateLayout();
            var darkBackground = Assert.IsType<Avalonia.Media.SolidColorBrush>(control.Background).Color;
            Assert.NotEqual(lightBackground, darkBackground);
            Assert.Equal(overlayBackground, Assert.IsType<Avalonia.Media.SolidColorBrush>(overlay.FindControl<Border>("Surface")!.Background).Color);
            Assert.Equal(ThemeVariant.Dark, overlay.ActualThemeVariant);
        }
        finally { overlay.Close(); control.Close(); }
    }

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
            Assert.Equal("解锁以滚动回看或调整字幕", AutomationProperties.GetName(button)); Assert.False(overlay.CanResize);
            Assert.Equal("解锁以滚动回看或调整字幕", ToolTip.GetTip(button));
            Assert.True(button.Bounds.Width >= 32 && button.Bounds.Height >= 32);
            Assert.True(overlay.LayoutLock.FindControl<Avalonia.Controls.Shapes.Path>("LockedIcon")!.IsVisible);
            overlay.LayoutLock.MouseDown(point, MouseButton.Left); overlay.LayoutLock.MouseUp(point, MouseButton.Left);
            Assert.True(vm.LayoutMode); Assert.True(overlay.CanResize); Assert.Equal("锁定位置", AutomationProperties.GetName(button));
            Assert.True(overlay.LayoutLock.FindControl<Avalonia.Controls.Shapes.Path>("UnlockedIcon")!.IsVisible);
            overlay.LayoutLock.MouseDown(point, MouseButton.Left); overlay.LayoutLock.MouseUp(point, MouseButton.Left);
            Assert.False(vm.LayoutMode); Assert.False(overlay.CanResize); Assert.True(button.IsEffectivelyVisible);
            control.Show(); control.FindControl<ListBox>("MainNavigation")!.SelectedIndex = 2; control.UpdateLayout();
            Assert.DoesNotContain(control.GetVisualDescendants().OfType<Button>(), x => x.Command == vm.ToggleLayoutCommand);
            var anchor = overlay.FindControl<Border>("LockAnchor")!;
            var edge = anchor.TranslatePoint(new Point(anchor.Bounds.Width, anchor.Bounds.Height), overlay)!.Value;
            Assert.InRange(edge.X, 1, overlay.Bounds.Width); Assert.InRange(edge.Y, 1, overlay.Bounds.Height);
            overlay.Close(); Assert.False(overlay.LayoutLock.IsVisible);
        }
        finally { vm.OverlayChanged -= apply; control.Close(); overlay.Close(); }
    }

    /// <summary>最小控制窗的实时入口与设置职责分离，运行时字幕可调而运行配置禁用。</summary><returns>界面初始化与清理任务。</returns>
    [AvaloniaFact]
    public async Task ControlTabsKeepActionsVisibleAndAppearanceEditableWhileRunning()
    {
        var vm = new DesktopViewModel(new FakeSession(), new FakeStore(), action => action()); await vm.InitializeAsync();
        var control = new ControlWindow { DataContext = vm, Width = 760, Height = 600 };
        try
        {
            control.Show(); await vm.ToggleCommand.ExecuteAsync(null); control.UpdateLayout();
            var navigation = control.FindControl<ListBox>("MainNavigation")!;
            var settings = control.FindControl<UserControl>("SettingsPanel")!;
            var tabs = settings.FindControl<TabControl>("SettingsTabs")!;
            var save = settings.FindControl<Button>("SaveSettingsButton")!;
            var start = control.FindControl<Button>("SessionButton")!;
            Assert.True(start.IsEffectivelyVisible); Assert.Equal("停止翻译", start.Content);
            Assert.False(settings.IsEffectivelyVisible);
            Assert.True(!save.IsEffectivelyVisible || TopLevel.GetTopLevel(save) is null);
            navigation.SelectedIndex = 2; control.UpdateLayout();
            for (var index = 0; index < 4; index++)
            {
                tabs.SelectedIndex = index; control.UpdateLayout();
                var point = save.TranslatePoint(new Point(0, save.Bounds.Height), control)!.Value;
                Assert.InRange(point.Y, 1, control.Bounds.Height);
                Assert.False(start.IsEffectivelyVisible); Assert.True(save.IsEffectivelyVisible);
            }
            tabs.SelectedIndex = 1; control.UpdateLayout();
            var slider = settings.FindControl<Slider>("FontSizeSlider")!;
            Assert.True(slider.IsEffectivelyEnabled); slider.Value = 36; Assert.Equal(36, vm.CurrentSettings.FontSize);
            var fontInput = settings.FindControl<NumericUpDown>("FontSizeInput")!;
            Assert.Equal(36m, fontInput.Value);
            Assert.True(fontInput.GetVisualDescendants().OfType<TextBox>().Single().Bounds.Width >= 40);
            tabs.SelectedIndex = 2; control.UpdateLayout();
            Assert.False(settings.FindControl<StackPanel>("RuntimeInputs")!.IsEffectivelyEnabled); Assert.False(save.IsEffectivelyEnabled);
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
    /// <param name="lockBeforeReturn">是否在回看后锁定浮窗，通过独立按钮恢复跟随。</param>
    [AvaloniaTheory, InlineData(false), InlineData(true)]
    public void AutoFollowPausesForReviewAndResumesFromButton(bool lockBeforeReturn)
    {
        var vm = new DesktopViewModel(new FakeSession(), new FakeStore(), action => action());
        var row = new SubtitleRow(new(new(Guid.NewGuid(), 1), new(0, 16000), 1, 1,
            new string('文', 600), new string('译', 600), 1, Core.Subtitles.SubtitlePhase.Revised,
            Core.Subtitles.TranslationState.Complete, null, Core.Subtitles.BoundaryDecision.Pending));
        vm.History = [row]; vm.OverlayRows = [row];
        var overlay = new SubtitleWindow { DataContext = vm, Width = 600, Height = 360 };
        var control = new ControlWindow { DataContext = vm, Width = 760, Height = 600 };
        try
        {
            control.Show(); control.UpdateLayout();
            var list = control.FindControl<ListBox>("HistoryList")!;
            var listViewport = list.Scroll!;
            var listHit = list.TranslatePoint(new Point(80, 35), control)!.Value;
            Assert.True(listViewport.Extent.Height > listViewport.Viewport.Height * 2);
            control.MouseWheel(listHit, new Vector(0, 4)); control.UpdateLayout();
            Assert.True(vm.ReviewingTranscript);
            overlay.Show(); vm.ToggleLayoutCommand.Execute(null); overlay.SetInteractive(vm.LayoutMode); overlay.UpdateLayout();
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
            Window buttonHost = overlay;
            if (lockBeforeReturn)
            {
                vm.ToggleLayoutCommand.Execute(null);
                overlay.SetInteractive(vm.LayoutMode);
                overlay.LockedResume.DataContext = vm;
                overlay.LockedResume.Show(overlay);
                buttonHost = overlay.LockedResume;
            }
            buttonHost.UpdateLayout();
            var button = buttonHost.FindControl<Button>(lockBeforeReturn ? "LockedResumeButton" : "ResumeFollowingButton")!;
            Assert.True(button.IsEffectivelyVisible);
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), buttonHost)!.Value;
            buttonHost.MouseDown(point, MouseButton.Left); buttonHost.MouseUp(point, MouseButton.Left);
            overlay.UpdateLayout();
            Assert.Equal(!lockBeforeReturn, vm.LayoutMode);
            Assert.Equal(vm.LayoutMode, overlay.CanResize);
            Assert.True(vm.FollowLatest);
            Assert.Equal(scroll.Extent.Height - scroll.Viewport.Height, scroll.Offset.Y, 1);
            overlay.Width = 420; overlay.UpdateLayout();
            Assert.Equal(scroll.Extent.Height - scroll.Viewport.Height, scroll.Offset.Y, 1);
            Assert.True(vm.ReviewingTranscript);
            vm.ResumeReadingCommand.Execute(null); control.UpdateLayout();
            Assert.Equal(listViewport.Extent.Height - listViewport.Viewport.Height, listViewport.Offset.Y, 1);
            control.Width = 890; control.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); control.UpdateLayout();
            Assert.Equal(listViewport.Extent.Height - listViewport.Viewport.Height, listViewport.Offset.Y, 1);
        }
        finally { overlay.Close(); control.Close(); }
    }

    /// <summary>向活动会话发送字幕快照，验证主窗回看、两窗返回未译的最新句及旧译文回填后的跟随位置。</summary>
    /// <returns>活动会话与两个实际字幕视口的验证任务。</returns>
    [AvaloniaFact]
    public async Task LiveViewsFollowTranslatedPairsWhileMoreRecognizedPhrasesWait()
    {
        var sources = Enumerable.Range(1, 40).Select(index => $"第{index}句。").ToArray();
        var translations = sources.Select((source, index) => new SubtitlePhraseTranslation(source, $"译{index + 1}。另一句中文！")).ToImmutableArray();
        var segment = new SubtitleSegment(new(Guid.NewGuid(), 1), new(0, 16000), 2, 2, string.Concat(sources),
            "旧整块译文", 1, SubtitlePhase.Revised, TranslationState.Pending, null, BoundaryDecision.Pending);
        var session = new FakeSession();
        var vm = new DesktopViewModel(session, new FakeStore(), action => action());
        await vm.InitializeAsync(); await vm.ToggleCommand.ExecuteAsync(null);
        var identity = new PipelineIdentity(vm.SessionId, 1, 1, 1);
        var control = new ControlWindow { DataContext = vm };
        var overlay = new SubtitleWindow { DataContext = vm, Width = 340, Height = 150 };
        try
        {
            session.Publish(new(vm.SessionId, 1, new(identity, 1, false, [segment], []), "运行中", 0, true));
            control.Show(); overlay.Show(); control.UpdateLayout(); overlay.UpdateLayout();
            Assert.Equal(40, vm.LivePhrases.Count);
            Assert.Equal(sources[0], Assert.Single(vm.OverlayPhrases).Phrase.Source);
            Assert.All(vm.LivePhrases, item => Assert.Equal("等待中文…", item.Phrase.Translation));

            segment = segment with { PhraseTranslations = translations[..20], SubtitleRevision = 3 };
            session.Publish(new(vm.SessionId, 2, new(identity, 2, false, [segment], []), "运行中", 0, true));
            control.UpdateLayout(); overlay.UpdateLayout();
            var latest = vm.LivePhrases[19].Phrase;
            Assert.Equal((sources[19], translations[19].Translation), (latest.Source, latest.Translation));
            Assert.Equal(latest, vm.OverlayPhrases[^2].Phrase);
            Assert.Null(vm.OverlayPhrases[^1].Phrase.Chinese);
            var phrases = overlay.FindControl<ItemsControl>("PhraseList")!;
            var scroll = overlay.FindControl<ScrollViewer>("SubtitleScroll")!;
            // 跟随在布局结束时设置滚动偏移，等待下一帧将偏移应用到正文控件。
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            overlay.UpdateLayout();
            var pair = phrases.ContainerFromIndex(19)!;
            var top = pair.TranslatePoint(default, scroll)!.Value.Y;
            Assert.InRange(top, -1, scroll.Viewport.Height - pair.Bounds.Height + 1);
            var texts = pair.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text).ToArray();
            Assert.Equal(new[] { latest.Source, latest.Translation }, texts);
            var list = control.FindControl<ListBox>("HistoryList")!;
            Assert.True(list.Scroll!.Offset.Y > 0);
            var listBar = list.GetVisualDescendants().OfType<ScrollBar>().Single(bar => bar.Orientation == Avalonia.Layout.Orientation.Vertical);
            Assert.True(listBar.IsEffectivelyVisible); Assert.False(listBar.AllowAutoHide);
            var hit = list.TranslatePoint(new Point(30, 40), control)!.Value;
            control.MouseWheel(hit, new Vector(0, 4)); control.UpdateLayout();
            Assert.True(vm.ReviewingTranscript); Assert.True(vm.FollowLatest);
            var reviewed = list.Scroll.Offset.Y;

            segment = segment with { PhraseTranslations = translations[..30], SubtitleRevision = 4 };
            session.Publish(new(vm.SessionId, 3, new(identity, 3, false, [segment], []), "运行中", 0, true));
            control.UpdateLayout();
            Assert.Equal(reviewed, list.Scroll.Offset.Y, 1);
            var resume = control.GetVisualDescendants().OfType<Button>().Single(button => button.Command == vm.ResumeReadingCommand);
            var click = resume.TranslatePoint(new Point(resume.Bounds.Width / 2, resume.Bounds.Height / 2), control)!.Value;
            control.MouseDown(click, MouseButton.Left); control.MouseUp(click, MouseButton.Left);
            control.UpdateLayout(); overlay.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(vm.ReviewingTranscript); Assert.True(list.Scroll.Offset.Y > reviewed);
            Assert.Equal(sources[^1], vm.OverlayPhrases[^1].Phrase.Source);
            Assert.Equal(list.Scroll.Extent.Height - list.Scroll.Viewport.Height, list.Scroll.Offset.Y, 1);
            Assert.Equal(scroll.Extent.Height - scroll.Viewport.Height, scroll.Offset.Y, 1);
            foreach (var (items, viewport) in new (ItemsControl, Control)[] { (list, list), (phrases, scroll) })
            {
                var tail = items.ContainerFromIndex(39)!;
                var lastLine = tail.TranslatePoint(new Point(0, tail.Bounds.Height), viewport)!.Value.Y;
                Assert.InRange(lastLine, 0, viewport.Bounds.Height + 1);
            }

            segment = segment with { Source = segment.Source + "新的一句。", SourceRevision = 3, SubtitleRevision = 5,
                PhraseTranslations = translations[..31] };
            session.Publish(new(vm.SessionId, 4, new(identity, 4, false, [segment], []), "运行中", 0, true));
            control.UpdateLayout(); overlay.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal("新的一句。", vm.LivePhrases[vm.LivePhraseIndex].Phrase.Source);
            Assert.Equal("新的一句。", vm.OverlayPhrases[^1].Phrase.Source);
            Assert.Equal(list.Scroll.Extent.Height - list.Scroll.Viewport.Height, list.Scroll.Offset.Y, 1);
            Assert.Equal(scroll.Extent.Height - scroll.Viewport.Height, scroll.Offset.Y, 1);
            vm.DisplayModeIndex = 2;
            Assert.Equal("新的一句。", vm.OverlayPhrases[^1].Phrase.Source);
        }
        finally { overlay.Close(); control.Close(); await vm.CloseAsync(); }
    }

    /// <summary>新块加入后两个窗口继续展示相邻的译文，浮窗向上滚动可以回到更早的块。</summary>
    [AvaloniaFact]
    public void NewPendingBlockKeepsEarlierTranslationsInView()
    {
        var rows = Enumerable.Range(1, 8).Select(index => new SubtitleRow(new(new(Guid.NewGuid(), 1),
            new(index * 16000, (index + 1) * 16000), 1, 2, $"原句{index}。", $"译文{index}。", 1,
            SubtitlePhase.Final, TranslationState.Complete, null, BoundaryDecision.Pending))).ToArray();
        var pending = new SubtitleRow(new(new(Guid.NewGuid(), 1), new(144000, 160000), 1, 1,
            "下一句还在识别。", null, null, SubtitlePhase.Draft, TranslationState.Pending, null, BoundaryDecision.Pending));
        var vm = new DesktopViewModel(new FakeSession(), new FakeStore(), action => action());
        var control = new ControlWindow { DataContext = vm };
        var overlay = new SubtitleWindow { DataContext = vm, Width = 520, Height = 260 };
        try
        {
            vm.History = rows; vm.OverlayRows = vm.History;
            control.Show(); overlay.Show(); control.UpdateLayout(); overlay.UpdateLayout();
            vm.History = [..rows, pending]; vm.OverlayRows = vm.History;
            control.UpdateLayout(); overlay.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            control.UpdateLayout(); overlay.UpdateLayout();
            var scroll = overlay.FindControl<ScrollViewer>("SubtitleScroll")!;
            var body = overlay.FindControl<ItemsControl>("PhraseList")!;
            var list = control.FindControl<ListBox>("HistoryList")!;
            foreach (var index in new[] { 6, 7 })
            {
                foreach (var (items, viewport) in new (ItemsControl, Control)[] { (body, scroll), (list, list) })
                {
                    var pair = items.ContainerFromIndex(index)!;
                    var translation = pair.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Text == $"译文{index + 1}。");
                    var top = translation.TranslatePoint(default, viewport)!.Value.Y;
                    Assert.InRange(top, -1, viewport.Bounds.Height - translation.Bounds.Height + 1);
                }
            }
            var mainBody = list.TranslatePoint(new Point(30, 40), control)!.Value;
            control.MouseWheel(mainBody, new Vector(0, 3)); control.UpdateLayout();
            Assert.True(vm.ReviewingTranscript); Assert.True(vm.FollowLatest);
            vm.ResumeReadingCommand.Execute(null);
            control.UpdateLayout(); overlay.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(scroll.Extent.Height - scroll.Viewport.Height, scroll.Offset.Y, 1);
            overlay.SetInteractive(true); overlay.UpdateLayout();
            var overlayBar = scroll.GetVisualDescendants().OfType<ScrollBar>().Single(bar => bar.Orientation == Avalonia.Layout.Orientation.Vertical);
            Assert.True(overlayBar.IsEffectivelyVisible); Assert.False(overlayBar.AllowAutoHide);
            var thumb = overlayBar.GetVisualDescendants().OfType<Thumb>().Single();
            var pointer = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), overlay)!.Value;
            var topOfTrack = overlayBar.TranslatePoint(new Point(overlayBar.Bounds.Width / 2, 0), overlay)!.Value;
            overlay.MouseDown(pointer, MouseButton.Left);
            overlay.MouseMove(topOfTrack, RawInputModifiers.LeftMouseButton);
            overlay.MouseUp(topOfTrack, MouseButton.Left); overlay.UpdateLayout();
            Assert.False(vm.FollowLatest);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            overlay.UpdateLayout();
            Assert.Equal(0, scroll.Offset.Y);
            Assert.Contains(body.ContainerFromIndex(0)!.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "译文1。");
            overlay.SetInteractive(false); overlay.UpdateLayout();
            Assert.False(overlayBar.IsEffectivelyVisible);
        }
        finally { overlay.Close(); control.Close(); }
    }

    /// <summary>双语重译等待时仍能读到上一版，首个新译文返回后跟随该句，切换仅原文展示本次识别。</summary>
    [AvaloniaFact]
    public void RevisingCaptionsFollowTheNewTranslationAndSourceOnlyShowsCurrentText()
    {
        var previous = Enumerable.Range(1, 20).Select(index => new SubtitlePhraseTranslation($"旧{index}。", $"旧译{index}。")).ToImmutableArray();
        var segment = new SubtitleSegment(new(Guid.NewGuid(), 1), new(0, 16000), 2, 2, "新甲。新乙。", null, null,
            SubtitlePhase.Revised, TranslationState.Pending, null, BoundaryDecision.Pending) { PreviousPhraseTranslations = previous };
        var vm = new DesktopViewModel(new FakeSession(), new FakeStore(), action => action());
        var overlay = new SubtitleWindow { DataContext = vm, Width = 520, Height = 260 };
        try
        {
            vm.History = [new(segment)]; vm.OverlayRows = vm.History;
            overlay.Show(); overlay.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); overlay.UpdateLayout();
            var scroll = overlay.FindControl<ScrollViewer>("SubtitleScroll")!;
            Assert.True(scroll.Offset.Y > 0);
            Assert.Equal("旧译20。", vm.OverlayPhrases[^1].Translation);
            vm.History = [new(segment with { PhraseTranslations = [new("新甲。", "译文甲。")] })];
            vm.OverlayRows = vm.History; overlay.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); overlay.UpdateLayout();
            Assert.Equal(0, scroll.Offset.Y);
            Assert.Equal("译文甲。", vm.OverlayPhrases[0].Translation);
            Assert.Equal("旧译2。", vm.OverlayPhrases[1].Translation);
            vm.PauseFollowing(); vm.ResumeFollowingCommand.Execute(null);
            overlay.UpdateLayout(); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); overlay.UpdateLayout();
            var latest = overlay.FindControl<ItemsControl>("PhraseList")!.ContainerFromIndex(1)!;
            var bottom = latest.TranslatePoint(new Point(0, latest.Bounds.Height), scroll)!.Value.Y;
            Assert.InRange(bottom, 0, scroll.Viewport.Height + 1);
            vm.DisplayModeIndex = (int)Core.Desktop.SubtitleDisplayMode.SourceOnly;
            Assert.Equal(new[] { "新甲。", "新乙。" }, vm.OverlayPhrases.Select(item => item.Phrase.Source));
        }
        finally { overlay.Close(); }
    }

    /// <summary>鼠标停在当前会话行上时，音频状态和其他短句的回填保留悬停行，并更新其无障碍诊断。</summary>
    /// <returns>真实视图接收会话更新的验证任务。</returns>
    [AvaloniaFact]
    public async Task HoveredTranscriptRowKeepsItsVisualsAcrossAudioAndTranslationUpdates()
    {
        var session = new FakeSession();
        var vm = new DesktopViewModel(session, new FakeStore(), action => action());
        await vm.InitializeAsync(); await vm.ToggleCommand.ExecuteAsync(null);
        var identity = new PipelineIdentity(vm.SessionId, 1, 1, 1);
        var segment = new SubtitleSegment(new(Guid.NewGuid(), 1), new(0, 16000), 1, 2, "第一句。第二句。",
            null, null, SubtitlePhase.Revised, TranslationState.Pending, null, BoundaryDecision.Pending)
        { PhraseTranslations = [new("第一句。", "第一条译文")] };
        var snapshot = new SubtitleSnapshot(identity, 2, false, [segment], []);
        session.Publish(new(vm.SessionId, 1, snapshot, "运行中", 0, true));
        var control = new ControlWindow { DataContext = vm };
        try
        {
            control.Show(); control.UpdateLayout();
            var list = control.FindControl<ListBox>("HistoryList")!;
            var item = (ListBoxItem)list.ContainerFromIndex(0)!;
            Assert.Empty(item.GetVisualDescendants().OfType<Expander>());
            var translation = item.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "第一条译文");
            var hit = item.TranslatePoint(new Point(25, 25), control)!.Value;
            control.MouseMove(hit); control.UpdateLayout();
            Assert.True(item.IsPointerOver);
            var changes = 0;
            vm.LivePhrases.CollectionChanged += (_, _) => changes++;
            session.Publish(new(vm.SessionId, 2, snapshot, "运行中", 1, true));
            segment = segment with { SubtitleRevision = 3, PhraseTranslations = [.. segment.PhraseTranslations, new("第二句。", "第二条译文")] };
            session.Publish(new(vm.SessionId, 3, snapshot with { StateSequence = 3, Segments = [segment] }, "运行中", 0, true));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs(); control.UpdateLayout();
            Assert.Same(item, list.ContainerFromIndex(0));
            Assert.True(item.IsPointerOver);
            Assert.Equal(0, changes);
            Assert.Contains("字幕 v3", AutomationProperties.GetHelpText(translation));
            Assert.Contains(list.ContainerFromIndex(1)!.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "第二条译文");
        }
        finally { control.Close(); await vm.CloseAsync(); }
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
            control.FindControl<ListBox>("MainNavigation")!.SelectedIndex = 2; control.UpdateLayout();
            var settings = control.FindControl<UserControl>("SettingsPanel")!;
            settings.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 1; control.UpdateLayout();
            var selector = settings.FindControl<ComboBox>("DisplayModeSelector")!;
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
