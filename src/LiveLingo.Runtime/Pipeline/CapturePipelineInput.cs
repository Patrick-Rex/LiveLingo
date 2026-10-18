using System.Buffers;
using System.Runtime.CompilerServices;
using LiveLingo.Core.Audio;

namespace LiveLingo.Runtime.Pipeline;

/// <summary>将 M1 原始包转换为连续模型输入，在真实断点建立独立时间轴。</summary>
public static class CapturePipelineInput
{
    /// <summary>逐包转移并释放 PCM 所有权，采集代次、格式或设备断点不跨越滤波状态。</summary>
    /// <param name="packets">M1 有界入口提供的原始包。</param><param name="session">本次管道会话。</param>
    /// <param name="cancellationToken">停止消费及释放当前包。</param><returns>有序音频及显式代次重置。</returns>
    public static async IAsyncEnumerable<PipelineInput> ReadAsync(IAsyncEnumerable<AudioPacket> packets, Guid session,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StreamingPcmConverter? converter = null;
        AudioFormat? format = null;
        long sourceEpoch = 0, pipelineEpoch = 1, sourceEnd = 0, sourceSequence = 0, sequence = 0, frames = 0;
        await foreach (var packet in packets.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            using (packet)
            {
                if (packet.SessionId != session || packet.FrameCount <= 0) throw new InvalidDataException("capture_packet_identity_or_size");
                var reset = converter is not null && (packet.Epoch != sourceEpoch || packet.Format != format ||
                    packet.Flags.HasFlag(PacketFlags.Discontinuity) || packet.FrameStart != sourceEnd || packet.Sequence != sourceSequence + 1);
                if (reset)
                {
                    var tail = converter!.Flush();
                    if (tail.Samples.Length != 0) yield return new PipelineAudio(pipelineEpoch, tail);
                    yield return new PipelineCaptureReset(++pipelineEpoch, packet.Epoch != sourceEpoch ? "capture_epoch_changed" : "capture_discontinuity_or_format_change");
                    converter = null;
                }
                if (converter is null)
                {
                    format = packet.Format; sourceEpoch = packet.Epoch; frames = sequence = 0;
                    converter = new(format, session, pipelineEpoch);
                }
                // 保留实际 PCM，原生断点已经通过上面的控制事件表达，不伪造连续设备时间。
                var buffer = ArrayPool<byte>.Shared.Rent(packet.ByteCount);
                AudioPacket? normalized = null;
                try
                {
                    packet.Pcm.CopyTo(buffer);
                    normalized = new(buffer, session, pipelineEpoch, ++sequence, frames, packet.FrameCount, packet.Format,
                        packet.Flags & ~PacketFlags.Discontinuity, packet.DevicePositionFrames, packet.QpcPosition100ns, packet.AvailableTicks);
                    var audio = converter.Convert(normalized);
                    frames += packet.FrameCount; sourceEnd = packet.FrameEnd; sourceSequence = packet.Sequence;
                    if (audio.Samples.Length != 0) yield return new PipelineAudio(pipelineEpoch, audio);
                }
                finally { if (normalized is null) ArrayPool<byte>.Shared.Return(buffer); else normalized.Dispose(); }
            }
        }
        if (converter is not null)
        {
            var tail = converter.Flush();
            if (tail.Samples.Length != 0) yield return new PipelineAudio(pipelineEpoch, tail);
        }
    }
}
