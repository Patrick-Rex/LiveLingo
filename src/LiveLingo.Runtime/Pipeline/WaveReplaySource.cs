using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using LiveLingo.Core.Audio;

namespace LiveLingo.Runtime.Pipeline;

/// <summary>从实际 PCM WAV 按墙钟回放，不用加速时间冒充持续运行。</summary>
public static class WaveReplaySource
{
    /// <summary>流式读取有限文件，必要时明确循环；有状态转换跨包与循环保持连续。</summary>
    /// <param name="path">绝对 WAV 路径。</param><param name="session">本次会话。</param>
    /// <param name="durationSeconds">零表示完整一遍，正数表示按指定墙钟时长循环。</param>
    /// <param name="realtime">按真实音频时间节奏输入；false 仅用于明确压力测试。</param>
    /// <param name="cancellationToken">停止读取。</param><returns>完整来源范围的 16k 输入。</returns>
    public static async IAsyncEnumerable<PipelineInput> ReadAsync(string path, Guid session, double durationSeconds, bool realtime,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(path) || durationSeconds < 0 || durationSeconds > 86400) throw new ArgumentException("回放路径或时长无效。");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var (format, offset, length) = ReadHeader(stream);
        var framesInFile = length / format.BlockAlign;
        var framesTotal = durationSeconds == 0 ? framesInFile : checked((long)Math.Ceiling(durationSeconds * format.SampleRate));
        var converter = new StreamingPcmConverter(format, session, 1);
        long frame = 0, sequence = 0;
        var started = Stopwatch.GetTimestamp();
        while (frame < framesTotal)
        {
            var position = frame % framesInFile;
            if (position == 0) stream.Position = offset;
            var count = checked((int)Math.Min(Math.Min(format.SampleRate / 50, framesInFile - position), framesTotal - frame));
            var bytes = ArrayPool<byte>.Shared.Rent(count * format.BlockAlign);
            AudioPacket? packet = null;
            try
            {
                await stream.ReadExactlyAsync(bytes.AsMemory(0, count * format.BlockAlign), cancellationToken).ConfigureAwait(false);
                packet = new(bytes, session, 1, ++sequence, frame, count, format, PacketFlags.None, 0, 0, 0);
                var audio = converter.Convert(packet);
                frame += count;
                if (realtime)
                {
                    var wait = TimeSpan.FromSeconds((double)frame / format.SampleRate) - Stopwatch.GetElapsedTime(started);
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }
                if (audio.Samples.Length != 0) yield return new PipelineAudio(1, audio);
            }
            finally { if (packet is null) ArrayPool<byte>.Shared.Return(bytes); else packet.Dispose(); }
        }
        var tail = converter.Flush();
        if (tail.Samples.Length != 0) yield return new PipelineAudio(1, tail);
    }

    /// <summary>检查 RIFF/PCM 格式及实际数据边界，不加载整段录音。</summary>
    /// <param name="stream">可定位文件流。</param><returns>实际格式、数据位置及字节长度。</returns>
    private static (AudioFormat Format, long Offset, long Length) ReadHeader(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (stream.Length < 44 || reader.ReadUInt32() != 0x46464952) throw new InvalidDataException("需要 RIFF PCM WAV。");
        var size = reader.ReadUInt32();
        if (size + 8L != stream.Length || reader.ReadUInt32() != 0x45564157) throw new InvalidDataException("WAV 长度不完整。");
        AudioFormat? format = null;
        while (stream.Position + 8 <= stream.Length)
        {
            var kind = reader.ReadUInt32(); var length = reader.ReadUInt32(); var end = stream.Position + length;
            if (end > stream.Length) throw new InvalidDataException("WAV 块越界。");
            if (kind == 0x20746d66)
            {
                if (length < 16 || reader.ReadUInt16() != 1) throw new InvalidDataException("仅支持 PCM16 WAV。");
                var channels = reader.ReadUInt16(); var rate = reader.ReadUInt32(); var bytesPerSecond = reader.ReadUInt32();
                var align = reader.ReadUInt16(); var bits = reader.ReadUInt16();
                if (bits != 16 || rate is not (16000 or 44100 or 48000) || channels is 0 or > 32 || align != channels * 2 || bytesPerSecond != rate * align)
                    throw new InvalidDataException("WAV 格式与支持的 PCM 配置不一致。");
                format = new(checked((int)rate), channels);
            }
            else if (kind == 0x61746164)
            {
                if (format is null || length == 0 || length % format.BlockAlign != 0) throw new InvalidDataException("WAV 数据未对齐或缺少格式。");
                return (format, stream.Position, length);
            }
            stream.Position = end + length % 2;
        }
        throw new InvalidDataException("WAV 缺少音频数据。");
    }
}
