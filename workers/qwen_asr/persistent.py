"""Bounded control/inference worker; cancellation acknowledges actual GPU exit."""

import collections
import gc
import queue
import threading

from workers.qwen_asr import ipc


class AudioCache:
    """Retain contiguous PCM with explicit release; never evict unprocessed data."""

    def __init__(self, maximum_samples=16000 * 120, maximum_chunks=4096):
        """Set finite worker memory independently from the host's authoritative cache."""
        ipc.integer(maximum_samples, 480000, 16000 * 600)
        ipc.integer(maximum_chunks, 1, 8192)
        self.maximum_samples = maximum_samples
        self.maximum_chunks = maximum_chunks
        self.blocks = collections.deque()
        self.scope = None
        self.start = self.end = self.sequence = 0

    def append(self, header, payload):
        """Append only exact continuity; reject overflow before mutating retained PCM."""
        scope = (header["sessionId"], header["captureEpoch"])
        start, end = header["startSample"], header["endSample"]
        if self.scope != scope:
            if self.blocks:
                raise ValueError("worker_epoch_audio_retained")
            self.scope, self.start, self.end, self.sequence = scope, start, start, 0
        elif not self.blocks and start >= self.end:
            # A fully released model cache may start at the next valid host window.
            self.start = self.end = start
        if start != self.end or header["frameSequence"] <= self.sequence:
            raise ValueError("worker_audio_discontinuity")
        if end - self.start > self.maximum_samples or len(self.blocks) >= self.maximum_chunks:
            raise ValueError("worker_audio_capacity")
        self.blocks.append((start, bytes(payload)))
        self.end, self.sequence = end, header["frameSequence"]

    def window(self, request):
        """Copy exactly the requested window so concurrent releases cannot mutate inference input."""
        identity = request["ticket"]["identity"]
        start, end = ipc.sample_range(request["windowRange"])
        if self.scope != (identity["sessionId"], identity["captureEpoch"]) or not self.start <= start < end <= self.end:
            raise ValueError("worker_window_unavailable")
        data = bytearray((end - start) * 4)
        copied = 0
        for block_start, block in self.blocks:
            left, right = max(start, block_start), min(end, block_start + len(block) // 4)
            if left < right:
                data[(left - start) * 4:(right - start) * 4] = block[(left - block_start) * 4:(right - block_start) * 4]
                copied += right - left
        if copied != end - start:
            raise ValueError("worker_window_gap")
        return data

    def release(self, header):
        """Release only the explicit continuous watermark, retaining every later sample."""
        before = header["startSample"]
        if self.scope != (header["sessionId"], header["captureEpoch"]) or not self.start <= before <= self.end:
            raise ValueError("worker_release_range")
        while self.blocks and self.blocks[0][0] + len(self.blocks[0][1]) // 4 <= before:
            self.blocks.popleft()
        if self.blocks and self.blocks[0][0] < before:
            block_start, block = self.blocks.popleft()
            self.blocks.appendleft((before, block[(before - block_start) * 4:]))
        self.start = before

    def reset(self, header):
        """Rebase only this scope's model copy; host must upload every requested sample again."""
        if self.scope != (header["sessionId"], header["captureEpoch"]):
            raise ValueError("worker_reset_scope")
        self.blocks.clear()
        self.start = self.end = header["startSample"]


class PersistentWorker:
    """Main thread handles control; one owned thread loads and executes the model."""

    def __init__(self, epoch, information, engine_factory, send):
        """Inject only model and output boundaries for deterministic lifecycle tests."""
        self.epoch = ipc.integer(epoch, 1)
        self.information = information
        self.engine_factory = engine_factory
        self.send_callback = send
        self.lock = threading.RLock()
        self.output_lock = threading.Lock()
        self.stop = threading.Event()
        self.ready = threading.Event()
        self.tasks = queue.Queue(maxsize=1)
        self.cache = AudioCache()
        self.active = None
        self.cancelled = False
        self.failure = None
        self.thread = threading.Thread(target=self.infer, name="qwen-inference", daemon=False)

    def emit(self, kind, **values):
        """Serialize frames from both threads without interleaving bytes."""
        with self.output_lock:
            self.send_callback({"version": 1, "type": kind, "workerEpoch": self.epoch, **values})

    def start(self):
        """Start exactly one model owner thread."""
        self.emit("Hello", information=self.information)
        self.thread.start()

    def receive(self, header, payload=b""):
        """Process bounded controls without waiting for synchronous CUDA generation."""
        ipc.validate(header, len(payload))
        if header["workerEpoch"] != self.epoch:
            raise ValueError("worker_epoch_mismatch")
        kind = header["type"]
        with self.lock:
            if kind == "Shutdown":
                self.stop.set()
                self.cancelled = True
                return
            if kind == "Ping":
                self.emit("Pong", nonce=header["nonce"])
                return
            if self.stop.is_set() or not self.ready.is_set():
                raise ValueError("worker_not_ready")
            if kind == "AudioChunk":
                scope = (header["sessionId"], header["captureEpoch"])
                if self.active and scope != self.cache.scope:
                    raise ValueError("worker_active_epoch")
                self.cache.append(header, payload)
            elif kind == "ReleaseBefore":
                if self.active and header["startSample"] > self.active["windowRange"]["start"]:
                    raise ValueError("worker_active_pin")
                self.cache.release(header)
            elif kind == "ResetAudio":
                if self.active is not None:
                    raise ValueError("worker_active_pin")
                self.cache.reset(header)
            elif kind == "Recognize":
                if self.active is not None:
                    raise ValueError("worker_request_capacity")
                request = header["correlation"]
                data = self.cache.window(request)
                self.active, self.cancelled = request, False
                self.tasks.put_nowait((request, data))
            elif kind == "Cancel":
                if header["correlation"] != self.active:
                    raise ValueError("worker_unknown_cancel")
                self.cancelled = True
            else:
                raise ValueError("worker_wrong_direction")

    def infer(self):
        """Acknowledge Cancel only after generation exits; clean the model before Stopped."""
        engine = None
        try:
            engine = self.engine_factory()
            if not self.stop.is_set():
                self.ready.set()
                self.emit("Ready", information=self.information)
            while not self.stop.is_set():
                try:
                    request, data = self.tasks.get(timeout=0.1)
                except queue.Empty:
                    continue
                try:
                    with self.lock:
                        cancelled_before_start = self.cancelled
                    result = None if cancelled_before_start else engine.recognize(data, request)
                    with self.lock:
                        if self.cancelled or self.stop.is_set():
                            self.emit("Cancelled", correlation=request)
                        else:
                            values = {"correlation": request, "text": result["text"], "complete": result["complete"],
                                      "reason": result["termination"], "elapsedSeconds": result["elapsedSeconds"]}
                            if result.get("language") is not None:
                                values["language"] = result["language"]
                            self.emit("Transcription", **values)
                        self.active = None
                except Exception:
                    self.emit("Error", correlation=request, reason="worker_inference_failed")
                    raise
                finally:
                    self.tasks.task_done()
        except Exception as error:
            self.failure = type(error).__name__
            self.stop.set()
            try:
                self.emit("Failed", reason="worker_failed")
            except (OSError, ValueError):
                pass
        finally:
            self.ready.clear()
            with self.lock:
                self.active = None
                self.cache.blocks.clear()
                while not self.tasks.empty():
                    self.tasks.get_nowait()
                    self.tasks.task_done()
            if engine is not None:
                try:
                    engine.close()
                except Exception as error:
                    self.failure = type(error).__name__
                engine = None
            gc.collect()
            if self.failure is None:
                try:
                    self.emit("Stopped")
                except OSError:
                    pass

    def close(self, timeout=10):
        """Bound cooperative exit; host must close the Job if model code cannot stop."""
        self.stop.set()
        with self.lock:
            self.cancelled = True
        self.thread.join(timeout)
        if self.thread.is_alive():
            raise TimeoutError("worker_shutdown_timeout_requires_job_close")
