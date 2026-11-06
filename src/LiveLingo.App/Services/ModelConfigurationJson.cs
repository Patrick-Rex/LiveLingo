using System.Text.Json;
using System.Text.Json.Serialization;
using LiveLingo.Core.Desktop;

namespace LiveLingo.App.Services;

/// <summary>在一份 JSON 与整体模型表单之间转换，保留文件未涉及的桌面设置。</summary>
public static class ModelConfigurationJson
{
    /// <summary>导出识别和翻译的当前选择及凭据，调用方决定展示或保存位置。</summary>
    /// <param name="settings">当前表单快照。</param><returns>包含两个阶段的 JSON。</returns>
    public static string Write(DesktopSettings settings) => JsonSerializer.Serialize(new ModelConfiguration(
        Endpoint(settings.AsrBackend, "Qwen/Qwen3-ASR-1.7B", settings.AsrApi),
        Endpoint(settings.TranslationBackend, settings.TranslationModel, settings.TranslationApi), settings.AsrLanguage),
        ModelConfigurationJsonContext.Default.ModelConfiguration);

    /// <summary>原子解析整体配置，只替换模型字段；任一阶段无效时不应用任何部分。</summary>
    /// <param name="json">用户输入或一次导入的整体配置。</param><param name="current">需保留路径及外观的当前草稿。</param>
    /// <returns>两个阶段均有效的完整设置副本。</returns>
    /// <exception cref="ArgumentException">JSON 结构或所选模型配置无效。</exception>
    public static DesktopSettings Read(string json, DesktopSettings current)
    {
        if (json.Length > 32768) throw new ArgumentException("模型配置 JSON 超过 32 Ki 字符。");
        ModelConfiguration config;
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 8 });
            CheckDuplicateProperties(document.RootElement);
            config = JsonSerializer.Deserialize(json, ModelConfigurationJsonContext.Default.ModelConfiguration)
                ?? throw new ArgumentException("模型配置不能为空。");
        }
        catch (JsonException) { throw new ArgumentException("JSON 格式无效：请提供 asr、translation 和 asr_language，检查字段名称及值类型。"); }
        if (config.Asr is null || config.Translation is null) throw new ArgumentException("整体配置必须同时包含 asr 和 translation。");
        if (config.AsrLanguage is not ("auto" or "Japanese")) throw new ArgumentException("识别语言须为 auto 或 Japanese。");
        var asrBackend = Backend(config.Asr.Provider);
        var translationBackend = Backend(config.Translation.Provider);
        if (asrBackend == ModelBackend.Local && config.Asr.Model != "Qwen/Qwen3-ASR-1.7B") throw new ArgumentException("本地识别模型须为 Qwen/Qwen3-ASR-1.7B。");
        if (translationBackend == ModelBackend.Local && config.Translation.Model is not ("translategemma:4b" or "translategemma:12b"))
            throw new ArgumentException("本地翻译模型须为 translategemma:4b 或 translategemma:12b。");
        var asrApi = asrBackend == ModelBackend.Local ? current.AsrApi : Api(config.Asr);
        var translationApi = translationBackend == ModelBackend.Local ? current.TranslationApi : Api(config.Translation);
        return current with
        {
            AsrBackend = asrBackend, TranslationBackend = translationBackend, AsrApi = asrApi, TranslationApi = translationApi,
            TranslationModel = translationBackend == ModelBackend.Local ? config.Translation.Model : current.TranslationModel,
            AsrLanguage = config.AsrLanguage
        };
    }

    /// <summary>拒绝同一对象的重复字段，避免配置被不同工具解释成不同值。</summary>
    /// <param name="element">有限深度配置节点。</param>
    private static void CheckDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new ArgumentException("模型配置包含重复字段。");
            CheckDuplicateProperties(property.Value);
        }
    }

    /// <summary>映射明确来源名称，不推测未声明的来源。</summary><param name="provider">local 或 openai。</param><returns>执行来源。</returns>
    private static ModelBackend Backend(string provider) => provider switch
    { "local" => ModelBackend.Local, "openai" => ModelBackend.OpenAiCompatible, _ => throw new ArgumentException("provider 须为 local 或 openai。") };

    /// <summary>验证选中的 API 字段。</summary><param name="endpoint">阶段配置。</param><returns>有效 API 配置。</returns>
    private static OpenAiApiSettings Api(ModelEndpoint endpoint)
    {
        var api = new OpenAiApiSettings { BaseUrl = endpoint.BaseUrl ?? "", Model = endpoint.Model, ApiKey = endpoint.ApiKey ?? "" };
        api.Validate(); return api;
    }

    /// <summary>仅导出当前所选来源的字段。</summary>
    /// <param name="backend">来源。</param><param name="localModel">本地模型。</param><param name="api">API 配置。</param><returns>可序列化阶段。</returns>
    private static ModelEndpoint Endpoint(ModelBackend backend, string localModel, OpenAiApiSettings api) =>
        backend == ModelBackend.Local ? new("local", localModel, null, null) : new("openai", api.Model, api.BaseUrl, api.ApiKey);
}

/// <summary>一次编辑与导入两个阶段的完整模型配置。</summary>
/// <param name="Asr">识别来源与模型。</param><param name="Translation">翻译来源与模型。</param><param name="AsrLanguage">识别语言。</param>
internal sealed record ModelConfiguration(ModelEndpoint Asr, ModelEndpoint Translation, string AsrLanguage = "auto");
/// <summary>使用通用客户端字段表达一个阶段，不引入服务商专有参数。</summary>
/// <param name="Provider">local 或 openai。</param><param name="Model">模型标识。</param>
/// <param name="BaseUrl">API 版本根地址。</param><param name="ApiKey">API 凭据。</param>
internal sealed record ModelEndpoint(string Provider, string Model, string? BaseUrl, string? ApiKey)
{
    /// <summary>防止日志直接输出配置中的凭据。</summary><returns>固定脱敏名称。</returns>
    public override string ToString() => "Model endpoint configuration";
}
/// <summary>生成整体模型配置的固定 JSON 契约。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8)]
[JsonSerializable(typeof(ModelConfiguration))]
internal partial class ModelConfigurationJsonContext : JsonSerializerContext;
