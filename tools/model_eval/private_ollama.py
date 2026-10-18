"""Run a disposable Ollama instance beneath the Probe's kill-on-close Job."""

import argparse
import ctypes
import json
import os
import pathlib
import socket
import subprocess
import time

from common import configure_cache, new_output, sha256, source_fingerprint, utc_now, write_json
from translation import api, translate
from fidelity import translate_checked


def stop_owned_service(process, report):
    """Bound shutdown of our Popen child and preserve cleanup failures in evidence."""
    if process is None:
        return
    try:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
        report["ownedServiceExited"] = process.poll() is not None
    except OSError as error:
        report.update(passed=False, cleanupError={"type": type(error).__name__, "message": str(error)})
    except subprocess.TimeoutExpired as error:
        report.update(passed=False, cleanupError={"type": type(error).__name__, "message": str(error)})


def require_owned_job():
    """Refuse to start private services without an enclosing Windows process Job."""
    in_job = ctypes.c_int()
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.GetCurrentProcess.restype = ctypes.c_void_p
    kernel.IsProcessInJob.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(ctypes.c_int)]
    if os.environ.get("LIVELINGO_OWNED_EVALUATION") != "1" or not kernel.IsProcessInJob(kernel.GetCurrentProcess(), None, ctypes.byref(in_job)) or not in_job.value:
        raise RuntimeError("Start through Probe model-eval so all child processes are owned")


def main():
    """Measure selected models on a private port, then terminate only our service."""
    parser = argparse.ArgumentParser()
    parser.add_argument("--ollama", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--models", nargs="+", default=["translategemma:4b"])
    parser.add_argument("--pull-missing", action="store_true")
    parser.add_argument("--inputs", help="JSON items with id, text and origin; references remain unverified")
    parser.add_argument("--max-new-tokens", type=int, default=512)
    parser.add_argument("--prompt-policy", choices=("official", "faithful-v1", "faithful-v2", "faithful-v3"), default="official")
    parser.add_argument("--fidelity-retries", type=int, choices=(0, 1, 2), default=0)
    parser.add_argument("--fidelity-mode", choices=("source", "draft"), default="source")
    args = parser.parse_args()
    require_owned_job()
    if any(m not in ("translategemma:4b", "translategemma:12b") for m in args.models):
        raise ValueError("Unsupported model")
    output = new_output(args.output)
    configure_cache(offline=False)
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = reservation.getsockname()[1]
    endpoint = f"http://127.0.0.1:{port}"
    environment = os.environ.copy()
    environment.update(OLLAMA_HOST=f"127.0.0.1:{port}", OLLAMA_MODELS=r"D:\Soft Ware\Ollama",
                       OLLAMA_CONTEXT_LENGTH="4096", OLLAMA_NUM_PARALLEL="1", OLLAMA_MAX_LOADED_MODELS="1")
    report = {"schemaVersion": 1, "startedUtc": utc_now(), "kind": "private-ollama-smoke",
              "endpoint": endpoint, "qualityPassed": False, "models": [], "passed": False, "sourceSha256": source_fingerprint()}
    process = None
    with (output / "service.log").open("xb") as log:
        try:
            inputs = [{"id": f"smoke-{i}", "origin": "authored-test", "text": "田中さんが来ないわけではありません。確認して、確認してから、決めます。"}
                      for i in range(3)]
            if args.inputs:
                input_path = pathlib.Path(args.inputs)
                if not input_path.is_absolute() or input_path.stat().st_size > 1024 * 1024:
                    raise ValueError("Bounded absolute input JSON required")
                inputs = json.loads(input_path.read_text(encoding="utf-8"))["items"]
                if not 1 <= len(inputs) <= 100 or len({x["id"] for x in inputs}) != len(inputs):
                    raise ValueError("Expected 1-100 unique input items")
                if any(not isinstance(x.get("text"), str) or not x["text"].strip() or len(x["text"]) > 12000
                       or x.get("origin") not in ("official-reference", "qwen-asr", "whisper-asr", "authored-test") for x in inputs):
                    raise ValueError("Invalid translation input item")
                report["inputsSha256"] = sha256(input_path)
            process = subprocess.Popen([args.ollama, "serve"], env=environment, stdout=log, stderr=log,
                                       creationflags=subprocess.CREATE_NO_WINDOW)
            report["ownedServicePid"] = process.pid
            for _ in range(60):
                time.sleep(0.5)
                if process.poll() is not None:
                    raise RuntimeError("Private service exited during startup")
                try:
                    report["version"] = api(endpoint, "/api/version", timeout=2)
                    break
                except OSError:
                    continue
            else:
                raise TimeoutError("Private service startup deadline exceeded")
            for model in args.models:
                tags = api(endpoint, "/api/tags")
                if model not in [m["name"] for m in tags["models"]]:
                    if not args.pull_missing:
                        raise ValueError("Model missing; explicit preparation required: " + model)
                    # Preparation is explicit, bounded by the supervising host's deadline.
                    api(endpoint, "/api/pull", {"model": model, "stream": False}, timeout=3600)
                    tags = api(endpoint, "/api/tags")
                tag = next(m for m in tags["models"] if m["name"] == model)
                entry = {"model": model, "identity": tag, "show": api(endpoint, "/api/show", {"model": model}),
                         "before": api(endpoint, "/api/ps"), "runs": []}
                report["models"].append(entry)
                for index, item in enumerate(inputs):
                    result = (translate_checked(endpoint, model, item["text"], args.max_new_tokens,
                                                args.prompt_policy, args.fidelity_retries, args.fidelity_mode)
                              if args.fidelity_retries else
                              translate(endpoint, model, item["text"], args.max_new_tokens, args.prompt_policy))
                    result.update(id=item["id"], origin=item["origin"])
                    result["temperature"] = "first-request" if index == 0 else "warm"
                    result["placementAfter"] = api(endpoint, "/api/ps")
                    entry["runs"].append(result)
                # This unload affects only our private endpoint, never an external service.
                api(endpoint, "/api/generate", {"model": model, "keep_alive": 0})
            report["passed"] = all(r["complete"] and r.get("fidelityPassed", True)
                                   for m in report["models"] for r in m["runs"])
        except Exception as error:
            report["error"] = {"type": type(error).__name__, "message": str(error)}
        finally:
            stop_owned_service(process, report)
            report["completedUtc"] = utc_now()
            write_json(output / "report.json", report)
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
