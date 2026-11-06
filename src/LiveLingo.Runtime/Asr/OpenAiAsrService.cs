using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using LiveLingo.Core.Desktop;
using LiveLingo.Runtime.Api;
using LiveLingo.Runtime.Storage;

namespace LiveLingo.Runtime.Asr;

/// <summary>把当前有界窗口编码为内存 WAV，使用标准音频转写接口识别，不创建本地模型进程。</summary>
public sealed class OpenAiAsrService : IDisposable
{
    private readonly OpenAiClient client;
    private readonly string model;

    /// <summary>绑定识别 API，允许测试替换传输。</summary>
    /// <param name="settings">独立识别配置。</param><param name="timeout">总请求预算。</param>
    /// <param name="handler">可选测试传输，随服务释放。</param>
    public OpenAiAsrService(OpenAiApiSettings settings, TimeSpan timeout, HttpMessageHandler? handler = null)
    { client = new(settings, timeout, handler); model = settings.Model; }

    /// <summary>上传当前完整窗口，结果沿用原请求身份，不生成逐字时间或本地 EOS 证据。</summary>
    /// <param name="request">已调度的识别请求。</param><param name="window">仍持有的音频租约。</param>
    /// <param name="cancellationToken">会话取消。</param><returns>标识为 API 响应的窗口转写。</returns>
    public async Task<IpcHeader> RecognizeAsync(AsrRequest request, AudioWindowLease window, CancellationToken cancellationToken)
    {
        IpcProtocol.Validate(new() { Type = "Recognize", WorkerEpoch = request.Ticket.Identity.WorkerEpoch, Correlation = request }, 0);
        if (window.Range != request.WindowRange) throw new InvalidOperationException("api_audio_range_mismatch");
        cancellationToken.ThrowIfCancellationRequested();
        using var message = new HttpRequestMessage(HttpMethod.Post, "audio/transcriptions");
        var form = new MultipartFormDataContent(); message.Content = form;
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent("json"), "response_format");
        if (request.Language == "Japanese") form.Add(new StringContent("ja"), "language");
        var audio = new ByteArrayContent(EncodeWave(window.Samples.Span));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "window.wav");
        var started = Stopwatch.GetTimestamp();
        using var json = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String || text.GetString()!.Length > 12000)
            throw new InvalidDataException("音频 API 缺少有效 text 或文本超出上限。");
        var complete = !root.TryGetProperty("finish_reason", out var reason) || reason.ValueKind == JsonValueKind.String && reason.GetString() == "stop";
        var result = new IpcHeader
        {
            Type = "Transcription", WorkerEpoch = request.Ticket.Identity.WorkerEpoch, Correlation = request,
            Text = text.GetString(), Language = request.Language == "Japanese" ? "Japanese" : "unknown",
            Complete = complete, Reason = complete ? "api_response" : "api_incomplete",
            ElapsedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds
        };
        IpcProtocol.Validate(result, 0);
        return result;
    }

    /// <summary>将最多 120 秒的单声道 16 kHz 浮点样本编码为 PCM16 WAV。</summary>
    /// <param name="samples">完整窗口，非有限采样拒绝。</param><returns>包含 RIFF 头和 PCM 正文的有限字节数组。</returns>
    private static byte[] EncodeWave(ReadOnlySpan<float> samples)
    {
        if (samples.Length is <= 0 or > 1920000) throw new ArgumentException("API 音频窗口超出 120 秒预算。", nameof(samples));
        var data = new byte[44 + samples.Length * 2];
        "RIFF"u8.CopyTo(data); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), data.Length - 8);
        "WAVEfmt "u8.CopyTo(data.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), 16000); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(34), 16);
        "data"u8.CopyTo(data.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), samples.Length * 2);
        for (var i = 0; i < samples.Length; i++)
        {
            if (!float.IsFinite(samples[i])) throw new InvalidDataException("音频包含非有限采样。");
            var pcm = (short)Math.Clamp(MathF.Round(samples[i] * 32768), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(44 + i * 2), pcm);
        }
        return data;
    }

    /// <inheritdoc />
    public void Dispose() => client.Dispose();
}
