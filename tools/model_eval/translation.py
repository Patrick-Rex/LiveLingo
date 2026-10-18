"""Official TranslateGemma prompting and bounded loopback evaluation requests."""

import json
import time
import urllib.parse
import urllib.request


class NoRedirect(urllib.request.HTTPRedirectHandler):
    """Prevent a local service response from redirecting inference off the machine."""

    def redirect_request(self, request, file_pointer, code, message, headers, new_url):
        """Fail before following any redirected request."""
        raise ValueError("Ollama redirects are not allowed")


def prompt(text, policy="official", fidelity_feedback=None):
    """Format a single user message, preserving text and the two blank lines."""
    if not text.strip() or len(text) > 12000:
        raise ValueError("Expected nonempty bounded source text")
    if policy not in ("official", "faithful-v1", "faithful-v2", "faithful-v3"):
        raise ValueError("Unsupported prompt policy")
    constraints = ("Preserve genuine repetitions in the source text, including repeated words, names, and complete sentences. "
                   "Do not omit or merge repeated occurrences.\n"
                   "Preserve negation, double negation, and the original degree of certainty. "
                   "Do not soften assertions or introduce uncertainty that is not present in the source.\n") if policy == "faithful-v1" else ""
    if policy == "faithful-v2":
        constraints = ("Translate each source occurrence exactly once: retain repeated words and names, "
                       "without adding repetitions absent from the source.\n"
                       "Match the source's negation and certainty, without adding uncertainty.\n")
    if policy == "faithful-v3":
        constraints = ("Keep every occurrence of repeated names and phrases in the source, "
                       "and do not invent extra occurrences.\n"
                       "Keep the source's degree of certainty and its negation unchanged.\n")
    if fidelity_feedback:
        if not isinstance(fidelity_feedback, str) or len(fidelity_feedback) > 4000:
            raise ValueError("Bounded fidelity feedback required")
        constraints += "Source fidelity requirements:\n" + fidelity_feedback + "\n"
    return ("You are a professional Japanese (ja) to Chinese (zh-Hans) translator. "
            "Your goal is to accurately convey the meaning and nuances of the original Japanese text "
            "while adhering to Chinese grammar, vocabulary, and cultural sensitivities.\n" + constraints +
            "Produce only the Chinese translation, without any additional explanations or commentary. "
            "Please translate the following Japanese text into Chinese:\n\n\n" + text)


def api(endpoint, route, body=None, timeout=300):
    """Use only explicit local HTTP, refusing redirects and oversized responses."""
    parsed = urllib.parse.urlsplit(endpoint)
    if parsed.scheme != "http" or parsed.hostname not in ("127.0.0.1", "localhost", "::1") or parsed.path:
        raise ValueError("A loopback-only Ollama endpoint is required")
    data = json.dumps(body).encode("utf-8") if body is not None else None
    request = urllib.request.Request(endpoint + route, data=data, headers={"Content-Type": "application/json"})
    # A local model endpoint must not inherit HTTP proxy routing.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    with opener.open(request, timeout=timeout) as response:
        if response.url != endpoint + route:
            raise ValueError("Unexpected endpoint redirect")
        raw = response.read(8 * 1024 * 1024 + 1)
        if len(raw) > 8 * 1024 * 1024:
            raise ValueError("Ollama response exceeded evidence budget")
    return json.loads(raw)


def translate(endpoint, model, text, max_new_tokens=512, prompt_policy="official", fidelity_feedback=None):
    """Return complete timing/termination evidence; never repair or deduplicate text."""
    if model not in ("translategemma:4b", "translategemma:12b"):
        raise ValueError("Unsupported evaluation model")
    if not isinstance(max_new_tokens, int) or not 1 <= max_new_tokens <= 2048:
        raise ValueError("Output token budget must be in [1,2048]")
    request = {"model": model, "stream": False, "keep_alive": "5m",
               "messages": [{"role": "user", "content": prompt(text, prompt_policy, fidelity_feedback)}],
               "options": {"num_ctx": 4096, "num_predict": max_new_tokens, "temperature": 0,
                           "seed": 42, "repeat_penalty": 1.0}}
    started = time.perf_counter()
    response = api(endpoint, "/api/chat", request)
    elapsed = time.perf_counter() - started
    text_out = response.get("message", {}).get("content", "")
    valid = response.get("done") is True and response.get("done_reason") == "stop" and bool(text_out.strip())
    return {"request": request, "response": response, "elapsedSeconds": elapsed, "complete": valid,
            "promptPolicy": prompt_policy}
