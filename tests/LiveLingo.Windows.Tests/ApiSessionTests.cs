using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LiveLingo.Core.Desktop;
using LiveLingo.Desktop.Windows;
using LiveLingo.Runtime.Pipeline;

namespace LiveLingo.Windows.Tests;

/// <summary>验证生产宿主在全 API 下不读取本地模型资源或创建进程。</summary>
public sealed class ApiSessionTests
{
    /// <summary>真实本机 HTTP 请求贯通识别、翻译与字幕，整个会话无需任何本地推理资产。</summary>
    /// <returns>字幕完成、服务关闭及进程清理验证任务。</returns>
    [Fact]
    public async Task AllApiSessionProducesSubtitlesOverHttpWithoutLocalAssets()
    {
        var root = Path.Combine(Path.GetTempPath(), "api-http-session-" + Guid.NewGuid().ToString("N"));
        await using var server = new LoopbackApi();
        var settings = new DesktopSettings
        {
            AsrBackend = ModelBackend.OpenAiCompatible, TranslationBackend = ModelBackend.OpenAiCompatible,
            AsrApi = new() { BaseUrl = server.BaseUrl, Model = "speech-fixture", ApiKey = "asr-fixture-key" },
            TranslationApi = new() { BaseUrl = server.BaseUrl, Model = "text-fixture", ApiKey = "translation-fixture-key" },
            PythonPath = "", ModelPath = "", OllamaPath = "", OllamaModelsPath = "", DataPath = root, CachePath = Path.Combine(root, "cache")
        };
        var session = new DesktopSession(Path.Combine(root, "missing-content")) { VerificationInput = AudioInput };
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PipelineSnapshot? final = null;
        session.PipelineUpdated += value => final = value;
        session.Updated += value => { if (!value.Active) stopped.TrySetResult(); };
        try
        {
            await session.StartAsync(Guid.NewGuid(), new("HTTP fixture", new(123, DateTimeOffset.UtcNow, @"D:\fixture.exe")), settings, TestContext.Current.CancellationToken);
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            Assert.Null(session.LastError);
            Assert.NotNull(final); Assert.Equal("completed", final.Status);
            var segment = Assert.Single(final.Subtitles.Segments);
            Assert.Equal("今日は行きません。", segment.Source); Assert.Equal("今天不去。", segment.Translation);
            Assert.True(server.RecognitionCalls > 0); Assert.True(server.TranslationCalls > 0);
            Assert.Empty(session.LastOwned); Assert.Empty(session.RemainingOwned); Assert.Empty(session.RemainingTemporary);
        }
        finally
        {
            await session.StopAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>提供四秒有声样本触发实际 API 调用，音频本身不作为模型质量证据。</summary>
    /// <param name="id">会话身份。</param><param name="token">测试生命周期。</param><returns>单个连续音频块。</returns>
    private static async IAsyncEnumerable<PipelineInput> AudioInput(Guid id, [EnumeratorCancellation] CancellationToken token)
    {
        await Task.Yield(); token.ThrowIfCancellationRequested();
        var samples = new float[64000]; Array.Fill(samples, 0.1f);
        yield return new PipelineAudio(1, new(new(0, samples.Length), samples));
    }

    /// <summary>提供有界的本机 OpenAI 兼容测试端点，验证真实请求后返回确定文本。</summary>
    private sealed class LoopbackApi : IAsyncDisposable
    {
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
        public LoopbackApi()
        {
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
                    RecognitionCalls++; response = "{\"text\":\"今日は行きません。\"}";
                }
                else
                {
                    Assert.Equal("POST /fixture/v1/chat/completions HTTP/1.1", lines[0]);
                    Assert.Equal("Bearer translation-fixture-key", headers["Authorization"]);
                    using var json = JsonDocument.Parse(body);
                    Assert.Equal("text-fixture", json.RootElement.GetProperty("model").GetString());
                    Assert.Equal("今日は行きません。", json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
                    TranslationCalls++; response = "{\"choices\":[{\"message\":{\"content\":\"今天不去。\"},\"finish_reason\":\"stop\"}]}";
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

    /// <summary>缺少 profile、worker 和全部本地模型路径仍可启动空输入会话并正常清理。</summary><returns>真实宿主生命周期检查任务。</returns>
    [Fact]
    public async Task AllApiSessionNeedsNoLocalAssetsOrProcesses()
    {
        var root = Path.Combine(Path.GetTempPath(), "api-session-" + Guid.NewGuid().ToString("N"));
        var api = new OpenAiApiSettings { Model = "test-model", BaseUrl = "https://example.invalid/v1" };
        var settings = new DesktopSettings { AsrBackend = ModelBackend.OpenAiCompatible, TranslationBackend = ModelBackend.OpenAiCompatible,
            AsrApi = api, TranslationApi = api, PythonPath = "", ModelPath = "", OllamaPath = "", OllamaModelsPath = "", DataPath = root, CachePath = Path.Combine(root, "cache") };
        var session = new DesktopSession(Path.Combine(root, "missing-content")) { VerificationInput = EmptyInput };
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Updated += update => { if (!update.Active) stopped.TrySetResult(); };
        try
        {
            await session.StartAsync(Guid.NewGuid(), new("fixture", new(123, DateTimeOffset.UtcNow, @"D:\fixture.exe")), settings, TestContext.Current.CancellationToken);
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Null(session.LastError); Assert.Empty(session.LastOwned); Assert.Empty(session.RemainingTemporary);
        }
        finally
        {
            await session.StopAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>提供无采集、无远程调用的已结束输入。</summary>
    /// <param name="id">本次会话。</param><param name="token">生命周期取消。</param><returns>空输入序列。</returns>
    private static async IAsyncEnumerable<PipelineInput> EmptyInput(Guid id, [EnumeratorCancellation] CancellationToken token)
    {
        await Task.Yield(); token.ThrowIfCancellationRequested(); yield break;
    }
}
