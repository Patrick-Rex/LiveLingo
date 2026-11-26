using System.Text.Json.Serialization;
using LiveLingo.Core.Transcription;

namespace LiveLingo.Core.Desktop;

/// <summary>控制桌面界面使用系统、浅色或深色主题。</summary>
public enum DesktopTheme
{
    /// <summary>跟随操作系统外观。</summary>
    System,
    /// <summary>使用浅色界面。</summary>
    Light,
    /// <summary>使用深色界面。</summary>
    Dark
}

/// <summary>控制浮窗呈现的语言，不改变识别、翻译或完整历史。</summary>
public enum SubtitleDisplayMode
{
    /// <summary>同时显示原文与译文。</summary>
    Bilingual,
    /// <summary>只显示译文及其等待或修订状态。</summary>
    TranslationOnly,
    /// <summary>只显示原文及字幕状态。</summary>
    SourceOnly
}

/// <summary>保存可版本化的本地模型、资源和字幕布局设置；运行中保持不可变。</summary>
public sealed record DesktopSettings
{
    /// <summary>初始化桌面默认设置，确保旧 JSON 缺省保留期和 API 配置仍有有效默认对象。</summary>
    /// <param name="historyRetentionDays">历史保留天数；零表示永久保留。</param>
    /// <param name="asrApi">可选识别 API 配置；旧文件未提供时使用空模型配置。</param>
    /// <param name="translationApi">可选翻译 API 配置；旧文件未提供时使用空模型配置。</param>
    /// <param name="runtimeProfile">本地推理配置；旧文件有解释器但无配置标识时沿用 cu130。</param>
    /// <param name="pythonPath">保存的隔离解释器路径；新配置使用所选 profile 的用户目录。</param>
    /// <param name="glossaryLibrary">已保存的命名词库；旧版本没有此字段时使用关闭的示例库。</param>
    /// <param name="asrLanguage">识别语言。读取旧版 auto 或缺失字段时，沿用项目的日语优先设置。</param>
    /// <param name="targetLanguage">翻译目标语言，旧文件缺省为简体中文。</param>
    [JsonConstructor]
    public DesktopSettings(int historyRetentionDays = 30, OpenAiApiSettings? asrApi = null, OpenAiApiSettings? translationApi = null,
        string? runtimeProfile = null, string? pythonPath = null, TranslationGlossaryLibrary? glossaryLibrary = null,
        string asrLanguage = AsrLanguages.Default, string targetLanguage = AsrLanguages.DefaultTarget)
    {
        RuntimeProfile = runtimeProfile ?? (pythonPath is null ? "win-x64-cpu" : "win-x64-cu130");
        PythonPath = pythonPath ?? GetManagedInterpreter(RuntimeProfile);
        HistoryRetentionDays = historyRetentionDays;
        AsrApi = asrApi ?? new(); TranslationApi = translationApi ?? new();
        GlossaryLibrary = glossaryLibrary ?? new();
        AsrLanguage = asrLanguage == "auto" ? AsrLanguages.Default : asrLanguage;
        TargetLanguage = targetLanguage;
    }

