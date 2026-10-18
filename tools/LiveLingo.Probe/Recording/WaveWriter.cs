using System.Buffers.Binary;
using LiveLingo.Core.Audio;

namespace LiveLingo.Probe.Recording;

/// <summary>独立消费者写入 PCM16 WAV；释放时即使运行失败也完成已写数据的头部。</summary>
internal sealed class WaveWriter : IDisposable
{
    private readonly FileStream stream;
    private readonly AudioFormat format;
    private bool disposed;

    /// <summary>使用 CreateNew 拒绝覆盖任何已存在文件。</summary>
    /// <param name="path">独立运行内的 WAV 路径。</param><param name="format">初始化格式。</param>
    public WaveWriter(string path, AudioFormat format)
    {
        this.format = format;
        stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.SequentialScan);
        try { stream.Write(new byte[44]); }
        catch { stream.Dispose(); throw; }
    }

    /// <summary>获取已成功写入的 PCM 字节数。</summary>
    public long Bytes { get; private set; }

    /// <summary>写入当前所有权有效期内的 PCM，不在采集线程执行。</summary>
    /// <param name="pcm">PCM16 little-endian 内容。</param>
    /// <exception cref="IOException">文件或 RIFF 长度超限。</exception>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (Bytes + pcm.Length > uint.MaxValue - 36L) throw new IOException("WAV 超过 RIFF 上限。");
        stream.Write(pcm);
        Bytes += pcm.Length;
    }

    /// <summary>写回实际 RIFF/PCM 长度，并关闭录音句柄。</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            Span<byte> header = stackalloc byte[44];
            "RIFF"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)(Bytes + 36)));
            "WAVEfmt "u8.CopyTo(header[8..]);
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
            BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)format.Channels);
            BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)format.SampleRate);
            BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)(format.SampleRate * format.BlockAlign));
            BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)format.BlockAlign);
            BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
            "data"u8.CopyTo(header[36..]);
            BinaryPrimitives.WriteUInt32LittleEndian(header[40..], checked((uint)Bytes));
            stream.Position = 0;
            stream.Write(header);
            stream.Flush(true);
        }
        finally { stream.Dispose(); }
    }
}
