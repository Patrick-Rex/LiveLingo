"""Real process-tree tests for the built Probe model evaluation command."""

import json
import os
import pathlib
import subprocess
import sys
import unittest
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[2]


@unittest.skipUnless(os.environ.get("LIVELINGO_TEST_PROBE"), "Set LIVELINGO_TEST_PROBE for real Job checks")
class SupervisorChecks(unittest.TestCase):
    """Exercise real exit-code retrieval and timeout cleanup of a spawned descendant."""

    def run_fixture(self, wait, timeout, expected):
        """Run one isolated tree, preserving its evidence even when an assertion fails."""
        base = pathlib.Path(r"D:\Soft Ware\LiveLingo\data\evaluation")
        name = "supervisor-test-" + uuid.uuid4().hex
        request_path = base / (name + "-request.json")
        child_file = base / (name + "-child.json")
        request = {"Executable": sys.executable, "Arguments": [str(ROOT / "tests/model_eval/owned_process_fixture.py"),
                   "--output", str(child_file), "--wait", str(wait)],
                   "WorkingDirectory": str(ROOT), "TimeoutSeconds": timeout}
        request_path.write_text(json.dumps(request) + "\n", encoding="utf-8", newline="\n")
        result = subprocess.run([os.environ["LIVELINGO_TEST_PROBE"], "model-eval", "--request", str(request_path),
                                 "--output", str(base / name)], capture_output=True, timeout=25)
        report = json.loads((base / name / "supervisor.json").read_text(encoding="utf-8"))
        self.assertEqual(expected, result.returncode, report)
        self.assertEqual([], report["OwnedRemaining"])
        child = json.loads(child_file.read_text())["ownedChildPid"]
        self.assertIn(child, [p["ProcessId"] for p in report["OwnedObserved"]])
        self.assertEqual(report["ExternalBefore"], report["ExternalRetained"])

    def test_normal_exit_reports_zero_and_reaps_child(self):
        """A successful parent must retain its exit code while its child is killed."""
        self.run_fixture(2, 10, 0)

    def test_timeout_reports_failure_and_reaps_tree(self):
        """Deadline cancellation must terminate both the waiting parent and child."""
        self.run_fixture(120, 2, 3)


if __name__ == "__main__":
    unittest.main()
