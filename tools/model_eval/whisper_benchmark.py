"""Independent CTranslate2 comparison using the user's existing Whisper Turbo assets."""

import argparse
import importlib.metadata
import os
import pathlib
import sys
import time

from common import configure_cache, load_profile, new_output, sha256, source_fingerprint, utc_now, write_json
from compare import GpuSampler


def main():
    """Load only local assets and include lazy segment generation in measured time."""
    parser = argparse.ArgumentParser()
    for name in ("profile", "model", "audio", "output"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--max-window-seconds", type=int, default=30)
    parser.add_argument("--language", choices=("Japanese", "English", "Chinese", "auto"), default="Japanese")
    args = parser.parse_args()
    output = new_output(args.output)
    configure_cache()
    handles = []
    sampler = GpuSampler()
    report = {"schemaVersion": 1, "kind": "whisper-gpu-comparison", "startedUtc": utc_now(),
              "sourceSha256": source_fingerprint(), "qualityPassed": False, "passed": False, "runs": []}
    try:
        report["profile"] = load_profile(args.profile)
        libraries = pathlib.Path(sys.prefix) / "Lib" / "site-packages" / "nvidia"
        for path in libraries.glob("*/bin"):
            handles.append(os.add_dll_directory(str(path)))
            os.environ["PATH"] = str(path) + os.pathsep + os.environ["PATH"]
        import ctranslate2
        from faster_whisper import WhisperModel
        sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
        from workers.qwen_asr.inference import read_audio
        if not 1 <= args.repeats <= 100:
            raise ValueError("Repeat count must be in [1,100]")
        audio = read_audio(args.audio, args.max_window_seconds)
        if ctranslate2.get_cuda_device_count() < 1:
            raise RuntimeError("CUDA unavailable; no CPU fallback")
        report["supportedComputeTypes"] = sorted(ctranslate2.get_supported_compute_types("cuda"))
        model_path = pathlib.Path(args.model)
        if not model_path.is_absolute() or not model_path.is_dir():
            raise ValueError("Existing absolute local Whisper directory required")
        report["modelFiles"] = [{"path": p.name, "bytes": p.stat().st_size, "sha256": sha256(p)}
                                for p in sorted(model_path.iterdir()) if p.is_file()]
        report.update(inputSha256=sha256(args.audio),
                      packages={n: importlib.metadata.version(n) for n in ("faster-whisper", "ctranslate2", "nvidia-cublas-cu12", "nvidia-cudnn-cu12")})
        sampler.thread.start()
        started = time.perf_counter()
        model = WhisperModel(str(model_path), device="cuda", compute_type="float16", local_files_only=True)
        report["modelLoadSeconds"] = time.perf_counter() - started
        language = {"Japanese": "ja", "English": "en", "Chinese": "zh", "auto": None}[args.language]
        report["parameters"] = {"language": args.language, "beamSize": 5, "temperature": 0,
                                "vadFilter": False, "repetitionPenalty": 1.0, "computeType": "float16"}
        for index in range(args.repeats):
            started = time.perf_counter()
            segments, info = model.transcribe(audio, language=language, beam_size=5, temperature=0,
                                              vad_filter=False, repetition_penalty=1.0, no_repeat_ngram_size=0)
            rows = [{"start": s.start, "end": s.end, "text": s.text, "tokens": s.tokens} for s in segments]
            duration = len(audio) / 16000
            report["runs"].append({"temperature": "first-request" if index == 0 else "warm", "segments": rows,
                                    "text": "".join(s["text"] for s in rows), "audioSeconds": duration,
                                    "backendReportedDuration": info.duration,
                                    "detectedLanguage": info.language,
                                    "outOfRangeSegments": [i for i, s in enumerate(rows)
                                                           if s["start"] < 0 or s["end"] > duration or s["end"] < s["start"]],
                                    "elapsedSeconds": time.perf_counter() - started,
                                    "terminationAssessment": "not inferred from segment punctuation"})
        report["passed"] = len(report["runs"]) == args.repeats
    except Exception as error:
        report["error"] = {"type": type(error).__name__, "message": str(error)}
    finally:
        if sampler.thread.ident is not None:
            try:
                report["gpu"] = sampler.finish()
            except Exception as error:
                report.update(passed=False, samplerError={"type": type(error).__name__, "message": str(error)})
        for handle in handles:
            handle.close()
        report["completedUtc"] = utc_now()
        write_json(output / "report.json", report)
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
