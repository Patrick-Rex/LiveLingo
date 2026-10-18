using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveLingo.Core.Audio;
using LiveLingo.Core.Subtitles;

namespace LiveLingo.Runtime.Asr;

/// <summary>识别请求区分完整逻辑块与本次真实模型窗口，不伪造逐字覆盖。</summary>
/// <param name="Ticket">完整逻辑调用身份。</param><param name="WindowRange">本次实际传入模型的范围。</param>
/// <param name="BodyStartSample">窗口内正文锚点。</param><param name="Language">auto 或明确 Japanese。</param>
/// <param name="MaxNewTokens">有限输出预算。</param>
public sealed record AsrRequest(PipelineRequest Ticket, SampleRange WindowRange, long BodyStartSample, string Language, int MaxNewTokens);

/// <summary>有限 IPC 消息头；请求结果必须原样回显 Correlation。</summary>
public sealed record IpcHeader
{
    /// <summary>获取协议主版本。</summary>
    [JsonRequired]
    public int Version { get; init; } = 1;
    /// <summary>获取有限消息类型。</summary>
    public required string Type { get; init; }
    /// <summary>获取进程实例代次。</summary>
    public required long WorkerEpoch { get; init; }
    /// <summary>获取音频所属会话。</summary>
    public Guid? SessionId { get; init; }
    /// <summary>获取音频采集代次。</summary>
    public long? CaptureEpoch { get; init; }
    /// <summary>获取音频传输单调序号。</summary>
    public long? FrameSequence { get; init; }
    /// <summary>获取音频起点或可释放位置。</summary>
    public long? StartSample { get; init; }
    /// <summary>获取音频终点。</summary>
    public long? EndSample { get; init; }
    /// <summary>获取请求完整关联，不从响应文本推断身份。</summary>
    public AsrRequest? Correlation { get; init; }
    /// <summary>获取完整原始文本。</summary>
    public string? Text { get; init; }
    /// <summary>获取模型检测语言。</summary>
    public string? Language { get; init; }
    /// <summary>获取实际生成正常完成状态。</summary>
    public bool? Complete { get; init; }
    /// <summary>获取模型终止原因或无正文错误码。</summary>
    public string? Reason { get; init; }
    /// <summary>获取实际推理耗时。</summary>
    public double? ElapsedSeconds { get; init; }
    /// <summary>获取心跳关联值。</summary>
    public string? Nonce { get; init; }
    /// <summary>获取握手中的实际模型和环境信息。</summary>
    public WorkerInformation? Information { get; init; }
}

/// <summary>工作进程加载后的实际环境，不用进程存活替代模型就绪。</summary>
/// <param name="Python">解释器版本。</param><param name="Torch">包版本。</param><param name="Cuda">运行库版本。</param>
/// <param name="ModelRevision">固定模型 revision。</param><param name="Device">实际计算设备。</param>
public sealed record WorkerInformation(string Python, string Torch, string Cuda, string ModelRevision, string Device);

/// <summary>拥有独立有限字节缓冲的 IPC 帧。</summary>
/// <param name="Header">经过结构校验的消息头。</param><param name="Payload">仅 AudioChunk 可非空的 f32le 数据。</param>
public sealed record IpcFrame(IpcHeader Header, byte[] Payload);

/// <summary>严格长度前缀帧；调用方必须保证单 writer，损坏流不可继续复用。</summary>
public static class IpcProtocol
{
    /// <summary>最大 JSON 头字节数。</summary>
    public const int MaximumHeaderBytes = 65536;
    /// <summary>最大单帧音频字节数。</summary>
    public const int MaximumPayloadBytes = 16777216;
    private static readonly HashSet<string> Types = ["Hello", "Ready", "Failed", "AudioChunk", "Recognize", "Transcription", "ReleaseBefore", "ResetAudio", "Cancel", "Cancelled", "Ping", "Pong", "Error", "Shutdown", "Stopped"];

