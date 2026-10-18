"""Shared explicit evaluation paths, fingerprints and failure-preserving reports."""

import hashlib
import json
import os
import pathlib
import platform
import sys
from datetime import datetime, timezone


def non_system_path(value):
    """Require an absolute non-system-drive path after resolving links."""
    path = pathlib.Path(value)
    if not path.is_absolute():
        raise ValueError("An absolute non-C path is required")
    path = path.resolve()
    if len(path.drive) != 2 or path.drive[1] != ":" or path.drive.upper() == "C:":
        raise ValueError("Evaluation assets must remain on a non-C drive")
    return path


def new_output(value):
    """Exclusively create an evidence directory, refusing previous results."""
    path = non_system_path(value)
    path.mkdir(parents=True, exist_ok=False)
    return path


def sha256(path):
    """Hash large assets with bounded memory."""
    with pathlib.Path(path).open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def write_json(path, value):
    """Preserve existing evidence and write UTF-8/LF JSON exclusively."""
    with pathlib.Path(path).open("x", encoding="utf-8", newline="\n") as target:
        json.dump(value, target, ensure_ascii=False, indent=2, allow_nan=False)
        target.write("\n")


def configure_cache(offline=True):
    """Set every model cache before importing torch or Hugging Face packages."""
    root = non_system_path(os.environ.get("LIVELINGO_CACHE_ROOT", r"D:\Soft Ware\LiveLingo\cache"))
    values = {"HF_HOME": "huggingface", "HF_HUB_CACHE": "huggingface/hub",
              "TORCH_HOME": "torch", "TORCHINDUCTOR_CACHE_DIR": "torchinductor",
              "CUDA_CACHE_PATH": "cuda", "TEMP": "tmp", "TMP": "tmp",
              "PIP_CACHE_DIR": "pip", "UV_CACHE_DIR": "uv"}
    for key, relative in values.items():
        path = root / relative
        path.mkdir(parents=True, exist_ok=True)
        os.environ[key] = str(path)
    os.environ["HF_HUB_OFFLINE"] = "1" if offline else "0"
    os.environ["TRANSFORMERS_OFFLINE"] = "1" if offline else "0"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"


def load_profile(path, interpreter=None):
    """Reject wrong interpreters and stale dependency fingerprints."""
    path = pathlib.Path(path).resolve()
    value = json.loads(path.read_text(encoding="utf-8"))
    if value["schemaVersion"] != 1 or value["python"] != platform.python_version():
        raise ValueError("Unsupported profile/Python version")
    if pathlib.Path(sys.executable).resolve() != non_system_path(interpreter or value["interpreter"]):
        raise ValueError("Run with the selected profile interpreter")
    if sha256(path.with_name("requirements.lock")) != value["lockSha256"]:
        raise ValueError("Dependency lock fingerprint mismatch")
    return value


def utc_now():
    """Return a timezone-explicit timestamp for run metadata."""
    return datetime.now(timezone.utc).isoformat()


def source_fingerprint():
    """Fingerprint the current evaluation implementation independently of a Git checkout."""
    root = pathlib.Path(__file__).resolve().parents[2]
    files = sorted([*root.glob("tools/model_eval/*.py"), *root.glob("workers/qwen_asr/*.py")])
    digest = hashlib.sha256()
    for path in files:
        digest.update(path.relative_to(root).as_posix().encode() + b"\n")
        digest.update(sha256(path).encode() + b"\n")
    return digest.hexdigest()
