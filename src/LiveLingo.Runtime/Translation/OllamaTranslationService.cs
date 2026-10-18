using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveLingo.Runtime.Translation;

/// <summary>只连接显式 loopback 服务，不跟随重定向、代理或自动下载权重。</summary>
public sealed class OllamaTranslationService : ITranslationService, IDisposable
{
    private readonly HttpClient client;
    private readonly string model;
    private readonly string digest;
    private bool validated;
    private const int MaximumResponseBytes = 256 * 1024;

    /// <summary>建立固定模型、有限超时的本地翻译连接。</summary>
    /// <param name="endpoint">显式本地 HTTP 服务根地址。</param><param name="model">已准备的 4B 或 12B 标签。</param>
    /// <param name="digest">M2 已核实的权重 SHA-256。</param><param name="timeout">单次请求有限时限。</param>
    public OllamaTranslationService(Uri endpoint, string model, string digest, TimeSpan timeout)
    {
        if (endpoint.Scheme != "http" || !IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) ||
            !IPAddress.IsLoopback(address) || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 ||
            endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("必须使用数字 loopback HTTP 根地址。", nameof(endpoint));
        if (model is not ("translategemma:4b" or "translategemma:12b")) throw new ArgumentException("不支持的模型。", nameof(model));
        if (digest.Length != 64 || !digest.All(Uri.IsHexDigit)) throw new ArgumentException("需要完整权重摘要。", nameof(digest));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.model = model; this.digest = digest;
        client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = endpoint, Timeout = timeout };
    }

    /// <summary>读取已有权重清单并检查精确摘要，不触发 pull 或改变其他服务进程。</summary>
    /// <param name="cancellationToken">初始化取消信号。</param><returns>验证完成任务。</returns>
    public async Task ValidateModelAsync(CancellationToken cancellationToken)
    {
        validated = false;
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/tags");
        using var json = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array ||
            !models.EnumerateArray().Any(x => x.TryGetProperty("name", out var name) && name.GetString() == model &&
                x.TryGetProperty("digest", out var found) && string.Equals(found.GetString(), digest, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("已安装模型与配置摘要不一致。");
        validated = true;
    }

    /// <inheritdoc />
    public async Task<TranslationResult> TranslateAsync(TranslationInput input, CancellationToken cancellationToken)
    {
        if (!validated) throw new InvalidOperationException("模型身份尚未通过验证。");
        var prompt = OfficialTranslationPrompt.Format(input.Source);
        // UTF-8 字节上界为 byte-fallback 留足 token 余量，另留 256 个模板 token；超限保留原文并报告。
        if (Encoding.UTF8.GetByteCount(prompt) > 4096 - 512 - 256) throw new ArgumentException("完整块超出已支持上下文预算。", nameof(input));
        var body = new OllamaChatRequest(model, false, "5m", [new("user", prompt)], new(4096, 512, 0, 42, 1.0));
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, TranslationJsonContext.Default.OllamaChatRequest)) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var started = Stopwatch.GetTimestamp();
        using var json = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        if (!root.TryGetProperty("model", out var actual) || actual.GetString() != model) throw new InvalidDataException("响应模型不一致。");
        var text = root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) ? content.GetString() ?? "" : "";
        var reason = root.TryGetProperty("done_reason", out var termination) ? termination.GetString() ?? "missing_termination" : "missing_termination";
        var complete = root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True && reason == "stop" && !string.IsNullOrWhiteSpace(text);
        if (Number(root, "prompt_eval_count") is > 3584) { complete = false; reason = "context_capacity"; }
        return new(input, text, complete, reason, Stopwatch.GetElapsedTime(started), digest,
            Number(root, "load_duration"), Number(root, "eval_duration"), checked((int?)Number(root, "eval_count")),
            Number(root, "prompt_eval_duration"), checked((int?)Number(root, "prompt_eval_count")));
    }

    /// <summary>限制整个响应体并拒绝 HTTP 错误及重定向，不将服务正文写入异常。</summary>
    /// <param name="request">受控本地请求。</param><param name="cancellationToken">取消信号。</param>
    /// <returns>调用方负责释放的有限 JSON。</returns>
    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(client.Timeout);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("响应超出长度限制。");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > MaximumResponseBytes) throw new InvalidDataException("响应超出长度限制。");
            output.Write(buffer, 0, read);
        }
        return JsonDocument.Parse(output.ToArray(), new() { MaxDepth = 16 });
    }

    /// <summary>读取可选的非负性能计数，不接受负值。</summary>
    /// <param name="root">模型响应。</param><param name="name">计数名称。</param><returns>未提供时为空；非法计数使响应失败。</returns>
    private static long? Number(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (!value.TryGetInt64(out var number) || number < 0) throw new InvalidDataException("invalid_translation_metric");
        return number;
    }

    /// <inheritdoc />
    public void Dispose() => client.Dispose();
}

/// <summary>固定 Ollama 请求，使用源生成 JSON 避免运行时反射契约漂移。</summary>
/// <param name="Model">模型标签。</param><param name="Stream">固定非流式。</param><param name="KeepAlive">显式驻留时间。</param>
/// <param name="Messages">唯一 user 消息。</param><param name="Options">已测推理参数。</param>
internal sealed record OllamaChatRequest(string Model, bool Stream, string KeepAlive, OllamaMessage[] Messages, OllamaOptions Options);
/// <summary>官方单条对话消息。</summary><param name="Role">user 角色。</param><param name="Content">完整提示。</param>
internal sealed record OllamaMessage(string Role, string Content);
/// <summary>固定上下文与输出预算，不隐式采用服务默认值。</summary>
/// <param name="NumCtx">上下文预算。</param><param name="NumPredict">输出上限。</param><param name="Temperature">温度。</param>
/// <param name="Seed">种子。</param><param name="RepeatPenalty">保持真实重复的惩罚参数。</param>
internal sealed record OllamaOptions(int NumCtx, int NumPredict, double Temperature, int Seed, double RepeatPenalty);
/// <summary>编译生成有限请求类型的 JSON 元数据。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(OllamaChatRequest))]
internal partial class TranslationJsonContext : JsonSerializerContext;
