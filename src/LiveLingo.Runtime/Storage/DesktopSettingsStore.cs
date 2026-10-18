using System.Text.Json;
using System.Text.Json.Serialization;
using LiveLingo.Core.Desktop;

namespace LiveLingo.Runtime.Storage;

/// <summary>在非 C 盘同目录写入临时文件，再原子替换唯一设置文件。</summary>
/// <param name="path">设置文件绝对路径。</param>
public sealed class DesktopSettingsStore(string path) : IDesktopSettingsStore
{
    private readonly string path = ValidatePath(path);

    /// <inheritdoc />
    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(path)) return new(new(), null);
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("设置文件过大。");
            await using var stream = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync(stream, DesktopSettingsJsonContext.Default.DesktopSettings, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("设置为空。");
            settings.Validate();
            return new(settings, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new(new(), "设置未加载，原文件已保留：" + error.Message); }
    }

    /// <inheritdoc />
    public async Task SaveAsync(DesktopSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, DesktopSettingsJsonContext.Default.DesktopSettings, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>先验证写入位置，避免读取失败后默默回退系统盘。</summary><param name="value">绝对路径。</param><returns>规范化路径。</returns>
    private static string ValidatePath(string value) { DesktopSettings.ValidateDataPath(value); return Path.GetFullPath(value); }
}

/// <summary>生成稳定设置序列化元数据。</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DesktopSettings))]
internal partial class DesktopSettingsJsonContext : JsonSerializerContext;
