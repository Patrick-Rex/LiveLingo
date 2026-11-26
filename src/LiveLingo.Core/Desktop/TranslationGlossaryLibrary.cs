using System.Collections.Immutable;
using LiveLingo.Core.Transcription;

namespace LiveLingo.Core.Desktop;

/// <summary>保存一份可以独立编辑和选用的原文与特定目标语言的对照表。</summary>
/// <param name="Name">用于在桌面设置中切换的名称。</param>
/// <param name="Text">每行一条原文与指定译文的对照。</param>
/// <param name="LocalOnly">仅在翻译来源为本地模型时使用此表；旧表保留原有使用范围。</param>
/// <param name="TargetLanguage">词条译文使用的语言，旧词表默认为简体中文。</param>
public sealed record TranslationGlossaryProfile(string Name, string Text, bool LocalOnly = false, string TargetLanguage = AsrLanguages.DefaultTarget);

/// <summary>保存词库开关、选择及各表正文，关闭后仍保留用户的选择和编辑。</summary>
public sealed record TranslationGlossaryLibrary
{
    /// <summary>获取同一配置可保存的词库数量上限。</summary>
    public const int MaximumProfiles = 8;

    /// <summary>获取是否在下次翻译会话使用选中的词库。</summary>
    public bool Enabled { get; init; }

    /// <summary>获取当前选中的词库位置，关闭时仍保留。</summary>
    public int SelectedIndex { get; init; }

    /// <summary>获取按界面次序保存的词库，首次使用时提供一份可修改的示例。</summary>
    public ImmutableArray<TranslationGlossaryProfile> Profiles { get; init; } =
        [new("游戏与网络用语", "神ゲー=神作\n初見プレイ=初见游玩\nナイスプレイ！=打得漂亮！")];

    /// <summary>检查选择、唯一名称和每份词库的格式，以便切换后能独立使用。</summary>
    /// <exception cref="ArgumentException">选择或词库格式无效，消息指明需要修正的位置。</exception>
    public void Validate()
    {
        if (Profiles.IsDefaultOrEmpty || Profiles.Length > MaximumProfiles)
            throw new ArgumentException($"请保留 1 至 {MaximumProfiles} 份词库。");
        if (SelectedIndex < 0 || SelectedIndex >= Profiles.Length)
            throw new ArgumentException("请选择已有词库。");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < Profiles.Length; index++)
        {
            var profile = Profiles[index];
            if (profile is null || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 32 ||
                profile.Name.Trim() != profile.Name || profile.Name.Any(char.IsControl))
                throw new ArgumentException($"第 {index + 1} 份词库需要 1 至 32 个字符的名称，首尾不能有空白。");
            if (!names.Add(profile.Name))
                throw new ArgumentException($"词库名“{profile.Name}”已被使用。");
            if (!AsrLanguages.IsSupported(profile.TargetLanguage)) throw new ArgumentException($"请选择词库“{profile.Name}”的译文语言。");
            try { TranslationGlossary.Parse(profile.Text); }
            catch (ArgumentException error) { throw new ArgumentException($"词库“{profile.Name}”：{error.Message}", error); }
        }
    }

    /// <summary>逐项比较词库内容，使加载后的配置和已撤销的草稿可按值比较。</summary>
    /// <param name="other">另一份词库配置。</param><returns>开关、选择、顺序及词条均一致时为真。</returns>
    public bool Equals(TranslationGlossaryLibrary? other) =>
        ReferenceEquals(this, other) || other is not null && Enabled == other.Enabled &&
        SelectedIndex == other.SelectedIndex && Profiles.AsSpan().SequenceEqual(other.Profiles.AsSpan());

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Enabled);
        hash.Add(SelectedIndex);
        foreach (var profile in Profiles.AsSpan()) hash.Add(profile);
        return hash.ToHashCode();
    }
}
