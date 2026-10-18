using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveLingo.Core.Sessions;
using LiveLingo.Core.Subtitles;
using LiveLingo.Platform.Windows.Processes;
using LiveLingo.Probe.Recording;
using LiveLingo.Runtime.Asr;
using LiveLingo.Runtime.Pipeline;

namespace LiveLingo.Probe;

/// <summary>常驻 worker 的实际管道与 GPU 冒烟，独立于最终字幕流水线验收。</summary>
internal static class AsrWorkerCheck
{
    /// <summary>两次识别同一完整窗口，记录冷启动、热调用及实际退出身份。</summary>
    /// <param name="repository">项目绝对根路径。</param><param name="profilePath">固定 profile。</param><param name="model">准备好的模型目录。</param>
    /// <param name="input">完整 16k mono PCM16 WAV。</param><param name="output">独占非 C 盘证据目录。</param>
    /// <returns>0 表示管道运行和清理通过，不表示语义全部正确。</returns>
    public static async Task<int> RunAsync(string repository, string profilePath, string model, string input, string output)
    {
        foreach (var path in new[] { repository, profilePath, model, input })
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("所有输入路径必须为绝对路径。");
        var audio = ReadWave(input);
        using var profile = JsonDocument.Parse(File.ReadAllBytes(profilePath));
        var root = profile.RootElement;
        var expected = new WorkerInformation(root.GetProperty("python").GetString()!, root.GetProperty("torch").GetString()!,
            root.GetProperty("cuda").GetString()!, root.GetProperty("modelRevision").GetString()!, "");
        var directory = OutputDirectory.Prepare(output);
        var results = new List<AsrCheckResult>();
        TargetIdentity? owned = null;
        string? error = null;
        var cleanup = false;
        double startup = 0;
        long diagnosticBytes = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        AsrWorkerClient? client = null;
        OwnedWorkerConnection? connection = null;
        try
        {
            connection = new(root.GetProperty("interpreter").GetString()!,
                ["-u", "-m", "workers.qwen_asr.service", "--profile", profilePath, "--model", model, "--worker-epoch", "1"],
                repository, "persistent-qwen-check", new Dictionary<string, string>
                { ["LIVELINGO_OWNED_WORKER"] = "1", ["PYTHONUTF8"] = "1", ["LIVELINGO_WORKER_DIAGNOSTICS"] = "1" });
            owned = connection.Process.Identity;
            client = new(connection, 1, expected, new() { StartupTimeout = TimeSpan.FromSeconds(60) }, captureDiagnosticTail: true);
            var started = Stopwatch.GetTimestamp();
            await client.InitializeAsync(stop.Token).ConfigureAwait(false);
            startup = Stopwatch.GetElapsedTime(started).TotalSeconds;
            var identity = new PipelineIdentity(Guid.NewGuid(), 1, 1, 1);
            long sequence = 0;
            for (var start = 0; start < audio.Length; start += 16000)
            {
                var count = Math.Min(16000, audio.Length - start);
                await client.AppendAudioAsync(identity, ++sequence, new(start, start + count), audio.AsMemory(start, count), stop.Token).ConfigureAwait(false);
            }
            var segment = new SegmentKey(Guid.NewGuid(), 1);
            for (var index = 1; index <= 2; index++)
            {
                var request = new AsrRequest(new(Guid.NewGuid(), index, PipelineRequestKind.Recognition, identity, segment, new(0, audio.Length), 0),
                    new(0, audio.Length), 0, "auto", 512);
                started = Stopwatch.GetTimestamp();
                var inference = client.RecognizeAsync(request, stop.Token);
                await client.PingAsync(stop.Token).ConfigureAwait(false);
                var result = await inference.ConfigureAwait(false);
                results.Add(new(request.Ticket.Id, index, 0, audio.Length, result.Text!, result.Language, result.Complete!.Value,
                    result.Reason!, result.ElapsedSeconds!.Value, Stopwatch.GetElapsedTime(started).TotalSeconds));
            }
            await client.ReleaseBeforeAsync(identity, audio.Length, stop.Token).ConfigureAwait(false);
        }
        catch (Exception exception) { error = exception.GetType().Name + ": " + exception.Message; }
        finally
        {
            Console.CancelKeyPress -= cancel;
            try
            {
                if (client is not null) { diagnosticBytes = client.StderrBytes; await client.DisposeAsync().ConfigureAwait(false); }
                else if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
                var current = owned is null ? null : WindowsTargets.Query(owned.ProcessId).Identity;
                cleanup = owned is null || current is null || !owned.SameInstance(current);
            }
            catch (Exception exception) { error = (error is null ? "" : error + "; ") + "cleanup: " + exception.GetType().Name; }
        }
        var report = new AsrCheckReport(error is null && cleanup && results.Count == 2 && results.All(x => x.Complete), error, expected,
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(input))), startup, diagnosticBytes, owned, cleanup, results.ToArray());
        await File.WriteAllTextAsync(Path.Combine(directory, "worker-stderr-tail.txt"), client?.DiagnosticTail() ?? "").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, AsrCheckJsonContext.Default.AsrCheckReport) + "\n").ConfigureAwait(false);
        Console.WriteLine($"AsrWorkerPassed={report.Passed}; CleanupCompleted={cleanup}; Results={results.Count}; StartupSeconds={startup:F3}; Output={directory}");
        return report.Passed ? 0 : 3;
    }

    /// <summary>读取已准备的完整有限 WAV，不隐式截断或改变采样率。</summary>
    /// <param name="path">本地文件。</param><returns>归一化 f32 mono。</returns>
    internal static float[] ReadWave(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length is < 44 or > 1024 * 1024 || reader.ReadUInt32() != 0x46464952) throw new InvalidDataException("需要不超过 30 秒的 RIFF WAV。");
        var total = reader.ReadUInt32();
        if (total + 8L != stream.Length || reader.ReadUInt32() != 0x45564157) throw new InvalidDataException("WAV 容器不完整。");
        var format = false;
        while (stream.Position + 8 <= stream.Length)
        {
            var kind = reader.ReadUInt32();
            var length = reader.ReadUInt32();
            var end = checked(stream.Position + length);
            if (end > stream.Length) throw new InvalidDataException("WAV 块超出文件。");
            if (kind == 0x20746d66)
            {
                if (length < 16 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != 1 || reader.ReadUInt32() != 16000 ||
                    reader.ReadUInt32() != 32000 || reader.ReadUInt16() != 2 || reader.ReadUInt16() != 16)
                    throw new InvalidDataException("需要 16k mono PCM16。");
                format = true;
            }
            else if (kind == 0x61746164)
            {
                if (!format || length is 0 or > 960000 || length % 2 != 0) throw new InvalidDataException("音频格式、完整性或长度无效。");
                var data = new float[length / 2];
                for (var index = 0; index < data.Length; index++) data[index] = reader.ReadInt16() / 32768f;
                return data;
            }
            stream.Position = end + (length % 2);
        }
        throw new InvalidDataException("缺少完整音频。");
    }
}

