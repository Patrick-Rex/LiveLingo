"""Check that recorded-evidence audits detect forged versions and missing completion."""

import copy
import json
import pathlib
import tempfile
import unittest

from tools.model_eval.audit_pipeline import audit


class PipelineAuditTests(unittest.TestCase):
    """Use small isolated records; these checks do not execute models or devices."""

    def test_valid_record_and_invalid_final_are_distinguished(self):
        """A current translated final passes, but an older translation cannot."""
        with tempfile.TemporaryDirectory(prefix="pipeline-audit-") as folder:
            directory = pathlib.Path(folder)
            segment = self.segment()
            self.write(directory, [segment], segment)
            self.assertTrue(audit(directory)["auditPassed"])
            segment["TranslatedSourceRevision"] = 0
            self.write(directory, [segment], segment)
            result = audit(directory)
            self.assertFalse(result["auditPassed"])
            self.assertIn("final_without_current_translation_and_boundary", result["failures"])

    def test_source_and_final_cannot_change_without_new_identity(self):
        """Evidence must expose both missing revisions and an unauthorized final edit."""
        with tempfile.TemporaryDirectory(prefix="pipeline-audit-") as folder:
            directory = pathlib.Path(folder)
            first = self.segment()
            second = copy.deepcopy(first)
            second["Source"] = "変更された原文"
            second["SubtitleRevision"] = 3
            self.write(directory, [first, second], second)
            result = audit(directory)
            self.assertIn("changed_source_without_revision", result["failures"])
            self.assertIn("final_changed_without_reopen", result["failures"])

    def test_running_evidence_never_claims_completion(self):
        """Partial lines are tolerated only for an explicitly in-progress view."""
        with tempfile.TemporaryDirectory(prefix="pipeline-audit-") as folder:
            directory = pathlib.Path(folder)
            self.write(directory, [self.segment()], self.segment(), final=False)
            with (directory / "changes.jsonl").open("a", encoding="utf-8") as stream:
                stream.write('{"Sequence":')
            result = audit(directory, allow_in_progress=True)
            self.assertTrue(result["inProgress"])
            self.assertIsNone(result["auditPassed"])
            with self.assertRaisesRegex(ValueError, "final_report_missing"):
                audit(directory)

    def test_attempt_window_must_match_logical_and_enabled_recovery_budget(self):
        """An apparently complete report cannot hide an oversized or unanchored model input."""
        with tempfile.TemporaryDirectory(prefix="pipeline-audit-") as folder:
            directory = pathlib.Path(folder)
            self.write(directory, [self.segment()], self.segment())
            path = directory / "report.json"
            report = json.loads(path.read_text(encoding="utf-8"))
            report["Final"].update(RecognitionCalls=1, TranslationCalls=0)
            path.write_text(json.dumps(report), encoding="utf-8")
            operation = {"Request": {"Kind": 0, "Range": {"Start": 0, "End": 960000}},
                         "Window": {"Start": 0, "End": 960000}, "RecognitionTokenBudget": 1024,
                         "CountedAttempt": True, "HostSeconds": 1, "QueueSeconds": 0}
            log = directory / "operations.jsonl"
            log.write_text(json.dumps(operation) + "\n", encoding="utf-8")
            result = audit(directory)
            self.assertIn("operation_window_exceeds_enabled_budget", result["failures"])
            self.assertIn("recognition_recovery_not_enabled", result["failures"])
            report["Options"]["AlignmentRecoverySamples"] = 960000
            path.write_text(json.dumps(report), encoding="utf-8")
            self.assertTrue(audit(directory)["auditPassed"])
            operation["Window"]["End"] += 1
            log.write_text(json.dumps(operation) + "\n", encoding="utf-8")
            self.assertIn("operation_window_outside_logical_range", audit(directory)["failures"])

    def test_repetition_rejection_requires_enabled_scope_and_raw_evidence(self):
        """A guard reason alone cannot establish that the authorized check actually ran."""
        with tempfile.TemporaryDirectory(prefix="pipeline-audit-") as folder:
            directory = pathlib.Path(folder)
            self.write(directory, [self.segment()], self.segment())
            path = directory / "report.json"
            report = json.loads(path.read_text(encoding="utf-8"))
            report["Final"].update(RecognitionCalls=1, TranslationCalls=0)
            report["Options"]["AlignmentRecoverySamples"] = 960000
            path.write_text(json.dumps(report), encoding="utf-8")
            request = {"Id": "guard-request", "Kind": 0, "Range": {"Start": 0, "End": 960000}}
            operation = {"Request": request, "Window": request["Range"], "RecognitionTokenBudget": 1024,
                         "CountedAttempt": True, "HostSeconds": 1, "QueueSeconds": 0, "GenerationComplete": True,
                         "Disposition": "rejected:observed_repetition_reduced"}
            (directory / "operations.jsonl").write_text(json.dumps(operation) + "\n", encoding="utf-8")
            result = audit(directory)
            self.assertIn("repetition_check_outside_enabled_recovery", result["failures"])
            self.assertIn("repetition_rejection_evidence_missing", result["failures"])
            report["Options"]["PreserveObservedRepetitions"] = True
            path.write_text(json.dumps(report), encoding="utf-8")
            rejection = {"Request": request, "Reason": "observed_repetition_reduced",
                         "Previous": {"Range": {"Start": 0, "End": 480000}, "Text": "はい、はい"},
                         "Current": {"Range": request["Range"], "Text": "はい"}}
            (directory / "rejections.jsonl").write_text(json.dumps(rejection) + "\n", encoding="utf-8")
            result = audit(directory)
            self.assertTrue(result["auditPassed"])
            self.assertEqual(1, result["observedRepetitionRejections"])

    def test_retired_candidate_remains_in_raw_latency_but_not_retained_statistics(self):
        """A withdrawn no-speech draft must not be relabeled as a retained or correct subtitle."""
        with tempfile.TemporaryDirectory(prefix="pipeline-audit-") as folder:
            directory = pathlib.Path(folder)
            segment = self.segment()
            segment["Phase"] = 1
            segment["Boundary"]["CanClose"] = False
            self.write(directory, [segment], segment)
            key = segment["Key"]["Id"]
            with (directory / "changes.jsonl").open("a", encoding="utf-8") as stream:
                stream.write(json.dumps({"Sequence": 2, "Changed": [], "Retired": [key]}) + "\n")
            path = directory / "report.json"
            report = json.loads(path.read_text(encoding="utf-8"))
            report["Final"]["Subtitles"].update(Segments=[], RetiredIds=[key])
            report.update(Capture=None, FirstReadableSeconds=[2.5])
            path.write_text(json.dumps(report), encoding="utf-8")
            result = audit(directory)
            self.assertTrue(result["auditPassed"])
            self.assertTrue(result["firstReadableAssociationComplete"])
            self.assertEqual(1, result["firstReadableSeconds"]["count"])
            self.assertEqual(0, result["retainedFirstReadableSeconds"]["count"])
            self.assertEqual(1, result["retiredFirstReadableCandidates"])
            report["FirstReadableSeconds"] = []
            path.write_text(json.dumps(report), encoding="utf-8")
            result = audit(directory)
            self.assertFalse(result["firstReadableAssociationComplete"])
            self.assertIsNone(result["retainedFirstReadableSeconds"])
            self.assertIn("first_readable_identity_count_mismatch", result["failures"])

    @staticmethod
    def segment():
        """Create one internally consistent final record."""
        return {"Key": {"Id": "example-segment", "Generation": 1}, "SourceRevision": 1,
                "SubtitleRevision": 2, "Source": "行きません。", "Translation": "不去。",
                "TranslatedSourceRevision": 1, "TranslationState": 2, "Phase": 2,
                "Range": {"Start": 0, "End": 16000},
                "Boundary": {"CanClose": True, "HasRightContext": True, "StableObservations": 2}}

    @staticmethod
    def write(directory, changes, segment, final=True):
        """Write only enough real-shape evidence to test the independent auditor."""
        data = [{"Sequence": index + 1, "Changed": [value], "Retired": []} for index, value in enumerate(changes)]
        (directory / "changes.jsonl").write_text("".join(json.dumps(x) + "\n" for x in data), encoding="utf-8")
        metric = {"ElapsedSeconds": 1, "BacklogSamples": 0, "AudioEnd": 16000, "RecognizedEnd": 16000,
                  "MemorySamples": 0, "SpilledSamples": 0, "Pins": 0, "Handles": 10,
                  "PrivateBytes": 1024, "ClosedQueue": 0}
        (directory / "metrics.jsonl").write_text(json.dumps(metric) + "\n", encoding="utf-8")
        if final:
            report = {"Options": {"MemorySamples": 480000, "SpillSamples": 0, "ClosedQueueCapacity": 1},
                      "Final": {"Subtitles": {"Stopped": True, "Segments": [segment], "RetiredIds": []},
                                "Pins": 0, "MemorySamples": 0, "SpilledSamples": 0},
                      "Remaining": [], "TemporaryRemaining": [], "QualityPassed": False, "Passed": True}
            (directory / "report.json").write_text(json.dumps(report), encoding="utf-8")


if __name__ == "__main__":
    unittest.main()
