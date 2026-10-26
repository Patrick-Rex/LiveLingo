using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using LiveLingo.Core.History;

namespace LiveLingo.App.Services;

/// <summary>在所属控制窗内确认删除，使用系统文件选择器和剪贴板交付文本。</summary>
/// <param name="owner">拥有交互的控制窗。</param>
public sealed class HistoryInteraction(Window owner) : IHistoryInteraction
{
    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new Window
        {
            Title = "删除翻译记录", Width = 420, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = owner.ActualThemeVariant
        };
        var cancel = new Button { Content = "取消", IsDefault = true, IsCancel = true, MinWidth = 80 };
        var confirm = new Button { Content = "删除", MinWidth = 80 };
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 20,
            Children =
            {
                new TextBlock { Text = message + "\n删除后无法撤销。", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, confirm } }
            }
        };
        using var registration = cancellationToken.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => dialog.Close(false)));
        return await dialog.ShowDialog<bool>(owner);
    }

    /// <inheritdoc />
    public async Task CopyAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clipboard = owner.Clipboard ?? throw new InvalidOperationException("当前窗口无法访问剪贴板。");
        await clipboard.SetTextAsync(text);
    }

    /// <inheritdoc />
    public async Task<bool> SaveAsync(string text, string suggestedName, HistoryExportFormat format, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = format == HistoryExportFormat.Markdown ? "md" : "txt";
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出翻译记录", SuggestedFileName = suggestedName + "." + extension,
            DefaultExtension = extension, ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(extension == "md" ? "Markdown" : "纯文本") { Patterns = ["*." + extension] }]
        });
        if (file is null) return false;
        using (file)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.TryGetLocalPath() is { } path)
            {
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(temporary, path, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            else
            {
                await using var stream = await file.OpenWriteAsync();
                if (stream.CanSeek) stream.SetLength(0);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }
        return true;
    }
}
