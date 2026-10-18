using System.Buffers;
using LiveLingo.Core.Audio;
using LiveLingo.Core.Sessions;

namespace LiveLingo.Core.Tests.Audio;

/// <summary>验证音频区间、所有权及实例身份的外部契约。</summary>
public sealed class AudioContractTests
{
    /// <summary>验证空包的半开区间与幂等释放。</summary>
    [Fact]
    public void EmptyPacketHasEmptyRangeAndCannotBeReadAfterDisposal()
    {
        var packet = new AudioPacket(ArrayPool<byte>.Shared.Rent(1), Guid.NewGuid(), 1, 1, 42, 0,
            new AudioFormat(44100, 2), PacketFlags.TimestampError, 0, 0, 0);
        Assert.Equal(42, packet.FrameEnd);
        Assert.False(packet.TimestampValid);
        packet.Dispose();
        packet.Dispose();
        Assert.Throws<ObjectDisposedException>(() => packet.Pcm);
    }

    /// <summary>验证同一个 PID 的另一创建时间不能接续原实例。</summary>
    [Fact]
    public void ReusedPidIsNotTheSelectedInstance()
    {
        var original = new TargetIdentity(12, DateTimeOffset.UnixEpoch, Path.GetFullPath("app.exe"));
        var reused = new TargetIdentity(12, DateTimeOffset.UnixEpoch.AddSeconds(1), original.ExecutablePath);
        Assert.True(original.SameApplication(reused));
        Assert.False(original.SameInstance(reused));
    }
}
