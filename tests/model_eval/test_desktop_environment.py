"""Verify explicit desktop paths without loading a model or accessing the network."""

import hashlib
import json
import os
import pathlib
import platform
import sys
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "model_eval"))
from common import configure_cache, load_profile
from workers.qwen_asr.inference import select_device


class DesktopEnvironmentTests(unittest.TestCase):
    """Keep configured cache roots and interpreter identity effective in workers."""

    def test_explicit_cache_root_and_user_default(self):
        """Cache paths inherit a validated private root rather than a hidden default."""
        with tempfile.TemporaryDirectory(prefix="desktop-cache-") as directory:
            with patch.dict(os.environ, {"LIVELINGO_CACHE_ROOT": directory}):
                configure_cache()
                self.assertEqual(pathlib.Path(directory) / "torch", pathlib.Path(os.environ["TORCH_HOME"]))
                self.assertEqual("1", os.environ["HF_HUB_OFFLINE"])
            with patch.dict(os.environ, {"LOCALAPPDATA": directory}):
                os.environ.pop("LIVELINGO_CACHE_ROOT", None)
                configure_cache()
                self.assertEqual(pathlib.Path(directory) / "LiveLingoData" / "cache" / "torch",
                                 pathlib.Path(os.environ["TORCH_HOME"]))

    def test_accelerator_selection_checks_build_and_gpu_availability(self):
        """CPU never opens a GPU driver, while explicit GPU profiles require their own backend."""
        for backend, version, cuda, hip, runtime, device in (
                ("cpu", "2.10.0+cpu", None, None, "none", "cpu"),
                ("cuda", "2.10.0+cu128", "12.8", None, "12.8", "cuda:0"),
                ("rocm", "2.9.1+rocm7.2.1", None, "7.2.53211", "rocm", "cuda:0")):
            with self.subTest(backend=backend):
                torch = SimpleNamespace(__version__=version, version=SimpleNamespace(cuda=cuda, hip=hip),
                                        cuda=Mock())
                profile = {"backend": backend, "torch": version, "cuda": runtime}
                torch.cuda.is_available.return_value = True
                self.assertEqual(device, select_device(torch, profile))
                if backend == "cpu":
                    torch.cuda.is_available.assert_not_called()
                else:
                    torch.cuda.is_available.return_value = False
                    with self.assertRaises(RuntimeError):
                        select_device(torch, profile)
                with self.assertRaises(RuntimeError):
                    select_device(torch, {**profile, "torch": "unrelated build"})

    def test_explicit_interpreter_keeps_version_and_lock_validation(self):
        """Relocated prepared interpreters still require the pinned lock fingerprint."""
        with tempfile.TemporaryDirectory(prefix="desktop-profile-") as directory:
            path = pathlib.Path(directory) / "profile.json"
            lock = b"fixed dependency lock\n"
            path.with_name("requirements.lock").write_bytes(lock)
            profile = {"schemaVersion": 1, "python": ".".join(platform.python_version_tuple()[:2]),
                       "id": "test", "lockSha256": hashlib.sha256(lock).hexdigest()}
            path.write_text(json.dumps(profile), encoding="utf-8")
            with self.assertRaises(ValueError):
                load_profile(path, str(path.parent / "wrong-python.exe"))
            self.assertEqual(profile, load_profile(path, sys.executable))
            path.with_name("requirements.lock").write_bytes(b"changed")
            with self.assertRaises(ValueError):
                load_profile(path, sys.executable)


if __name__ == "__main__":
    unittest.main()
