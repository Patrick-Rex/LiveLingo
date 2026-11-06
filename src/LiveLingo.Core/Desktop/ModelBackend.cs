using System.Text.Json.Serialization;

namespace LiveLingo.Core.Desktop;

/// <summary>为识别或翻译独立选择唯一的执行来源。</summary>
public enum ModelBackend
{
    /// <summary>使用本程序管理的本地模型。</summary>
    Local,
    /// <summary>使用用户配置的 OpenAI 兼容接口。</summary>
    OpenAiCompatible
}

/// <summary>保存一个阶段独立的 OpenAI 兼容服务地址和模型。</summary>
public sealed record OpenAiApiSettings
{
    /// <summary>获取包含版本前缀的 API 根地址。</summary>
    [JsonPropertyName("base_url")]
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";
    /// <summary>获取服务商提供的模型标识，不自动替换或发现模型。</summary>
    [JsonPropertyName("model")]
    public string Model { get; init; } = "";
    /// <summary>获取用户输入的凭据，按明确选择直接保存在本地配置；空值用于无需鉴权的服务。</summary>
    [JsonPropertyName("api_key")]
    public string ApiKey { get; init; } = "";

    /// <summary>验证地址和模型，不发送网络请求。</summary>
    /// <exception cref="ArgumentException">地址或模型不满足接口契约。</exception>
    public void Validate()
    {
        _ = GetBaseUri();
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 256 || Model.Any(char.IsControl))
            throw new ArgumentException("API 模型名称不能为空、超过 256 字符或包含控制字符。");
        if (ApiKey is null || ApiKey.Length > 8192 || ApiKey.Any(char.IsControl))
            throw new ArgumentException("API Key 不能超过 8192 字符或包含控制字符。");
    }

    /// <summary>生成保留版本前缀的绝对根地址；仅本机允许明文 HTTP。</summary>
    /// <returns>以斜杠结束的有效根地址。</returns>
    /// <exception cref="ArgumentException">地址不是 HTTPS 或本机 HTTP，或包含凭据、查询、片段。</exception>
    public Uri GetBaseUri()
    {
        if (BaseUrl is null || BaseUrl.Length > 2048 || !Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("API 地址须为 HTTPS（本机可用 HTTP），包含版本前缀且不能含凭据、查询或片段。");
        return new(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    /// <summary>避免日志或调试摘要意外打印凭据和服务地址。</summary>
    /// <returns>固定的脱敏配置标识。</returns>
    public override string ToString() => "OpenAI compatible API configuration";
}
