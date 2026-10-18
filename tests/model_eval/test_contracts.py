"""Behavior checks which do not import torch, load models or use a GPU."""

import pathlib
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "model_eval"))
sys.path.insert(0, str(ROOT))
from common import new_output, non_system_path, write_json
from workers.qwen_asr.output import parse_output
from translation import prompt
from quality import edit_distance, normalize, score


class EvaluationContracts(unittest.TestCase):
    """Keep ownership, immutable evidence and transcription semantics testable."""

    def test_repeated_words_and_sentences_are_preserved(self):
        """Model output must retain repetitions even beyond upstream heuristics."""
        body = "はい、" * 30 + "行かないわけではない。" * 25
        result = parse_output("language Japanese<asr_text>" + body, None, [10, 151645], [151645], 512)
        self.assertEqual(body, result["text"])
        self.assertTrue(result["complete"])

    def test_token_limit_without_eos_is_incomplete(self):
        """Punctuation must not disguise a generation stopped at its budget."""
        result = parse_output("終わりました。", "Japanese", [1, 2], [99], 2)
        self.assertTrue(result["truncated"])
        self.assertFalse(result["complete"])

    def test_unknown_early_stop_is_not_complete(self):
        """An early stop without EOS cannot be called a finalized transcript."""
        result = parse_output("途中", "Japanese", [1], [99], 20)
        self.assertFalse(result["truncated"])
        self.assertFalse(result["complete"])

    def test_model_no_speech_requires_empty_output_and_eos(self):
        """Normalize the model sentinel without mistaking incomplete empty output for silence."""
        result = parse_output("language None<asr_text>", None, [99], [99], 512)
        self.assertIsNone(result["language"])
        self.assertTrue(result["noSpeech"])
        self.assertTrue(result["complete"])
        self.assertFalse(parse_output("language None<asr_text>", None, [1], [99], 512)["noSpeech"])

    def test_literal_marker_inside_text_is_preserved(self):
        """Only a leading model language header may be stripped."""
        body = "表示は <asr_text> のままです。"
        self.assertEqual(body, parse_output(body, "Japanese", [99], [99], 20)["text"])

    def test_official_translation_prompt_matches_golden(self):
        """Lock the official wording and exact two blank lines before source data."""
        expected = (pathlib.Path(__file__).parent / "translation-prompt.txt").read_text(encoding="utf-8")
        self.assertEqual(expected.rstrip("\n"), prompt("確認して、確認してください。"))

    def test_translation_preserves_source_and_rejects_empty(self):
        """Source repetitions and whitespace are data, while empty requests fail."""
        source = " はい。" * 30 + "\n"
        self.assertTrue(prompt(source).endswith(":\n\n\n" + source))
        with self.assertRaises(ValueError):
            prompt(" \n")

    def test_authorized_prompt_constraints_preserve_original_payload(self):
        """The comparison variant adds only instructions and retains every source occurrence."""
        source = "中野さん、中野さん。行かないわけではありません。\n"
        value = prompt(source, "faithful-v1")
        self.assertTrue(value.endswith(":\n\n\n" + source))
        self.assertIn("Do not omit or merge repeated occurrences.", value)
        self.assertIn("Do not soften assertions or introduce uncertainty", value)
        self.assertNotIn("Do not omit or merge", prompt(source))
        self.assertTrue(prompt(source, "faithful-v2").endswith(":\n\n\n" + source))
        self.assertIn("without adding repetitions absent from the source.", prompt(source, "faithful-v2"))
        self.assertTrue(prompt(source, "faithful-v3").endswith(":\n\n\n" + source))
        self.assertIn("do not invent extra occurrences.", prompt(source, "faithful-v3"))
        with self.assertRaises(ValueError):
            prompt(source, "unknown")

    def test_cer_counts_missing_real_repetition(self):
        """Removing one real occurrence must count as an error, not normalization."""
        self.assertEqual("はいはい", normalize("はい、はい。"))
        self.assertEqual(2, edit_distance(normalize("はい、はい。"), normalize("はい。")))

    def test_unverified_reference_cannot_enter_quality_statistics(self):
        """Even matching text is unscored until a person verifies the reference."""
        result = score({"id": "sample", "referenceText": "同じ", "humanVerified": False},
                       {"text": "同じ", "complete": True})
        self.assertFalse(result["scored"])
        self.assertNotIn("cer", result)

    def test_negative_sentence_deletion_remains_an_error(self):
        """Normalization must retain the Japanese negative predicate."""
        self.assertGreater(edit_distance(normalize("行かない。"), normalize("行く。")), 0)

    def test_official_independent_reference_preserves_assistant_provenance(self):
        """Delegated source verification permits metrics without inventing a human review."""
        item = {"id": "official", "referenceText": "はい、はい。", "humanVerified": False,
                "referenceVerification": {"kind": "official-paired-script", "sourceCheckedBy": "assistant",
                                          "referenceIndependentOfModels": True, "sourceSha256": "a" * 64,
                                          "sourcePage": "official script page 2", "alignmentEvidence": "paired clip boundaries audited"}}
        result = score(item, {"text": "はい。", "complete": True})
        self.assertTrue(result["scored"])
        self.assertFalse(result["humanVerified"])
        self.assertEqual(0.5, result["cer"])
        del item["referenceVerification"]["alignmentEvidence"]
        self.assertFalse(score(item, {"text": "はい。", "complete": True})["scored"])

    def test_reject_system_and_relative_paths(self):
        """Evaluation writes cannot silently use the working or system drive."""
        for value in ("relative", r"C:\Users\test", r"C:\Temp\..\Models"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                non_system_path(value)

    def test_existing_evidence_is_never_overwritten(self):
        """Both run directories and individual reports are exclusive writes."""
        base = pathlib.Path(r"D:\Soft Ware\LiveLingo\cache\tmp")
        with tempfile.TemporaryDirectory(prefix="m2-contract-", dir=base) as directory:
            with self.assertRaises(FileExistsError):
                new_output(directory)
            file = pathlib.Path(directory) / "report.json"
            write_json(file, {"original": True})
            with self.assertRaises(FileExistsError):
                write_json(file, {"original": False})
            self.assertIn('"original": true', file.read_text())


if __name__ == "__main__":
    unittest.main()
