"""Verify staged browser tones against captured samples and monotonic operation times."""

import argparse
import array
import hashlib
import json
import math
import pathlib
import wave

from verify_tones import amplitude


def inspect_capture(directory, phases, independent):
    """Check every stable phase, packet continuity, WAV identity and session health."""
    report = json.loads((directory / "report.json").read_text(encoding="utf-8"))
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8"))
    packets = []
    with (directory / "events.jsonl").open(encoding="utf-8") as source:
        for line in source:
            item = json.loads(line)
            if item.get("Packet"):
                packets.append(item["Packet"])
    if len(report["Waves"]) != 1 or not packets:
        raise ValueError("Expected one uninterrupted nonempty recording")
    metadata = report["Waves"][0]
    path = directory / metadata["FileName"]
    checks = []
    frequency = manifest["StopwatchFrequency"]
    with wave.open(str(path), "rb") as audio:
        rate, channels = audio.getframerate(), audio.getnchannels()
        if audio.getsampwidth() != 2:
            raise ValueError("Expected PCM16")
        frame_count = audio.getnframes()
        for phase in phases:
            # Exclude control/renderer transition edges, then use exactly one second.
            lower = phase["Ticks"] + frequency
            upper = phase["Ticks"] + 3 * frequency
            selected = [p for p in packets if lower <= p["AvailableTicks"] <= upper]
            if not selected or selected[-1]["FrameStart"] + selected[-1]["FrameCount"] - selected[0]["FrameStart"] < rate:
                raise ValueError("Insufficient stable PCM: " + phase["Phase"])
            start = selected[0]["FrameStart"]
            audio.setpos(start)
            samples = array.array("h", audio.readframes(rate))[::channels]
            amplitudes = {hz: amplitude(samples, rate, hz) for hz in (440, 660, 997)}
            expected = [997] if independent else phase["ExpectedHz"]
            rms = math.sqrt(sum((sample / 32768) ** 2 for sample in samples) / len(samples))
            reference = min((amplitudes[hz] for hz in expected), default=1.0)
            passed = all(amplitudes[hz] > 0.001 for hz in expected)
            passed &= all(amplitudes[hz] < reference * 0.01 for hz in amplitudes if hz not in expected)
            if not expected:
                passed &= rms < 0.0001
            checks.append({"phase": phase["Phase"], "frameStart": start, "frames": rate,
                           "expectedHz": expected, "amplitudes": amplitudes, "rms": rms, "passed": passed})
    contiguous = packets[0]["FrameStart"] == 0 and all(
        current["Sequence"] == previous["Sequence"] + 1 and
        current["FrameStart"] == previous["FrameStart"] + previous["FrameCount"]
        for previous, current in zip(packets, packets[1:]))
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    healthy = (report["ExitCode"] == 0 and report["CleanupCompleted"] and report["GapCount"] == 0
               and report["InvalidTimestampSamples"] == 0 and metadata["HeaderFinalized"]
               and digest == metadata["Sha256"] and frame_count == report["Frames"]
               and sum(p["FrameCount"] for p in packets) == frame_count and contiguous)
    return {"file": str(path), "sha256": digest, "healthy": healthy, "checks": checks,
            "passed": healthy and all(check["passed"] for check in checks)}


def main():
    """Write a new evidence file, failing on missing phases or cleanup/content mismatch."""
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=pathlib.Path)
    args = parser.parse_args()
    directory = args.directory.resolve()
    phases = [json.loads(line) for line in (directory / "operations.jsonl").read_text(encoding="utf-8").splitlines()]
    expected = ["both-tabs", "paused-tabs", "first-only", "both-resumed", "audio-tabs-closed-root-retained", "replacement-tab"]
    if [p["Phase"] for p in phases] != expected:
        raise ValueError("Missing or reordered operation phases")
    run = json.loads((directory / "scope-run.json").read_text(encoding="utf-8"))
    captures = [inspect_capture(directory / "capture", phases, False),
                inspect_capture(directory / "independent-capture", phases, True)]
    passed = (run["ExitCode"] == 0 and run["Error"] is None and not run["OwnedRemaining"]
              and run["ExternalBefore"] == run["ExternalRetained"] and all(c["passed"] for c in captures))
    result = {"criterion": "Each expected tone >0.001; each excluded tone <1% of weakest expected; paused RMS <0.0001",
              "captures": captures, "passed": passed}
    with (directory / "scope-spectrum.json").open("x", encoding="utf-8", newline="\n") as output:
        json.dump(result, output, ensure_ascii=False, indent=2)
        output.write("\n")
    print(json.dumps(result, ensure_ascii=False))
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
