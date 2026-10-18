using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LiveLingo.Core.Subtitles;
using LiveLingo.Runtime.Translation;

namespace LiveLingo.Runtime.Tests.Translation;

/// <summary>使用真实本机 TCP 检查 HTTP 边界；服务响应为受控夹具，不计模型推理。</summary>
public sealed class OllamaTranslationTests
{
    private static readonly string Digest = new('a', 64);

    /// <summary>共用 M2 golden，保留字形、重复、换行及官方单 user 模板。</summary>
    [Fact]
    public void OfficialPromptMatchesM2Golden()
    {
        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "translation-prompt.txt"));
        Assert.Equal(golden.TrimEnd('\n'), OfficialTranslationPrompt.Format("確認して、確認してください。"));
        var source = "\n田中さん、田中さん。\n今日は行きません。\n";
        Assert.EndsWith(":\n\n\n" + source, OfficialTranslationPrompt.Format(source));
        Assert.Throws<ArgumentException>(() => OfficialTranslationPrompt.Format(" "));
    }

    /// <summary>所有 HTTP 参数显式且正文完整，权重先检查再调用。</summary>
    /// <returns>完成两次真实本地请求的任务。</returns>
    [Fact]
    public async Task RequestPreservesFullSourceAndParameters()
    {
        using var listener = Listen();
        var serving = ServeAsync(listener, [Tags(), Chat("stop", "是的，是的。不去。")]);
        using var service = Service(listener);
        await service.ValidateModelAsync(CancellationToken.None);
        var input = Input();
        var result = await service.TranslateAsync(input, CancellationToken.None);
        var requests = await serving;
        Assert.True(result.Complete);
        Assert.Equal(input, result.Input);
        Assert.Null(result.LoadNanoseconds);
        Assert.Null(result.PromptEvaluationNanoseconds);
        Assert.Null(result.OutputTokens);
        using var body = JsonDocument.Parse(requests[1]);
        var root = body.RootElement;
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("5m", root.GetProperty("keep_alive").GetString());
        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal(OfficialTranslationPrompt.Format(input.Source), message.GetProperty("content").GetString());
        var options = root.GetProperty("options");
        Assert.Equal(4096, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(512, options.GetProperty("num_predict").GetInt32());
        Assert.Equal(0, options.GetProperty("temperature").GetDouble());
        Assert.Equal(1, options.GetProperty("repeat_penalty").GetDouble());
    }

    /// <summary>截断或空内容不会被 HTTP 成功状态提升为完整译文。</summary>
    /// <param name="reason">终止原因。</param><param name="content">受控模型文本。</param>
    /// <returns>检查本地请求的任务。</returns>
    [Theory]
    [InlineData("length", "未完成")]
    [InlineData("stop", " ")]
    public async Task IncompleteResponsesRemainIncomplete(string reason, string content)
    {
        using var listener = Listen();
        var serving = ServeAsync(listener, [Tags(), Chat(reason, content)]);
        using var service = Service(listener);
        await service.ValidateModelAsync(CancellationToken.None);
        Assert.False((await service.TranslateAsync(Input(), CancellationToken.None)).Complete);
        await serving;
    }

    /// <summary>摘要不匹配时不发送推理，禁止代理、远程主机和重定向地址。</summary>
    /// <returns>读取错误权重清单的任务。</returns>
    [Fact]
    public async Task WrongDigestPreventsInference()
    {
        using var listener = Listen();
        var serving = ServeAsync(listener, [Tags().Replace(Digest, new string('b', 64))]);
        using var service = Service(listener);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ValidateModelAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync(Input(), CancellationToken.None));
        await serving;
        Assert.Throws<ArgumentException>(() => new OllamaTranslationService(new("http://example.com"), "translategemma:12b", Digest, TimeSpan.FromSeconds(5)));
        Assert.Throws<ArgumentException>(() => new OllamaTranslationService(new("http://127.0.0.1/other"), "translategemma:12b", Digest, TimeSpan.FromSeconds(5)));
    }

    /// <summary>恶意重定向与声明的超大体在分配或跟随后被拒绝。</summary>
    /// <param name="response">完整受控 HTTP 响应头。</param><returns>检查失败的任务。</returns>
    [Theory]
    [InlineData("HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 300000\r\n\r\n")]
    public async Task RedirectAndOversizedBodyAreRejected(string response)
    {
        using var listener = Listen();
        var serving = ServeAsync(listener, [response], raw: true);
        using var service = Service(listener);
        var error = await Record.ExceptionAsync(() => service.ValidateModelAsync(CancellationToken.None));
        Assert.True(error is HttpRequestException or InvalidDataException, error?.ToString());
        await serving;
    }

    /// <summary>保留服务实际输入/输出耗时及数量，不能把未执行阶段写成零。</summary>
    /// <returns>带指标响应的真实 HTTP 检查任务。</returns>
    [Fact]
    public async Task ResponseMetricsArePreserved()
    {
        using var listener = Listen();
        var chat = JsonSerializer.Serialize(new { model = "translategemma:12b", done = true, done_reason = "stop", message = new { content = "不去。" },
            load_duration = 11, prompt_eval_duration = 22, eval_duration = 33, prompt_eval_count = 44, eval_count = 55 });
        var serving = ServeAsync(listener, [Tags(), chat]);
        using var service = Service(listener);
        await service.ValidateModelAsync(CancellationToken.None);
        var result = await service.TranslateAsync(Input(), CancellationToken.None);
        await serving;
        Assert.Equal(11, result.LoadNanoseconds); Assert.Equal(22, result.PromptEvaluationNanoseconds);
        Assert.Equal(33, result.EvaluationNanoseconds); Assert.Equal(44, result.InputTokens); Assert.Equal(55, result.OutputTokens);
    }

    /// <summary>绑定系统分配的 loopback 端口。</summary><returns>调用方负责释放的监听器。</returns>
    private static TcpListener Listen() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return listener; }
    /// <summary>以受控服务地址建立真实客户端。</summary><param name="listener">当前监听器。</param><returns>翻译客户端。</returns>
    private static OllamaTranslationService Service(TcpListener listener) => new(new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/"), "translategemma:12b", Digest, TimeSpan.FromSeconds(5));
    /// <summary>构造有效翻译身份。</summary><returns>完整输入。</returns>
    private static TranslationInput Input() => new(new(Guid.NewGuid(), 1, PipelineRequestKind.Translation,
        new(Guid.NewGuid(), 1, 1, 1), new(Guid.NewGuid(), 1), new(0, 16000), 1), "はい、はい。行きません。");
    /// <summary>构造权重清单。</summary><returns>有限 JSON。</returns>
    private static string Tags() => "{\"models\":[{\"name\":\"translategemma:12b\",\"digest\":\"" + Digest + "\"}]}";
    /// <summary>构造输出响应。</summary><param name="reason">终止原因。</param><param name="text">原文输出。</param><returns>有限 JSON。</returns>
    private static string Chat(string reason, string text) => JsonSerializer.Serialize(new { model = "translategemma:12b", done = true, done_reason = reason, message = new { content = text } });

    /// <summary>按顺序处理有限请求，限制测试自身等待、头和正文，不遗留服务。</summary>
    /// <param name="listener">测试所有的监听器。</param><param name="responses">有限响应集合。</param><param name="raw">是否提供完整 HTTP 响应。</param>
    /// <returns>收到的请求正文。</returns>
    private static async Task<List<string>> ServeAsync(TcpListener listener, string[] responses, bool raw = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var bodies = new List<string>();
        foreach (var response in responses)
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            var header = new List<byte>();
            var one = new byte[1];
            while (header.Count < 16384)
            {
                await stream.ReadExactlyAsync(one, timeout.Token);
                header.Add(one[0]);
                if (header.Count >= 4 && Encoding.ASCII.GetString(header.TakeLast(4).ToArray()) == "\r\n\r\n") break;
            }
            var lengthLine = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n").SingleOrDefault(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var length = lengthLine is null ? 0 : int.Parse(lengthLine.Split(':')[1].Trim());
            Assert.InRange(length, 0, 65536);
            var body = new byte[length];
            await stream.ReadExactlyAsync(body, timeout.Token);
            bodies.Add(Encoding.UTF8.GetString(body));
            var data = Encoding.UTF8.GetBytes(response);
            if (!raw) await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n"), timeout.Token);
            await stream.WriteAsync(data, timeout.Token);
        }
        return bodies;
    }
}
