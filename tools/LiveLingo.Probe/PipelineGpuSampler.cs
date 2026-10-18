using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveLingo.Core.Processes;

namespace LiveLingo.Probe;

/// <summary>单个受监管 nvidia-smi 持续采样，避免每秒创建辅助进程。</summary>
internal sealed class PipelineGpuSampler : IAsyncDisposable
{
    private readonly IWorkerConnection connection;
    private readonly Task samples, errors;
    private string? failure;
    private long count;
    private int maximumMiB;
    private volatile bool stopping;

    /// <summary>读取已启动的有限字段查询并保存到明确启用的证据目录。</summary>
    /// <param name="connection">私有 Job 内持续查询进程。</param><param name="path">独占 JSONL 证据文件。</param>
    public PipelineGpuSampler(IWorkerConnection connection, string path)
    { this.connection = connection; samples = ReadAsync(path); errors = DrainAsync(connection.Error); }

    /// <summary>获取采样摘要；停止读取后作为最终证据。</summary>
    public PipelineGpuSummary Summary => new(Interlocked.Read(ref count), Volatile.Read(ref maximumMiB), failure);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        stopping = true;
        await connection.TerminateAsync().ConfigureAwait(false);
        await Task.WhenAll(samples, errors).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    /// <summary>记录整卡占用，不误称为仅本程序显存。</summary><param name="path">明确的证据路径。</param><returns>输出关闭后结束。</returns>
    private async Task ReadAsync(string path)
    {
        try
        {
            using var reader = new StreamReader(connection.Output, Encoding.UTF8, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var values = line.Split(',');
                if (line.Length > 256 || values.Length != 3 || !int.TryParse(values[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var memory) ||
                    !int.TryParse(values[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var total) ||
                    !int.TryParse(values[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var utilization) || memory < 0 || total < memory || utilization is < 0 or > 100)
                { failure = "gpu_sample_invalid"; continue; }
                if (writer.BaseStream.Length > 16L * 1024 * 1024) { failure = "gpu_evidence_capacity"; continue; }
                await writer.WriteLineAsync(JsonSerializer.Serialize(new PipelineGpuMetric(DateTimeOffset.UtcNow, memory, total, utilization), PipelineGpuJsonContext.Default.PipelineGpuMetric)).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                Interlocked.Increment(ref count); Volatile.Write(ref maximumMiB, Math.Max(maximumMiB, memory));
            }
            if (!stopping) failure = "gpu_sampler_early_eof";
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { if (!stopping) failure = "gpu_sampler_io_failed"; }
    }

    /// <summary>排空固定查询的诊断管道，不保存无限输出。</summary><param name="stream">查询进程 stderr。</param><returns>EOF 后完成。</returns>
    private static async Task DrainAsync(Stream stream)
    {
        var buffer = new byte[4096];
        try { while (await stream.ReadAsync(buffer).ConfigureAwait(false) != 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }
}

/// <summary>整卡实时负载，与宿主和模型进程资源分开记录。</summary>
/// <param name="Utc">采样时间。</param><param name="DeviceMemoryMiB">整卡已用显存。</param><param name="TotalMiB">整卡容量。</param><param name="UtilizationPercent">整卡利用率。</param>
internal sealed record PipelineGpuMetric(DateTimeOffset Utc, int DeviceMemoryMiB, int TotalMiB, int UtilizationPercent);
/// <summary>GPU 采样完整性和峰值摘要。</summary><param name="Samples">有效样本数。</param><param name="MaximumMiB">最大整卡显存。</param><param name="Error">采样失败原因。</param>
internal sealed record PipelineGpuSummary(long Samples, int MaximumMiB, string? Error);
/// <summary>持续 GPU 记录的编译期序列化元数据。</summary>
[JsonSerializable(typeof(PipelineGpuMetric))]
internal partial class PipelineGpuJsonContext : JsonSerializerContext;