    /// <summary>验证并逐段写入一帧；不允许多个调用同时交错写入。</summary>
    /// <param name="stream">宿主管道。</param><param name="frame">完整帧。</param><param name="cancellationToken">写入取消。</param>
    /// <returns>完整帧提交任务。</returns>
    public static async Task WriteAsync(Stream stream, IpcFrame frame, CancellationToken cancellationToken)
    {
        Validate(frame.Header, frame.Payload.Length);
        ValidatePayload(frame);
        var header = JsonSerializer.SerializeToUtf8Bytes(frame.Header, IpcJsonContext.Default.IpcHeader);
        if (header.Length > MaximumHeaderBytes) throw new InvalidDataException("ipc_header_capacity");
        var prefix = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)header.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), (uint)frame.Payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (frame.Payload.Length != 0) await stream.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>在分配前验证长度，允许合法分段读取，EOF 均使 worker 失效。</summary>
    /// <param name="stream">宿主管道。</param><param name="cancellationToken">读取取消。</param><returns>完整已验证帧。</returns>
    public static async Task<IpcFrame> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[8];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(4));
        if (headerLength is 0 or > MaximumHeaderBytes || payloadLength > MaximumPayloadBytes) throw new InvalidDataException("ipc_frame_capacity");
        var headerBytes = new byte[headerLength];
        await stream.ReadExactlyAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        using (var document = JsonDocument.Parse(headerBytes, new() { MaxDepth = 16 })) CheckUniqueProperties(document.RootElement);
        var header = JsonSerializer.Deserialize(headerBytes, IpcJsonContext.Default.IpcHeader) ?? throw new InvalidDataException("ipc_missing_header");
        Validate(header, (int)payloadLength);
        var payload = new byte[payloadLength];
        if (payloadLength != 0) await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        var frame = new IpcFrame(header, payload);
        ValidatePayload(frame);
        return frame;
    }

    /// <summary>校验消息类型及各类最少关联字段，不接受默认身份和错误音频长度。</summary>
    /// <param name="header">待校验头。</param><param name="payloadLength">前缀声明的实际长度。</param>
    public static void Validate(IpcHeader header, int payloadLength)
    {
        if (header.Version != 1 || !Types.Contains(header.Type) || header.WorkerEpoch <= 0) throw new InvalidDataException("ipc_identity_or_version");
        if (payloadLength < 0 || payloadLength > MaximumPayloadBytes || header.Type != "AudioChunk" && payloadLength != 0) throw new InvalidDataException("ipc_payload_type");
        if (header.Text?.Length > 12000 || header.Reason?.Length > 256 || header.Nonce?.Length > 128 || header.Language?.Length > 128) throw new InvalidDataException("ipc_text_capacity");
        if (header.Type is "AudioChunk" or "ReleaseBefore" or "ResetAudio")
        {
            if (header.SessionId is null || header.SessionId == Guid.Empty || header.CaptureEpoch is null or <= 0 || header.StartSample is null or < 0)
                throw new InvalidDataException("ipc_audio_identity");
        }
        if (header.Type == "AudioChunk" && (header.EndSample is null || header.EndSample <= header.StartSample || header.FrameSequence is null or <= 0 ||
            header.EndSample - header.StartSample > MaximumPayloadBytes / 4 || payloadLength != (header.EndSample - header.StartSample) * 4))
            throw new InvalidDataException("ipc_audio_range");
        if (header.Type is "Recognize" or "Transcription" or "Cancel" or "Cancelled")
        {
            var request = header.Correlation ?? throw new InvalidDataException("ipc_missing_correlation");
            var ticket = request.Ticket;
            ticket.Identity.Validate();
            if (ticket.Id == Guid.Empty || ticket.Sequence <= 0 || ticket.Kind != PipelineRequestKind.Recognition ||
                ticket.Segment.Id == Guid.Empty || ticket.Segment.Generation <= 0 || ticket.SourceRevision < 0 ||
                ticket.Identity.WorkerEpoch != header.WorkerEpoch || !ticket.Range.Contains(request.WindowRange) ||
                request.WindowRange.Length is <= 0 or > 1920000 || request.BodyStartSample < request.WindowRange.Start ||
                request.BodyStartSample >= request.WindowRange.End || request.Language is not ("auto" or "Japanese") || request.MaxNewTokens is < 1 or > 2048)
                throw new InvalidDataException("ipc_request_metadata");
        }
        if (header.Type == "Transcription" && (header.Text is null || header.Complete is null || header.Reason is null ||
            header.ElapsedSeconds is null || !double.IsFinite(header.ElapsedSeconds.Value) || header.ElapsedSeconds < 0)) throw new InvalidDataException("ipc_transcription_metadata");
        if (header.Type is "Hello" or "Ready" && header.Information is null) throw new InvalidDataException("ipc_missing_information");
        if (header.Type is "Failed" or "Error" && string.IsNullOrWhiteSpace(header.Reason)) throw new InvalidDataException("ipc_missing_failure");
        if (header.Type is "Ping" or "Pong" && string.IsNullOrWhiteSpace(header.Nonce)) throw new InvalidDataException("ipc_missing_nonce");
    }

    /// <summary>拒绝非有限浮点音频，防止异常数值污染推理。</summary>
    /// <param name="frame">完整帧。</param>
    private static void ValidatePayload(IpcFrame frame)
    {
        for (var index = 0; index < frame.Payload.Length; index += 4)
            if (!float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(frame.Payload.AsSpan(index, 4)))) throw new InvalidDataException("ipc_nonfinite_audio");
    }

    /// <summary>拒绝重复 JSON 键，避免双方解析器选择不同字段。</summary>
    /// <param name="value">有限深度的 JSON 值。</param>
    private static void CheckUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("ipc_duplicate_property"); CheckUniqueProperties(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) CheckUniqueProperties(child);
    }
}

/// <summary>固定 IPC JSON 字段名、未知字段拒绝与只读计算属性排除。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    IgnoreReadOnlyProperties = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16,
    Converters = [typeof(SampleRangeJsonConverter)])]
[JsonSerializable(typeof(IpcHeader))]
internal partial class IpcJsonContext : JsonSerializerContext;

/// <summary>只序列化范围端点，并强制通过范围构造校验，不序列化计算长度。</summary>
internal sealed class SampleRangeJsonConverter : JsonConverter<SampleRange>
{
    /// <inheritdoc />
    public override SampleRange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("range_object_required");
        long? start = null, end = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("range_property_required");
            var property = reader.GetString();
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var value)) throw new JsonException("range_integer_required");
            if (property == "start" && start is null) start = value;
            else if (property == "end" && end is null) end = value;
            else throw new JsonException("range_unknown_or_duplicate_property");
        }
        if (start is null || end is null || start < 0 || end < start) throw new JsonException("range_invalid");
        return new(start.Value, end.Value);
    }
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, SampleRange value, JsonSerializerOptions options)
    {
        writer.WriteStartObject(); writer.WriteNumber("start", value.Start); writer.WriteNumber("end", value.End); writer.WriteEndObject();
    }
}
