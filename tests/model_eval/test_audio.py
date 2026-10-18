"""Audio behavior tests run with a prepared profile, without importing torch."""

import importlib.util
import pathlib
import struct
import sys
import tempfile
import unittest
import wave

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
from workers.qwen_asr.inference import digital_silence, read_audio


@unittest.skipUnless(importlib.util.find_spec("numpy"), "Run in an installed model profile for audio checks")
class AudioChecks(unittest.TestCase):
    """Reject damaged/oversized windows and preserve explicitly requested samples."""

    def test_only_exact_zero_is_silence(self):
        """Quiet nonzero samples and repeated signals must never be thresholded away."""
        import numpy as np
        self.assertTrue(digital_silence(np.zeros(16000, dtype=np.float32)))
        self.assertFalse(digital_silence(np.full(16000, 1 / 32768, dtype=np.float32)))
        audio = np.zeros(16000, dtype=np.float32)
        audio[8000] = np.nextafter(np.float32(0), np.float32(1))
        self.assertFalse(digital_silence(audio))

    def test_no_implicit_cut_at_default_window_limit(self):
        """A longer waveform is rejected by default, accepted whole with an explicit budget."""
        with tempfile.TemporaryDirectory(dir=r"D:\Soft Ware\LiveLingo\cache\tmp") as directory:
            path = pathlib.Path(directory) / "audio.wav"
            with wave.open(str(path), "wb") as output:
                output.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
                output.writeframes(struct.pack("<h", 16384) * (31 * 16000))
            with self.assertRaises(ValueError):
                read_audio(path)
            actual = read_audio(path, 60)
            self.assertEqual(31 * 16000, len(actual))
            self.assertTrue((actual == 0.5).all())

    def test_truncated_payload_is_rejected(self):
        """A WAV header declaring more frames than actually present must fail."""
        with tempfile.TemporaryDirectory(dir=r"D:\Soft Ware\LiveLingo\cache\tmp") as directory:
            path = pathlib.Path(directory) / "audio.wav"
            with wave.open(str(path), "wb") as output:
                output.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
                output.writeframes(b"\0" * 32000)
            data = path.read_bytes()
            path.write_bytes(data[:-100])
            with self.assertRaisesRegex(ValueError, "Truncated"):
                read_audio(path)


if __name__ == "__main__":
    unittest.main()
