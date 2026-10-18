"""Reference-gated Japanese CER; semantic review remains a separate evidence field."""

import argparse
import json
import pathlib
import unicodedata

from common import new_output, sha256, write_json


def normalize(text):
    """Use NFKC and remove whitespace/punctuation, never lexical repetitions."""
    return "".join(c for c in unicodedata.normalize("NFKC", text)
                   if not c.isspace() and not unicodedata.category(c).startswith("P"))


def edit_distance(reference, hypothesis):
    """Compute character Levenshtein edits with bounded linear-row memory."""
    if len(reference) > 100000 or len(hypothesis) > 100000 or len(reference) * len(hypothesis) > 25000000:
        raise ValueError("Quality input exceeds evaluation budget")
    row = list(range(len(hypothesis) + 1))
    for i, expected in enumerate(reference, 1):
        next_row = [i]
        for j, actual in enumerate(hypothesis, 1):
            next_row.append(min(next_row[-1] + 1, row[j] + 1, row[j - 1] + (expected != actual)))
        row = next_row
    return row[-1]


def score(item, hypothesis):
    """Refuse unverified or incomplete outputs rather than silently inflating quality."""
    provenance = item.get("referenceVerification", {})
    official = (provenance.get("kind") == "official-paired-script"
                and provenance.get("sourceCheckedBy") == "assistant"
                and provenance.get("referenceIndependentOfModels") is True
                and provenance.get("sourceSha256") and provenance.get("sourcePage")
                and provenance.get("alignmentEvidence"))
    if (item.get("humanVerified") is not True and not official) or not isinstance(item.get("referenceText"), str):
        return {"id": item["id"], "scored": False, "reason": "reference_not_verified"}
    if hypothesis.get("complete") is not True:
        return {"id": item["id"], "scored": False, "reason": "incomplete_inference"}
    expected, actual = normalize(item["referenceText"]), normalize(hypothesis["text"])
    edits = edit_distance(expected, actual)
    return {"id": item["id"], "scored": True, "referenceCharacters": len(expected), "edits": edits,
            "referenceVerification": "human" if item.get("humanVerified") else "official-source-checked-by-assistant",
            "humanVerified": item.get("humanVerified") is True,
            "cer": edits / len(expected) if expected else None,
            "silenceHallucinatedCharacters": len(actual) if not expected else None,
            "categories": item.get("categories", []), "semanticReview": "separate assessment required"}


def main():
    """Produce reproducible aggregate metrics without asserting human semantic acceptance."""
    parser = argparse.ArgumentParser()
    parser.add_argument("--corpus", required=True)
    parser.add_argument("--results", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    output = new_output(args.output)
    corpus = json.loads(pathlib.Path(args.corpus).read_text(encoding="utf-8"))
    results = json.loads(pathlib.Path(args.results).read_text(encoding="utf-8"))
    ids = [x["id"] for x in corpus["items"]]
    if len(ids) != len(set(ids)) or len(ids) > 1000:
        raise ValueError("Duplicate or oversized corpus")
    rows = [score(item, results.get(item["id"], {"complete": False})) for item in corpus["items"]]
    characters = sum(x.get("referenceCharacters", 0) for x in rows)
    edits = sum(x.get("edits", 0) for x in rows)
    report = {"schemaVersion": 1, "normalization": "NFKC; remove whitespace and Unicode punctuation; preserve repeats",
              "corpusSha256": sha256(args.corpus), "resultsSha256": sha256(args.results), "rows": rows,
              "cer": edits / characters if characters else None,
              "totalItems": len(rows),
              "incompleteOrMissing": sum(results.get(x, {}).get("complete") is not True for x in ids),
              "inferenceFailureRate": (sum(results.get(x, {}).get("complete") is not True for x in ids) / len(ids)) if ids else None,
              "unscored": sum(not x["scored"] for x in rows), "qualityPassed": False}
    write_json(output / "quality.json", report)
    print(f"Scored={len(rows) - report['unscored']}; unscored={report['unscored']}; semantic review is separate")
    return 0 if characters else 1


if __name__ == "__main__":
    raise SystemExit(main())
