using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using System.Text.Json.Nodes;
using LiveLingo.App.Services;
using LiveLingo.App.ViewModels;
using LiveLingo.App.Views;
using LiveLingo.Core.Desktop;

namespace LiveLingo.App.Tests;

/// <summary>验证整体 JSON 和表单使用同一草稿，只有所选来源参与校验与保存。</summary>
public sealed class ModelConfigurationTests
{
    private const string ApiJson = """
        {
          "asr": { "provider": "openai", "base_url": "https://asr.example/v1", "model": "speech-model", "api_key": "speech-key" },
          "translation": { "provider": "openai", "base_url": "https://translate.example/v1", "model": "text-model", "api_key": "text-key" },
          "asr_language": "Japanese"
        }
        """;

    /// <summary>四种组合只验证所选依赖，未选 API 可以没有地址与模型。</summary>
    /// <param name="asr">识别来源。</param><param name="translation">翻译来源。</param>
    [Theory]
    [InlineData(ModelBackend.Local, ModelBackend.Local)]
    [InlineData(ModelBackend.Local, ModelBackend.OpenAiCompatible)]
    [InlineData(ModelBackend.OpenAiCompatible, ModelBackend.Local)]
    [InlineData(ModelBackend.OpenAiCompatible, ModelBackend.OpenAiCompatible)]
    public void EachStageValidatesOnlySelectedSource(ModelBackend asr, ModelBackend translation)
    {
        var settings = ModelConfigurationJson.Read(ApiJson, new()) with { AsrBackend = asr, TranslationBackend = translation };
        settings = settings with
        {
            PythonPath = asr == ModelBackend.Local ? settings.PythonPath : "",
            ModelPath = asr == ModelBackend.Local ? settings.ModelPath : "",
            OllamaPath = translation == ModelBackend.Local ? settings.OllamaPath : "",
            OllamaModelsPath = translation == ModelBackend.Local ? settings.OllamaModelsPath : "",
            AsrApi = asr == ModelBackend.Local ? new() { BaseUrl = "" } : settings.AsrApi,
            TranslationApi = translation == ModelBackend.Local ? new() { BaseUrl = "" } : settings.TranslationApi
        };
        settings.Validate();
        Assert.Equal(settings, ModelConfigurationJson.Read(ModelConfigurationJson.Write(settings), settings));
    }

    /// <summary>语言缺省、自动检测、空值或未知值都保留现有配置，提示明确的可选语言。</summary>
    /// <param name="language">模型文件中的无效语言。</param><param name="omit">是否省略该字段。</param>
    [Theory, InlineData("auto", false), InlineData("French", false), InlineData(null, false), InlineData(null, true)]
    public void ImportedConfigurationRequiresAnExplicitSupportedLanguage(string? language, bool omit)
    {
        var config = JsonNode.Parse(ApiJson)!.AsObject();
        if (omit) config.Remove("asr_language");
        else config["asr_language"] = language;
        var error = Assert.Throws<ArgumentException>(() => ModelConfigurationJson.Read(config.ToJsonString(), new()));
        Assert.Contains("asr_language", error.Message);
    }

