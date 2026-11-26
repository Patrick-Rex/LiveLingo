using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using LiveLingo.Core.Desktop;
using LiveLingo.Core.Subtitles;
using LiveLingo.Runtime.Asr;
using LiveLingo.Runtime.Pipeline;
using LiveLingo.Runtime.Storage;
using LiveLingo.Runtime.Translation;

namespace LiveLingo.Runtime.Tests.Translation;

/// <summary>验证标准 API 协议、输入边界与取消；所有请求使用受控传输，不访问外部服务。</summary>
public sealed class OpenAiApiTests
{
    /// <summary>保留地址前缀、独立凭据和完整原文，响应模型别名可与请求名称不同。</summary><returns>协议检查任务。</returns>
    [Fact]
    public async Task TranslationUsesStandardRequestAndPreservesSource()
    {
        var input = Input();
        using var service = new OpenAiTranslationService(Settings(), TimeSpan.FromSeconds(2), new Handler(async (request, token) =>
        {
            Assert.Equal("https://example.test/prefix/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer test-key", request.Headers.Authorization!.ToString());
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var body = json.RootElement;
            Assert.Equal("chosen-model", body.GetProperty("model").GetString());
            Assert.False(body.GetProperty("stream").GetBoolean());
            Assert.Equal(2048, body.GetProperty("max_completion_tokens").GetInt32());
            Assert.Equal(input.Source, body.GetProperty("messages")[1].GetProperty("content").GetString());
            return Json("""{"model":"resolved-model","choices":[{"message":{"role":"assistant","content":"不去，不去。"},"finish_reason":"stop"}],"usage":{"prompt_tokens":20,"completion_tokens":8}}""");
        }));
        var result = await service.TranslateAsync(input, TestContext.Current.CancellationToken);
        Assert.True(result.Complete); Assert.Equal(input, result.Input);
        Assert.Equal("api:resolved-model", result.Digest); Assert.Equal(20, result.InputTokens); Assert.Equal(8, result.OutputTokens);
        Assert.Null(result.LoadNanoseconds);
    }

