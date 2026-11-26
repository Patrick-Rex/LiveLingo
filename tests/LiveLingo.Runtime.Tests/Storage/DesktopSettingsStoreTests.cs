using LiveLingo.Core.Desktop;
using LiveLingo.Runtime.Storage;

namespace LiveLingo.Runtime.Tests.Storage;

/// <summary>验证真实文件原子设置保存和损坏保护，不启动模型。</summary>
public sealed class DesktopSettingsStoreTests
{
    /// <summary>读取旧语言字段时保留其他设置，首次应用新选择后重启仍使用所选语言。</summary>
    /// <param name="previousLanguage">旧字段值，空引用表示旧文件缺少该字段。</param><returns>真实文件的迁移与保存检查。</returns>
    [Theory, InlineData("auto"), InlineData(null), InlineData("Japanese")]
    public async Task LegacyLanguageLoadsAsJapaneseAndNewChoicePersists(string? previousLanguage)
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            var token = TestContext.Current.CancellationToken;
            var store = new DesktopSettingsStore(path);
            Assert.Equal("Japanese", (await store.LoadAsync(token)).Settings.AsrLanguage);
            await store.SaveAsync(new() { FontSize = 36 }, token);
            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, token))!.AsObject();
            if (previousLanguage is null) json.Remove("AsrLanguage");
            else json["AsrLanguage"] = previousLanguage;
            var legacy = json.ToJsonString();
            await File.WriteAllTextAsync(path, legacy, token);
            var loaded = await store.LoadAsync(token);
            Assert.Null(loaded.Error);
            Assert.Equal("Japanese", loaded.Settings.AsrLanguage);
            Assert.Equal(36, loaded.Settings.FontSize);
            Assert.Equal(legacy, await File.ReadAllTextAsync(path, token));
            await store.SaveAsync(loaded.Settings with { AsrLanguage = "Korean" }, token);
            var restarted = await new DesktopSettingsStore(path).LoadAsync(token);
            Assert.Null(restarted.Error);
            Assert.Equal("Korean", restarted.Settings.AsrLanguage);
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(loaded.Settings with { AsrLanguage = "auto" }, token));
        }
        finally { directory.Delete(recursive: true); }
    }

    /// <summary>过大的配置或空 JSON 对象值保留原文件，并返回桌面可显示的错误。</summary>
    /// <param name="oversized">是否生成刚超过存储上限的配置。</param><returns>实际加载与原文件保护检查。</returns>
    [Theory, InlineData(false), InlineData(true)]
    public async Task OversizedOrNullDocumentReportsLoadError(bool oversized)
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            var token = TestContext.Current.CancellationToken;
            var content = oversized ? new string(' ', 256 * 1024) + "{}" : "null";
            await File.WriteAllTextAsync(path, content, token);
            var result = await new DesktopSettingsStore(path).LoadAsync(token);
            Assert.NotNull(result.Error);
            Assert.False(result.Settings.GlossaryLibrary.Enabled);
            Assert.Equal(content, await File.ReadAllTextAsync(path, token));
        }
        finally { directory.Delete(recursive: true); }
    }

    /// <summary>旧设置默认关闭词库，选择与自定义词表可往返；无效草稿保留上次保存的配置。</summary>
    /// <returns>使用独立临时设置文件的验证任务。</returns>
    [Fact]
    public async Task GlossarySelectionSurvivesDisablingAndRestart()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            var store = new DesktopSettingsStore(path);
            var token = TestContext.Current.CancellationToken;
            await store.SaveAsync(new(), token);
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, token))!.AsObject();
            Assert.True(legacy.Remove(nameof(DesktopSettings.GlossaryLibrary)));
            await File.WriteAllTextAsync(path, legacy.ToJsonString(), token);
            var previous = await store.LoadAsync(token);
            Assert.Null(previous.Error);
            Assert.False(previous.Settings.GlossaryLibrary.Enabled);
            Assert.Empty(previous.Settings.TranslationGlossaryText);

            var selected = previous.Settings.GlossaryLibrary with
            {
                Profiles = previous.Settings.GlossaryLibrary.Profiles.Add(new("演出", "アンコール=安可", LocalOnly: true)),
                SelectedIndex = 1, Enabled = true
            };
            var enabled = previous.Settings with { GlossaryLibrary = selected };
            await store.SaveAsync(enabled, token);
            var restored = (await new DesktopSettingsStore(path).LoadAsync(token)).Settings;
            Assert.Equal(enabled, restored);
            Assert.Equal("アンコール=安可", restored.TranslationGlossaryText);
            Assert.Empty((restored with { TranslationBackend = ModelBackend.OpenAiCompatible }).TranslationGlossaryText);

            var oldProfile = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, token))!;
            Assert.True(oldProfile["GlossaryLibrary"]!["Profiles"]![1]!.AsObject().Remove("LocalOnly"));
            await File.WriteAllTextAsync(path, oldProfile.ToJsonString(), token);
            var oldSettings = await store.LoadAsync(token);
            Assert.Null(oldSettings.Error);
            Assert.Equal("アンコール=安可", (oldSettings.Settings with { TranslationBackend = ModelBackend.OpenAiCompatible }).TranslationGlossaryText);

            var disabled = restored with { GlossaryLibrary = selected with { Enabled = false } };
            await store.SaveAsync(disabled, token);
            var duplicate = selected with { Profiles = selected.Profiles.Add(new("演出", "歌=歌曲")) };
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(disabled with { GlossaryLibrary = duplicate }, token));
            var result = await store.LoadAsync(token);
            Assert.Null(result.Error);
            Assert.Equal(disabled, result.Settings);
            Assert.Empty(result.Settings.TranslationGlossaryText);
            Assert.Equal("演出", result.Settings.GlossaryLibrary.Profiles[result.Settings.GlossaryLibrary.SelectedIndex].Name);
        }
        finally { directory.Delete(recursive: true); }
    }

    /// <summary>读取旧位置和自定义 cu130 解释器，首次保存写入新位置并优先使用新文件。</summary>
    /// <returns>兼容迁移与新安装默认值的验证任务。</returns>
    [Fact]
    public async Task LegacyPathAndCustomInterpreterSurviveUpgrade()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var fresh = Path.Combine(root, "user data", "settings.json");
        var legacy = Path.Combine(root, "legacy.json");
        var store = new DesktopSettingsStore(fresh, legacyPath: legacy);
        var token = TestContext.Current.CancellationToken;
        try
        {
            Assert.Equal("win-x64-cpu", (await store.LoadAsync(token)).Settings.RuntimeProfile);
            var custom = Path.Combine(root, "my environment", "Scripts", "python.exe");
            await new DesktopSettingsStore(legacy).SaveAsync(new() { PythonPath = custom, FontSize = 37 }, token);
            var oldSettings = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(legacy, token))!.AsObject();
            Assert.True(oldSettings.Remove("RuntimeProfile")); var oldContent = oldSettings.ToJsonString();
            await File.WriteAllTextAsync(legacy, oldContent, token);
            var loaded = await store.LoadAsync(token);
            Assert.Null(loaded.Error); Assert.Equal("win-x64-cu130", loaded.Settings.RuntimeProfile);
            Assert.Equal(custom, loaded.Settings.PythonPath); Assert.Equal(37, loaded.Settings.FontSize);
            await store.SaveAsync(loaded.Settings with { FontSize = 40 }, token);
            Assert.Equal(40, (await store.LoadAsync(token)).Settings.FontSize);
            Assert.Equal(oldContent, await File.ReadAllTextAsync(legacy, token));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>渠道默认值仅用于首次启动，不能重写既有本地配置或损坏文件。</summary><returns>真实设置文件兼容验证任务。</returns>
    [Fact]
    public async Task InstallationDefaultsApplyOnlyWhenSettingsFileIsMissing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "channel-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        var initial = new DesktopSettings { AsrBackend = ModelBackend.OpenAiCompatible, TranslationBackend = ModelBackend.OpenAiCompatible };
        var store = new DesktopSettingsStore(path, initial);
        try
        {
            var first = await store.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Null(first.Error); Assert.Equal(initial, first.Settings); Assert.False(File.Exists(path));
            Assert.Equal(ModelBackend.Local, (await new DesktopSettingsStore(path).LoadAsync(TestContext.Current.CancellationToken)).Settings.AsrBackend);
            await store.SaveAsync(new(), TestContext.Current.CancellationToken);
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!.AsObject();
            Assert.True(legacy.Remove("AsrBackend")); Assert.True(legacy.Remove("TranslationBackend"));
            await File.WriteAllTextAsync(path, legacy.ToJsonString(), TestContext.Current.CancellationToken);
            var existing = await store.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Null(existing.Error);
            Assert.Equal(ModelBackend.Local, existing.Settings.AsrBackend); Assert.Equal(ModelBackend.Local, existing.Settings.TranslationBackend);
            await File.WriteAllTextAsync(path, "{broken", TestContext.Current.CancellationToken);
            var corrupt = await store.LoadAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(corrupt.Error); Assert.Equal(ModelBackend.Local, corrupt.Settings.AsrBackend);
            Assert.Equal("{broken", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    /// <summary>旧文件默认双本地，整体 API 配置及明文 Key 可往返，非法来源保留原文件。</summary><returns>真实文件兼容检查任务。</returns>
    [Fact]
    public async Task ModelSourcesAndApiSettingsRoundTripWithoutLocalPaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), "model-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json"); var store = new DesktopSettingsStore(path);
        try
        {
            await store.SaveAsync(new(), TestContext.Current.CancellationToken);
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!.AsObject();
            foreach (var key in new[] { "AsrBackend", "TranslationBackend", "AsrApi", "TranslationApi" }) Assert.True(legacy.Remove(key));
            await File.WriteAllTextAsync(path, legacy.ToJsonString(), TestContext.Current.CancellationToken);
            var old = await store.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Null(old.Error); Assert.Equal(ModelBackend.Local, old.Settings.AsrBackend); Assert.Equal(ModelBackend.Local, old.Settings.TranslationBackend);
            var settings = new DesktopSettings
            {
                AsrBackend = ModelBackend.OpenAiCompatible, TranslationBackend = ModelBackend.OpenAiCompatible,
                AsrApi = new() { Model = "speech", ApiKey = "test-key-a" }, TranslationApi = new() { Model = "text", ApiKey = "test-key-b" },
                PythonPath = "", ModelPath = "", OllamaPath = "", OllamaModelsPath = ""
            };
            await store.SaveAsync(settings, TestContext.Current.CancellationToken);
            Assert.Equal(settings, (await store.LoadAsync(TestContext.Current.CancellationToken)).Settings);
            var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains("test-key-a", text); Assert.Contains("test-key-b", text);
            legacy["AsrBackend"] = 99;
            var invalid = legacy.ToJsonString(); await File.WriteAllTextAsync(path, invalid, TestContext.Current.CancellationToken);
            Assert.NotNull((await store.LoadAsync(TestContext.Current.CancellationToken)).Error);
            Assert.Equal(invalid, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

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
