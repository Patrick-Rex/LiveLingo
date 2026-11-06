using System.Net.Http.Headers;
using System.Text.Json;
using LiveLingo.Core.Desktop;

namespace LiveLingo.Runtime.Api;

/// <summary>执行可取消、有界的兼容 API 请求，不跟随重定向或输出凭据与服务正文。</summary>
internal sealed class OpenAiClient : IDisposable
{
    private readonly HttpClient client;
    private readonly TimeSpan timeout;
    private readonly string apiKey;
    private const int MaximumResponseBytes = 256 * 1024;

    /// <summary>绑定一个阶段的地址、凭据和总请求预算。</summary>
    /// <param name="settings">已选择的 API 配置。</param><param name="timeout">包含读取响应体的时限。</param>
    /// <param name="handler">可选测试传输；所有权交给本客户端。</param>
    public OpenAiClient(OpenAiApiSettings settings, TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        settings.Validate();
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.timeout = timeout; apiKey = settings.ApiKey;
        client = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        { BaseAddress = settings.GetBaseUri(), Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>提交一次请求并读取有限 JSON，HTTP 错误只保留状态码。</summary>
    /// <param name="request">调用方拥有的请求。</param><param name="cancellationToken">会话或请求取消。</param>
    /// <returns>调用方负责释放的响应文档。</returns>
    public async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"API 请求失败（HTTP {(int)response.StatusCode}）。", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("API 响应超过 256 KiB。");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + read > MaximumResponseBytes) throw new InvalidDataException("API 响应超过 256 KiB。");
                output.Write(buffer, 0, read);
            }
            deadline.Token.ThrowIfCancellationRequested();
            return JsonDocument.Parse(output.ToArray(), new() { MaxDepth = 16 });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("API 请求超时。"); }
        catch (HttpRequestException error) when (error.StatusCode is null)
        { throw new HttpRequestException("API 连接失败，请检查地址、网络及证书。"); }
        catch (JsonException) { throw new InvalidDataException("API 未返回有效的 JSON 对象。"); }
    }

    /// <inheritdoc />
    public void Dispose() => client.Dispose();
}