    /// <summary>获取文件契约主版本。</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>获取桌面主题；旧配置默认跟随系统。</summary>
    public DesktopTheme Theme { get; init; } = DesktopTheme.System;
    /// <summary>获取历史保留天数；零表示永久保留，旧配置默认三十天。</summary>
    public int HistoryRetentionDays { get; init; } = 30;
    /// <summary>获取识别来源；旧设置继续使用本地 Qwen。</summary>
    public ModelBackend AsrBackend { get; init; }
    /// <summary>获取翻译来源；旧设置继续使用本地 TranslateGemma。</summary>
    public ModelBackend TranslationBackend { get; init; }
    /// <summary>获取独立的识别 API 配置。</summary>
    public OpenAiApiSettings AsrApi { get; init; } = new();
    /// <summary>获取独立的翻译 API 配置。</summary>
    public OpenAiApiSettings TranslationApi { get; init; } = new();
    /// <summary>获取用于会话记录的所选翻译模型。</summary>
    [JsonIgnore]
    public string EffectiveTranslationModel => TranslationBackend == ModelBackend.Local ? TranslationModel : TranslationApi.Model;
    /// <summary>获取明确选择的翻译模型。</summary>
    public string TranslationModel { get; init; } = "translategemma:12b";
    /// <summary>获取本地保存的命名词库；首次安装和旧设置默认关闭。</summary>
    public TranslationGlossaryLibrary GlossaryLibrary { get; init; }
    /// <summary>获取已启用且适用于当前目标语言与翻译来源的词库正文，供会话启动时构建匹配表。</summary>
    [JsonIgnore]
    public string TranslationGlossaryText
    {
        get
        {
            if (!GlossaryLibrary.Enabled) return "";
            var profile = GlossaryLibrary.Profiles[GlossaryLibrary.SelectedIndex];
            if (profile.TargetLanguage != TargetLanguage || (profile.LocalOnly && TranslationBackend != ModelBackend.Local)) return "";
            return profile.Text;
        }
    }
    /// <summary>获取本地或 API 识别使用的明确原文语言。</summary>
    public string AsrLanguage { get; init; }
    /// <summary>获取本地模型和 API 翻译共用的目标语言，中文目标使用简体。</summary>
    public string TargetLanguage { get; init; }
    /// <summary>获取每用户的资源根，独立于可更新或卸载的程序目录。</summary>
    public static string ResourceRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveLingoData");
    /// <summary>获取选中的本地推理配置标识。</summary>
    public string RuntimeProfile { get; init; }
    /// <summary>获取已有的隔离解释器路径。</summary>
    public string PythonPath { get; init; }
    /// <summary>获取固定 Qwen 权重目录。</summary>
    public string ModelPath { get; init; } = Path.Combine(ResourceRoot, "models", "Qwen3-ASR-1.7B-7278e1e");
    /// <summary>获取已有 Ollama 安装路径。</summary>
    public string OllamaPath { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
    /// <summary>获取既有翻译权重根目录。</summary>
    public string OllamaModelsPath { get; init; } = Environment.GetEnvironmentVariable("OLLAMA_MODELS") is { Length: > 0 } models && IsLocalAbsolutePath(models)
        ? models : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");
    /// <summary>获取音频溢写和应用数据根目录。</summary>
    public string DataPath { get; init; } = Path.Combine(ResourceRoot, "data");
    /// <summary>获取子进程大型缓存根目录。</summary>
    public string CachePath { get; init; } = Path.Combine(ResourceRoot, "cache");
    /// <summary>获取字幕左上角物理像素横坐标。</summary>
    public int OverlayX { get; init; } = 160;
    /// <summary>获取字幕左上角物理像素纵坐标。</summary>
    public int OverlayY { get; init; } = 650;
    /// <summary>获取字幕 DIP 宽度。</summary>
    public double OverlayWidth { get; init; } = 1000;
    /// <summary>获取字幕 DIP 高度。</summary>
    public double OverlayHeight { get; init; } = 260;
    /// <summary>获取译文正文字号。</summary>
    public double FontSize { get; init; } = 28;
    /// <summary>获取背景不透明度。</summary>
    public double BackgroundOpacity { get; init; } = 0.75;
    /// <summary>获取浮窗语言显示方式；旧配置默认双语。</summary>
    public SubtitleDisplayMode DisplayMode { get; init; } = SubtitleDisplayMode.Bilingual;
    /// <summary>获取启停快捷键，统一使用 Ctrl+Alt 加功能键。</summary>
    public int StartKey { get; init; } = 7;
    /// <summary>获取字幕显隐快捷键。</summary>
    public int VisibilityKey { get; init; } = 8;
    /// <summary>获取布局交互快捷键。</summary>
    public int LayoutKey { get; init; } = 9;

    /// <summary>拒绝未知模式、非有限布局、重复快捷键和不安全的资源路径。</summary>
    /// <exception cref="ArgumentException">配置不满足本地桌面契约。</exception>
    public void Validate()
    {
        if (SchemaVersion != 1 || !Enum.IsDefined(AsrBackend) || !Enum.IsDefined(TranslationBackend))
            throw new ArgumentException("设置版本、模型或语言不受支持。");
        if (!AsrLanguages.IsSupported(AsrLanguage)) throw new ArgumentException("请选择中文、英语、日语或韩语作为识别语言。");
        if (!AsrLanguages.IsSupported(TargetLanguage)) throw new ArgumentException("请选择简体中文、英语、日语或韩语作为翻译目标语言。");
        if (!IsSupportedRuntime(RuntimeProfile)) throw new ArgumentException("本地识别运行配置不受支持。");
        (GlossaryLibrary ?? throw new ArgumentException("词库配置须为对象。")).Validate();
        if (!Enum.IsDefined(DisplayMode)) throw new ArgumentException("字幕显示模式不受支持。");
        if (!Enum.IsDefined(Theme)) throw new ArgumentException("界面主题不受支持。");
        if (HistoryRetentionDays is not (0 or 7 or 30 or 90)) throw new ArgumentException("历史保留期须为永久、7、30 或 90 天。");
        if (AsrApi is null || TranslationApi is null) throw new ArgumentException("API 配置须为对象；未使用时可保留空配置。");
        ValidateDataPath(DataPath); ValidateDataPath(CachePath);
        if (AsrBackend == ModelBackend.Local) { ValidateDataPath(PythonPath); ValidateDataPath(ModelPath); }
        else (AsrApi ?? throw new ArgumentException("缺少识别 API 配置。")).Validate();
        if (TranslationBackend == ModelBackend.Local)
        {
            if (TranslationModel is not ("translategemma:4b" or "translategemma:12b")) throw new ArgumentException("本地翻译模型不受支持。");
            ValidateDataPath(OllamaModelsPath);
            ValidateDataPath(OllamaPath);
        }
        else (TranslationApi ?? throw new ArgumentException("缺少翻译 API 配置。")).Validate();
        if (!double.IsFinite(OverlayWidth) || OverlayWidth is < 320 or > 3840 || !double.IsFinite(OverlayHeight) || OverlayHeight is < 120 or > 2160 ||
            !double.IsFinite(FontSize) || FontSize is < 16 or > 64 || !double.IsFinite(BackgroundOpacity) || BackgroundOpacity is < 0.1 or > 1)
            throw new ArgumentException("字幕尺寸、字号或背景不透明度超出范围。");
        var keys = new[] { StartKey, VisibilityKey, LayoutKey };
        if (keys.Any(x => x is < 1 or > 11) || keys.Distinct().Count() != 3) throw new ArgumentException("快捷键须选择三个不同的 Ctrl+Alt+F1–F11；F12 由系统保留。");
    }

    /// <summary>判断配置名是否对应随程序分发的本地识别环境。</summary>
    /// <param name="profile">用户选择或从文件读取的 profile 标识。</param><returns>是否为支持的标识。</returns>
    public static bool IsSupportedRuntime(string profile) => profile is "win-x64-cpu" or "win-x64-rocm" or "win-x64-cu128" or "win-x64-cu130";

    /// <summary>返回配置专属的用户解释器路径，让不同推理包并存。</summary>
    /// <param name="profile">支持的隔离配置标识。</param><returns>托管虚拟环境的 Python 绝对路径。</returns>
    /// <exception cref="ArgumentException">配置标识不受支持。</exception>
    public static string GetManagedInterpreter(string profile) => IsSupportedRuntime(profile)
        ? Path.Combine(ResourceRoot, "runtimes", profile, "Scripts", "python.exe")
        : throw new ArgumentException("本地识别运行配置不受支持。", nameof(profile));

    /// <summary>判断路径是否可用于本地资源，排除相对、设备及网络路径。</summary>
    /// <param name="path">待检查位置。</param><returns>是否为本地绝对路径。</returns>
    public static bool IsLocalAbsolutePath(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
        (!OperatingSystem.IsWindows() || Path.GetPathRoot(path) is { Length: 3 } root && char.IsAsciiLetter(root[0]) && root[1] == ':') &&
        path.IndexOfAny(Path.GetInvalidPathChars()) < 0;

    /// <summary>校验资源使用本地绝对路径。</summary><param name="path">待使用的路径。</param>
    /// <exception cref="ArgumentException">路径不是本地绝对路径。</exception>
    public static void ValidateDataPath(string path)
    {
        if (!IsLocalAbsolutePath(path)) throw new ArgumentException("模型、隔离环境及数据缓存需要本地绝对路径。");
    }
}
