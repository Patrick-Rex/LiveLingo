"""Verify that ranged downloads cannot publish corrupt or misaddressed model bytes."""

import hashlib
import pathlib
import sys
import tempfile
import unittest
from unittest.mock import MagicMock, patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "tools/model_eval"))
from download import download_large, fetch_range


class DownloadChecks(unittest.TestCase):
    """Exercise proxy range mistakes, resumable files and final digest enforcement."""

    def test_wrong_content_range_is_rejected(self):
        """A cached response for another offset must never be accepted as this chunk."""
        response = MagicMock()
        response.__enter__.return_value = response
        response.status = 206
        response.headers = {"Content-Range": "bytes 0-3/8"}
        with patch("download.urllib.request.urlopen", return_value=response), patch("download.time.sleep"):
            with self.assertRaises(ValueError):
                fetch_range("https://example.invalid/model", 4, 7, 8)
        response.read.assert_not_called()

    def test_resume_publishes_only_after_full_hash_matches(self):
        """Completed chunks are reused while the final file still requires its official digest."""
        data = b"a" * (8 * 1024 * 1024) + b"tail"
        with tempfile.TemporaryDirectory(prefix="range-test-", dir=r"D:\Soft Ware\LiveLingo\cache\tmp") as directory:
            target = pathlib.Path(directory) / "model.safetensors"
            target.with_name(target.name + ".download").write_bytes(data[:-4])
            with patch("download.fetch_range", return_value=b"tail") as fetch:
                download_large("https://example.invalid/model", target, len(data), hashlib.sha256(data).hexdigest())
            fetch.assert_called_once_with("https://example.invalid/model", len(data) - 4, len(data) - 1, len(data))
            self.assertEqual(data, target.read_bytes())

    def test_corrupt_download_does_not_publish_model(self):
        """A failed hash leaves partial evidence, without a loadable final filename."""
        with tempfile.TemporaryDirectory(prefix="range-test-", dir=r"D:\Soft Ware\LiveLingo\cache\tmp") as directory:
            target = pathlib.Path(directory) / "model.safetensors"
            with patch("download.fetch_range", return_value=b"bad"), self.assertRaises(ValueError):
                download_large("https://example.invalid/model", target, 3, hashlib.sha256(b"yes").hexdigest())
            self.assertFalse(target.exists())
            self.assertEqual(b"bad", target.with_name(target.name + ".download").read_bytes())


if __name__ == "__main__":
    unittest.main()
