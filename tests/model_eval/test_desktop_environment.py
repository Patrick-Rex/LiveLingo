"""Verify explicit desktop paths without loading a model or accessing the network."""

import hashlib
import json
import os
import pathlib
import platform
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "model_eval"))
from common import configure_cache, load_profile


class DesktopEnvironmentTests(unittest.TestCase):
    """Keep configured cache roots and interpreter identity effective in workers."""

    def test_explicit_cache_root_is_used_and_c_drive_rejected(self):
        """Cache paths inherit a validated private root rather than a hidden default."""
        with tempfile.TemporaryDirectory(prefix="desktop-cache-") as directory:
            with patch.dict(os.environ, {"LIVELINGO_CACHE_ROOT": directory}):
                configure_cache()
                self.assertEqual(pathlib.Path(directory) / "torch", pathlib.Path(os.environ["TORCH_HOME"]))
                self.assertEqual("1", os.environ["HF_HUB_OFFLINE"])
            with patch.dict(os.environ, {"LIVELINGO_CACHE_ROOT": r"C:\bad"}):
                with self.assertRaises(ValueError):
                    configure_cache()

    def test_explicit_interpreter_keeps_version_and_lock_validation(self):
        """Relocated prepared interpreters still require the pinned lock fingerprint."""
        with tempfile.TemporaryDirectory(prefix="desktop-profile-") as directory:
            path = pathlib.Path(directory) / "profile.json"
            lock = b"fixed dependency lock\n"
            path.with_name("requirements.lock").write_bytes(lock)
            profile = {"schemaVersion": 1, "python": platform.python_version(),
                       "interpreter": r"D:\unavailable\python.exe", "lockSha256": hashlib.sha256(lock).hexdigest()}
            path.write_text(json.dumps(profile), encoding="utf-8")
            with self.assertRaises(ValueError):
                load_profile(path)
            self.assertEqual(profile, load_profile(path, sys.executable))
            path.with_name("requirements.lock").write_bytes(b"changed")
            with self.assertRaises(ValueError):
                load_profile(path, sys.executable)


if __name__ == "__main__":
    unittest.main()
