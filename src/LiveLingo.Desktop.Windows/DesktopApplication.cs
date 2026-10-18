using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using LiveLingo.App.ViewModels;
using LiveLingo.App.Views;
using LiveLingo.Core.Desktop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LiveLingo.Desktop.Windows;

/// <summary>持有所有窗口和唯一异步退出任务，在 UI 消息循环结束前等待资源回收。</summary>
/// <param name="host">组合根宿主。</param><param name="arguments">显式启动参数。</param>
public sealed class DesktopApplication(IHost host, string[] arguments) : Application
{
    private IClassicDesktopStyleApplicationLifetime? desktop;
    private DesktopViewModel? model;
    private OverlayBridge? bridge;
    private Task? closing;
    private bool cleaned;
    private Task? initialization;
    private Task? verification;
    private DesktopVerification? verifier;
    private int requestedExit;

    /// <inheritdoc />
    public override void Initialize() { RequestedThemeVariant = ThemeVariant.Dark; Styles.Add(new FluentTheme()); }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            desktop = lifetime; desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            model = new(host.Services.GetRequiredService<IDesktopSession>(), host.Services.GetRequiredService<IDesktopSettingsStore>(), action => Dispatcher.UIThread.Post(action));
            var control = new ControlWindow { DataContext = model };
            var overlay = new SubtitleWindow { DataContext = model };
            desktop.MainWindow = control;
            control.Closing += OnClosing; overlay.Closing += OnClosing; desktop.ShutdownRequested += OnShutdownRequested;
            control.Opened += (_, _) => { initialization = InitializeAsync(control, overlay); };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>窗口创建后接通原生策略；所有异常保持控制窗可见。</summary><param name="control">控制窗。</param><param name="overlay">字幕窗。</param><returns>初始化及显式验证任务。</returns>
    private async Task InitializeAsync(ControlWindow control, SubtitleWindow overlay)
    {
        try
        {
            overlay.Show(); bridge = new(control, overlay, model!);
            await host.StartAsync(); await model!.InitializeAsync();
            if (arguments.Length != 0)
            {
                verifier = new DesktopVerification(arguments);
                verification = verifier.RunAsync(model, host.Services.GetRequiredService<DesktopSession>(), control, overlay, exit => { requestedExit = exit; RequestClose(); });
            }
        }
        catch (Exception error) { model!.Error = "桌面初始化失败：" + error.Message; }
    }

    /// <summary>同步取消关闭，不能让 async 事件的首次 await 提前结束消息循环。</summary><param name="sender">窗口。</param><param name="args">关闭请求。</param>
    private void OnClosing(object? sender, WindowClosingEventArgs args)
    { if (cleaned) return; args.Cancel = true; RequestClose(); }

    /// <summary>会话退出同样复用统一清理路径。</summary><param name="sender">桌面宿主。</param><param name="args">退出请求。</param>
    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs args)
    { if (cleaned) return; args.Cancel = true; RequestClose(); }

    /// <summary>登记唯一退出任务，重复关闭不重入。</summary>
    public void RequestClose() { closing ??= CloseAsync(); }

    /// <summary>依次结束 UI 消费、模型资源、热键及宿主，最后正常关闭窗口。</summary><returns>清理完成任务。</returns>
    private async Task CloseAsync()
    {
        var exit = requestedExit;
        try
        {
            if (initialization is not null) await initialization;
            if (verification is { IsCompleted: false } && verifier is { Finished: false }) { verifier.Cancel(); await verification; exit = requestedExit; }
            if (model is not null) await model.CloseAsync();
        }
        catch (Exception error) { exit = 1; if (model is not null) model.Error = "退出失败：" + error.Message; }
        finally
        {
            bridge?.Dispose();
            try { await host.StopAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { exit = 1; }
            cleaned = true; desktop?.TryShutdown(exit);
        }
    }
}
