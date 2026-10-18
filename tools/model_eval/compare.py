"""Compare co-resident serial/concurrent inference under an owned private service."""

import argparse
import concurrent.futures
import json
import os
import pathlib
import socket
import subprocess
import sys
import threading
import time

from common import configure_cache, load_profile, new_output, sha256, source_fingerprint, utc_now, write_json
from prepare_model import verify_model
from translation import api, translate

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))


class GpuSampler:
    """Sample whole-device memory separately from PyTorch allocator measurements."""

    def __init__(self):
        """Create one bounded background observer for the current experiment."""
        self.stop = threading.Event()
        self.samples = []
        self.errors = []
        self.thread = threading.Thread(target=self.run, daemon=True)

    def run(self):
        """Poll at most five times per second; report missing measurements explicitly."""
        while not self.stop.is_set() and len(self.samples) < 18000:
            try:
                process = subprocess.run(["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"],
                                         capture_output=True, text=True, timeout=2,
                                         creationflags=subprocess.CREATE_NO_WINDOW)
                if process.returncode != 0:
                    raise RuntimeError("nvidia-smi failed")
                self.samples.append({"time": time.monotonic(), "usedMiB": int(process.stdout.strip().splitlines()[0])})
            except Exception as error:
                if len(self.errors) < 20:
                    self.errors.append(type(error).__name__)
            self.stop.wait(0.2)

    def finish(self):
        """Join the observer and expose sampling uncertainty instead of inventing peaks."""
        self.stop.set()
        self.thread.join(timeout=3)
        if self.thread.is_alive():
            raise TimeoutError("GPU sampler did not stop")
        return {"samples": self.samples, "errors": self.errors,
                "sampledPeakMiB": max((x["usedMiB"] for x in self.samples), default=None),
                "scope": "whole-device sampled usage including desktop; not exact instantaneous peak"}


def main():
    """Measure identical ASR windows and translation inputs in both scheduling modes."""
    parser = argparse.ArgumentParser()
    for name in ("profile", "model", "audio", "ollama", "output"):
        parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    # The parent Probe enforces tree ownership and the total execution deadline.
    from private_ollama import require_owned_job, stop_owned_service
    require_owned_job()
    output = new_output(args.output)
    configure_cache()
    report = {"schemaVersion": 1, "startedUtc": utc_now(), "kind": "co-resident-scheduling",
              "qualityPassed": False, "runs": [], "passed": False, "sourceSha256": source_fingerprint()}
    service = None
    sampler = GpuSampler()
    with (output / "service.log").open("xb") as log:
        try:
            profile = load_profile(args.profile)
            import torch
            if torch.__version__ != profile["torch"] or torch.version.cuda != profile["cuda"]:
                raise ValueError("Wrong torch/CUDA build")
            verify_model(args.model)
            from workers.qwen_asr.inference import QwenInference, read_audio
            audio = read_audio(args.audio)
            report.update(profile=profile, inputSha256=sha256(args.audio))
            sampler.thread.start()
            engine = QwenInference(args.model)
            report["asrWarmup"] = engine.recognize(audio)
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
                    report["serviceVersion"] = api(endpoint, "/api/version", timeout=2)
                    break
                except OSError:
                    continue
            else:
                raise TimeoutError("Private service not ready")
            for model in ("translategemma:4b", "translategemma:12b"):
                tag = next(x for x in api(endpoint, "/api/tags")["models"] if x["name"] == model)
                text = "田中さんが来ないわけではありません。確認して、確認してから、決めます。"
                warmup = translate(endpoint, model, text)
                for mode in ("serial", "concurrent"):
                    entry = {"model": model, "identity": tag, "mode": mode, "translationWarmup": warmup,
                             "sampleStartTime": time.monotonic()}
                    report["runs"].append(entry)
                    started = time.perf_counter()
                    try:
                        if mode == "serial":
                            entry["asr"] = engine.recognize(audio)
                            entry["translation"] = translate(endpoint, model, text)
                        else:
                            with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
                                asr = pool.submit(engine.recognize, audio)
                                translation = pool.submit(translate, endpoint, model, text)
                                entry["asr"], entry["translation"] = asr.result(), translation.result()
                        entry["complete"] = entry["asr"]["complete"] and entry["translation"]["complete"]
                    except Exception as error:
                        entry.update(complete=False, error={"type": type(error).__name__, "message": str(error)})
                        torch.cuda.empty_cache()
                    entry["elapsedSeconds"] = time.perf_counter() - started
                    entry["placementAfter"] = api(endpoint, "/api/ps")
                    entry["sampleEndTime"] = time.monotonic()
                api(endpoint, "/api/generate", {"model": model, "keep_alive": 0})
            report["passed"] = len(report["runs"]) == 4 and all(x["complete"] for x in report["runs"])
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
