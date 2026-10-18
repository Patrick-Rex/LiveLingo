using System.Buffers;
using LiveLingo.Core.Audio;
using LiveLingo.Runtime.Diagnostics;
using LiveLingo.Runtime.Pipeline;

namespace LiveLingo.Runtime.Tests.Pipeline;

/// <summary>验证慢消费者、容量失败及队列完成的真实所有权行为。</summary>
public sealed class AudioIngressTests
{
    /// <summary>容量耗尽时保留已接受的包，释放被拒绝包，最终明确失败。</summary>
    /// <returns>完成排空和错误断言的任务。</returns>
    [Fact]
    public async Task OverflowIsVisibleAndDoesNotDropAcceptedAudioAsync()
    {
        using var ingress = new AudioIngress(new() { QueueAudioSeconds = 0.01 }, new(1000, 1));
        var first = Packet(10);
        var rejected = Packet(1);
        Assert.True(ingress.TryPublish(first));
        Assert.False(ingress.TryPublish(rejected));
        Assert.Throws<ObjectDisposedException>(() => rejected.Pcm);
        var frames = 0;
        await Assert.ThrowsAsync<AudioCapacityException>(async () =>
        {
            await foreach (var packet in ingress.ReadAllAsync(TestContext.Current.CancellationToken))
            {
                using (packet) frames += packet.FrameCount;
            }
        });
        Assert.Equal(10, frames);
        Assert.Equal(0, ingress.QueuedFrames);
        Assert.True(ingress.PeakFrames <= ingress.MaxFrames);
    }

    /// <summary>正常完成后仍可读取最后一个包，清理释放未读取音频。</summary>
    /// <returns>完成所有权断言的任务。</returns>
    [Fact]
    public async Task CompletionDrainsAndDisposalReturnsPendingOwnershipAsync()
    {
        using var ingress = new AudioIngress(new(), new(1000, 1));
        Assert.True(ingress.TryPublish(Packet(10)));
        ingress.Complete();
        var total = 0;
        await foreach (var packet in ingress.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            using (packet) total += packet.FrameCount;
        }
        Assert.Equal(10, total);
        var pending = Packet(2);
        using (var abandoned = new AudioIngress(new(), new(1000, 1))) abandoned.TryPublish(pending);
        Assert.Throws<ObjectDisposedException>(() => pending.Pcm);
    }

    /// <summary>确认无效 QPC 不参与精确时延统计，退避有明确上限。</summary>
    [Fact]
    public void InvalidTimestampIsExcludedAndBackoffIsBounded()
    {
        Assert.Null(MonotonicClock.CaptureLatencyMs(100, 100, false));
        var policy = new CapturePolicy();
        Assert.Equal(TimeSpan.FromMilliseconds(250), policy.RetryDelay(1));
        Assert.Equal(policy.RetryMaxDelay, policy.RetryDelay(100));
    }

    /// <summary>租用一个具备指定帧数的有效包。</summary>
    /// <param name="frames">帧数。</param>
    /// <returns>由调用方释放的包。</returns>
    private static AudioPacket Packet(int frames) => new(ArrayPool<byte>.Shared.Rent(Math.Max(1, frames * 2)),
        Guid.NewGuid(), 1, 1, 0, frames, new(1000, 1), PacketFlags.None, 0, 0, 0);
}
