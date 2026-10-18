using System.Buffers.Binary;
using System.Text;
using LiveLingo.Core.Subtitles;
using LiveLingo.Runtime.Asr;

namespace LiveLingo.Runtime.Tests.Asr;

/// <summary>验证实际编码后的范围、部分读取、损坏前缀和请求关联。</summary>
public sealed class IpcProtocolTests
{
    /// <summary>跨多次短读保持请求身份和逻辑/实际窗口的不同范围。</summary>
    /// <returns>编码与解码任务。</returns>
    [Fact]
    public async Task CorrelationSurvivesShortReads()
    {
        var request = new AsrRequest(new(Guid.NewGuid(), 7, PipelineRequestKind.Recognition, new(Guid.NewGuid(), 2, 3, 4),
            new(Guid.NewGuid(), 2), new(0, 640000), 8), new(160000, 640000), 170000, "auto", 512);
        var header = new IpcHeader { Type = "Recognize", WorkerEpoch = 3, Correlation = request };
        using var bytes = new MemoryStream();
        await IpcProtocol.WriteAsync(bytes, new(header, []), CancellationToken.None);
        using var chunks = new ShortReadStream(bytes.ToArray());
        var frame = await IpcProtocol.ReadAsync(chunks, CancellationToken.None);
        Assert.Equal(header, frame.Header);
        Assert.Empty(frame.Payload);
        Assert.Equal(480000, frame.Header.Correlation!.WindowRange.Length);
        Assert.Equal(640000, frame.Header.Correlation.Ticket.Range.Length);
    }

    /// <summary>损坏长度在读取正文前拒绝，未知版本、重复键及不完整 EOF 均失败。</summary>
    /// <returns>检查有限异常的任务。</returns>
    [Fact]
    public async Task CorruptFramesCannotContinue()
    {
        var oversized = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(oversized, uint.MaxValue);
        await Assert.ThrowsAsync<InvalidDataException>(() => IpcProtocol.ReadAsync(new MemoryStream(oversized), CancellationToken.None));
        await Assert.ThrowsAsync<EndOfStreamException>(() => IpcProtocol.ReadAsync(new MemoryStream([1, 0, 0]), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => IpcProtocol.ReadAsync(Raw("{\"version\":2,\"type\":\"Shutdown\",\"workerEpoch\":1}"), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => IpcProtocol.ReadAsync(Raw("{\"version\":1,\"version\":1,\"type\":\"Shutdown\",\"workerEpoch\":1}"), CancellationToken.None));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => IpcProtocol.ReadAsync(Raw("{\"version\":1,\"type\":\"Shutdown\",\"workerEpoch\":1,\"unknown\":1}"), CancellationToken.None));
    }

    /// <summary>静音和微小非零采样原样往返，错误范围和非有限值被拒绝。</summary>
    /// <returns>音频编码检查任务。</returns>
    [Fact]
    public async Task AudioLengthAndFiniteSamplesAreEnforced()
    {
        var header = new IpcHeader { Type = "AudioChunk", WorkerEpoch = 1, SessionId = Guid.NewGuid(), CaptureEpoch = 1, FrameSequence = 1, StartSample = 0, EndSample = 2 };
        var payload = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(4), float.Epsilon);
        using var bytes = new MemoryStream();
        await IpcProtocol.WriteAsync(bytes, new(header, payload), CancellationToken.None);
        bytes.Position = 0;
        Assert.Equal(payload, (await IpcProtocol.ReadAsync(bytes, CancellationToken.None)).Payload);
        Assert.Throws<InvalidDataException>(() => IpcProtocol.Validate(header with { EndSample = 3 }, 8));
        BinaryPrimitives.WriteSingleLittleEndian(payload, float.NaN);
        await Assert.ThrowsAsync<InvalidDataException>(() => IpcProtocol.WriteAsync(new MemoryStream(), new(header, payload), CancellationToken.None));
    }

    /// <summary>构造需要经过真实解析的有限原始帧。</summary><param name="header">JSON 头。</param><returns>独立内存流。</returns>
    private static MemoryStream Raw(string header)
    {
        var bytes = Encoding.UTF8.GetBytes(header);
        var result = new byte[8 + bytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)bytes.Length);
        bytes.CopyTo(result, 8);
        return new(result);
    }

    /// <summary>模拟操作系统管道只返回少量字节，不改变其余流语义。</summary>
    private sealed class ShortReadStream : MemoryStream
    {
        /// <summary>绑定编码后的字节。</summary><param name="bytes">完整合法帧。</param>
        public ShortReadStream(byte[] bytes) : base(bytes) { }
        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
    }
}
