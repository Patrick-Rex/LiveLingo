using System.Runtime.InteropServices;

namespace LiveLingo.Runtime.Storage;

/// <summary>仅操作非 C 盘独占目录中由本实例创建的临时 PCM 文件。</summary>
public sealed class FileAudioSpillStore : IAudioSpillStore
{
    private readonly string directory;
    private readonly HashSet<string> owned = new(StringComparer.Ordinal);
    private bool disposed;
    private bool writeFaulted;

    /// <summary>建立不存在且不经过链接的会话专属目录。</summary>
    /// <param name="directory">绝对非 C 盘新目录。</param>
    /// <exception cref="ArgumentException">路径不满足所有权约束。</exception>
    /// <exception cref="IOException">目录已存在或无法创建。</exception>
    public FileAudioSpillStore(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || Path.GetPathRoot(directory)?.StartsWith("C:", StringComparison.OrdinalIgnoreCase) == true)
            throw new ArgumentException("临时音频需要绝对非 C 盘路径。", nameof(directory));
        this.directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (Directory.Exists(this.directory) || File.Exists(this.directory)) throw new IOException("临时目录必须独占且尚不存在。");
        for (var parent = new DirectoryInfo(this.directory); parent is not null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("临时目录不得经过链接。", nameof(directory));
        Directory.CreateDirectory(this.directory);
    }

    /// <inheritdoc />
    public string Write(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (writeFaulted) throw new IOException("先前溢写失败，本会话不能继续分配临时文件。");
        var key = Guid.NewGuid().ToString("N") + ".pcm";
        var path = Path.Combine(directory, key);
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            owned.Add(key);
            stream.Write(MemoryMarshal.AsBytes(samples));
        }
        catch (IOException) { writeFaulted = true; throw; }
        catch (UnauthorizedAccessException) { writeFaulted = true; throw; }
        return key;
    }

    /// <inheritdoc />
    public void Read(string key, Span<float> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!owned.Contains(key)) throw new ArgumentException("不属于本实例的临时音频。", nameof(key));
        using var stream = new FileStream(Path.Combine(directory, key), FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = MemoryMarshal.AsBytes(samples);
        if (stream.Length != bytes.Length) throw new IOException("临时音频长度变化。");
        stream.ReadExactly(bytes);
    }

    /// <inheritdoc />
    public void Delete(string key)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!owned.Contains(key)) throw new ArgumentException("不属于本实例的临时音频。", nameof(key));
        File.Delete(Path.Combine(directory, key));
        owned.Remove(key);
    }

    /// <summary>逐个删除已登记临时文件，陌生文件不会递归删除。</summary>
    /// <exception cref="AggregateException">一个或多个自有文件或目录无法清理。</exception>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        List<Exception> errors = [];
        foreach (var key in owned)
        {
            try { File.Delete(Path.Combine(directory, key)); }
            catch (IOException error) { errors.Add(error); }
            catch (UnauthorizedAccessException error) { errors.Add(error); }
        }
        owned.Clear();
        try { Directory.Delete(directory, false); }
        catch (IOException error) { errors.Add(error); }
        catch (UnauthorizedAccessException error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("临时音频未全部清理。", errors);
    }
}
