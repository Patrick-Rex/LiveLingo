using CommunityToolkit.Mvvm.ComponentModel;
using LiveLingo.App.Services;
using LiveLingo.Core.Desktop;

namespace LiveLingo.App.ViewModels;

/// <summary>管理两个独立来源的表单与单份整体 JSON 草稿。</summary>
public sealed partial class DesktopViewModel
{
    private bool loadingModelInputs;
    private int modelInputMode;

    /// <summary>获取来源选项，顺序与来源枚举一致。</summary>
    public string[] ModelBackends { get; } = ["本地模型", "OpenAI 兼容 API"];
    /// <summary>获取同一整体模型配置的编辑方式。</summary>
    public string[] ModelInputModes { get; } = ["表单", "整体 JSON"];
    /// <summary>切换编辑方式，JSON 无效时保留输入供用户修正。</summary>
    public int ModelInputMode
    {
        get => modelInputMode;
        set
        {
            if (value is < 0 or > 1 || value == modelInputMode || !CanEdit) return;
            if (value == 0 && ModelJsonError.Length != 0) return;
            if (value == 1) ModelJson = ModelConfigurationJson.Write(ReadSettings());
            SetProperty(ref modelInputMode, value);
            OnPropertyChanged(nameof(IsModelForm)); OnPropertyChanged(nameof(IsModelJson));
        }
    }
    /// <summary>获取模型表单是否可见。</summary>
    public bool IsModelForm => ModelInputMode == 0;
    /// <summary>获取整体 JSON 编辑器是否可见。</summary>
    public bool IsModelJson => ModelInputMode == 1;
    /// <summary>获取待应用的识别来源索引。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsLocalAsr)), NotifyPropertyChangedFor(nameof(IsApiAsr))]
    public partial int AsrBackendIndex { get; set; }
    /// <summary>获取待应用的翻译来源索引。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsLocalTranslation)), NotifyPropertyChangedFor(nameof(IsApiTranslation))]
    public partial int TranslationBackendIndex { get; set; }
    /// <summary>获取是否使用本地识别及其路径。</summary>
    public bool IsLocalAsr => AsrBackendIndex == 0;
    /// <summary>获取是否显示识别 API 输入。</summary>
    public bool IsApiAsr => AsrBackendIndex == 1;
    /// <summary>获取是否使用本地翻译及其路径。</summary>
    public bool IsLocalTranslation => TranslationBackendIndex == 0;
    /// <summary>获取是否显示翻译 API 输入。</summary>
    public bool IsApiTranslation => TranslationBackendIndex == 1;
    /// <summary>获取识别 API 根地址草稿。</summary>
    [ObservableProperty] public partial string AsrApiBaseUrl { get; set; } = new OpenAiApiSettings().BaseUrl;
    /// <summary>获取识别 API 模型草稿。</summary>
    [ObservableProperty] public partial string AsrApiModel { get; set; } = "";
    /// <summary>获取识别 API Key 草稿。</summary>
    [ObservableProperty] public partial string AsrApiKey { get; set; } = "";
    /// <summary>获取翻译 API 根地址草稿。</summary>
    [ObservableProperty] public partial string TranslationApiBaseUrl { get; set; } = new OpenAiApiSettings().BaseUrl;
    /// <summary>获取翻译 API 模型草稿。</summary>
    [ObservableProperty] public partial string TranslationApiModel { get; set; } = "";
    /// <summary>获取翻译 API Key 草稿。</summary>
    [ObservableProperty] public partial string TranslationApiKey { get; set; } = "";
    /// <summary>获取同时包含识别和翻译的 JSON 草稿。</summary>
    [ObservableProperty] public partial string ModelJson { get; set; } = "";
    /// <summary>获取整体 JSON 的解析错误，不包含密钥或原始正文。</summary>
    [ObservableProperty] public partial string ModelJsonError { get; set; } = "";

    /// <summary>一次导入整个模型文件，尚未应用到会话或磁盘。</summary>
    /// <param name="json">有界 UTF-8 文件正文。</param>
    public void ImportModelConfiguration(string json)
    {
        if (!CanEdit) return;
        ModelInputMode = 1;
        ModelJson = json;
        ParseModelJson(json);
    }

    /// <summary>在 JSON 编辑时原子更新模型表单，非法中间输入保留但不能应用。</summary>
    /// <param name="value">当前 JSON 草稿。</param>
    partial void OnModelJsonChanged(string value)
    {
        if (!loadingModelInputs && IsModelJson) ParseModelJson(value);
    }

    /// <summary>解析两个阶段，任一失败均保留上次有效模型草稿。</summary>
    /// <param name="json">整体配置。</param>
    private void ParseModelJson(string json)
    {
        try
        {
            var next = ModelConfigurationJson.Read(json, ReadSettings());
            LoadModelInputs(next, updateJson: false); ModelJsonError = "";
        }
        catch (ArgumentException error) { ModelJsonError = error.Message; }
        NotifyPendingSettings();
    }

    /// <summary>回填整体模型表单，可选同步 JSON，避免字段变化触发递归解析。</summary>
    /// <param name="value">已解析或已保存设置。</param><param name="updateJson">是否重建 JSON 文本。</param>
    private void LoadModelInputs(DesktopSettings value, bool updateJson = true)
    {
        loadingModelInputs = true;
        try
        {
            AsrBackendIndex = (int)value.AsrBackend; TranslationBackendIndex = (int)value.TranslationBackend;
            AsrApiBaseUrl = value.AsrApi.BaseUrl; AsrApiModel = value.AsrApi.Model; AsrApiKey = value.AsrApi.ApiKey;
            TranslationApiBaseUrl = value.TranslationApi.BaseUrl; TranslationApiModel = value.TranslationApi.Model; TranslationApiKey = value.TranslationApi.ApiKey;
            TranslationModel = value.TranslationModel; AsrLanguage = value.AsrLanguage;
            if (updateJson) { ModelJson = ModelConfigurationJson.Write(value); ModelJsonError = ""; }
        }
        finally { loadingModelInputs = false; }
    }
}
