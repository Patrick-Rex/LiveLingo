"""Measure explicit 30/60/120-second ASR capacity with the selected 12B model resident."""

import argparse
import os
import pathlib
import socket
import subprocess
import sys
import time

from common import configure_cache, load_profile, new_output, sha256, source_fingerprint, utc_now, write_json
from compare import GpuSampler
from prepare_model import verify_model
from private_ollama import require_owned_job, stop_owned_service
from translation import api, translate

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))


def main():
    """Retain complete raw results and failures; this capacity experiment does not prove alignment quality."""
    parser = argparse.ArgumentParser()
    for name in ("profile", "model", "audio", "ollama", "output"):
        parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    require_owned_job()
    configure_cache()
    output = new_output(args.output)
    report = {"schemaVersion": 1, "kind": "bounded-recovery-window-capacity", "startedUtc": utc_now(),
              "passed": False, "qualityPassed": False, "alignmentAssessed": False,
              "sourceSha256": source_fingerprint(), "runs": []}
    service = None
    sampler = GpuSampler()
    with (output / "service.log").open("xb") as log:
        try:
            profile = load_profile(args.profile)
            import torch
            if torch.__version__ != profile["torch"] or torch.version.cuda != profile["cuda"] or not torch.cuda.is_available():
                raise ValueError("Wrong or unavailable CUDA profile")
            manifest = verify_model(args.model)
            from workers.qwen_asr.inference import QwenInference, read_audio
            audio = read_audio(args.audio, 120)
            if len(audio) != 120 * 16000:
                raise ValueError("Prepare exactly 120 seconds; no implicit padding or truncation")
            report.update(profile=profile, modelRevision=manifest["revision"], inputSha256=sha256(args.audio),
                          inputPath=str(pathlib.Path(args.audio).resolve()), language="auto",
                          policy="explicit trailing windows sharing the same endpoint; not semantic segmentation")
            sampler.thread.start()
            started = time.perf_counter()
            engine = QwenInference(args.model, max_window_seconds=120)
            report["asrLoadSeconds"] = time.perf_counter() - started
            with socket.socket() as reservation:
                reservation.bind(("127.0.0.1", 0))
                port = reservation.getsockname()[1]
            endpoint = f"http://127.0.0.1:{port}"
            environment = os.environ.copy()
            environment.update(OLLAMA_HOST=f"127.0.0.1:{port}", OLLAMA_MODELS=r"D:\Soft Ware\Ollama",
                               OLLAMA_CONTEXT_LENGTH="4096", OLLAMA_NUM_PARALLEL="1", OLLAMA_MAX_LOADED_MODELS="1")
            service = subprocess.Popen([args.ollama, "serve"], env=environment, stdout=log, stderr=log,
                                       creationflags=subprocess.CREATE_NO_WINDOW)
            for _ in range(60):
                time.sleep(0.5)
                if service.poll() is not None:
                    raise RuntimeError("Private service exited")
                try:
                    tags = api(endpoint, "/api/tags", timeout=2)
                    break
                except OSError:
                    continue
            else:
                raise TimeoutError("Private service not ready")
            tag = next(x for x in tags["models"] if x["name"] == "translategemma:12b")
            if tag["digest"] != "c2f9a9ca1ec7149f2422581c89937ab0363d9399d9355514d753ee012e99c252":
                raise ValueError("Unexpected translation digest")
            report["translationIdentity"] = tag
            report["translationWarmup"] = translate(endpoint, "translategemma:12b", "準備ができました。")
            if not report["translationWarmup"]["complete"]:
                raise ValueError("Translation warmup incomplete")
            for seconds in (30, 60, 120):
                for repeat in range(2):
                    entry = {"seconds": seconds, "repeat": repeat, "sampleStart": len(audio) - seconds * 16000,
                             "sampleEnd": len(audio), "tokenBudget": 512 * (seconds // 30),
                             "sampleStartTime": time.monotonic()}
                    report["runs"].append(entry)
                    engine.max_new_tokens = entry["tokenBudget"]
                    try:
                        entry["asr"] = engine.recognize(audio[entry["sampleStart"]:], None)
                        entry["placementAfter"] = api(endpoint, "/api/ps")
                    except Exception as error:
                        entry["error"] = {"type": type(error).__name__, "message": str(error)}
                        torch.cuda.empty_cache()
                    entry["sampleEndTime"] = time.monotonic()
            report["passed"] = len(report["runs"]) == 6 and all(x.get("asr", {}).get("complete") for x in report["runs"])
        except Exception as error:
            report["error"] = {"type": type(error).__name__, "message": str(error)}
        finally:
            if sampler.thread.ident is not None:
                try:
                    report["gpu"] = sampler.finish()
                except Exception as error:
                    report.update(passed=False, samplerError={"type": type(error).__name__, "message": str(error)})
            stop_owned_service(service, report)
            report["completedUtc"] = utc_now()
            write_json(output / "report.json", report)
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
