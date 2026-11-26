using System.Collections.Immutable;

namespace LiveLingo.Core.Transcription;

/// <summary>统一识别设置、Qwen 强制语言名称、界面显示和转写 API 的 ISO 639-1 代码。</summary>
public static class AsrLanguages
{
    /// <summary>获取新配置及旧版自动识别配置采用的明确语言。</summary>
    public const string Default = "Japanese";

    private static readonly (string Name, string Label, string Code)[] Options =
        [("Chinese", "中文", "zh"), ("English", "英语", "en"), (Default, "日语", "ja"), ("Korean", "韩语", "ko")];

    /// <summary>获取按界面顺序排列的配置值，也是传给 Qwen 的语言名称。</summary>
    public static ImmutableArray<string> Names { get; } = [.. Options.Select(option => option.Name)];

    /// <summary>检查是否指定了受支持的识别语言。</summary>
    /// <param name="name">设置、模型配置或识别请求中的规范名称。</param><returns>名称精确匹配时为真。</returns>
    public static bool IsSupported(string? name) => name is not null && Names.Contains(name);

    /// <summary>将已选语言转换成转写 API 需要的 ISO 639-1 代码。</summary>
    /// <param name="name">规范语言名称。</param><returns>两位小写语言代码。</returns>
    /// <exception cref="ArgumentException">名称不属于支持的识别语言。</exception>
    public static string ToApiCode(string name) => Options[GetIndex(name)].Code;

    /// <summary>获取已选识别语言的中文显示名。</summary>
    /// <param name="name">规范语言名称。</param><returns>供用户核对的语言名。</returns>
    /// <exception cref="ArgumentException">名称不属于支持的识别语言。</exception>
    public static string DisplayName(string name) => Options[GetIndex(name)].Label;

    /// <summary>定位配置名对应的条目，拒绝自动检测或未知语言。</summary>
    /// <param name="name">待查规范名称。</param><returns>条目在选项表内的位置。</returns>
    /// <exception cref="ArgumentException">未提供支持的名称。</exception>
    private static int GetIndex(string name)
    {
        var index = Names.IndexOf(name);
        return index >= 0 ? index : throw new ArgumentException("请选择中文、英语、日语或韩语作为识别语言。", nameof(name));
    }
}
