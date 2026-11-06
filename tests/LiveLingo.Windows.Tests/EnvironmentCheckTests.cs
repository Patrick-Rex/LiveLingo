using LiveLingo.Core.Desktop;
using LiveLingo.Desktop.Windows;

namespace LiveLingo.Windows.Tests;

/// <summary>验证环境初检不把空模型目录当作可用依赖。</summary>
public sealed class EnvironmentCheckTests
{
    /// <summary>四种组合只列出所选本地依赖，API 的文件初检不要求模型或程序路径。</summary>
    /// <param name="asr">识别来源。</param><param name="translation">翻译来源。</param>
    [Theory]
    [InlineData(ModelBackend.Local, ModelBackend.Local)]
    [InlineData(ModelBackend.Local, ModelBackend.OpenAiCompatible)]
    [InlineData(ModelBackend.OpenAiCompatible, ModelBackend.Local)]
    [InlineData(ModelBackend.OpenAiCompatible, ModelBackend.OpenAiCompatible)]
    public void DependencyChecksFollowBothSources(ModelBackend asr, ModelBackend translation)
    {
        var api = new OpenAiApiSettings { Model = "test-model" };
        var settings = new DesktopSettings { AsrBackend = asr, TranslationBackend = translation, AsrApi = api, TranslationApi = api,
            PythonPath = "", ModelPath = "", OllamaPath = "", OllamaModelsPath = "" };
        var checks = DesktopEnvironment.CheckFiles(settings);
        Assert.Equal(asr == ModelBackend.Local, checks.Any(x => x.Component == DependencyComponent.Runtime));
        Assert.Equal(asr == ModelBackend.Local, checks.Any(x => x.Component == DependencyComponent.QwenModel));
        Assert.Equal(translation == ModelBackend.Local, checks.Any(x => x.Component == DependencyComponent.Ollama));
        Assert.Equal(translation == ModelBackend.Local, checks.Any(x => x.Component == DependencyComponent.TranslationModel));
        Assert.All(checks.Where(x => x.Name.EndsWith("API", StringComparison.Ordinal)), x => Assert.True(x.Ready));
    }

    /// <summary>全 API 不执行驱动或解释器探测，也不采用发现的本地路径。</summary><returns>初检验证任务。</returns>
    [Fact]
    public async Task AllApiSkipsLocalProbesAndDiscovery()
    {
        var api = new OpenAiApiSettings { Model = "test-model" };
        var settings = new DesktopSettings { AsrBackend = ModelBackend.OpenAiCompatible, TranslationBackend = ModelBackend.OpenAiCompatible,
            AsrApi = api, TranslationApi = api, PythonPath = "", ModelPath = "", OllamaPath = "", OllamaModelsPath = "" };
        var environment = new DesktopEnvironment("missing-content-root");
        var checks = await environment.CheckAsync(settings, TestContext.Current.CancellationToken);
        Assert.Equal(2, checks.Count); Assert.All(checks, x => Assert.True(x.Ready));
        Assert.Equal(settings, await environment.DiscoverAsync(settings, TestContext.Current.CancellationToken));
    }

    /// <summary>取消启动检测时不继续探测驱动或解释器。</summary><returns>取消验证任务。</returns>
    [Fact]
    public async Task CanceledCheckDoesNotStartProbes()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DesktopEnvironment(AppContext.BaseDirectory).CheckAsync(new(), cancellation.Token));
    }

    /// <summary>损坏的解释器可执行文件产生缺项提示，不掩盖为退出清理异常。</summary><returns>进程启动失败验证任务。</returns>
    [Fact]
    public async Task InvalidInterpreterIsReportedWithoutCleanupFailure()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
        await File.WriteAllTextAsync(file, "not an executable", TestContext.Current.CancellationToken);
        try
        {
            var result = await new DesktopEnvironment(AppContext.BaseDirectory).CheckAsync(new() { PythonPath = file }, TestContext.Current.CancellationToken);
            Assert.False(result.Single(x => x.Name == "Python 推理环境").Ready);
        }
        finally { File.Delete(file); }
    }

    /// <summary>缺少文件时每个依赖均给出明确失败，不启动外部服务。</summary>
    [Fact]
    public void MissingAssetsAreReportedSeparately()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var checks = DesktopEnvironment.CheckFiles(new() { PythonPath = Path.Combine(root, "python.exe"),
            ModelPath = root, OllamaPath = Path.Combine(root, "ollama.exe"), OllamaModelsPath = root });
        Assert.Equal(4, checks.Count); Assert.All(checks, item => Assert.False(item.Ready));
    }

    /// <summary>空目录和错误 revision 均不能通过 Qwen 与翻译模型初检。</summary>
    [Fact]
    public void EmptyDirectoriesAndWrongModelRevisionFail()
    {
        var root = Path.Combine(Path.GetTempPath(), "livelingo-environment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new DesktopSettings { ModelPath = root, OllamaModelsPath = root };
            Assert.False(DesktopEnvironment.CheckFiles(settings).Single(x => x.Name == "Qwen 模型").Ready);
            File.WriteAllText(Path.Combine(root, "asset-manifest.json"), "{\"repository\":\"Qwen/Qwen3-ASR-1.7B\",\"revision\":\"wrong\",\"files\":[]}");
            var result = DesktopEnvironment.CheckFiles(settings);
            Assert.False(result.Single(x => x.Name == "Qwen 模型").Ready);
            Assert.False(result.Single(x => x.Name == "TranslateGemma 模型").Ready);
        }
        finally { Directory.Delete(root, true); }
    }
}
