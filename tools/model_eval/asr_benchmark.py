"""Measure one bounded recording without presenting performance as quality proof."""

import argparse
import importlib.metadata
import pathlib
import sys
import time

from common import configure_cache, load_profile, new_output, sha256, source_fingerprint, utc_now, write_json
from prepare_model import verify_model

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))


def main():
    """Keep original text, failures and cold/warm timings in explicit local evidence."""
    parser = argparse.ArgumentParser()
    parser.add_argument("--profile", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--audio", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--max-new-tokens", type=int, default=512)
    parser.add_argument("--max-window-seconds", type=int, default=30)
    parser.add_argument("--language", choices=("Japanese", "English", "Chinese", "auto"), default="Japanese")
    args = parser.parse_args()
    output = new_output(args.output)
    report = {"schemaVersion": 1, "kind": "gpu-inference-performance", "startedUtc": utc_now(),
              "humanVerified": False, "qualityPassed": False, "passed": False, "runs": [], "sourceSha256": source_fingerprint()}
    try:
        if not 1 <= args.repeats <= 100:
            raise ValueError("Repeat count must be in [1,100]")
        configure_cache()
        profile = load_profile(args.profile)
        import torch
        if torch.__version__ != profile["torch"] or torch.version.cuda != profile["cuda"]:
            raise ValueError("Wrong torch/CUDA build")
        if not torch.cuda.is_available() or not torch.cuda.is_bf16_supported():
            raise RuntimeError("Required CUDA/BF16 unavailable")
        manifest = verify_model(args.model)
        from workers.qwen_asr.inference import QwenInference, read_audio
        audio = read_audio(args.audio, args.max_window_seconds)
        report.update(profile=profile, modelRevision=manifest["revision"],
                      inputSha256=sha256(args.audio), inputPath=str(pathlib.Path(args.audio).resolve()),
                      parameters={"dtype": "bfloat16", "attention": "sdpa", "maxNewTokens": args.max_new_tokens,
                                  "language": args.language, "doSample": False, "maxWindowSeconds": args.max_window_seconds},
                      packages={n: importlib.metadata.version(n) for n in ("torch", "qwen-asr", "transformers", "accelerate")})
        started = time.perf_counter()
        engine = QwenInference(args.model, args.max_new_tokens, args.max_window_seconds)
        torch.cuda.synchronize()
        report["modelLoadSeconds"] = time.perf_counter() - started
        for index in range(args.repeats):
            result = engine.recognize(audio, None if args.language == "auto" else args.language)
            result.update(index=index, temperature="first-request" if index == 0 else "warm")
            report["runs"].append(result)
        report["passed"] = all(x["complete"] for x in report["runs"])
    except Exception as error:
        report["error"] = {"type": type(error).__name__, "message": str(error)}
    finally:
        report["completedUtc"] = utc_now()
        write_json(output / "report.json", report)
    print(f"ASR performance passed={report['passed']}; quality not assessed; report={output / 'report.json'}")
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
