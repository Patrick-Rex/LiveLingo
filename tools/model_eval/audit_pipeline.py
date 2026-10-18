"""Read recorded M3 evidence; never substitute this audit for an actual GPU run."""

import argparse
import hashlib
import json
import math
import pathlib
import statistics


def rows(path, allow_partial=False):
    """Read a bounded JSONL file without loading raw PCM or an entire transcript log."""
    if not path.exists():
        return
    if path.stat().st_size > 65 * 1024 * 1024:
        raise ValueError(f"evidence_file_capacity:{path.name}")
    with path.open(encoding="utf-8") as stream:
        for line in stream:
            if allow_partial and not line.endswith("\n"):
                break
            if len(line) > 2 * 1024 * 1024:
                raise ValueError(f"evidence_line_capacity:{path.name}")
            yield json.loads(line)


def percentiles(values):
    """Return nearest-rank samples, preserving absent evidence as null."""
    values = sorted(values)
    if not values:
        return {"count": 0, "p50": None, "p95": None, "maximum": None}
    return {"count": len(values), "p50": values[math.ceil(len(values) * .5) - 1],
            "p95": values[math.ceil(len(values) * .95) - 1], "maximum": values[-1]}


def trend(times, values):
    """Describe measured growth with quarter medians and a least-squares slope."""
    if not values:
        return None
    quarter = max(1, len(values) // 4)
    slope = statistics.linear_regression(times, values).slope if len(set(times)) > 1 else None
    return {"samples": len(values), "firstQuarterMedian": statistics.median(values[:quarter]),
            "lastQuarterMedian": statistics.median(values[-quarter:]), "maximum": max(values), "perSecondSlope": slope}


def audit(directory, allow_in_progress=False):
    """Check exact recorded versions, final evidence, resource bounds and cleanup."""
    directory = pathlib.Path(directory)
    report_path = directory / "report.json"
    report = json.loads(report_path.read_text(encoding="utf-8")) if report_path.exists() else None
    if report is None and not allow_in_progress:
        raise ValueError("final_report_missing")
    failures = []
    states, retired, last_sequence = {}, set(), 0
    first_translated_keys, first_translated_seen = [], set()
    change_count = 0
    identities_recorded = True
    for change in rows(directory / "changes.jsonl", allow_in_progress):
        change_count += 1
        if change["Sequence"] <= last_sequence:
            failures.append("state_sequence_not_increasing")
        last_sequence = change["Sequence"]
        identities_recorded &= "Identity" in change
        for key in change["Retired"]:
            if key in retired:
                failures.append("retired_identity_repeated")
            retired.add(key)
            states.pop(key, None)
        for segment in change["Changed"]:
            key = segment["Key"]["Id"]
            if segment["TranslationState"] == 2 and key not in first_translated_seen:
                first_translated_seen.add(key)
                first_translated_keys.append(key)
            previous = states.get(key)
            if key in retired:
                failures.append("retired_identity_resurrected")
            if previous is not None:
                if segment["SubtitleRevision"] <= previous["SubtitleRevision"] or segment["SourceRevision"] < previous["SourceRevision"]:
                    failures.append("segment_version_regressed")
                if (segment["Source"] != previous["Source"] or segment["Range"] != previous["Range"]) and segment["SourceRevision"] <= previous["SourceRevision"]:
                    failures.append("changed_source_without_revision")
                if previous["Phase"] == 2 and segment != previous and segment["Key"]["Generation"] <= previous["Key"]["Generation"]:
                    failures.append("final_changed_without_reopen")
            translated = segment.get("TranslatedSourceRevision")
            if translated is not None and translated > segment["SourceRevision"]:
                failures.append("translation_from_future_source")
            if segment["Phase"] == 2 and (segment["TranslationState"] != 2 or translated != segment["SourceRevision"] or
                    not segment["Boundary"]["CanClose"] or not segment["Boundary"]["HasRightContext"] or segment["Boundary"]["StableObservations"] < 2):
                failures.append("final_without_current_translation_and_boundary")
            states[key] = segment
    times, backlog, memory, spill, handles, private = [], [], [], [], [], []
    workers = {}
    options = report["Options"] if report else None
    for metric in rows(directory / "metrics.jsonl", allow_in_progress):
        times.append(metric["ElapsedSeconds"])
        backlog.append(metric["BacklogSamples"] / 16000)
        memory.append(metric["MemorySamples"] / 16000)
        spill.append(metric["SpilledSamples"] * 4)
        handles.append(metric["Handles"])
        private.append(metric["PrivateBytes"] / 1048576)
        if metric["BacklogSamples"] != max(0, metric["AudioEnd"] - metric["RecognizedEnd"]):
            failures.append("hidden_audio_backlog")
        if metric["Pins"] not in (0, 1):
            failures.append("window_lease_capacity")
        if options and (metric["MemorySamples"] > options["MemorySamples"] or metric["SpilledSamples"] > options["SpillSamples"] or metric["ClosedQueue"] > options["ClosedQueueCapacity"]):
            failures.append("recorded_resource_capacity_exceeded")
        for worker in metric.get("Workers", []):
            identity = worker["Identity"]
            key = f'{identity["ProcessId"]}@{identity["CreationTimeUtc"]}'
            values = workers.setdefault(key, {"identity": identity, "times": [], "private": [], "handles": []})
            values["times"].append(metric["ElapsedSeconds"])
            values["private"].append(worker["PrivateBytes"] / 1048576)
            values["handles"].append(worker["Handles"])
    if not times or not change_count:
        failures.append("missing_pipeline_evidence")
    if report:
        final = report.get("Final")
        if not final:
            failures.append("missing_final_snapshot")
        else:
            if not final["Subtitles"]["Stopped"] or final["Pins"] or final["MemorySamples"] or final["SpilledSamples"]:
                failures.append("final_resources_not_released")
            if states != {value["Key"]["Id"]: value for value in final["Subtitles"]["Segments"]}:
                failures.append("delta_and_final_snapshot_mismatch")
            if retired != set(final["Subtitles"]["RetiredIds"]):
                failures.append("retired_history_mismatch")
        if report["Remaining"] or report["TemporaryRemaining"]:
            failures.append("recorded_owned_resources_remain")
        if report.get("QualityPassed"):
            failures.append("unsupported_automatic_quality_claim")
    retained_first_readable = None
    retired_first_readable_count = None
    first_readable_association_complete = None
    if report and report.get("Capture") is None and "FirstReadableSeconds" in report:
        values = report["FirstReadableSeconds"]
        first_readable_association_complete = len(values) == len(first_translated_keys)
        if first_readable_association_complete:
            retained_first_readable = percentiles([value for key, value in zip(first_translated_keys, values) if key in states])
            retired_first_readable_count = sum(key in retired for key in first_translated_keys)
        else:
            failures.append("first_readable_identity_count_mismatch")
    gpu = list(rows(directory / "gpu.jsonl", allow_in_progress))
    operations = list(rows(directory / "operations.jsonl", allow_in_progress))
    repetition_rejections = [value for value in rows(directory / "rejections.jsonl", allow_in_progress)
                            if value.get("Reason") == "observed_repetition_reduced"]
    repetition_attempts = [value for value in operations if value.get("Disposition") == "rejected:observed_repetition_reduced"]
    if report:
        for operation in repetition_attempts:
            if not options.get("PreserveObservedRepetitions", False) or operation.get("RecognitionTokenBudget") not in (1024, 2048) or not operation.get("GenerationComplete"):
                failures.append("repetition_check_outside_enabled_recovery")
            evidence = [value for value in repetition_rejections if value["Request"] == operation["Request"]]
            if len(evidence) != 1 or evidence[0].get("Previous") is None or evidence[0].get("Current") is None:
                failures.append("repetition_rejection_evidence_missing")
        if len(repetition_rejections) != len(repetition_attempts):
            failures.append("repetition_rejection_count_mismatch")
    for operation in operations:
        window = operation.get("Window")
        budget = operation.get("RecognitionTokenBudget")
        if window:
            logical = operation["Request"]["Range"]
            if not logical["Start"] <= window["Start"] < window["End"] <= logical["End"]:
                failures.append("operation_window_outside_logical_range")
            if options and window["End"] - window["Start"] > max(options.get("MaximumWindowSamples", 480000), options.get("AlignmentRecoverySamples", 0)):
                failures.append("operation_window_exceeds_enabled_budget")
        if "RecognitionTokenBudget" in operation and operation.get("CountedAttempt", True) and operation["Request"]["Kind"] == 0:
            if window is None or budget not in (512, 1024, 2048):
                failures.append("recognition_attempt_metadata_missing")
            elif window["End"] - window["Start"] > 480000 * (budget // 512):
                failures.append("recognition_window_token_budget_mismatch")
            if options and budget in (1024, 2048) and options.get("AlignmentRecoverySamples", 0) < 480000 * (budget // 512):
                failures.append("recognition_recovery_not_enabled")
        for name in ("QueueSeconds", "HostSeconds", "ModelSeconds"):
            value = operation.get(name)
            if value is not None and (not isinstance(value, (int, float)) or not math.isfinite(value) or value < 0):
                failures.append("invalid_operation_timing")
    attempts = sum(x.get("CountedAttempt", True) for x in operations)
    if report and operations and report.get("Final") and attempts != report["Final"]["RecognitionCalls"] + report["Final"]["TranslationCalls"]:
        failures.append("model_operation_count_mismatch")
    return {"kind": "recorded-evidence-audit", "auditorSha256": hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest(),
            "actualRunReportedPassed": report.get("Passed") if report else None,
            "auditPassed": not failures if report else None, "inProgress": report is None,
            "failures": sorted(set(failures)), "elapsedAudioWallSeconds": times[-1] if times else None,
            "changes": change_count, "segments": len(states), "finalSegments": sum(s["Phase"] == 2 for s in states.values()),
            "captureIdentitiesRecorded": identities_recorded, "operationCount": len(operations),
            "recognitionBudgetsRecorded": bool(operations) and all("RecognitionTokenBudget" in x for x in operations),
            "observedRepetitionRejections": len(repetition_attempts),
            "queueSeconds": percentiles([x["QueueSeconds"] for x in operations if x["QueueSeconds"] is not None]),
            "hostComputeSeconds": sum(x["HostSeconds"] for x in operations) if operations else None,
            "firstReadableSeconds": percentiles(report.get("FirstReadableSeconds", [])) if report else None,
            "retainedFirstReadableSeconds": retained_first_readable,
            "retiredFirstReadableCandidates": retired_first_readable_count,
            "firstReadableAssociationComplete": first_readable_association_complete,
            "finalSeconds": percentiles(report.get("FinalSeconds", [])) if report else None,
            "finalAfterRangeEndSeconds": percentiles(report.get("FinalAfterRangeEndSeconds", [])) if report else None,
            "backlogSeconds": trend(times, backlog), "memoryAudioSeconds": trend(times, memory),
            "spillBytes": trend(times, spill), "hostPrivateMiB": trend(times, private), "hostHandles": trend(times, handles),
            "gpuMiB": percentiles([x["DeviceMemoryMiB"] for x in gpu]),
            "workers": [{"identity": v["identity"], "privateMiB": trend(v["times"], v["private"]),
                         "handles": trend(v["times"], v["handles"])} for v in workers.values()]}


def main():
    """Print an explicit offline audit; incomplete runs never return a passing verdict."""
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=pathlib.Path)
    parser.add_argument("--progress", action="store_true")
    args = parser.parse_args()
    result = audit(args.directory, args.progress)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if result["auditPassed"] is True else 2 if result["inProgress"] else 3


if __name__ == "__main__":
    raise SystemExit(main())
