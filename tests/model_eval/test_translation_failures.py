"""Failure responses must remain visible rather than becoming accepted translations."""

import pathlib
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "tools/model_eval"))
from translation import NoRedirect, api, translate


class TranslationFailures(unittest.TestCase):
    """Exercise termination, network and redirection acceptance boundaries."""

    def test_empty_truncated_or_unfinished_responses_are_incomplete(self):
        """Neither a full stop nor done=true alone proves a valid translation."""
        responses = [
            {"done": True, "done_reason": "length", "message": {"content": "结束。"}},
            {"done": True, "done_reason": "stop", "message": {"content": " \n"}},
            {"done": False, "message": {"content": "途中"}},
        ]
        for response in responses:
            with self.subTest(response=response), patch("translation.api", return_value=response):
                result = translate("http://127.0.0.1:12345", "translategemma:4b", "はい。")
                self.assertFalse(result["complete"])
                self.assertEqual(response, result["response"])

    def test_network_failure_propagates_without_manufacturing_output(self):
        """The batch must record a service error instead of an empty successful run."""
        with patch("translation.api", side_effect=ConnectionRefusedError("test service unavailable")):
            with self.assertRaises(ConnectionRefusedError):
                translate("http://127.0.0.1:12345", "translategemma:4b", "はい。")

    def test_nonlocal_endpoints_and_redirects_are_rejected(self):
        """A local inference request must not follow an off-machine redirect."""
        with self.assertRaises(ValueError):
            api("https://example.com", "/api/chat")
        with self.assertRaises(ValueError):
            NoRedirect().redirect_request(None, None, 302, "redirect", {}, "https://example.com")


if __name__ == "__main__":
    unittest.main()
