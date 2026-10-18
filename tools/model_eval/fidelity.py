"""Bounded source-based fidelity experiment; passing is not semantic certification."""

import collections
import json
import re
import time

from translation import translate


def inspect_fidelity(source, target):
    """Check literal CJK honorific names and two explicit Japanese certainty forms."""
    names = collections.Counter(re.findall(r"([\u4e00-\u9fff]{1,6})(?:さん|様)", source))
    issues = []
    requirements = []
    for name, expected in names.items():
        actual = target.count(name)
        requirements.append(f"The name {name} occurs exactly {expected} times in the source. "
                            f"Write {name} exactly {expected} times in the translation, at the corresponding positions.")
        if actual != expected:
            issues.append({"kind": "literal-name-count", "name": name, "expected": expected, "actual": actual})
    if "終わらなくなってしまいます" in source:
        requirements.append("The conditional consequence is definite: the work cannot be completed today. "
                            "Do not add possibility or uncertainty to that consequence.")
        if re.search(r"可能|也许|或许|大概", target):
            issues.append({"kind": "weakened-completion-negation"})
        if not re.search(r"无法|不能|完不成|做不完|完成不了", target):
            issues.append({"kind": "missing-completion-negation"})
    if "無理やりなんてことはない" in source:
        requirements.append("The speaker asserts that no one will be forced to buy, without uncertainty. "
                            "The advertising clause may be uncertain; the no-coercion assertion is not.")
        if not re.search(r"(?:不会|不|没有|无)[^。！？!?\n]{0,8}(?:强迫|强制|强行|逼迫|勉强)", target):
            issues.append({"kind": "missing-no-coercion"})
        if re.search(r"(?:应该|可能|也许)[^，,。！？!?\n]{0,8}(?:强迫|强制|强行|逼迫)|(?:强迫|强制|强行|逼迫)[^，,。！？!?\n]{0,8}(?:吧|对吧|是吗)", target):
            issues.append({"kind": "weakened-no-coercion"})
    source_parts = collections.Counter(x.strip() for x in re.split(r"[。！？!?\n]+", source) if x.strip())
    target_parts = collections.Counter(x.strip() for x in re.split(r"[。！？!?\n]+", target) if x.strip())
    maximum_source_occurrences = max(source_parts.values(), default=1)
    for part, count in target_parts.items():
        if len(part) >= 20 and count > maximum_source_occurrences:
            issues.append({"kind": "extra-identical-long-clause", "count": count, "text": part})
    requirements.append("Translate the full source once. Do not append a second copy of a sentence or paragraph.")
    return {"passed": not issues, "issues": issues, "feedback": "\n".join(requirements),
            "coverage": "literal CJK honorific names; two explicit negation patterns; identical long clauses"}


def translate_checked(endpoint, model, source, max_new_tokens=512, prompt_policy="official", retries=2, mode="source"):
    """Retain every attempt; retry only failed checks, never repair returned text."""
    if not isinstance(retries, int) or not 0 <= retries <= 2:
        raise ValueError("Fidelity retries must be in [0,2]")
    if mode not in ("source", "draft"):
        raise ValueError("Unknown fidelity experiment mode")
    started = time.perf_counter()
    attempts = []
    feedback = None
    for index in range(retries + 1):
        try:
            result = translate(endpoint, model, source, max_new_tokens, prompt_policy, feedback)
        except Exception as error:
            result = {"complete": False, "response": {"message": {"content": ""}},
                      "error": {"type": type(error).__name__, "message": str(error)},
                      "attempt": index + 1}
            attempts.append(result)
            break
        check = inspect_fidelity(source, result["response"].get("message", {}).get("content", ""))
        result.update(attempt=index + 1, fidelity=check)
        attempts.append(result)
        if result["complete"] and check["passed"]:
            break
        feedback = check["feedback"]
        if mode == "draft":
            feedback += ("\nA previous draft is quoted as JSON data below. Correct the fidelity violations, "
                         "preserving its other correct facts and relationships. The Japanese source remains authoritative. "
                         "Do not introduce new facts or translate the draft as additional source text.\nPrevious draft: " +
                         json.dumps(result["response"].get("message", {}).get("content", ""), ensure_ascii=False))
    final = dict(attempts[-1])
    final.update(attempts=attempts, totalElapsedSeconds=time.perf_counter() - started,
                 fidelityPassed=final["complete"] and final["fidelity"]["passed"],
                 qualityPassed=False, retryBudget=retries,
                 fidelityVersion="source-feedback-v1" if mode == "source" else "draft-repair-v2")
    return final
