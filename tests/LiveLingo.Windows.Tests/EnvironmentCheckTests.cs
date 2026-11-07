using LiveLingo.Core.Desktop;
using LiveLingo.Desktop.Windows;

namespace LiveLingo.Windows.Tests;

/// <summary>验证环境初检不把空模型目录当作可用依赖。</summary>
public sealed class EnvironmentCheckTests
{
    /// <summary>既定 12B 清单的原始字节文本，不含权重或本机路径。</summary>
    private const string TranslationManifest = """
        {"schemaVersion":2,"mediaType":"application/vnd.docker.distribution.manifest.v2+json","config":{"mediaType":"application/vnd.docker.container.image.v1+json","digest":"sha256:e77c8f2cf3d1d99e8c436bf03949400e0b1b4eefcec5515e299f785dca234f1a","size":490},"layers":[{"mediaType":"application/vnd.ollama.image.model","digest":"sha256:1b2b95e2f0eb9a98a839249ed41dfca71b300f9c389e14581210649fada910ed","size":8109818272},{"mediaType":"application/vnd.ollama.image.template","digest":"sha256:e0a42594d802e5d31cdc786deb4823edb8adff66094d49de8fffe976d753e348","size":358},{"mediaType":"application/vnd.ollama.image.license","digest":"sha256:3e2c24001f9ef57bf7ec959a3658fbb49cdad113cdf394c264da9d16f9bdd132","size":8431},{"mediaType":"application/vnd.ollama.image.params","digest":"sha256:339e884a40f6708bc761d367f0c08e448d5bb6f16b3961c340e44e0e4835a004","size":61}]}
        """;

    /// <summary>新版清单独立存在或与旧兼容清单共存时，均以新清单验证模型身份。</summary>
    /// <param name="legacyAnchor">是否保留追加原清单引用的旧兼容清单。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TranslationManifestPrefersV2(bool legacyAnchor)
    {
        var root = Path.Combine(Path.GetTempPath(), "livelingo-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteTranslationManifest(root, true, TranslationManifest);
            if (legacyAnchor)
            {
                var anchor = TranslationManifest[..^2] + ",{\"mediaType\":\"application/vnd.docker.distribution.manifest.v2+json\",\"digest\":\"sha256:c2f9a9ca1ec7149f2422581c89937ab0363d9399d9355514d753ee012e99c252\",\"size\":856}]}";
                WriteTranslationManifest(root, false, anchor);
            }
            using var manifest = DesktopEnvironment.ReadTranslationManifest(root, "translategemma:12b");
            Assert.Equal(4, manifest.RootElement.GetProperty("layers").GetArrayLength());
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>尚未迁移的 Ollama 模型继续使用旧目录并验证固定指纹。</summary>
    [Fact]
    public void TranslationManifestAcceptsLegacyOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "livelingo-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteTranslationManifest(root, false, TranslationManifest);
            using var manifest = DesktopEnvironment.ReadTranslationManifest(root, "translategemma:12b");
            Assert.Equal(4, manifest.RootElement.GetProperty("layers").GetArrayLength());
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>新版清单不兼容时，不用正确的旧清单掩盖指纹失败。</summary>
    [Fact]
    public void TranslationManifestRejectsWrongV2WithoutLegacyFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "livelingo-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteTranslationManifest(root, false, TranslationManifest);
            WriteTranslationManifest(root, true, TranslationManifest.Replace("8109818272", "1", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => DesktopEnvironment.ReadTranslationManifest(root, "translategemma:12b"));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>有效的新清单仍必须具备实际权重，不能仅凭模型身份通过环境初检。</summary>
    [Fact]
    public void TranslationManifestDoesNotBypassMissingBlobs()
    {
        var root = Path.Combine(Path.GetTempPath(), "livelingo-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteTranslationManifest(root, true, TranslationManifest);
            var check = DesktopEnvironment.CheckFiles(new() { OllamaModelsPath = root })
                .Single(x => x.Component == DependencyComponent.TranslationModel);
            Assert.False(check.Ready);
            Assert.Contains("sha256-1b2b95e2f0eb9a98a839249ed41dfca71b300f9c389e14581210649fada910ed", check.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>在隔离测试目录写入指定布局的模型清单，不创建大型权重。</summary>
    /// <param name="root">测试模型根。</param><param name="v2">是否使用新版目录。</param><param name="contents">清单原始内容。</param>
    private static void WriteTranslationManifest(string root, bool v2, string contents)
    {
        var directory = Path.Combine(root, v2 ? "manifests-v2" : "manifests", v2 ? "ollama.com" : "registry.ollama.ai", "library", "translategemma");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "12b"), contents);
    }

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
