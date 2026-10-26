using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using LiveLingo.App.ViewModels;
using LiveLingo.App.Services;
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
    private DesktopHistory? history;
    private bool historyNotificationPending;
    private readonly object historyNotifications = new();

    /// <inheritdoc />
    public override void Initialize() { RequestedThemeVariant = ThemeVariant.Default; Styles.Add(new FluentTheme()); }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            desktop = lifetime; desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            model = new(host.Services.GetRequiredService<IDesktopSession>(), host.Services.GetRequiredService<IDesktopSettingsStore>(), action => Dispatcher.UIThread.Post(action));
            var control = new ControlWindow { DataContext = model };
            model.Archive = new HistoryViewModel(new HistoryInteraction(control));
            history = host.Services.GetRequiredService<DesktopHistory>();
            history.Changed += QueueHistoryChanged;
            model.SettingsApplied += ApplySettings;
            model.AppearanceChanged += ApplyTheme;
            var overlay = new SubtitleWindow { DataContext = model };
            desktop.MainWindow = control;
            control.Closing += OnClosing; overlay.Closing += OnClosing; desktop.ShutdownRequested += OnShutdownRequested;
            control.Opened += (_, _) => { initialization = InitializeAsync(control, overlay); };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>应用已确认的偏好并切换档案目录，运行草稿不会触发此入口。</summary><param name="settings">已应用配置。</param>
    private void ApplySettings(DesktopSettings settings) { ApplyTheme(settings); history!.Configure(settings); ApplyHistoryState(); }

    /// <summary>主窗口主题独立于字幕背景，跟随系统或明确用户选择。</summary><param name="settings">当前偏好。</param>
    private void ApplyTheme(DesktopSettings settings) => RequestedThemeVariant = settings.Theme switch
    { DesktopTheme.Light => ThemeVariant.Light, DesktopTheme.Dark => ThemeVariant.Dark, _ => ThemeVariant.Default };

    /// <summary>合并后台保存状态，最多排队一个UI通知。</summary>
    private void QueueHistoryChanged()
    {
        lock (historyNotifications)
        {
            if (historyNotificationPending || cleaned) return;
            historyNotificationPending = true;
        }
        Dispatcher.UIThread.Post(() =>
        {
            lock (historyNotifications) historyNotificationPending = false;
            if (!cleaned) ApplyHistoryState();
        });
    }

    /// <summary>更新历史库和持续失败提示，成功状态不干扰当前阅读位置。</summary>
    private void ApplyHistoryState()
    {
        if (history is null || model?.Archive is not { } archive) return;
        if (history.Store is { } store) archive.Attach(store, history.Location);
        var recorder = history.FailedRecorder;
        archive.SetSaveStatus(recorder, recorder?.Status);
        model.HistorySaveMessage = recorder?.Status.Error ?? history.MaintenanceError ?? "";
        model.HistorySaveFailed = model.HistorySaveMessage.Length != 0;
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
                verifier = new DesktopVerification(arguments, history);
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
            if (model is not null)
            {
                var archiveClosing = model.Archive?.CloseAsync() ?? Task.CompletedTask;
                try { await model.CloseAsync(); }
                finally { await archiveClosing.WaitAsync(TimeSpan.FromSeconds(2)); }
            }
        }
        catch (Exception error) { exit = 1; if (model is not null) model.Error = "退出失败：" + error.Message; }
        finally
        {
            bridge?.Dispose();
            if (history is not null)
            {
                history.Changed -= QueueHistoryChanged;
                try { await history.DisposeAsync(); } catch (Exception) { exit = 1; }
            }
            try { await host.StopAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { exit = 1; }
            cleaned = true; desktop?.TryShutdown(exit);
        }
    }
}
