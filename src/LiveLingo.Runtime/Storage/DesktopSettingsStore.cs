using System.Text.Json;
using System.Text.Json.Serialization;
using LiveLingo.Core.Desktop;

namespace LiveLingo.Runtime.Storage;

/// <summary>在设置文件同目录写入临时文件，再原子替换唯一设置文件。</summary>
/// <param name="path">设置文件绝对路径。</param>
/// <param name="initialSettings">仅设置文件不存在时采用的安装渠道默认值；不改变已有或损坏文件的恢复规则。</param>
/// <param name="legacyPath">升级时可读取的旧版默认文件，仅在新位置尚无设置时使用。</param>
public sealed class DesktopSettingsStore(string path, DesktopSettings? initialSettings = null, string? legacyPath = null) : IDesktopSettingsStore
{
    private const int MaximumFileBytes = 256 * 1024;
    private readonly string path = ValidatePath(path);

    /// <inheritdoc />
    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var source = !File.Exists(path) && legacyPath is not null ? legacyPath : path;
            if (!File.Exists(source)) return new(initialSettings ?? new(), null);
            if (new FileInfo(source).Length > MaximumFileBytes) throw new InvalidDataException("设置文件超过 256 KiB。");
            await using var stream = File.OpenRead(source);
            var settings = await JsonSerializer.DeserializeAsync(stream, DesktopSettingsJsonContext.Default.DesktopSettings, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("设置为空。");
            settings.Validate();
            return new(settings, null);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new(new(), "设置未加载，原文件已保留：" + error.Message); }
    }

    /// <inheritdoc />
    public async Task SaveAsync(DesktopSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        var content = JsonSerializer.SerializeToUtf8Bytes(settings, DesktopSettingsJsonContext.Default.DesktopSettings);
        if (content.Length > MaximumFileBytes) throw new ArgumentException("设置文件超过 256 KiB，请缩短词库或运行配置。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>校验并规范化设置文件的写入位置。</summary><param name="value">绝对路径。</param><returns>规范化路径。</returns>
    private static string ValidatePath(string value) { DesktopSettings.ValidateDataPath(value); return Path.GetFullPath(value); }
}

/// <summary>生成稳定设置序列化元数据。</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DesktopSettings))]
internal partial class DesktopSettingsJsonContext : JsonSerializerContext;
