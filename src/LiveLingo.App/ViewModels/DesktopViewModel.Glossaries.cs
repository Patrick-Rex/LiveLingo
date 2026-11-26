using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveLingo.Core.Desktop;

namespace LiveLingo.App.ViewModels;

/// <summary>管理原文到中文的词库草稿，与模型来源共用应用和撤销边界。</summary>
public sealed partial class DesktopViewModel
{
    private TranslationGlossaryLibrary glossaryDraft = new();
    private bool loadingGlossaries;

    /// <summary>获取各命名词表的编辑项，切换时保留所有未提交的文字。</summary>
    public ObservableCollection<GlossaryProfileEditor> GlossaryProfiles { get; } = [];
    /// <summary>获取或设置下一场翻译是否应用选中的词表。</summary>
    [ObservableProperty] public partial bool GlossaryEnabled { get; set; }
    /// <summary>获取或设置当前编辑和使用的词表。</summary>
    [ObservableProperty] public partial GlossaryProfileEditor? SelectedGlossary { get; set; }
    /// <summary>获取全部词表校验后的错误提示。</summary>
    [ObservableProperty] public partial string GlossaryError { get; private set; } = "";
    /// <summary>获取词表库是否允许增加一份自定义表。</summary>
    public bool CanAddGlossary => GlossaryProfiles.Count < TranslationGlossaryLibrary.MaximumProfiles;
    /// <summary>获取删除当前表后是否仍有可供选择的词表。</summary>
    public bool CanRemoveGlossary => GlossaryProfiles.Count > 1;

    /// <summary>启用状态改变时保留选表和词条，重新检查运行草稿。</summary><param name="value">新的开关状态。</param>
    partial void OnGlossaryEnabledChanged(bool value) => UpdateGlossaryDraft();
    /// <summary>切换编辑项后刷新待应用的选中身份。</summary><param name="value">选中的词表。</param>
    partial void OnSelectedGlossaryChanged(GlossaryProfileEditor? value) => UpdateGlossaryDraft();

    /// <summary>将已保存的库回填到编辑器，整个列表准备好后再发送一次草稿更新。</summary>
    private void LoadGlossaryLibrary()
    {
        loadingGlossaries = true;
        try
        {
            glossaryDraft = settings.GlossaryLibrary;
            GlossaryEnabled = glossaryDraft.Enabled;
            GlossaryProfiles.Clear();
            foreach (var profile in glossaryDraft.Profiles)
                GlossaryProfiles.Add(new(profile, UpdateGlossaryDraft));
            SelectedGlossary = GlossaryProfiles[glossaryDraft.SelectedIndex];
        }
        finally { loadingGlossaries = false; }
        UpdateGlossaryDraft();
    }

    /// <summary>按当前名字分配可编辑的空白词表，不覆盖已有内容。</summary>
    [RelayCommand]
    private void AddGlossary()
    {
        if (!CanEdit || !CanAddGlossary) return;
        var index = 1;
        while (GlossaryProfiles.Any(profile => profile.Name == $"自定义词表 {index}")) index++;
        var added = new GlossaryProfileEditor(new($"自定义词表 {index}", ""), UpdateGlossaryDraft);
        GlossaryProfiles.Add(added);
        SelectedGlossary = added;
    }

    /// <summary>从草稿中移除当前词表并选择相邻表，可在保存前撤销。</summary>
    [RelayCommand]
    private void RemoveGlossary()
    {
        if (!CanEdit || !CanRemoveGlossary || SelectedGlossary is null) return;
        var index = GlossaryProfiles.IndexOf(SelectedGlossary);
        GlossaryProfiles.RemoveAt(index);
        SelectedGlossary = GlossaryProfiles[Math.Min(index, GlossaryProfiles.Count - 1)];
    }

    /// <summary>生成此次编辑的不可变设置，并显示所有词表的第一处校验错误。</summary>
    private void UpdateGlossaryDraft()
    {
        if (loadingGlossaries || SelectedGlossary is null) return;
        glossaryDraft = glossaryDraft with
        {
            Enabled = GlossaryEnabled, SelectedIndex = GlossaryProfiles.IndexOf(SelectedGlossary),
            Profiles = [.. GlossaryProfiles.Select(profile => new TranslationGlossaryProfile(profile.Name, profile.Text, profile.LocalOnly))]
        };
        try { glossaryDraft.Validate(); GlossaryError = ""; }
        catch (ArgumentException error) { GlossaryError = error.Message; }
        SettingsSaveMessage = "";
        OnPropertyChanged(nameof(CanAddGlossary)); OnPropertyChanged(nameof(CanRemoveGlossary));
        NotifyPendingSettings();
    }
}

/// <summary>保留选表控件中的行身份，改名和编辑词条时同步刷新同一份草稿。</summary>
/// <param name="profile">该表已保存或新建的初始内容。</param><param name="changed">检查全部草稿并通知保存状态。</param>
public sealed partial class GlossaryProfileEditor(TranslationGlossaryProfile profile, Action changed) : ObservableObject
{
    /// <summary>获取或设置列表中显示的词表名称。</summary>
    [ObservableProperty] public partial string Name { get; set; } = profile.Name;
    /// <summary>获取或设置可保留空行的原文与中文译文对照表。</summary>
    [ObservableProperty] public partial string Text { get; set; } = profile.Text;
    /// <summary>获取或设置该表是否仅在本地翻译时参与匹配。</summary>
    [ObservableProperty] public partial bool LocalOnly { get; set; } = profile.LocalOnly;
    /// <summary>通知库重新检查名字的唯一性。</summary><param name="value">编辑后的名称。</param>
    partial void OnNameChanged(string value) => changed();
    /// <summary>通知库检查词条格式和重复原文。</summary><param name="value">编辑后的完整词表。</param>
    partial void OnTextChanged(string value) => changed();
    /// <summary>将本表的使用范围纳入待应用配置。</summary><param name="value">是否限于本地翻译。</param>
    partial void OnLocalOnlyChanged(bool value) => changed();
}
