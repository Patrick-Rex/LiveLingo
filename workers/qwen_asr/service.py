"""Owned persistent Qwen process entry point; prepared assets only, never downloads."""

import argparse
import ctypes
import os
import pathlib
import platform
import sys

from workers.qwen_asr import ipc
from workers.qwen_asr.persistent import PersistentWorker


class ModelAdapter:
    """Adapt bounded f32 IPC payload to the M2-verified raw Qwen generation path."""

    def __init__(self, model):
        """Load one existing local model on the inference thread."""
        from workers.qwen_asr.inference import QwenInference
        self.engine = QwenInference(model, max_window_seconds=120)

    def recognize(self, data, request):
        """Infer exactly the supplied window, keeping the source text unmodified."""
        import numpy as np
        self.engine.max_new_tokens = request["maxNewTokens"]
        return self.engine.recognize(np.frombuffer(data, dtype="<f4"), None if request["language"] == "auto" else request["language"])

    def close(self):
        """Release model references and cached allocations before shutdown acknowledgement."""
        import gc
        import torch
        self.engine = None
        gc.collect()
        torch.cuda.synchronize()
        torch.cuda.empty_cache()


def main():
    """Preserve original binary stdout then redirect all library stdout to stderr."""
    parser = argparse.ArgumentParser()
    parser.add_argument("--profile", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--worker-epoch", required=True, type=int)
    args = parser.parse_args()
    binary_output = os.fdopen(os.dup(sys.stdout.fileno()), "wb", buffering=0)
    os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
    sys.stdout = sys.stderr
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.GetCurrentProcess.restype = ctypes.c_void_p
    kernel.IsProcessInJob.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(ctypes.c_int)]
    in_job = ctypes.c_int()
    if os.environ.get("LIVELINGO_OWNED_WORKER") != "1" or not kernel.IsProcessInJob(kernel.GetCurrentProcess(), None, ctypes.byref(in_job)) or not in_job.value:
        raise RuntimeError("Start through the owned LiveLingo pipeline host")
    root = pathlib.Path(__file__).resolve().parents[2]
    sys.path.insert(0, str(root / "tools" / "model_eval"))
    from common import configure_cache, load_profile
    from prepare_model import verify_model
    configure_cache()
    print("worker_cache_configured", file=sys.stderr, flush=True)
    profile = load_profile(args.profile, os.environ.get("LIVELINGO_PROFILE_INTERPRETER"))
    manifest = verify_model(args.model)
    print("worker_assets_verified", file=sys.stderr, flush=True)
    import torch
    print("worker_torch_imported", file=sys.stderr, flush=True)
    if torch.__version__ != profile["torch"] or torch.version.cuda != profile["cuda"] or not torch.cuda.is_available():
        raise RuntimeError("Selected CUDA profile is not available")
    information = {"python": platform.python_version(), "torch": torch.__version__, "cuda": torch.version.cuda,
                   "modelRevision": manifest["revision"], "device": torch.cuda.get_device_name(0)}
    if os.environ.get("LIVELINGO_WORKER_DIAGNOSTICS") == "1":
        import faulthandler
        faulthandler.dump_traceback_later(20, file=sys.stderr)
    # Import native numerical libraries before another thread blocks on stdin.
    # The model itself remains owned and executed by the serial inference thread.
    import qwen_asr
    print("worker_model_dependencies_imported", file=sys.stderr, flush=True)
    worker = PersistentWorker(args.worker_epoch, information, lambda: ModelAdapter(args.model),
                              lambda header: ipc.write_frame(binary_output, header))
    worker.start()
    print("worker_control_ready", file=sys.stderr, flush=True)
    failed = False
    try:
        while not worker.stop.is_set():
            header, payload = ipc.read_frame(sys.stdin.buffer)
            worker.receive(header, payload)
    except (EOFError, OSError, ValueError):
        failed = True
        try:
            worker.emit("Failed", reason="worker_protocol_or_capacity")
        except OSError:
            pass
    finally:
        worker.close()
        if os.environ.get("LIVELINGO_WORKER_DIAGNOSTICS") == "1":
            faulthandler.cancel_dump_traceback_later()
        binary_output.close()
    return 1 if failed or worker.failure else 0


if __name__ == "__main__":
    raise SystemExit(main())
