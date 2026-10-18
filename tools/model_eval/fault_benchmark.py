"""Exercise real CUDA allocation failure and bounded offline inference recovery."""

import argparse
import pathlib
import sys
import time

from common import configure_cache, load_profile, new_output, sha256, source_fingerprint, utc_now, write_json
from prepare_model import verify_model
from private_ollama import require_owned_job

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))


def main():
    """Record deliberate failures separately from successful post-failure requests."""
    parser = argparse.ArgumentParser()
    for name in ("profile", "model", "audio", "output"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--hold-after-ready", action="store_true")
    args = parser.parse_args()
    require_owned_job()
    output = new_output(args.output)
    configure_cache()
    report = {"schemaVersion": 1, "kind": "gpu-fault-recovery", "startedUtc": utc_now(),
              "sourceSha256": source_fingerprint(), "passed": False, "qualityPassed": False, "continuousRuns": []}
    try:
        import numpy as np
        import torch
        from workers.qwen_asr.inference import QwenInference, read_audio
        profile = load_profile(args.profile)
        if torch.__version__ != profile["torch"] or torch.version.cuda != profile["cuda"]:
            raise ValueError("Wrong torch/CUDA build")
        report.update(profile=profile, inputSha256=sha256(args.audio))
        verify_model(args.model)
        audio = read_audio(args.audio)
        engine = QwenInference(args.model)
        report["warmup"] = engine.recognize(audio)
        if args.hold_after_ready:
            write_json(output / "ready.json", {"utc": utc_now(), "allocatedBytes": torch.cuda.memory_allocated(),
                                              "purpose": "GPU model resident; waiting for supervisor deadline"})
            time.sleep(600)
            raise RuntimeError("Supervisor did not cancel within deadline")
        try:
            verify_model(output / "missing-model")
        except (ValueError, FileNotFoundError) as error:
            report["missingModelRejected"] = {"type": type(error).__name__, "message": str(error)}
        try:
            engine.recognize(np.full(16000, np.nan, dtype=np.float32))
        except ValueError as error:
            report["invalidAudioRejected"] = str(error)
        # Bound only this process's allocator; Windows shared memory can otherwise mask VRAM pressure.
        torch.cuda.empty_cache()
        free, total = torch.cuda.mem_get_info()
        original_fraction = torch.cuda.get_per_process_memory_fraction()
        fraction = 0.0
        requested = 1024 * 1024 * 1024
        report["oomRequest"] = {"freeBytes": free, "totalBytes": total, "requestedBytes": requested,
                                "allocatorFraction": fraction, "backend": torch.cuda.get_allocator_backend(),
                                "kind": "injected per-process CUDA allocator limit"}
        try:
            torch.cuda.set_per_process_memory_fraction(fraction)
            report["oomRequest"]["actualFraction"] = torch.cuda.get_per_process_memory_fraction()
            oversized = torch.empty(requested, dtype=torch.uint8, device="cuda")
            del oversized
            raise RuntimeError("Expected CUDA OOM did not occur")
        except torch.OutOfMemoryError as error:
            report["expectedOom"] = {"type": type(error).__name__, "message": str(error)}
        finally:
            torch.cuda.set_per_process_memory_fraction(original_fraction)
        torch.cuda.empty_cache()
        report["afterOom"] = engine.recognize(audio)
        report["silence"] = engine.recognize(np.zeros(5 * 16000, dtype=np.float32))
        report["silence"]["knownReference"] = ""
        # Serialized requests measure repeatability only, not a realtime queue or semantic corpus.
        for index in range(20):
            result = engine.recognize(audio)
            result.update(index=index, allocatedAfterBytes=torch.cuda.memory_allocated(),
                          reservedAfterBytes=torch.cuda.memory_reserved())
            report["continuousRuns"].append(result)
        report["passed"] = bool(report.get("missingModelRejected") and report.get("invalidAudioRejected")
                                and report.get("expectedOom") and report["afterOom"]["complete"]
                                and all(x["complete"] for x in report["continuousRuns"]))
    except Exception as error:
        report["error"] = {"type": type(error).__name__, "message": str(error)}
    finally:
        report["completedUtc"] = utc_now()
        write_json(output / "report.json", report)
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
