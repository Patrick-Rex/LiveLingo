"""Parse model framing without modifying lexical repetitions or punctuation."""


def parse_output(raw, forced_language, generated_ids, eos_ids, max_new_tokens):
    """Preserve original decoded text and expose incomplete generation explicitly."""
    language = forced_language
    text = raw
    if raw.startswith("language ") and "<asr_text>" in raw:
        prefix, text = raw.split("<asr_text>", 1)
        if prefix.startswith("language "):
            language = prefix[len("language "):].strip()
    ended = bool(generated_ids) and generated_ids[-1] in eos_ids
    if language == "None":
        language = None
    return {"raw": raw, "text": text, "language": language,
            "noSpeech": ended and language is None and not text.strip(),
            "generatedTokens": len(generated_ids), "endedWithEos": ended,
            "truncated": not ended and len(generated_ids) >= max_new_tokens,
            "complete": ended}
