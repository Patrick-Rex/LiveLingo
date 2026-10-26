using LiveLingo.Core.Desktop;
using LiveLingo.Runtime.Storage;

namespace LiveLingo.Runtime.Tests.Storage;

/// <summary>验证真实文件原子设置保存和损坏保护，不启动模型。</summary>
public sealed class DesktopSettingsStoreTests
{
    /// <summary>旧设置缺省系统主题和三十天保留，新增字段可往返且非法值保留原文件。</summary><returns>新增配置兼容验证任务。</returns>
    [Fact]
    public async Task ThemeAndRetentionPreserveLegacyDefaultsAndRejectUnknownValues()
    {
        var directory = Path.Combine(Path.GetTempPath(), "desktop-settings-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json"); var store = new DesktopSettingsStore(path);
        try
        {
            await store.SaveAsync(new(), TestContext.Current.CancellationToken);
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!.AsObject();
            Assert.True(legacy.Remove("Theme")); Assert.True(legacy.Remove("HistoryRetentionDays"));
            await File.WriteAllTextAsync(path, legacy.ToJsonString(), TestContext.Current.CancellationToken);
            var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Null(loaded.Error); Assert.Equal(DesktopTheme.System, loaded.Settings.Theme); Assert.Equal(30, loaded.Settings.HistoryRetentionDays);
            foreach (var theme in Enum.GetValues<DesktopTheme>())
            {
                foreach (var days in new[] { 0, 7, 30, 90 })
                {
                    var settings = new DesktopSettings { Theme = theme, HistoryRetentionDays = days };
                    await store.SaveAsync(settings, TestContext.Current.CancellationToken);
                    Assert.Equal(settings, (await store.LoadAsync(TestContext.Current.CancellationToken)).Settings);
                }
            }
            foreach (var field in new[] { "Theme", "HistoryRetentionDays" })
            {
                var invalid = legacy.DeepClone().AsObject(); invalid[field] = 99;
                var content = invalid.ToJsonString();
                await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
                Assert.NotNull((await store.LoadAsync(TestContext.Current.CancellationToken)).Error);
                Assert.Equal(content, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>旧配置缺省双语，新模式可往返保存，非法模式保留原文件并报错。</summary><returns>设置兼容验证任务。</returns>
    [Fact]
    public async Task DisplayModeSupportsExistingSettingsAndRejectsUnknownValues()
    {
        var directory = Path.Combine(Path.GetTempPath(), "desktop-settings-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json"); var store = new DesktopSettingsStore(path);
        try
        {
            await store.SaveAsync(new(), TestContext.Current.CancellationToken);
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!.AsObject();
            Assert.True(legacy.Remove("DisplayMode"));
            await File.WriteAllTextAsync(path, legacy.ToJsonString(), TestContext.Current.CancellationToken);
            var old = await store.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Null(old.Error); Assert.Equal(SubtitleDisplayMode.Bilingual, old.Settings.DisplayMode);
            foreach (var mode in Enum.GetValues<SubtitleDisplayMode>())
            {
                await store.SaveAsync(new() { DisplayMode = mode }, TestContext.Current.CancellationToken);
                Assert.Equal(mode, (await store.LoadAsync(TestContext.Current.CancellationToken)).Settings.DisplayMode);
            }
            legacy["DisplayMode"] = 99;
            var invalid = legacy.ToJsonString();
            await File.WriteAllTextAsync(path, invalid, TestContext.Current.CancellationToken);
            Assert.NotNull((await store.LoadAsync(TestContext.Current.CancellationToken)).Error);
            Assert.Equal(invalid, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>缺失使用默认值；损坏原文件保留，明确保存才替换。</summary><returns>实际文件验证任务。</returns>
    [Fact]
    public async Task MissingAndCorruptSettingsRemainRecoverable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "desktop-settings-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json"); var store = new DesktopSettingsStore(path);
        try
        {
            Assert.Null((await store.LoadAsync(TestContext.Current.CancellationToken)).Error); Assert.False(File.Exists(path));
            await File.WriteAllTextAsync(path, "{broken", TestContext.Current.CancellationToken); Assert.NotNull((await store.LoadAsync(TestContext.Current.CancellationToken)).Error); Assert.Equal("{broken", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            var next = new DesktopSettings { FontSize = 32, OverlayX = -1200 }; await store.SaveAsync(next, TestContext.Current.CancellationToken);
            Assert.Equal(next, (await store.LoadAsync(TestContext.Current.CancellationToken)).Settings); Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>校验失败和提交前取消都不能覆盖现有设置。</summary><returns>实际文件验证任务。</returns>
    [Fact]
    public async Task InvalidOrCancelledSavePreservesPreviousFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "desktop-settings-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json"); var store = new DesktopSettingsStore(path);
        try
        {
            await store.SaveAsync(new(), TestContext.Current.CancellationToken); var previous = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new() { FontSize = double.PositiveInfinity }, TestContext.Current.CancellationToken));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new() { FontSize = 40 }, cancelled.Token));
            Assert.Equal(previous, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)); Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
