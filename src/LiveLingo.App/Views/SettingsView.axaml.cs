using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using LiveLingo.App.ViewModels;
using LiveLingo.Core.Desktop;

namespace LiveLingo.App.Views;

/// <summary>承载设置分类、原生路径选择与不启动模型的资源存在检查。</summary>
public partial class SettingsView : UserControl
{
    /// <summary>加载设置页，运行状态由父级视图模型提供。</summary>
    public SettingsView() => AvaloniaXamlLoader.Load(this);

    /// <summary>让用户选择本地文件或目录，取消选择不改变设置草稿。</summary>
    /// <param name="sender">携带设置属性标识的浏览按钮。</param><param name="e">按钮点击事件。</param>
    private async void BrowsePath_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } || DataContext is not DesktopViewModel vm || !vm.CanEdit || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        try
        {
            IStorageItem? selected;
            if (key is nameof(vm.PythonPath) or nameof(vm.OllamaPath))
            {
                if (!topLevel.StorageProvider.CanOpen) throw new NotSupportedException("当前环境不支持文件选择，请直接填写路径。");
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = key == nameof(vm.PythonPath) ? "选择 Python 可执行文件" : "选择 Ollama 可执行文件",
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("可执行文件") { Patterns = ["*.exe"] }, FilePickerFileTypes.All]
                });
                selected = files.FirstOrDefault();
            }
            else
            {
                if (!topLevel.StorageProvider.CanPickFolder) throw new NotSupportedException("当前环境不支持目录选择，请直接填写路径。");
                selected = (await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择资源目录", AllowMultiple = false })).FirstOrDefault();
            }
            using (selected)
            {
                if (selected is null || !vm.CanEdit || !ReferenceEquals(DataContext, vm)) return;
                var path = selected.TryGetLocalPath() ?? throw new IOException("请选择本地文件或目录。");
                switch (key)
                {
                    case nameof(vm.PythonPath): vm.PythonPath = path; break;
                    case nameof(vm.ModelPath): vm.ModelPath = path; break;
                    case nameof(vm.OllamaPath): vm.OllamaPath = path; break;
                    case nameof(vm.OllamaModelsPath): vm.OllamaModelsPath = path; break;
                    case nameof(vm.DataPath): vm.DataPath = path; break;
                    case nameof(vm.CachePath): vm.CachePath = path; break;
                }
                this.FindControl<TextBlock>("PathCheckResult")!.Text = "路径已更新，请检查后应用。";
            }
        }
        catch (Exception error) { this.FindControl<TextBlock>("PathCheckResult")!.Text = "无法选择路径：" + error.Message; }
    }

    /// <summary>一次读取整体模型 JSON 文件，交给视图模型原子解析为待应用草稿。</summary>
    /// <param name="sender">导入按钮。</param><param name="e">点击事件。</param>
    private async void ImportModelConfiguration_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DesktopViewModel vm || !vm.CanEdit || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new()
            {
                Title = "导入整体模型配置", AllowMultiple = false,
                FileTypeFilter = [new("JSON 配置") { Patterns = ["*.json"] }]
            });
            if (files.Count == 0) return;
            using var file = files[0];
            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[32769];
            var count = await reader.ReadBlockAsync(buffer);
            if (count > 32768) throw new InvalidDataException("模型配置 JSON 超过 32 Ki 字符。");
            if (vm.CanEdit) vm.ImportModelConfiguration(new string(buffer, 0, count));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { vm.SettingsSaveMessage = "配置未导入：" + error.Message; }
    }

    /// <summary>在后台检查所选来源路径的格式及存在性，结果只对应本次输入快照。</summary>
    /// <param name="sender">检查按钮。</param><param name="e">按钮点击事件。</param>
    private async void CheckPaths_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DesktopViewModel vm || !vm.CanEdit) return;
        var button = this.FindControl<Button>("CheckPathsButton")!;
        var result = this.FindControl<TextBlock>("PathCheckResult")!;
        (string Label, string Path, bool IsFile, bool AllowSystemDrive)[] paths =
        [
            .. vm.IsLocalAsr ? new[] { ("Python", vm.PythonPath, true, false), ("Qwen 模型", vm.ModelPath, false, false) } : [],
            .. vm.IsLocalTranslation ? new[] { ("Ollama", vm.OllamaPath, true, true), ("Ollama 模型", vm.OllamaModelsPath, false, false) } : [],
            ("数据", vm.DataPath, false, false), ("缓存", vm.CachePath, false, false)
        ];
        button.IsEnabled = false; result.Text = "正在检查路径…";
        try { result.Text = await Task.Run(() => CheckPaths(paths)); }
        catch (Exception error) { result.Text = "路径检查失败：" + error.Message; }
        finally { button.IsEnabled = true; }
    }

    /// <summary>检查资源路径的基本约束与磁盘存在性，不读模型内容或启动服务。</summary>
    /// <param name="paths">在 UI 线程捕获的输入快照。</param><returns>逐项可读的检查结果。</returns>
    private static string CheckPaths((string Label, string Path, bool IsFile, bool AllowSystemDrive)[] paths)
    {
        var results = new List<string>(paths.Length + 1);
        foreach (var entry in paths)
        {
            try
            {
                if (entry.AllowSystemDrive)
                {
                    if (!Path.IsPathFullyQualified(entry.Path)) throw new ArgumentException("需要本地绝对路径。");
                }
                else DesktopSettings.ValidateDataPath(entry.Path);
                var exists = entry.IsFile ? File.Exists(entry.Path) : Directory.Exists(entry.Path);
                results.Add($"{entry.Label}：{(exists ? "已找到" : "未找到，请确认路径")}");
            }
            catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
            {
                results.Add($"{entry.Label}：{error.Message}");
            }
        }
        results.Add("检查后若修改路径，请重新检查；模型完整性在开始翻译时验证。");
        return string.Join(Environment.NewLine, results);
    }
}