/// <summary>实际调用证据，正文仅在显式评测保存。</summary>
/// <param name="RequestId">独立调用。</param><param name="Sequence">顺序。</param><param name="StartSample">真实窗口起点。</param>
/// <param name="EndSample">真实窗口终点。</param><param name="Text">原始文本。</param><param name="Language">检测语言。</param>
/// <param name="Complete">正常生成。</param><param name="Reason">终止原因。</param><param name="InferenceSeconds">GPU 推理时间。</param>
/// <param name="RoundTripSeconds">完整管道耗时。</param>
internal sealed record AsrCheckResult(Guid RequestId, long Sequence, long StartSample, long EndSample, string Text, string? Language,
    bool Complete, string Reason, double InferenceSeconds, double RoundTripSeconds);
/// <summary>常驻识别冒烟报告，不宣称已完成 M3 或语义质量通过。</summary>
/// <param name="Passed">调用与清理是否完成。</param><param name="Error">明确错误。</param><param name="Profile">预期运行环境。</param>
/// <param name="AudioSha256">原始输入指纹。</param><param name="StartupSeconds">加载就绪耗时。</param><param name="StderrBytes">排空诊断字节数。</param>
/// <param name="Owned">实际自有身份。</param><param name="CleanupCompleted">清理已核实。</param><param name="Results">独立请求结果。</param>
internal sealed record AsrCheckReport(bool Passed, string? Error, WorkerInformation Profile, string AudioSha256, double StartupSeconds,
    long StderrBytes, TargetIdentity? Owned, bool CleanupCompleted, AsrCheckResult[] Results);
/// <summary>生成实际识别检查证据的 JSON 元数据。</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AsrCheckReport))]
internal partial class AsrCheckJsonContext : JsonSerializerContext;
