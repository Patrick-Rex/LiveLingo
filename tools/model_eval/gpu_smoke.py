"""Exercise real FP32/BF16 CUDA kernels without loading a speech model."""

import argparse
import importlib.metadata
import time

from common import configure_cache, load_profile, new_output, utc_now, write_json


def main():
    """Record both successes and failures in a new evidence directory."""
    parser = argparse.ArgumentParser()
    parser.add_argument("--profile", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    output = new_output(args.output)
    report = {"schemaVersion": 1, "kind": "real-cuda-smoke", "startedUtc": utc_now(), "passed": False}
    try:
        configure_cache()
        profile = load_profile(args.profile)
        import torch
        report["profile"] = profile
        report["packages"] = {name: importlib.metadata.version(name) for name in
                              ("torch", "qwen-asr", "transformers", "accelerate", "nagisa")}
        report.update(torchVersion=torch.__version__, cudaVersion=torch.version.cuda,
                      available=torch.cuda.is_available())
        if torch.__version__ != profile["torch"] or torch.version.cuda != profile["cuda"]:
            raise ValueError("Wrong torch/CUDA build")
        if not torch.cuda.is_available():
            raise RuntimeError("CUDA unavailable; no CPU fallback")
        report.update(device=torch.cuda.get_device_name(0), capability=torch.cuda.get_device_capability(0),
                      architectures=torch.cuda.get_arch_list(), bf16=torch.cuda.is_bf16_supported())
        if not report["bf16"]:
            raise RuntimeError("BF16 is required")
        torch.cuda.reset_peak_memory_stats()
        started = time.perf_counter()
        checks = []
        for dtype in (torch.float32, torch.bfloat16):
            # Exactly representable dot products give an independent numerical oracle.
            left = torch.ones((256, 256), device="cuda", dtype=dtype)
            right = torch.full((256, 256), 0.5, device="cuda", dtype=dtype)
            actual = left @ right
            torch.cuda.synchronize()
            correct = bool(torch.all(actual == 128).item())
            checks.append({"dtype": str(dtype), "correct": correct})
            if not correct:
                raise RuntimeError("CUDA matrix result incorrect")
        report.update(checks=checks, elapsedSeconds=time.perf_counter() - started,
                      peakAllocatedBytes=torch.cuda.max_memory_allocated(),
                      peakReservedBytes=torch.cuda.max_memory_reserved(), passed=True)
    except Exception as error:
        report["error"] = {"type": type(error).__name__, "message": str(error)}
    finally:
        report["completedUtc"] = utc_now()
        write_json(output / "report.json", report)
    print(f"GPU smoke passed={report['passed']}; report={output / 'report.json'}")
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
