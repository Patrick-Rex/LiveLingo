using Avalonia;
using LiveLingo.Core.Desktop;
using LiveLingo.Runtime.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveLingo.Desktop.Windows;

/// <summary>Windows 唯一组合根；发布目录提供固定工作进程代码。</summary>
internal static class Program
{
    /// <summary>启动 UI 消息循环，返回后释放宿主，不以进程退出事件代替异步清理。</summary><param name="args">显式运行参数。</param><returns>桌面退出码。</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        var settingsPath = @"D:\Soft Ware\LiveLingo\data\settings.json";
        var settingsIndex = Array.IndexOf(args, "--settings");
        if (settingsIndex >= 0)
        {
            if (settingsIndex + 1 >= args.Length || Array.LastIndexOf(args, "--settings") != settingsIndex) throw new ArgumentException("--settings 需要唯一的非 C 盘文件路径。");
            settingsPath = args[settingsIndex + 1];
            args = args.Take(settingsIndex).Concat(args.Skip(settingsIndex + 2)).ToArray();
        }
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory, Args = [] });
        builder.Logging.ClearProviders();
        var verifyIndex = Array.IndexOf(args, "--verify");
        string? verificationData = null;
        if (verifyIndex >= 0)
        {
            if (verifyIndex + 1 >= args.Length || !Path.IsPathFullyQualified(args[verifyIndex + 1]) ||
                args[verifyIndex + 1].StartsWith("C:", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("验证需要非 C 盘的绝对路径。");
            verificationData = args[verifyIndex + 1] + ".data";
        }
        var history = new DesktopHistory(verificationData);
        builder.Services.AddSingleton(history);
        builder.Services.AddSingleton(new DesktopSession(AppContext.BaseDirectory, history));
        builder.Services.AddSingleton<IDesktopSession>(services => services.GetRequiredService<DesktopSession>());
        builder.Services.AddSingleton<IDesktopSettingsStore>(new DesktopSettingsStore(settingsPath));
        using var host = builder.Build();
        return AppBuilder.Configure(() => new DesktopApplication(host, args)).UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}
