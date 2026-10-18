"""No-GPU tests for strict persistent-worker frames and partial pipe I/O."""

import io
import json
import struct
import unittest
import uuid

from workers.qwen_asr import ipc


class ShortPipe(io.BytesIO):
    """Force partial reads and writes without modifying the wire format."""

    def read(self, size=-1):
        """Return at most three bytes per simulated pipe read."""
        return super().read(min(3, size))

    def write(self, data):
        """Accept at most five bytes per simulated pipe write."""
        return super().write(data[:5])


class IpcTests(unittest.TestCase):
    """Validate lengths and identities before handing model inputs to torch."""

    def test_request_roundtrip(self):
        """Keep logical coverage separate from the actual bounded model window."""
        ticket = {"id": str(uuid.uuid4()), "sequence": 7, "kind": 0,
                  "identity": {"sessionId": str(uuid.uuid4()), "captureEpoch": 2, "workerEpoch": 3, "configEpoch": 4},
                  "segment": {"id": str(uuid.uuid4()), "generation": 2},
                  "range": {"start": 0, "end": 640000}, "sourceRevision": 8}
        header = {"version": 1, "type": "Recognize", "workerEpoch": 3,
                  "correlation": {"ticket": ticket, "windowRange": {"start": 160000, "end": 640000},
                                  "bodyStartSample": 170000, "language": "auto", "maxNewTokens": 512}}
        pipe = ShortPipe()
        ipc.write_frame(pipe, header)
        pipe.seek(0)
        self.assertEqual((header, bytearray()), ipc.read_frame(pipe))
        header["correlation"]["ticket"]["identity"]["workerEpoch"] = 2
        with self.assertRaises(ValueError):
            ipc.validate(header, 0)

    def test_bad_prefix_and_header(self):
        """Reject before allocating oversized payload, duplicate keys, unknown version and EOF."""
        with self.assertRaises(ValueError):
            ipc.read_frame(io.BytesIO(struct.pack("<II", 2 ** 32 - 1, 0)))
        with self.assertRaises(EOFError):
            ipc.read_frame(io.BytesIO(b"abc"))
        for header in ('{"version":1,"version":1,"type":"Shutdown","workerEpoch":1}',
                       '{"version":2,"type":"Shutdown","workerEpoch":1}',
                       '{"version":1,"type":"Shutdown","workerEpoch":1,"unknown":1}',
                       '{"version":true,"type":"Shutdown","workerEpoch":1}'):
            data = header.encode()
            with self.assertRaises(ValueError):
                ipc.read_frame(io.BytesIO(struct.pack("<II", len(data), 0) + data))

    def test_audio_finite_and_exact_length(self):
        """Preserve low voice and zeros; reject nonfinite floats and malformed sample ranges."""
        header = {"version": 1, "type": "AudioChunk", "workerEpoch": 1, "sessionId": str(uuid.uuid4()),
                  "captureEpoch": 1, "frameSequence": 1, "startSample": 0, "endSample": 2}
        data = struct.pack("<ff", 0, 1e-20)
        pipe = ShortPipe()
        ipc.write_frame(pipe, header, data)
        pipe.seek(0)
        self.assertEqual((header, data), ipc.read_frame(pipe))
        with self.assertRaises(ValueError):
            ipc.write_frame(io.BytesIO(), header, struct.pack("<ff", float("nan"), 0))
        with self.assertRaises(ValueError):
            ipc.validate(header, 4)


if __name__ == "__main__":
    unittest.main()
