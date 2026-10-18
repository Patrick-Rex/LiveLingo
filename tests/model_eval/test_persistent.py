"""Deterministic checks of actual worker threads without loading GPU weights."""

import struct
import threading
import unittest
import uuid

from workers.qwen_asr.persistent import AudioCache, PersistentWorker


class FakeEngine:
    """Hold an inference call to verify that cancellation cannot acknowledge early."""

    def __init__(self):
        """Create independent barriers for each test."""
        self.entered = threading.Event()
        self.release = threading.Event()
        self.closed = False

    def recognize(self, data, request):
        """Keep the underlying computation alive until explicitly released."""
        self.entered.set()
        if not self.release.wait(5):
            raise TimeoutError("test_barrier")
        return {"text": "はい、はい", "complete": True, "termination": "model-eos", "elapsedSeconds": 1.0, "language": "Japanese"}

    def close(self):
        """Record actual model-owner cleanup."""
        self.closed = True


class PersistentTests(unittest.TestCase):
    """Test worker lifetime and memory ownership independently of model accuracy."""

    def test_cancel_waits_for_inference_exit(self):
        """Ping stays responsive and Cancelled is sent only after the model returns."""
        engine = FakeEngine()
        emitted = []
        info = {"python": "test", "torch": "test", "cuda": "test", "modelRevision": "test", "device": "test"}
        worker = PersistentWorker(1, info, lambda: engine, emitted.append)
        worker.start()
        try:
            self.assertTrue(worker.ready.wait(5))
            audio = self.audio_header(0, 16000)
            worker.receive(audio, bytes(64000))
            request = self.request(audio)
            worker.receive({"version": 1, "type": "Recognize", "workerEpoch": 1, "correlation": request})
            self.assertTrue(engine.entered.wait(5))
            worker.receive({"version": 1, "type": "Cancel", "workerEpoch": 1, "correlation": request})
            worker.receive({"version": 1, "type": "Ping", "workerEpoch": 1, "nonce": "still-alive"})
            self.assertEqual("Pong", emitted[-1]["type"])
            self.assertNotIn("Cancelled", [item["type"] for item in emitted])
            with self.assertRaisesRegex(ValueError, "worker_active_pin"):
                worker.receive({"version": 1, "type": "ResetAudio", "workerEpoch": 1,
                                "sessionId": audio["sessionId"], "captureEpoch": 1, "startSample": 0})
            with self.assertRaises(ValueError):
                worker.receive({"version": 1, "type": "Recognize", "workerEpoch": 1, "correlation": request})
            engine.release.set()
            worker.tasks.join()
            self.assertEqual("Cancelled", emitted[-1]["type"])
            self.assertEqual(request, emitted[-1]["correlation"])
        finally:
            engine.release.set()
            worker.close()
        self.assertTrue(engine.closed)
        self.assertFalse(worker.thread.is_alive())
        self.assertEqual("Stopped", emitted[-1]["type"])

    def test_cache_pressure_retains_audio_and_releases_exactly(self):
        """Reject overflow without covering or losing the previously accepted samples."""
        cache = AudioCache(maximum_samples=480000)
        header = self.audio_header(100, 480100)
        payload = struct.pack("<f", 1e-20) * 480000
        cache.append(header, payload)
        later = {**header, "frameSequence": 2, "startSample": 480100, "endSample": 480101}
        with self.assertRaises(ValueError):
            cache.append(later, bytes(4))
        request = self.request(header)
        self.assertEqual(payload, cache.window(request))
        cache.release({**header, "startSample": 200})
        request["windowRange"]["start"] = 200
        self.assertEqual(payload[400:], cache.window(request))
        cache.append(later, bytes(4))
        self.assertEqual(480101, cache.end)

    def test_fully_released_cache_can_start_next_valid_window(self):
        """Do not invent PCM for skipped host silence; only empty caches may rebase."""
        cache = AudioCache()
        header = self.audio_header(0, 16000)
        cache.append(header, bytes(64000))
        later = {**header, "frameSequence": 2, "startSample": 48000, "endSample": 64000}
        with self.assertRaises(ValueError):
            cache.append(later, bytes(64000))
        cache.release({**header, "startSample": 16000})
        cache.append(later, bytes(64000))
        self.assertEqual((48000, 64000), (cache.start, cache.end))
        self.assertEqual(64000, len(cache.window(self.request(later))))

    def test_reset_requires_same_scope_and_complete_reupload(self):
        """Earlier audio must come from the host again and cannot reuse old sequence numbers."""
        cache = AudioCache()
        header = self.audio_header(16000, 32000)
        cache.append(header, bytes(64000))
        with self.assertRaisesRegex(ValueError, "worker_reset_scope"):
            cache.reset({**header, "captureEpoch": 2, "startSample": 0})
        self.assertEqual(1, len(cache.blocks))
        cache.reset({**header, "startSample": 0})
        expanded = {**header, "startSample": 0, "endSample": 32000, "frameSequence": 2}
        with self.assertRaisesRegex(ValueError, "worker_window_unavailable"):
            cache.window(self.request(expanded))
        with self.assertRaisesRegex(ValueError, "worker_audio_discontinuity"):
            cache.append({**expanded, "frameSequence": 1}, bytes(128000))
        payload = struct.pack("<f", 0.25) * 32000
        cache.append(expanded, payload)
        self.assertEqual(payload, cache.window(self.request(expanded)))

    @staticmethod
    def audio_header(start, end):
        """Create one independent valid stream identity."""
        return {"version": 1, "type": "AudioChunk", "workerEpoch": 1, "sessionId": str(uuid.uuid4()),
                "captureEpoch": 1, "frameSequence": 1, "startSample": start, "endSample": end}

    @staticmethod
    def request(audio):
        """Associate an inference call with the exact retained audio scope."""
        interval = {"start": audio["startSample"], "end": audio["endSample"]}
        return {"ticket": {"id": str(uuid.uuid4()), "sequence": 1, "kind": 0, "sourceRevision": 0,
                           "identity": {"sessionId": audio["sessionId"], "captureEpoch": 1, "workerEpoch": 1, "configEpoch": 1},
                           "segment": {"id": str(uuid.uuid4()), "generation": 1}, "range": dict(interval)},
                "windowRange": dict(interval), "bodyStartSample": interval["start"], "language": "auto", "maxNewTokens": 512}


if __name__ == "__main__":
    unittest.main()
