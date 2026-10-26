using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Styling;
using LiveLingo.App.ViewModels;
using LiveLingo.App.Views;
using LiveLingo.Core.Desktop;
using LiveLingo.Core.History;
using LiveLingo.Core.Subtitles;
using LiveLingo.Platform.Windows.Processes;
using LiveLingo.Platform.Windows.Windows;
using LiveLingo.Runtime.Pipeline;

namespace LiveLingo.Desktop.Windows;

/// <summary>显式且有界的桌面验证模式；仅在独占非 C 盘目录保存正文和窗口证据。</summary>
public sealed class DesktopVerification
{
    private readonly string directory;
    private readonly double seconds;
    private readonly string? wave;
    private readonly int targetPid;
    private readonly List<DesktopUpdate> updates = [];
    private PipelineSnapshot? final;
    private long snapshots;
    private readonly CancellationTokenSource stop = new();
    private readonly DesktopHistory? history;

    /// <summary>获取验证是否已完成资源核验，避免统一退出再次取消已释放来源。</summary>
    public bool Finished { get; private set; }

    /// <summary>用户关闭窗口时停止验证循环，后续仍执行正常资源核验。</summary>
    public void Cancel() => stop.Cancel();

    /// <summary>验证参数和独占证据目录，不自动覆盖现有运行记录。</summary><param name="arguments">verify、seconds、wave 或 target 参数。</param><param name="history">已隔离的验证历史库。</param>
    public DesktopVerification(string[] arguments, DesktopHistory? history = null)
    {
        this.history = history;
        var options = new Dictionary<string, string>();
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 >= arguments.Length || arguments[index] is not ("--verify" or "--seconds" or "--wave" or "--target") || !options.TryAdd(arguments[index], arguments[index + 1]))
                throw new ArgumentException("验证参数无效。");
        }
        directory = options.GetValueOrDefault("--verify") ?? throw new ArgumentException("需要 --verify 非 C 盘新目录。");
        DesktopSettings.ValidateDataPath(directory);
        seconds = double.Parse(options.GetValueOrDefault("--seconds", "20"), System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(seconds) || seconds is < 5 or > 7500) throw new ArgumentException("验证时长须为 5–7500 秒。");
        wave = options.GetValueOrDefault("--wave"); targetPid = int.Parse(options.GetValueOrDefault("--target", "0"), System.Globalization.CultureInfo.InvariantCulture);
        if (wave is not null && (!Path.IsPathFullyQualified(wave) || !File.Exists(wave))) throw new ArgumentException("验证 WAV 不存在。");
        if (wave is not null && targetPid != 0 || targetPid < 0) throw new ArgumentException("只选择一种验证输入。");
        if (Directory.Exists(directory)) throw new IOException("验证目录必须是新的独占目录。");
        Directory.CreateDirectory(directory);
    }

    /// <summary>执行真实窗口更新及可选真实模型回放，在退出前保存资源核验。</summary>
    /// <param name="model">实际界面。</param><param name="session">真实运行服务。</param><param name="control">控制窗。</param><param name="overlay">字幕窗。</param>
    /// <param name="close">统一退出入口。</param><returns>有界验证任务。</returns>
    public async Task RunAsync(DesktopViewModel model, DesktopSession session, ControlWindow control, SubtitleWindow overlay, Action<int> close)
    {
        string? error = null;
        var started = Stopwatch.StartNew();
        session.Updated += OnUpdate; session.PipelineUpdated += OnPipeline;
        try
        {
            if (wave is not null || targetPid != 0)
            {
                if (wave is not null)
                {
                    session.VerificationInput = (id, token) => WaveReplaySource.ReadAsync(wave, id, seconds, true, token);
                    var own = WindowsTargets.Query(Environment.ProcessId).Identity!;
                    model.Targets = [new("显式 WAV 回放", own)]; model.SelectedTarget = model.Targets[0];
                }
                else
                {
                    model.Targets = [new("显式真实采集", WindowsTargets.Query(targetPid).Identity ?? throw new ArgumentException("目标身份不可核实。"))];
                    model.SelectedTarget = model.Targets[0];
                }
                await model.ToggleCommand.ExecuteAsync(null);
                if (!model.Active) throw new InvalidOperationException(model.Error);
            }
            else
            {
                model.Status = "窗口验证 · 合成字幕";
                var row = new SubtitleRow(new(new(Guid.NewGuid(), 1), new(0, 16000), 1, 1, "最後まで聞かなければ、行くかどうかは分からない。", "不听到最后，就无法知道是否会去。", 1,
                    Core.Subtitles.SubtitlePhase.Revised, Core.Subtitles.TranslationState.Complete, null, Core.Subtitles.BoundaryDecision.Pending));
                model.OverlayRows = [row]; model.History = [row];
                await SeedHistoryAsync(row.Segment);
            }
            await WriteAsync("ready.json", new { Pid = Environment.ProcessId, Control = OverlayBridge.Handle(control).ToInt64(), Overlay = OverlayBridge.Handle(overlay).ToInt64(), LayoutLock = OverlayBridge.Handle(overlay.LayoutLock).ToInt64() });
            var lastCommand = ""; var index = 0;
            var deadline = wave is not null || targetPid != 0 ? seconds + 180 : seconds;
            while (started.Elapsed.TotalSeconds < deadline && !stop.IsCancellationRequested)
            {
                await Task.Delay(250);
                if (File.Exists(Path.Combine(directory, "stop.request"))) break;
                var commandPath = Path.Combine(directory, "command.txt");
                if (File.Exists(commandPath))
                {
                    var command = await File.ReadAllTextAsync(commandPath);
                    if (command != lastCommand)
                    {
                        lastCommand = command;
                        if (command.StartsWith("position:", StringComparison.Ordinal))
                        { var p = command[9..].Split(',').Select(int.Parse).ToArray(); overlay.Position = new(p[0], p[1]); overlay.Width = p[2]; overlay.Height = p[3]; }
                        else if (command == "save-settings") { model.FontSize = 32; await model.SaveCommand.ExecuteAsync(null); }
                        else if (command.StartsWith("update", StringComparison.Ordinal)) model.Status = "更新证据 " + ++index;
                        else if (command.StartsWith("reshow", StringComparison.Ordinal)) { model.ToggleOverlayCommand.Execute(null); model.ToggleOverlayCommand.Execute(null); }
                    }
                }
                await WriteAsync("window.json", new { Utc = DateTimeOffset.UtcNow, Foreground = OverlayNative.GetForegroundWindow().ToInt64(),
                    Styles = OverlayNative.ReadStyles(OverlayBridge.Handle(overlay)), Transparency = overlay.ActualTransparencyLevel.ToString(),
                    overlay.Position, overlay.Width, overlay.Height, overlay.RenderScaling, model.LayoutMode, model.OverlayVisible, model.Status, model.Error,
                    LayoutLock = new { overlay.LayoutLock.Position, overlay.LayoutLock.Width, overlay.LayoutLock.Height, overlay.LayoutLock.RenderScaling, overlay.LayoutLock.IsVisible, Styles = OverlayNative.ReadStyles(OverlayBridge.Handle(overlay.LayoutLock)) },
                    Rows = model.OverlayRows.Count, History = model.History.Count, Settings = model.CurrentSettings,
                    MovePoint = MovePoint(overlay),
                    Screens = overlay.Screens?.All.Select(x => new { x.DisplayName, x.Scaling, x.Bounds, x.WorkingArea }).ToArray() });
                if (wave is not null && !model.Active) break;
                if (targetPid != 0 && started.Elapsed.TotalSeconds >= seconds) break;
            }
            using (var bitmap = new RenderTargetBitmap(new PixelSize((int)control.Bounds.Width, (int)control.Bounds.Height))) { bitmap.Render(control); bitmap.Save(Path.Combine(directory, "control-render.png"), PngBitmapEncoderOptions.Default); }
            await CaptureControlTabsAsync(control);
            using (var bitmap = new RenderTargetBitmap(new PixelSize((int)overlay.Bounds.Width, (int)overlay.Bounds.Height))) { bitmap.Render(overlay); bitmap.Save(Path.Combine(directory, "overlay-render.png"), PngBitmapEncoderOptions.Default); }
            await CaptureSubtitleModesAsync(model, overlay);
        }
        catch (Exception failure) { error = failure.ToString(); }
        finally
        {
            await session.StopAsync(); await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            session.Updated -= OnUpdate; session.PipelineUpdated -= OnPipeline;
            var passed = error is null && session.LastError is null && session.RemainingOwned.Length == 0 && session.RemainingTemporary.Length == 0 &&
                (wave is null || final is { Status: "completed" } && final.Gaps.Length == 0 && final.BacklogSamples == 0) &&
                (targetPid == 0 || final is { Status: "stopped" or "completed" });
            await WriteAsync("report.json", new { Passed = passed, QualityPassed = false, Error = error, SessionError = session.LastError, Seconds = started.Elapsed.TotalSeconds, Snapshots = snapshots,
                session.LastOwned, session.RemainingOwned, session.RemainingTemporary,
                Binary = typeof(DesktopSession).Assembly.Location, BinarySha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(typeof(DesktopSession).Assembly.Location))),
                Final = final, Displayed = model.History.Select(x => x.Segment).ToArray(), Updates = updates.ToArray(),
                Unexecuted = new[] { "实际拔屏与混合 DPI 移动", "独占全屏" } });
            Finished = true; stop.Dispose(); close(passed ? 0 : 1);
        }
    }

    /// <summary>在隔离库内建立双语样稿，普通运行不会执行。</summary><param name="segment">合成双语内容。</param><returns>写入任务。</returns>
    private async Task SeedHistoryAsync(SubtitleSegment segment)
    {
        if (history?.Store is not { } store) return;
        foreach (var app in new[] { "示例视频", "日语学习" })
        {
            var id = Guid.NewGuid();
            var recorder = store.BeginSession(new(id, app, "Japanese", "translategemma:12b", DateTimeOffset.UtcNow));
            recorder.Offer(new(new(id, 1, 1, 1), 1, false, [segment, segment with { Key = new(Guid.NewGuid(), 1), Range = new(16000, 32000), Source = "はい、はい。", Translation = "是的，是的。" }], []));
            await recorder.CompleteAsync();
        }
    }

    /// <summary>读取实际拖动区的物理中心，避免测试依赖已过时的固定坐标。</summary><param name="overlay">自有字幕窗口。</param><returns>屏幕物理坐标。</returns>
    private static PixelPoint MovePoint(SubtitleWindow overlay)
    {
        var handle = overlay.FindControl<Border>("MoveHandle")!;
        return handle.PointToScreen(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2));
    }

    /// <summary>渲染明暗主题、默认及最小尺寸的三个页面，不发送系统输入。</summary><param name="control">当前自有控制窗。</param><returns>布局及截图任务。</returns>
    private async Task CaptureControlTabsAsync(ControlWindow control)
    {
        var tabs = control.FindControl<ListBox>("MainNavigation")!;
        var settings = control.FindControl<SettingsView>("SettingsPanel")!.FindControl<TabControl>("SettingsTabs")!;
        var settingsIndex = settings.SelectedIndex;
        var selected = tabs.SelectedIndex; var width = control.Width; var height = control.Height;
        var theme = control.RequestedThemeVariant;
        try
        {
            if (control.DataContext is DesktopViewModel { Archive: { } archive }) await archive.PollAsync();
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach (var minimum in new[] { false, true })
            {
                control.RequestedThemeVariant = variant;
                control.Width = minimum ? control.MinWidth : width; control.Height = minimum ? control.MinHeight : height;
                for (var index = 0; index < tabs.Items.Count; index++)
                {
                    tabs.SelectedIndex = index;
                    await Dispatcher.UIThread.InvokeAsync(control.UpdateLayout, DispatcherPriority.Background);
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)control.Bounds.Width, (int)control.Bounds.Height));
                    bitmap.Render(control); bitmap.Save(Path.Combine(directory, $"control-{variant.Key}-{(minimum ? "minimum" : "default")}-page-{index}.png"), PngBitmapEncoderOptions.Default);
                    if (index != 2) continue;
                    for (var section = 0; section < settings.Items.Count; section++)
                    {
                        settings.SelectedIndex = section;
                        await Dispatcher.UIThread.InvokeAsync(control.UpdateLayout, DispatcherPriority.Background);
                        using var sectionImage = new RenderTargetBitmap(new PixelSize((int)control.Bounds.Width, (int)control.Bounds.Height));
                        sectionImage.Render(control);
                        sectionImage.Save(Path.Combine(directory, $"settings-{variant.Key}-{(minimum ? "minimum" : "default")}-{section}.png"), PngBitmapEncoderOptions.Default);
                    }
                    settings.SelectedIndex = settingsIndex;
                }
            }
        }
        finally { settings.SelectedIndex = settingsIndex; tabs.SelectedIndex = selected; control.Width = width; control.Height = height; control.RequestedThemeVariant = theme; }
    }

    /// <summary>渲染锁定与解锁字幕，并核对真实按钮 HWND 的命中样式、位置及显隐；不模拟鼠标输入。</summary><param name="model">字幕状态。</param><param name="overlay">当前字幕窗。</param><returns>截图和原生状态核验任务。</returns>
    private async Task CaptureSubtitleModesAsync(DesktopViewModel model, SubtitleWindow overlay)
    {
        var layout = model.LayoutMode; var visible = model.OverlayVisible; var width = overlay.Width; var height = overlay.Height;
        var checks = new Dictionary<string, bool>();
        try
        {
            if (!model.OverlayVisible) model.ToggleOverlayCommand.Execute(null);
            foreach (var minimum in new[] { false, true })
            {
                overlay.Width = minimum ? overlay.MinWidth : width; overlay.Height = Math.Max(height, 260);
                foreach (var unlocked in new[] { false, true })
                {
                    if (model.LayoutMode != unlocked) model.ToggleLayoutCommand.Execute(null);
                    await Dispatcher.UIThread.InvokeAsync(() => { overlay.UpdateLayout(); overlay.LayoutLock.UpdateLayout(); }, DispatcherPriority.Background);
                    var anchor = overlay.FindControl<Border>("LockAnchor")!;
                    var offset = anchor.TranslatePoint(default, overlay)!.Value;
                    var button = overlay.LayoutLock;
                    var key = $"{(minimum ? "minimum" : "default")}-{(unlocked ? "unlocked" : "locked")}";
                    checks[key] = button.IsVisible && button.Position == anchor.PointToScreen(default) &&
                        Math.Abs(button.Bounds.Height - anchor.Bounds.Height) < 1 &&
                        (OverlayNative.ReadStyles(OverlayBridge.Handle(button)) & (OverlayNative.NoActivate | OverlayNative.Transparent)) == OverlayNative.NoActivate &&
                        ((OverlayNative.ReadStyles(OverlayBridge.Handle(overlay)) & OverlayNative.Transparent) == 0) == unlocked;
                    using var body = new RenderTargetBitmap(new PixelSize((int)overlay.Bounds.Width, (int)overlay.Bounds.Height));
                    using var lockImage = new RenderTargetBitmap(new PixelSize((int)button.Bounds.Width, (int)button.Bounds.Height));
                    body.Render(overlay); lockImage.Render(button);
                    var canvas = new Canvas { Width = overlay.Bounds.Width, Height = overlay.Bounds.Height };
                    canvas.Children.Add(new Image { Source = body, Width = canvas.Width, Height = canvas.Height });
                    var corner = new Image { Source = lockImage, Width = button.Bounds.Width, Height = button.Bounds.Height };
                    Canvas.SetLeft(corner, offset.X); Canvas.SetTop(corner, offset.Y); canvas.Children.Add(corner);
                    canvas.Measure(overlay.Bounds.Size); canvas.Arrange(new Rect(overlay.Bounds.Size));
                    using var combined = new RenderTargetBitmap(new PixelSize((int)canvas.Width, (int)canvas.Height));
                    combined.Render(canvas); combined.Save(Path.Combine(directory, $"subtitle-{key}.png"), PngBitmapEncoderOptions.Default);
                }
            }
            model.ToggleOverlayCommand.Execute(null);
            checks["HiddenTogether"] = !overlay.IsVisible && !overlay.LayoutLock.IsVisible;
            await WriteAsync("subtitle-controls.json", checks);
            if (checks.Values.Any(value => !value)) throw new InvalidOperationException("字幕锁定按钮原生状态核验失败。");
        }
        finally
        {
            overlay.Width = width; overlay.Height = height;
            if (model.LayoutMode != layout) model.ToggleLayoutCommand.Execute(null);
            if (model.OverlayVisible != visible) model.ToggleOverlayCommand.Execute(null);
        }
    }

    /// <summary>有界保存显示更新，每秒级变化不保留无限历史。</summary><param name="value">完整快照。</param>
    private void OnUpdate(DesktopUpdate value) { lock (updates) { if (updates.Count == 256) updates.RemoveAt(0); updates.Add(value with { Subtitles = null }); } }
    /// <summary>保留最后权威版本以核对 UI 投影。</summary><param name="value">真实流水线快照。</param>
    private void OnPipeline(PipelineSnapshot value) { final = value; Interlocked.Increment(ref snapshots); }
    /// <summary>同目录原子提交证据，短暂共享冲突有界异步重试，不能阻塞窗口输入。</summary>
    /// <param name="name">文件名。</param><param name="value">证据对象。</param><returns>完整提交任务，预算耗尽明确失败。</returns>
    private async Task WriteAsync(string name, object value)
    {
        var path = Path.Combine(directory, name);
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(path + ".tmp", path, true); return; }
            catch (Exception error) when (attempt < 4 && error is IOException or UnauthorizedAccessException) { await Task.Delay(25 << attempt); }
        }
    }
}
