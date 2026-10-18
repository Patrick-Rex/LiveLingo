"""Verify bounded retry evidence and conservative, explicitly scoped checks."""

import pathlib
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "tools" / "model_eval"))
from fidelity import inspect_fidelity, translate_checked


class FidelityTests(unittest.TestCase):
    """Keep failure evidence and source occurrences intact across retries."""

    def test_name_counts_detect_omission_and_invention(self):
        """Literal name checks reject too few and too many occurrences."""
        source = "中野さん、中野さん、確認してください。"
        self.assertFalse(inspect_fidelity(source, "中野先生，请确认。名")['passed'])
        self.assertTrue(inspect_fidelity(source, "中野先生，中野先生，请确认。")['passed'])
        self.assertFalse(inspect_fidelity(source, "中野，中野，中野，请确认。")['passed'])

    def test_certainty_checks_do_not_reject_uncertain_advertising(self):
        """Uncertain advertising and definite no-coercion are different clauses."""
        source = "宣伝も兼ねてるんだろうけど、無理やりなんてことはないよ。"
        self.assertTrue(inspect_fidelity(source, "也许也是宣传，但不会强迫购买。")['passed'])
        self.assertFalse(inspect_fidelity(source, "也许是宣传，但应该不会强迫购买。")['passed'])
        self.assertFalse(inspect_fidelity("今日中には終わらなくなってしまいます", "今天可能无法完成。")['passed'])

    def test_retry_limit_preserves_each_failed_response(self):
        """A persistent failure exhausts two retries without patching output."""
        def response(*args):
            """Return a fresh result for each simulated model request."""
            return {"complete": True, "response": {"message": {"content": "中野先生。"}}, "elapsedSeconds": 0.1}
        with patch("fidelity.translate", side_effect=response) as call:
            result = translate_checked("http://localhost:1", "translategemma:12b", "中野さん、中野さん。", mode="draft")
        self.assertEqual(3, call.call_count)
        self.assertEqual(3, len(result["attempts"]))
        self.assertIn('Previous draft: "中野先生。"', call.call_args.args[-1])
        self.assertFalse(result["fidelityPassed"])
        self.assertEqual("中野先生。", result["response"]["message"]["content"])
        self.assertFalse(result["qualityPassed"])

    def test_success_stops_without_spending_retry_budget(self):
        """Already faithful text does not incur another model call."""
        with patch("fidelity.translate", return_value={"complete": True, "response": {"message": {"content": "中野，中野。"}}}) as call:
            result = translate_checked("http://localhost:1", "translategemma:12b", "中野さん、中野さん。")
        self.assertEqual(1, call.call_count)
        self.assertTrue(result["fidelityPassed"])
        self.assertFalse(result["qualityPassed"])

    def test_failed_retry_keeps_previous_output_and_error(self):
        """A service failure cannot erase the completed but rejected first attempt."""
        first = {"complete": True, "response": {"message": {"content": "中野。"}}}
        with patch("fidelity.translate", side_effect=[first, TimeoutError("service deadline")]):
            result = translate_checked("http://localhost:1", "translategemma:12b", "中野さん、中野さん。")
        self.assertEqual(2, len(result["attempts"]))
        self.assertEqual("中野。", result["attempts"][0]["response"]["message"]["content"])
        self.assertEqual("TimeoutError", result["error"]["type"])
        self.assertFalse(result["complete"])
        self.assertFalse(result["fidelityPassed"])


if __name__ == "__main__":
    unittest.main()
