using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
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
          "asr_language": "auto"
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
}