    /// <summary>四个中文选项对应同一模型 JSON 草稿，实时页只在用户应用后切换原文语言。</summary>
    /// <returns>真实控件绑定与保存验证任务。</returns>
    [AvaloniaFact]
    public async Task SourceLanguageSelectionIsExplicitAcrossFormJsonAndLiveSummary()
    {
        var vm = new DesktopViewModel(new FakeSession(), new FakeStore(), action => action());
        await vm.InitializeAsync();
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 960, Height = 760 };
        try
        {
            window.Show(); vm.SelectedSettingsIndex = 2; window.UpdateLayout();
            var selector = view.FindControl<ComboBox>("RecognitionLanguageSelector")!;
            Assert.Equal(new[] { "中文", "英语", "日语", "韩语" }, selector.Items.Cast<string>());
            Assert.Equal("日语", selector.SelectedItem);
            var names = new[] { "Chinese", "English", "Japanese", "Korean" };
            for (var index = 0; index < names.Length; index++)
            {
                selector.SelectedIndex = index;
                vm.ModelInputMode = 1;
                Assert.Equal(names[index], JsonNode.Parse(vm.ModelJson)!["asr_language"]!.GetValue<string>());
                vm.ModelInputMode = 0;
            }
            Assert.Contains("原文：日语", vm.RuntimeSummary); Assert.False(vm.CanToggle);
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Equal("Korean", vm.CurrentSettings.AsrLanguage); Assert.Contains("原文：韩语", vm.RuntimeSummary);
            vm.ImportModelConfiguration(ApiJson.Replace("Japanese", "English"));
            vm.ModelInputMode = 0; window.UpdateLayout();
            Assert.Equal("英语", selector.SelectedItem);
            vm.RevertSettingsCommand.Execute(null); window.UpdateLayout();
            Assert.Equal("韩语", selector.SelectedItem); Assert.False(vm.HasPendingSettings);
        }
        finally { window.Close(); await vm.CloseAsync(); }
    }

    /// <summary>一次导入同时更新两个阶段；自动保存排除草稿，一次应用才提交，撤销恢复两者。</summary><returns>保存与草稿验证任务。</returns>
    [Fact]
    public async Task WholeImportAndFormShareOneApplyBoundary()
    {
        var store = new FakeStore(); var vm = new DesktopViewModel(new FakeSession(), store, action => action());
        await vm.InitializeAsync();
        try
        {
            vm.ImportModelConfiguration(ApiJson);
            Assert.True(vm.IsApiAsr && vm.IsApiTranslation && vm.IsModelJson && vm.HasPendingSettings);
            Assert.Equal("speech-key", vm.AsrApiKey); Assert.Equal("text-model", vm.TranslationApiModel);
            vm.ThemeIndex = 2;
            var auto = await store.SaveCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(ModelBackend.Local, auto.AsrBackend); Assert.Equal("", auto.AsrApi.ApiKey);
            vm.ModelInputMode = 0; Assert.True(vm.IsModelForm);
            vm.PythonPath = ""; vm.ModelPath = ""; vm.OllamaPath = ""; vm.OllamaModelsPath = "";
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.False(vm.HasPendingSettings); Assert.Equal(ModelBackend.OpenAiCompatible, store.Writes.Last().AsrBackend);
            Assert.Equal("text-key", store.Writes.Last().TranslationApi.ApiKey);
            vm.AsrBackendIndex = 0; vm.TranslationBackendIndex = 0; vm.RevertSettingsCommand.Execute(null);
            Assert.True(vm.IsApiAsr && vm.IsApiTranslation); Assert.False(vm.HasPendingSettings);
        }
        finally { await vm.CloseAsync(); }
    }

    /// <summary>无效的第二阶段不能部分导入第一阶段，也不能被外观自动保存或应用。</summary><returns>原子解析与损坏草稿验证任务。</returns>
    [Fact]
    public async Task InvalidJsonPreservesBothStagesAndBlocksApply()
    {
        var store = new FakeStore(); var vm = new DesktopViewModel(new FakeSession(), store, action => action());
        await vm.InitializeAsync();
        try
        {
            vm.ImportModelConfiguration(ApiJson.Replace("text-model", ""));
            Assert.True(vm.IsLocalAsr && vm.IsLocalTranslation); Assert.NotEmpty(vm.ModelJsonError); Assert.True(vm.HasPendingSettings);
            await vm.SaveCommand.ExecuteAsync(null); Assert.Empty(store.Writes);
            vm.ModelInputMode = 0; Assert.True(vm.IsModelJson);
            vm.RevertSettingsCommand.Execute(null); Assert.Empty(vm.ModelJsonError); Assert.False(vm.HasPendingSettings);
            Assert.Throws<ArgumentException>(() => ModelConfigurationJson.Read("{\"asr\":{},\"asr\":{}}", new()));
            Assert.Throws<ArgumentException>(() => ModelConfigurationJson.Read("{}", new()));
        }
        finally { await vm.CloseAsync(); }
    }

    /// <summary>词表草稿在模型导入与外观保存后仍保留，无效词条不能部分应用，撤销恢复最近保存的词表。</summary>
    /// <returns>配置保存和会话启动验证任务。</returns>
    [Fact]
    public async Task GlossaryDraftRequiresValidExplicitSave()
    {
        var session = new FakeSession();
        var store = new FakeStore();
        var vm = new DesktopViewModel(session, store, action => action());
        await vm.InitializeAsync();
        try
        {
            Assert.False(vm.GlossaryEnabled);
            var original = Assert.Single(vm.GlossaryProfiles);
            vm.AddGlossaryCommand.Execute(null);
            var custom = vm.SelectedGlossary!;
            custom.Name = "直播台词";
            custom.Text = "神ゲー=神作\n无分隔符";
            vm.SelectedGlossary = original;
            Assert.Contains("直播台词", vm.GlossaryError);
            vm.ImportModelConfiguration(ApiJson);
            Assert.NotEmpty(vm.GlossaryError); Assert.Empty(vm.ModelJsonError);
            Assert.True(vm.HasPendingSettings); Assert.False(vm.CanToggle);
            await vm.ToggleCommand.ExecuteAsync(null);
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Equal(0, session.Starts); Assert.Empty(store.Writes);
            vm.ThemeIndex = 2;
            var auto = await store.SaveCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(auto.GlossaryLibrary.Enabled); Assert.Single(auto.GlossaryLibrary.Profiles);
            Assert.Equal(ModelBackend.Local, auto.TranslationBackend);

            vm.SelectedGlossary = custom;
            vm.GlossaryEnabled = true;
            custom.LocalOnly = true;
            custom.Text = "神ゲー=神作";
            Assert.Empty(vm.GlossaryError);
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Empty(store.Writes.Last().TranslationGlossaryText);
            Assert.Equal(ModelBackend.OpenAiCompatible, vm.CurrentSettings.TranslationBackend);
            custom.Text = "无分隔符";
            vm.RemoveGlossaryCommand.Execute(null);
            vm.RevertSettingsCommand.Execute(null);
            Assert.Equal("直播台词", vm.SelectedGlossary!.Name); Assert.Equal("神ゲー=神作", vm.SelectedGlossary.Text);
            Assert.True(vm.SelectedGlossary.LocalOnly);
            Assert.Empty(vm.GlossaryError); Assert.False(vm.HasPendingSettings); Assert.True(vm.CanToggle);
            vm.GlossaryEnabled = false;
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Empty(vm.CurrentSettings.TranslationGlossaryText);
            var library = vm.CurrentSettings.GlossaryLibrary;
            Assert.Equal(1, library.SelectedIndex); Assert.Equal("神ゲー=神作", library.Profiles[1].Text);
        }
        finally { await vm.CloseAsync(); }
    }

    /// <summary>在最小控制窗内滚动到词库正文，切换命名表及模型编辑方式，验证默认关闭和运行中编辑限制。</summary>
    /// <returns>设置视图绑定验证任务。</returns>
    [AvaloniaFact]
    public async Task NamedGlossariesRemainOptionalAndSeparateFromModelEditor()
    {
        var vm = new DesktopViewModel(new FakeSession(), new FakeStore(), action => action());
        await vm.InitializeAsync();
        var window = new ControlWindow { DataContext = vm };
        window.Width = window.MinWidth; window.Height = window.MinHeight;
        var view = window.FindControl<SettingsView>("SettingsPanel")!;
        try
        {
            vm.OpenRuntimeSettingsCommand.Execute(null);
            window.Show(); window.UpdateLayout();
            var glossary = view.GetVisualDescendants().OfType<Expander>().Single(x => Equals(x.Header, "翻译词库"));
            glossary.IsExpanded = true; window.UpdateLayout();
            var editor = view.FindControl<TextBox>("GlossaryEditor")!;
            var selector = view.FindControl<ComboBox>("GlossarySelector")!;
            var localOnly = view.FindControl<CheckBox>("LocalOnlyGlossary")!;
            var add = glossary.GetVisualDescendants().OfType<Button>().Single(x => x.Command == vm.AddGlossaryCommand);
            var scroll = selector.GetVisualAncestors().OfType<ScrollViewer>().First();
            var pointer = selector.TranslatePoint(new Point(12, selector.Bounds.Height / 2), window)!.Value;
            window.MouseWheel(pointer, new Vector(0, -1)); window.UpdateLayout();
            Assert.True(scroll.Offset.Y > 0);
            Assert.Equal(0, selector.SelectedIndex);
            editor.BringIntoView(); window.UpdateLayout();
            var editorTop = editor.TranslatePoint(default, scroll)!.Value.Y;
            Assert.InRange(editorTop, -1, scroll.Bounds.Height - editor.Bounds.Height + 1);
            Assert.True(editor.AcceptsReturn); Assert.True(editor.IsEffectivelyEnabled); Assert.False(vm.GlossaryEnabled);
            Assert.Contains("ナイスプレイ！=打得漂亮！", editor.Text); Assert.False(vm.HasPendingSettings);
            add.Command!.Execute(null); window.UpdateLayout();
            editor.Text = "自分の台詞=自己的台词";
            localOnly.IsChecked = true;
            selector.SelectedIndex = 0; window.UpdateLayout(); Assert.Contains("神ゲー=神作", editor.Text);
            Assert.False(localOnly.IsChecked);
            selector.SelectedIndex = 1;
            vm.ModelInputMode = 1; window.UpdateLayout();
            Assert.Equal("自分の台詞=自己的台词", editor.Text);
            Assert.True(localOnly.IsChecked);
            Assert.True(editor.IsEffectivelyVisible); Assert.DoesNotContain("台詞", vm.ModelJson);
            await vm.SaveCommand.ExecuteAsync(null);
            await vm.ToggleCommand.ExecuteAsync(null); window.UpdateLayout();
            Assert.True(vm.Active); Assert.False(editor.IsEffectivelyEnabled);
        }
        finally { window.Close(); await vm.CloseAsync(); }
    }

    /// <summary>不接受嵌入凭据、查询或远程明文地址，本机兼容服务可使用 HTTP。</summary>
    /// <param name="url">无效地址。</param>
    [Theory, InlineData("http://remote.example/v1"), InlineData("https://user:key@example.com/v1"), InlineData("https://example.com/v1?key=value"), InlineData("https://example.com/v1#x")]
    public void InvalidApiAddressesAreRejected(string url)
    {
        Assert.Throws<ArgumentException>(() => new OpenAiApiSettings { BaseUrl = url, Model = "model" }.Validate());
        new OpenAiApiSettings { BaseUrl = "http://127.0.0.1:8080/v1", Model = "model" }.Validate();
    }

    /// <summary>全 API 表单隐藏本地模型与路径，整体 JSON 为唯一编辑区。</summary><returns>Headless 绑定与可见性检查任务。</returns>
    [AvaloniaFact]
    public async Task ApiFormHidesLocalPathsAndShowsSingleJsonEditor()
    {
        var vm = new DesktopViewModel(new FakeSession(), new FakeStore(), action => action()); await vm.InitializeAsync();
        var view = new SettingsView { DataContext = vm }; var window = new Window { Content = view, Width = 960, Height = 760 };
        try
        {
            window.Show(); vm.SelectedSettingsIndex = 2; vm.ImportModelConfiguration(ApiJson); vm.ModelInputMode = 0; window.UpdateLayout();
            view.GetVisualDescendants().OfType<Expander>().Single(x => Equals(x.Header, "高级路径配置")).IsExpanded = true;
            window.UpdateLayout();
            var controls = view.GetVisualDescendants().OfType<TextBox>().ToArray();
            Assert.True(controls.Single(x => AutomationProperties.GetName(x) == "识别 API 地址").IsEffectivelyVisible);
            Assert.True(controls.Single(x => AutomationProperties.GetName(x) == "翻译 API 地址").IsEffectivelyVisible);
            Assert.False(controls.Single(x => AutomationProperties.GetName(x) == "Python 路径").IsEffectivelyVisible);
            vm.ModelInputMode = 1; window.UpdateLayout();
            Assert.Single(controls.Where(x => AutomationProperties.GetName(x) == "整体模型 JSON 配置" && x.IsEffectivelyVisible));
            Assert.False(controls.Single(x => AutomationProperties.GetName(x) == "识别 API 地址").IsEffectivelyVisible);
        }
        finally { window.Close(); await vm.CloseAsync(); }
    }

    /// <summary>收起后的来源选择器将滚轮交给页面，不把页面滚动写成模型来源草稿。</summary>
    /// <param name="sourceName">被验证的来源选择器无障碍名称。</param>
    /// <param name="initialIndex">滚动前的来源索引。</param>
    /// <returns>来源保持与页面滚动验证任务。</returns>
    [AvaloniaTheory]
    [InlineData("语音识别来源", 0)]
    [InlineData("语音识别来源", 1)]
    [InlineData("翻译来源", 0)]
    [InlineData("翻译来源", 1)]
    public async Task ClosedSourceDropdownScrollsPageWithoutChangingSource(string sourceName, int initialIndex)
    {
        var store = new FakeStore();
        var vm = new DesktopViewModel(new FakeSession(), store, action => action());
        await vm.InitializeAsync();
        var window = new ControlWindow { DataContext = vm };
        try
        {
            vm.AsrBackendIndex = initialIndex; vm.TranslationBackendIndex = initialIndex;
            vm.SelectedPageIndex = 2; vm.SelectedSettingsIndex = 2;
            window.Show(); window.UpdateLayout();
            var selector = window.GetVisualDescendants().OfType<ComboBox>()
                .Single(x => AutomationProperties.GetName(x) == sourceName);
            selector.BringIntoView(); window.UpdateLayout(); selector.Focus();
            var scroll = selector.GetVisualAncestors().OfType<ScrollViewer>().First();
            var originalOffset = scroll.Offset.Y;
            var originalPending = vm.HasPendingSettings;
            var point = selector.TranslatePoint(new Point(selector.Bounds.Width / 2, selector.Bounds.Height / 2), window)!.Value;

            window.MouseWheel(point, new Vector(0, initialIndex == 0 ? -0.1 : 0.1)); window.UpdateLayout();

            Assert.Equal(initialIndex, vm.AsrBackendIndex);
            Assert.Equal(initialIndex, vm.TranslationBackendIndex);
            Assert.Equal(originalPending, vm.HasPendingSettings);
            Assert.NotEqual(originalOffset, scroll.Offset.Y);
            Assert.Empty(store.Writes);
        }
        finally { window.Close(); await vm.CloseAsync(); }
    }

    /// <summary>来源下拉仅展开、悬停或取消不改变草稿，点击或键盘选择仍可写回。</summary>
    /// <param name="sourceName">被验证的来源选择器无障碍名称。</param>
    /// <param name="initialIndex">打开前的来源索引。</param>
    /// <returns>真实控件输入和草稿边界的验证任务。</returns>
    [AvaloniaTheory]
    [InlineData("语音识别来源", 0)]
    [InlineData("语音识别来源", 1)]
    [InlineData("翻译来源", 0)]
    [InlineData("翻译来源", 1)]
    public async Task SourceDropdownChangesOnlyAfterExplicitSelection(string sourceName, int initialIndex)
    {
        var store = new FakeStore();
        var vm = new DesktopViewModel(new FakeSession(), store, action => action());
        await vm.InitializeAsync();
        var window = new ControlWindow { DataContext = vm };
        try
        {
            vm.AsrBackendIndex = initialIndex; vm.TranslationBackendIndex = initialIndex;
            vm.SelectedPageIndex = 2; vm.SelectedSettingsIndex = 2;
            window.Show(); window.UpdateLayout();
            var selector = window.GetVisualDescendants().OfType<ComboBox>()
                .Single(x => AutomationProperties.GetName(x) == sourceName);
            selector.BringIntoView(); window.UpdateLayout();
            var point = selector.TranslatePoint(new Point(selector.Bounds.Width - 16, selector.Bounds.Height / 2), window)!.Value;
            var originalJson = vm.ModelJson;

            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); window.UpdateLayout();
            Assert.True(selector.IsDropDownOpen);
            Assert.Equal(initialIndex, selector.SelectedIndex);
            var other = selector.ContainerFromIndex(1 - initialIndex)!;
            var popup = TopLevel.GetTopLevel(other)!;
            var otherPoint = other.TranslatePoint(new Point(other.Bounds.Width / 2, other.Bounds.Height / 2), popup)!.Value;
            popup.MouseMove(otherPoint);
            popup.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None); window.UpdateLayout();
            Assert.False(selector.IsDropDownOpen);
            Assert.Equal(initialIndex, vm.AsrBackendIndex);
            Assert.Equal(initialIndex, vm.TranslationBackendIndex);
            Assert.Equal(originalJson, vm.ModelJson);
            Assert.Empty(store.Writes);

            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); window.UpdateLayout();
            Assert.True(selector.IsDropDownOpen);
            other = selector.ContainerFromIndex(1 - initialIndex)!;
            popup = TopLevel.GetTopLevel(other)!;
            otherPoint = other.TranslatePoint(new Point(other.Bounds.Width / 2, other.Bounds.Height / 2), popup)!.Value;
            popup.MouseDown(otherPoint, MouseButton.Left); popup.MouseUp(otherPoint, MouseButton.Left); window.UpdateLayout();
            Assert.False(selector.IsDropDownOpen);
            Assert.Equal(1 - initialIndex, sourceName == "语音识别来源" ? vm.AsrBackendIndex : vm.TranslationBackendIndex);
            Assert.Equal(initialIndex, sourceName == "语音识别来源" ? vm.TranslationBackendIndex : vm.AsrBackendIndex);
            selector.Focus();
            var key = initialIndex == 0 ? PhysicalKey.ArrowUp : PhysicalKey.ArrowDown;
            window.KeyPressQwerty(key, RawInputModifiers.None); window.KeyReleaseQwerty(key, RawInputModifiers.None);
            Assert.Equal(initialIndex, vm.AsrBackendIndex);
            Assert.Equal(initialIndex, vm.TranslationBackendIndex);
            Assert.Empty(store.Writes);
        }
        finally { window.Close(); await vm.CloseAsync(); }
    }
}
