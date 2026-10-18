using Avalonia;
using Avalonia.Controls;
using LiveLingo.App.ViewModels;
using LiveLingo.App.Views;
using LiveLingo.Core.Desktop;
using LiveLingo.Platform.Windows.Windows;

namespace LiveLingo.Desktop.Windows;

/// <summary>把 Avalonia 窗口生命周期接到原生策略，保持 UI 与 Win32 分层。</summary>
public sealed class OverlayBridge : IDisposable
{
    private readonly ControlWindow control;
    private readonly SubtitleWindow overlay;
    private readonly DesktopViewModel model;
    private readonly DesktopHotkeys hotkeys;
    private readonly Win32Properties.CustomWndProcHookCallback hook;
    private readonly Win32Properties.CustomWindowStylesCallback styles;
    private bool clamping;
    private bool disposed;

    /// <summary>登记窗口钩子并应用初始穿透，不启动模型。</summary>
    /// <param name="control">控制窗。</param><param name="overlay">字幕窗。</param><param name="model">当前界面状态。</param>
    public OverlayBridge(ControlWindow control, SubtitleWindow overlay, DesktopViewModel model)
    {
        this.control = control; this.overlay = overlay; this.model = model;
        hotkeys = new(Handle(control)); hook = OnMessage; styles = Merge;
        Win32Properties.AddWndProcHookCallback(control, hook);
        Win32Properties.AddWindowStylesCallback(overlay, styles);
        model.OverlayChanged += ApplyMode; model.SettingsApplied += ApplySettings;
        overlay.ScalingChanged += OnScreenChanged;
        if (overlay.Screens is { } screens) screens.Changed += OnScreenChanged;
        overlay.PositionChanged += OnPositionChanged;
        overlay.SizeChanged += OnSizeChanged;
        ApplySettings(model.CurrentSettings); ApplyMode();
    }

    /// <summary>获取已经创建且明确为 HWND 的平台句柄。</summary><param name="window">实际窗口。</param><returns>有效 HWND。</returns>
    public static nint Handle(Window window)
    {
        var handle = window.TryGetPlatformHandle();
        return handle is { HandleDescriptor: "HWND", Handle: not 0 } ? handle.Handle : throw new InvalidOperationException("未创建 Windows HWND。");
    }

    /// <summary>应用布局、视觉和快捷键，冲突显示在控制窗。</summary><param name="settings">通过校验的完整设置。</param>
    private void ApplySettings(DesktopSettings settings)
    {
        overlay.ApplyAppearance(settings);
        overlay.Width = settings.OverlayWidth; overlay.Height = settings.OverlayHeight;
        overlay.Position = new(settings.OverlayX, settings.OverlayY); Clamp();
        var error = hotkeys.Configure(settings.StartKey, settings.VisibilityKey, settings.LayoutKey);
        if (error.Length > 0) model.Error = "快捷键未全部注册：" + error;
    }

    /// <summary>保持显隐、布局与 HWND 样式一致，重显不激活窗口。</summary>
    private void ApplyMode()
    {
        overlay.SetInteractive(model.LayoutMode);
        OverlayNative.Apply(Handle(overlay), model.LayoutMode);
        if (model.OverlayVisible) overlay.Show(); else overlay.Hide();
    }

    /// <summary>合并框架重算的样式；回调内不执行原生样式写入。</summary><param name="style">普通样式。</param><param name="extended">扩展样式。</param><returns>完整样式。</returns>
    private (uint style, uint exStyle) Merge(uint style, uint extended) => (style, OverlayNative.MergeStyles(extended, model.LayoutMode));

    /// <summary>只处理已登记热键；命令自身负责互斥与错误。</summary>
    /// <param name="window">HWND。</param><param name="message">原生消息。</param><param name="wParam">消息参数。</param><param name="lParam">消息参数。</param><param name="handled">是否已处理。</param><returns>消息结果。</returns>
    private nint OnMessage(nint window, uint message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x312 || disposed) return 0;
        switch (hotkeys.Resolve((int)wParam))
        {
            case 0: model.ToggleCommand.Execute(null); break;
            case 1: model.ToggleOverlayCommand.Execute(null); break;
            case 2: model.ToggleLayoutCommand.Execute(null); break;
            default: return 0;
        }
        handled = true; return 0;
    }

    /// <summary>重新获取有效屏幕，不缓存可能已拔出的 Screen。</summary><param name="sender">屏幕来源。</param><param name="args">变化。</param>
    private void OnScreenChanged(object? sender, EventArgs args) => Clamp();
    /// <summary>位置变化后保存像素坐标，不重复乘 DPI。</summary><param name="sender">字幕窗。</param><param name="args">变化。</param>
    private void OnPositionChanged(object? sender, PixelPointEventArgs args) => Remember();
    /// <summary>尺寸变化后保存 DIP。</summary><param name="sender">字幕窗。</param><param name="args">变化。</param>
    private void OnSizeChanged(object? sender, SizeChangedEventArgs args) => Remember();
    /// <summary>保存当前布局，只有用户明确保存才落盘。</summary>
    private void Remember() => model.RememberBounds(overlay.Position.X, overlay.Position.Y, overlay.Width, overlay.Height);

    /// <summary>把当前布局放回实际工作区，保留负坐标屏幕。</summary>
    private void Clamp()
    {
        if (clamping || disposed || overlay.Screens is not { } screens) return;
        var screen = screens.ScreenFromWindow(overlay) ?? screens.Primary ?? screens.All.FirstOrDefault();
        if (screen is null || screen.WorkingArea.Width <= 0) return;
        clamping = true;
        try
        {
            var area = screen.WorkingArea;
            var placed = new OverlayPlacement(overlay.Position.X, overlay.Position.Y, overlay.Width, overlay.Height).Clamp(area.X, area.Y, area.Width, area.Height, screen.Scaling);
            overlay.Width = placed.Width; overlay.Height = placed.Height; overlay.Position = new(placed.X, placed.Y); Remember();
        }
        finally { clamping = false; }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        hotkeys.Dispose(); model.OverlayChanged -= ApplyMode; model.SettingsApplied -= ApplySettings;
        overlay.ScalingChanged -= OnScreenChanged;
        if (overlay.Screens is { } screens) screens.Changed -= OnScreenChanged;
        overlay.PositionChanged -= OnPositionChanged; overlay.SizeChanged -= OnSizeChanged;
        Win32Properties.RemoveWndProcHookCallback(control, hook); Win32Properties.RemoveWindowStylesCallback(overlay, styles);
    }
}
