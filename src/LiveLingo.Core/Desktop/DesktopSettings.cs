using System.Text.Json.Serialization;

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
    /// <summary>同时显示原文与中文。</summary>
    Bilingual,
    /// <summary>只显示中文及其等待或修订状态。</summary>
    TranslationOnly,
    /// <summary>只显示原文及字幕状态。</summary>
    SourceOnly
}

/// <summary>保存可版本化的本地模型、资源和字幕布局设置；运行中保持不可变。</summary>
public sealed record DesktopSettings
{
    /// <summary>初始化桌面默认设置，并让旧 JSON 缺省保留期采用三十天。</summary>
    /// <param name="historyRetentionDays">历史保留天数；零表示永久保留。</param>
    [JsonConstructor]
    public DesktopSettings(int historyRetentionDays = 30) => HistoryRetentionDays = historyRetentionDays;

    /// <summary>获取文件契约主版本。</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>获取桌面主题；旧配置默认跟随系统。</summary>
    public DesktopTheme Theme { get; init; } = DesktopTheme.System;
    /// <summary>获取历史保留天数；零表示永久保留，旧配置默认三十天。</summary>
    public int HistoryRetentionDays { get; init; } = 30;
    /// <summary>获取明确选择的翻译模型。</summary>
    public string TranslationModel { get; init; } = "translategemma:12b";
    /// <summary>获取识别语言策略。</summary>
    public string AsrLanguage { get; init; } = "auto";
    /// <summary>获取已有的隔离解释器路径。</summary>
    public string PythonPath { get; init; } = @"D:\Soft Ware\LiveLingo\runtimes\win-x64-cu130\Scripts\python.exe";
    /// <summary>获取固定 Qwen 权重目录。</summary>
    public string ModelPath { get; init; } = @"D:\Soft Ware\LiveLingo\models\Qwen3-ASR-1.7B-7278e1e";
    /// <summary>获取已有 Ollama 安装路径，允许位于 C 盘。</summary>
    public string OllamaPath { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
    /// <summary>获取既有翻译权重根目录。</summary>
    public string OllamaModelsPath { get; init; } = @"D:\Soft Ware\Ollama";
    /// <summary>获取音频溢写和应用数据根目录。</summary>
    public string DataPath { get; init; } = @"D:\Soft Ware\LiveLingo\data";
    /// <summary>获取子进程大型缓存根目录。</summary>
    public string CachePath { get; init; } = @"D:\Soft Ware\LiveLingo\cache";
    /// <summary>获取字幕左上角物理像素横坐标。</summary>
    public int OverlayX { get; init; } = 160;
    /// <summary>获取字幕左上角物理像素纵坐标。</summary>
    public int OverlayY { get; init; } = 650;
    /// <summary>获取字幕 DIP 宽度。</summary>
    public double OverlayWidth { get; init; } = 1000;
    /// <summary>获取字幕 DIP 高度。</summary>
    public double OverlayHeight { get; init; } = 260;
    /// <summary>获取中文正文字号。</summary>
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
        if (SchemaVersion != 1 || TranslationModel is not ("translategemma:4b" or "translategemma:12b") || AsrLanguage is not ("auto" or "Japanese"))
            throw new ArgumentException("设置版本、模型或语言不受支持。");
        if (!Enum.IsDefined(DisplayMode)) throw new ArgumentException("字幕显示模式不受支持。");
        if (!Enum.IsDefined(Theme)) throw new ArgumentException("界面主题不受支持。");
        if (HistoryRetentionDays is not (0 or 7 or 30 or 90)) throw new ArgumentException("历史保留期须为永久、7、30 或 90 天。");
        foreach (var path in new[] { PythonPath, ModelPath, OllamaModelsPath, DataPath, CachePath }) ValidateDataPath(path);
        if (string.IsNullOrWhiteSpace(OllamaPath) || !Path.IsPathFullyQualified(OllamaPath)) throw new ArgumentException("Ollama 需要绝对路径。");
        if (!double.IsFinite(OverlayWidth) || OverlayWidth is < 320 or > 3840 || !double.IsFinite(OverlayHeight) || OverlayHeight is < 120 or > 2160 ||
            !double.IsFinite(FontSize) || FontSize is < 16 or > 64 || !double.IsFinite(BackgroundOpacity) || BackgroundOpacity is < 0.1 or > 1)
            throw new ArgumentException("字幕尺寸、字号或背景不透明度超出范围。");
        var keys = new[] { StartKey, VisibilityKey, LayoutKey };
        if (keys.Any(x => x is < 1 or > 11) || keys.Distinct().Count() != 3) throw new ArgumentException("快捷键须选择三个不同的 Ctrl+Alt+F1–F11；F12 由系统保留。");
    }

    /// <summary>校验新增资源仅使用非 C 盘本地绝对路径。</summary><param name="path">待使用的路径。</param>
    /// <exception cref="ArgumentException">路径不是非 C 盘本地路径。</exception>
    public static void ValidateDataPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            Path.GetPathRoot(Path.GetFullPath(path))?.Equals(@"C:\", StringComparison.OrdinalIgnoreCase) == true)
            throw new ArgumentException("模型、隔离环境及数据缓存需要非 C 盘本地绝对路径。");
    }
}
