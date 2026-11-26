using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LiveLingo.Core.Desktop;
using LiveLingo.Core.History;
using LiveLingo.Desktop.Windows;
using LiveLingo.Runtime.Pipeline;

namespace LiveLingo.Windows.Tests;

/// <summary>验证生产宿主在全 API 下不读取本地模型资源或创建进程。</summary>
public sealed class ApiSessionTests
{
    /// <summary>真实本机 HTTP 请求贯通识别、翻译与字幕，整个会话无需任何本地推理资产。</summary>
    /// <param name="language">已应用的识别语言。</param><param name="code">转写接口预期的 ISO 639-1 代码。</param>
    /// <param name="target">本场翻译目标语言。</param><param name="first">第一句识别正文。</param><param name="second">第二句识别正文。</param>
    /// <param name="gapReason">宿主收到的未识别范围原因，为空时仅提供完整音频。</param>
    /// <returns>字幕完成、服务关闭及进程清理验证任务。</returns>
    [Theory]
    [InlineData("Japanese", "ja", "Chinese", "今日は行きません。", "また明日。", null)]
    [InlineData("Chinese", "zh", "English", "今天不去。", "明天见！", null)]
    [InlineData("English", "en", "Korean", "I cannot go today.", "See you tomorrow!", "audio_backlog_capacity")]
    [InlineData("Korean", "ko", "Japanese", "오늘은 안 가요.", "내일 봐요!", "live_recognition_gap")]
    public async Task AllApiSessionProducesSubtitlesOverHttpWithoutLocalAssets(string language, string code, string target, string first, string second, string? gapReason)
    {
        var root = Path.Combine(Path.GetTempPath(), "api-http-session-" + Guid.NewGuid().ToString("N"));
        var source = first + (language is "English" or "Korean" ? " " : "") + second;
        string[] translations = target switch
        {
            "Chinese" => ["今天不去。", "再见。明天见！"],
            "English" => ["I cannot go today.", "Goodbye. See you tomorrow!"],
            "Japanese" => ["今日は行きません。", "さようなら。また明日！"],
            "Korean" => ["오늘은 안 가요.", "잘 가요. 내일 봐요!"],
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        await using var server = new LoopbackApi(code, source, [first, second], target, translations);
        var settings = new DesktopSettings
        {
            AsrLanguage = language, TargetLanguage = target, AsrBackend = ModelBackend.OpenAiCompatible, TranslationBackend = ModelBackend.OpenAiCompatible,
            AsrApi = new() { BaseUrl = server.BaseUrl, Model = "speech-fixture", ApiKey = "asr-fixture-key" },
            TranslationApi = new() { BaseUrl = server.BaseUrl, Model = "text-fixture", ApiKey = "translation-fixture-key" },
            GlossaryLibrary = new() { Enabled = true, Profiles = [new("本地测试表", $"{first}=本地整句\n{second[..2]}=local-term-fixture", LocalOnly: true, TargetLanguage: target)] },
            PythonPath = "", ModelPath = "", OllamaPath = "", OllamaModelsPath = "", DataPath = root, CachePath = Path.Combine(root, "cache")
        };
        var history = new DesktopHistory(root);
        var session = new DesktopSession(Path.Combine(root, "missing-content"), history)
            { VerificationInput = (_, token) => AudioInput(gapReason, token) };
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Guid.NewGuid();
        DesktopUpdate? lastUpdate = null;
        PipelineSnapshot? final = null;
        session.PipelineUpdated += value => final = value;
        session.Updated += value => { if (!value.Active) { lastUpdate = value; stopped.TrySetResult(); } };
        try
        {
            await session.StartAsync(id, new("HTTP fixture", new(123, DateTimeOffset.UtcNow, @"D:\fixture.exe")), settings, TestContext.Current.CancellationToken);
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            Assert.Null(session.LastError);
            Assert.NotNull(final); Assert.Equal("completed", final.Status);
            var segment = Assert.Single(final.Subtitles.Segments);
            Assert.Equal(source, segment.Source);
            Assert.Equal([first, second], segment.PhraseTranslations.Select(pair => pair?.Source));
            Assert.Equal(translations, segment.PhraseTranslations.Select(pair => pair?.Translation));
            Assert.Equal(string.Join('\n', translations), segment.Translation);
            Assert.True(server.RecognitionCalls > 0); Assert.Equal(2, server.TranslationCalls);
            var saved = await history.Store!.ReadAsync(id, TestContext.Current.CancellationToken);
            Assert.Equal((language, target), (saved.Summary.Info.SourceLanguage, saved.Summary.Info.TargetLanguage));
            Assert.Equal(HistorySessionState.Completed, saved.Summary.State);
            Assert.Equal(gapReason is null ? 1 : 2, Assert.Single(saved.Entries).Key.CaptureEpoch);
            if (gapReason is not null)
            {
                Assert.Contains("未识别", lastUpdate!.Status);
                Assert.Contains("采集片段 1，0–0.5 秒", saved.Summary.Error);
            }
            else Assert.Null(saved.Summary.Error);
            Assert.Empty(session.LastOwned); Assert.Empty(session.RemainingOwned); Assert.Empty(session.RemainingTemporary);
        }
        finally
        {
            await session.StopAsync();
            await history.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>可先通过采集控制注入缺口通知验证宿主转存，再提供四秒有声样本触发实际 API 调用。</summary>
    /// <param name="gapReason">用于验证宿主显示和历史路由的缺口原因。</param><param name="token">测试生命周期。</param><returns>有界的采集输入。</returns>
    private static async IAsyncEnumerable<PipelineInput> AudioInput(string? gapReason, [EnumeratorCancellation] CancellationToken token)
    {
        await Task.Yield(); token.ThrowIfCancellationRequested();
        if (gapReason is not null)
        {
            var missed = new float[8000]; Array.Fill(missed, 0.1f);
            yield return new PipelineAudio(1, new(new(0, missed.Length), missed));
            yield return new PipelineCaptureReset(2, gapReason);
        }
        var samples = new float[64000]; Array.Fill(samples, 0.1f);
        yield return new PipelineAudio(gapReason is null ? 1 : 2, new(new(0, samples.Length), samples));
    }

    /// <summary>提供有界的本机 OpenAI 兼容测试端点，验证真实请求后返回确定文本。</summary>
    private sealed class LoopbackApi : IAsyncDisposable
    {
        private readonly string languageCode;
        private readonly string source;
        private readonly string[] phrases;
        private readonly string target;
        private readonly string[] translations;
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private readonly Task serving;
        /// <summary>获取包含非默认前缀的测试地址。</summary>
        public string BaseUrl { get; }
        /// <summary>获取已接收的识别调用数。</summary>
        public int RecognitionCalls { get; private set; }
        /// <summary>获取已接收的翻译调用数。</summary>
        public int TranslationCalls { get; private set; }

        /// <summary>在随机回环端口启动仅本测试拥有的服务。</summary>
        /// <param name="languageCode">每次转写应显式指定的代码。</param><param name="source">测试接口返回的识别原文。</param>
        /// <param name="phrases">应逐句送入翻译的正文。</param>
        /// <param name="target">应显式请求的目标语言。</param><param name="translations">服务逐句返回的译文。</param>
        public LoopbackApi(string languageCode, string source, string[] phrases, string target, string[] translations)
        {
            this.languageCode = languageCode; this.source = source; this.phrases = phrases;
            this.target = target; this.translations = translations;
            listener.Start(); BaseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/fixture/v1";
            serving = ServeAsync();
        }

        /// <summary>顺序处理带 Content-Length 的有界请求，每个响应后关闭连接。</summary>
        /// <returns>取消前的测试服务任务；协议断言失败直接传播。</returns>
        private async Task ServeAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                await using var stream = client.GetStream();
                var header = new byte[8192]; var length = 0;
                while (length < header.Length && !header.AsSpan(0, length).EndsWith("\r\n\r\n"u8))
                {
                    await stream.ReadExactlyAsync(header.AsMemory(length, 1), stop.Token); length++;
                }
                Assert.True(length < header.Length);
                var lines = Encoding.ASCII.GetString(header, 0, length).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var headers = lines.Skip(1).Select(x => x.Split(':', 2)).ToDictionary(x => x[0], x => x[1].Trim(), StringComparer.OrdinalIgnoreCase);
                var size = int.Parse(headers["Content-Length"], System.Globalization.CultureInfo.InvariantCulture);
                Assert.InRange(size, 1, 1024 * 1024);
                var body = new byte[size]; await stream.ReadExactlyAsync(body, stop.Token);
                string response;
                if (lines[0] == "POST /fixture/v1/audio/transcriptions HTTP/1.1")
                {
                    Assert.Equal("Bearer asr-fixture-key", headers["Authorization"]);
                    Assert.StartsWith("multipart/form-data;", headers["Content-Type"]);
                    Assert.True(body.AsSpan().IndexOf("RIFF"u8) >= 0);
                    Assert.Contains($"name=language\r\n\r\n{languageCode}\r\n", Encoding.ASCII.GetString(body).Replace("\"", ""));
                    RecognitionCalls++; response = JsonSerializer.Serialize(new { text = source });
                }
                else
                {
                    Assert.Equal("POST /fixture/v1/chat/completions HTTP/1.1", lines[0]);
                    Assert.Equal("Bearer translation-fixture-key", headers["Authorization"]);
                    using var json = JsonDocument.Parse(body);
                    Assert.Equal("text-fixture", json.RootElement.GetProperty("model").GetString());
                    var instruction = json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
                    Assert.Contains($"into {(target == "Chinese" ? "Simplified Chinese" : target)}.", instruction);
                    Assert.DoesNotContain("local-term-fixture", instruction);
                    Assert.Equal(phrases[TranslationCalls],
                        json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
                    var translation = translations[TranslationCalls++];
                    response = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = translation }, finish_reason = "stop" } } });
                }
                var payload = Encoding.UTF8.GetBytes(response);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                await stream.WriteAsync(payload, stop.Token);
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync(); listener.Stop();
            try { await serving; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
            finally { stop.Dispose(); }
        }
    }

    /// <summary>全 API 宿主正常退出或因输入异常停止时，界面和落盘档案均反映真实结束原因。</summary>
    /// <param name="disconnect">是否制造采集连续性错误。</param><returns>宿主生命周期及档案状态检查。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllApiSessionNeedsNoLocalAssetsOrProcesses(bool disconnect)
    {
        var root = Path.Combine(Path.GetTempPath(), "api-session-" + Guid.NewGuid().ToString("N"));
        var api = new OpenAiApiSettings { Model = "test-model", BaseUrl = "https://example.invalid/v1" };
        var settings = new DesktopSettings { AsrBackend = ModelBackend.OpenAiCompatible, TranslationBackend = ModelBackend.OpenAiCompatible,
            AsrApi = api, TranslationApi = api, PythonPath = "", ModelPath = "", OllamaPath = "", OllamaModelsPath = "", DataPath = root, CachePath = Path.Combine(root, "cache") };
        var id = Guid.NewGuid();
        var history = new DesktopHistory(root);
        var session = new DesktopSession(Path.Combine(root, "missing-content"), history)
            { VerificationInput = (_, token) => EndInput(disconnect, token) };
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Updated += update => { if (!update.Active) stopped.TrySetResult(); };
        try
        {
            await session.StartAsync(id, new("fixture", new(123, DateTimeOffset.UtcNow, @"D:\fixture.exe")), settings, TestContext.Current.CancellationToken);
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var saved = await history.Store!.ReadAsync(id, TestContext.Current.CancellationToken);
            Assert.Equal(disconnect ? HistorySessionState.Interrupted : HistorySessionState.Completed, saved.Summary.State);
            Assert.Equal(session.LastError, saved.Summary.Error);
            if (disconnect) Assert.Contains("会话已中断", session.LastError);
            else Assert.Null(session.LastError);
            Assert.Empty(session.LastOwned); Assert.Empty(session.RemainingTemporary);
        }
        finally
        {
            await session.StopAsync();
            await history.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>提供正常 EOF 或错误起点的音频，以验证在模型调用前退出的路径。</summary>
    /// <param name="disconnect">是否发送非连续输入。</param><param name="token">生命周期取消。</param><returns>有界验证输入。</returns>
    private static async IAsyncEnumerable<PipelineInput> EndInput(bool disconnect, [EnumeratorCancellation] CancellationToken token)
    {
        await Task.Yield(); token.ThrowIfCancellationRequested();
        if (disconnect) yield return new PipelineAudio(1, new(new(1, 2), [0.1f]));
    }
}
