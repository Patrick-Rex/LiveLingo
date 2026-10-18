using LiveLingo.Probe;
using LiveLingo.Runtime.Pipeline;

namespace LiveLingo.Windows.Tests;

/// <summary>使用实际 PCM 文件验证回放循环与结束边界，不加载模型或冒充持续运行。</summary>
public sealed class WaveReplaySourceTests
{
    /// <summary>完整一遍和跨多个文件循环均保持采样顺序、连续范围及精确总长度。</summary>
    /// <param name="durationSeconds">零为一遍，正数为显式循环时长。</param><param name="expectedSamples">应产生的采样总数。</param>
    /// <returns>文件回放及资源释放验证任务。</returns>
    [Theory]
    [InlineData(0, 1000)]
    [InlineData(0.2, 3200)]
    public async Task LoopAndEndPreserveEverySample(double durationSeconds, int expectedSamples)
    {
        var path = Path.Combine(Path.GetTempPath(), "pipeline-replay-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write("RIFF"u8); writer.Write(2036); writer.Write("WAVEfmt "u8); writer.Write(16);
                writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(16000); writer.Write(32000);
                writer.Write((ushort)2); writer.Write((ushort)16); writer.Write("data"u8); writer.Write(2000);
                for (var index = 0; index < 1000; index++) writer.Write((short)((index % 101) * 100 - 5000));
            }
            var observed = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var input in WaveReplaySource.ReadAsync(path, Guid.NewGuid(), durationSeconds, realtime: false, timeout.Token))
            {
                var audio = Assert.IsType<PipelineAudio>(input);
                Assert.Equal(1, audio.CaptureEpoch);
                Assert.Equal(observed, audio.Audio.Range.Start);
                foreach (var value in audio.Audio.Samples)
                {
                    Assert.Equal(((observed % 1000 % 101) * 100 - 5000) / 32768f, value);
                    observed++;
                }
                Assert.Equal(observed, audio.Audio.Range.End);
            }
            Assert.Equal(expectedSamples, observed);
        }
        finally { File.Delete(path); }
    }
}