    /// <summary>四种源语言均能发往四种目标，术语提示与请求方向相符，模型收到完整原文。</summary>
    /// <param name="language">选定源语言。</param><param name="term">该语言的源词。</param><param name="source">含词的测试句子。</param>
    /// <returns>受控 API 传输检查任务。</returns>
    [Theory]
    [InlineData("Chinese", "試玩", "現在來試玩。")]
    [InlineData("English", "playthrough", "My first playthrough.")]
    [InlineData("Japanese", "初見プレイ", "初見プレイです。")]
    [InlineData("Korean", "첫 플레이", "첫 플레이예요.")]
    public async Task GlossaryHintsReachApiWithoutChangingSource(string language, string term, string source)
    {
        foreach (var (target, rendering) in new[] { ("Chinese", "初见游玩"), ("English", "first playthrough"), ("Japanese", "初見プレイ"), ("Korean", "첫 플레이") })
        {
            var glossary = TranslationGlossary.Parse($"{term}={rendering}\n神ゲー=神作");
            var input = Input() with { Source = source, Terms = glossary.Match(source).Terms, SourceLanguage = language, TargetLanguage = target };
            using var service = new OpenAiTranslationService(Settings(), TimeSpan.FromSeconds(2), new Handler(async (request, token) =>
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var messages = json.RootElement.GetProperty("messages");
                var instructions = messages[0].GetProperty("content").GetString();
                Assert.Contains($"{language} text", instructions);
                if (language != target) Assert.Contains($"into {(target == "Chinese" ? "Simplified Chinese" : target)}.", instructions);
                else Assert.Contains(target == "Chinese" ? "Traditional Chinese" : "Preserve the original wording", instructions);
                Assert.Contains($"preferred {target} renderings where the {language} terms", instructions);
                Assert.Contains($"\"{term}\":\"{rendering}\"", instructions);
                Assert.DoesNotContain("神作", instructions);
                Assert.Equal(source, messages[1].GetProperty("content").GetString());
                return Json(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = rendering }, finish_reason = "stop" } } }));
            }));
            var result = await service.TranslateAsync(input, TestContext.Current.CancellationToken);
            Assert.True(result.Complete); Assert.Equal(rendering, result.Text);
        }
    }

    /// <summary>截断、拒绝、工具调用及空响应不能被标为完整译文。</summary>
    /// <param name="reason">服务结束原因。</param><param name="message">模型消息 JSON。</param><returns>完成状态检查任务。</returns>
    [Theory]
    [InlineData("length", "{\"content\":\"部分\"}")]
    [InlineData("stop", "{\"content\":\" \"}")]
    [InlineData("stop", "{\"content\":\"拒绝\",\"refusal\":\"no\"}")]
    [InlineData("stop", "{\"content\":\"工具\",\"tool_calls\":[{}]}")]
    public async Task TranslationRejectsIncomplete(string reason, string message)
    {
        using var service = new OpenAiTranslationService(Settings(), TimeSpan.FromSeconds(2), new Handler((_, _) =>
            Task.FromResult(Json("{\"choices\":[{\"message\":" + message + ",\"finish_reason\":\"" + reason + "\"}]}"))));
        Assert.False((await service.TranslateAsync(Input(), TestContext.Current.CancellationToken)).Complete);
    }

    /// <summary>上传单声道 PCM16 WAV，每条请求始终带所选语言代码，响应保留指定语言与原始请求身份。</summary>
    /// <param name="language">识别语言。</param><param name="code">标准 API 语言字段。</param><returns>音频请求与返回检查任务。</returns>
    [Theory, InlineData("Chinese", "zh"), InlineData("English", "en"), InlineData("Japanese", "ja"), InlineData("Korean", "ko")]
    public async Task RecognitionUsesWaveMultipartAndCorrelation(string language, string code)
    {
        using var audio = new ContinuousAudioStore(new FileAudioSpillStore(Path.Combine(Path.GetTempPath(), "api-audio-" + Guid.NewGuid().ToString("N"))), 4, 0, 4, 4);
        audio.Append(0, [-1f, 0f, 0.5f, 1f]);
        using var lease = audio.Acquire(new(0, 4));
        var request = new AsrRequest(new(Guid.NewGuid(), 1, PipelineRequestKind.Recognition, new(Guid.NewGuid(), 1, 1, 1), new(Guid.NewGuid(), 1), new(0, 4), 0), new(0, 4), 0, language, 512);
        using var service = new OpenAiAsrService(Settings() with { ApiKey = "" }, TimeSpan.FromSeconds(2), new Handler(async (message, token) =>
        {
            Assert.EndsWith("/prefix/v1/audio/transcriptions", message.RequestUri!.AbsoluteUri);
            Assert.Null(message.Headers.Authorization);
            var parts = Assert.IsType<MultipartFormDataContent>(message.Content).ToArray();
            var file = Assert.Single(parts.Where(x => x.Headers.ContentDisposition!.Name!.Trim('"') == "file"));
            Assert.Equal("audio/wav", file.Headers.ContentType!.MediaType);
            var bytes = await file.ReadAsByteArrayAsync(token);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4)); Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));
            Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)));
            Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22)));
            Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(34)));
            Assert.Equal(52, bytes.Length); Assert.Equal(short.MinValue, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44)));
            var languagePart = Assert.Single(parts.Where(x => x.Headers.ContentDisposition!.Name!.Trim('"') == "language"));
            Assert.Equal(code, await languagePart.ReadAsStringAsync(token));
            return Json("{\"text\":\"行きません。\"}");
        }));
        var result = await service.RecognizeAsync(request, lease, TestContext.Current.CancellationToken);
        Assert.Equal(request, result.Correlation); Assert.True(result.Complete); Assert.Equal("api_response", result.Reason);
        Assert.Equal(language, result.Language);
        Assert.Equal("行きません。", result.Text);
    }

    /// <summary>HTTP 鉴权、限流和重定向只暴露状态码，不输出服务正文或凭据。</summary>
    /// <param name="status">响应状态码。</param><returns>错误检查任务。</returns>
    [Theory, InlineData(401), InlineData(429), InlineData(302), InlineData(500)]
    public async Task HttpFailuresRemainVisibleAndRedacted(int status)
    {
        using var service = new OpenAiTranslationService(Settings(), TimeSpan.FromSeconds(2), new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("secret-server-body") })));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => service.TranslateAsync(Input(), TestContext.Current.CancellationToken));
        Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.DoesNotContain("secret", error.Message); Assert.DoesNotContain("test-key", error.Message);
    }

    /// <summary>无效 JSON、异常结构与超大响应被拒绝。</summary>
    /// <param name="body">受控响应。</param><returns>边界检查任务。</returns>
    [Theory, InlineData("not-json"), InlineData("[]"), InlineData("{\"choices\":[]}")]
    public async Task InvalidResponsesAreRejected(string body)
    {
        using var service = new OpenAiTranslationService(Settings(), TimeSpan.FromSeconds(2), new Handler((_, _) => Task.FromResult(Json(body))));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.TranslateAsync(Input(), TestContext.Current.CancellationToken));
    }

    /// <summary>限制响应长度，且中止在途请求时保留调用者取消语义。</summary><returns>大小、取消与超时验证任务。</returns>
    [Fact]
    public async Task ResponseLimitsAndCancellationAreEnforced()
    {
        using (var service = new OpenAiTranslationService(Settings(), TimeSpan.FromSeconds(2), new Handler((_, _) => Task.FromResult(Json(new string('x', 262145))))))
            await Assert.ThrowsAsync<InvalidDataException>(() => service.TranslateAsync(Input(), TestContext.Current.CancellationToken));
        using var canceled = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var service = new OpenAiTranslationService(Settings(), TimeSpan.FromSeconds(2), new Handler(async (_, token) =>
        { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json("{}"); })))
        {
            var request = service.TranslateAsync(Input(), canceled.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await canceled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        }
        using var timed = new OpenAiTranslationService(Settings(), TimeSpan.FromMilliseconds(30), new Handler(async (_, token) =>
        { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json("{}"); }));
        await Assert.ThrowsAsync<TimeoutException>(() => timed.TranslateAsync(Input(), TestContext.Current.CancellationToken));
    }

    /// <summary>全 API 初始化不创建进程，也不发送预热或模型列表请求。</summary><returns>初始化与清理任务。</returns>
    [Fact]
    public async Task AllApiResetRequiresNoLocalFactories()
    {
        await using var models = new LocalPipelineModels(null, null, null, null, new());
        await models.ResetAsync(new(Guid.NewGuid(), 1, 1, 1),
            new("chosen-model", "Japanese", ModelBackend.OpenAiCompatible, ModelBackend.OpenAiCompatible, Settings(), Settings()), TestContext.Current.CancellationToken);
    }

    /// <summary>构造无真实凭据的独立 API 配置。</summary><returns>测试配置。</returns>
    private static OpenAiApiSettings Settings() => new() { BaseUrl = "https://example.test/prefix/v1", ApiKey = "test-key", Model = "chosen-model" };
    /// <summary>构造带版本的完整翻译输入。</summary><returns>翻译请求。</returns>
    private static TranslationInput Input() => new(new(Guid.NewGuid(), 1, PipelineRequestKind.Translation,
        new(Guid.NewGuid(), 1, 1, 1), new(Guid.NewGuid(), 1), new(0, 16000), 1), "行きません。行きません。");
    /// <summary>返回测试所有的 JSON 响应。</summary><param name="body">正文。</param><returns>可释放响应。</returns>
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>提供受控传输，无网络或付费调用。</summary><param name="send">当前请求处理。</param>
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
