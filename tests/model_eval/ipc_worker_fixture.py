"""Explicit no-model child for host protocol failure tests, always owned by a Job."""

import pathlib
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
from workers.qwen_asr import ipc
from workers.qwen_asr.persistent import AudioCache


def main():
    """Send valid startup then exercise one selected failure or exact echo response."""
    mode = sys.argv[1]
    epoch = int(sys.argv[2]) if len(sys.argv) > 2 else 1
    info = {"python": "test", "torch": "test", "cuda": "test", "modelRevision": "test", "device": "test"}
    output = sys.stdout.buffer
    sys.stderr.buffer.write(b"diagnostic\n" * 120000)
    sys.stderr.buffer.flush()
    ipc.write_frame(output, {"version": 1, "type": "Hello", "workerEpoch": epoch, "information": info})
    if mode == "badframe":
        output.write(b"\xff\xff\xff\xff\x00\x00\x00\x00")
        output.flush()
        time.sleep(60)
        return
    if mode == "slowready":
        time.sleep(1.6)
    ipc.write_frame(output, {"version": 1, "type": "Ready", "workerEpoch": epoch, "information": info})
    audio_start, audio_samples = None, 0
    cache = AudioCache()
    while True:
        header, payload = ipc.read_frame(sys.stdin.buffer)
        kind = header["type"]
        if mode == "expanded":
            if kind == "AudioChunk":
                cache.append(header, payload)
            elif kind == "ReleaseBefore":
                cache.release(header)
            elif kind == "ResetAudio":
                cache.reset(header)
        if kind == "AudioChunk":
            if mode == "slowaudio":
                time.sleep(0.05)
            if audio_start is None:
                audio_start = header["startSample"]
            audio_samples += header["endSample"] - header["startSample"]
        if kind == "Recognize":
            if mode == "hang":
                time.sleep(60)
                return
            request = header["correlation"]
            if mode == "expanded":
                import struct
                data = cache.window(request)
                audio_start, audio_samples = cache.start, len(data) // 4
                if any(sample[0] != 0.25 for sample in struct.iter_unpack("<f", data)):
                    raise ValueError("fixture_audio_changed")
            if mode == "badidentity":
                request["ticket"]["identity"]["configEpoch"] += 1
            ipc.write_frame(output, {"version": 1, "type": "Transcription", "workerEpoch": epoch,
                                    "correlation": request, "text": f"{audio_start}:{audio_samples}" if mode in ("replay", "expanded") else "はい、はい", "complete": True,
                                    "reason": "model-eos", "elapsedSeconds": 0.1, "language": "Japanese"})
        elif kind == "Ping":
            ipc.write_frame(output, {"version": 1, "type": "Pong", "workerEpoch": epoch, "nonce": header["nonce"]})
        elif kind == "Shutdown":
            ipc.write_frame(output, {"version": 1, "type": "Stopped", "workerEpoch": epoch})
            return


if __name__ == "__main__":
    main()
