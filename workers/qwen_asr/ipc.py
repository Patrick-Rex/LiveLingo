"""Strict bounded IPC v1; stdout is exclusively framed binary, never model logs."""

import array
import json
import math
import struct
import sys
import uuid

MAX_HEADER = 65536
MAX_PAYLOAD = 16777216
TYPES = {"Hello", "Ready", "Failed", "AudioChunk", "Recognize", "Transcription",
         "ReleaseBefore", "ResetAudio", "Cancel", "Cancelled", "Ping", "Pong", "Error", "Shutdown", "Stopped"}
FIELDS = {"version", "type", "workerEpoch", "sessionId", "captureEpoch", "frameSequence",
          "startSample", "endSample", "correlation", "text", "language", "complete", "reason",
          "elapsedSeconds", "nonce", "information"}


def integer(value, minimum=0, maximum=9223372036854775807):
    """Reject bools and out-of-contract integral values."""
    if type(value) is not int or not minimum <= value <= maximum:
        raise ValueError("ipc_integer")
    return value


def identifier(value):
    """Accept canonical nonempty GUID text shared with System.Text.Json."""
    if not isinstance(value, str) or len(value) != 36 or not uuid.UUID(value).int:
        raise ValueError("ipc_identifier")


def fields(value, expected):
    """Require an object with exactly the agreed structural fields."""
    if not isinstance(value, dict) or set(value) != set(expected):
        raise ValueError("ipc_fields")


def sample_range(value):
    """Return a checked half-open sample range."""
    fields(value, ("start", "end"))
    start = integer(value["start"])
    return start, integer(value["end"], start)


def validate(header, payload_length):
    """Validate before allocating payload, preserving exact request correlation."""
    if not isinstance(header, dict) or set(header) - FIELDS:
        raise ValueError("ipc_unknown_fields")
    integer(header.get("version"), 1, 1)
    epoch = integer(header.get("workerEpoch"), 1)
    kind = header.get("type")
    if kind not in TYPES:
        raise ValueError("ipc_message_type")
    integer(payload_length, 0, MAX_PAYLOAD)
    if kind != "AudioChunk" and payload_length:
        raise ValueError("ipc_payload_type")
    for name, maximum in (("text", 12000), ("reason", 256), ("nonce", 128), ("language", 128)):
        value = header.get(name)
        if value is not None and (not isinstance(value, str) or len(value.encode("utf-16-le")) // 2 > maximum):
            raise ValueError("ipc_text_capacity")
    if kind in ("AudioChunk", "ReleaseBefore", "ResetAudio"):
        identifier(header.get("sessionId"))
        integer(header.get("captureEpoch"), 1)
        integer(header.get("startSample"))
    if kind == "AudioChunk":
        integer(header.get("frameSequence"), 1)
        start = header["startSample"]
        end = integer(header.get("endSample"), start + 1, start + MAX_PAYLOAD // 4)
        if payload_length != (end - start) * 4:
            raise ValueError("ipc_audio_range")
    if kind in ("Recognize", "Transcription", "Cancel", "Cancelled"):
        request = header.get("correlation")
        fields(request, ("ticket", "windowRange", "bodyStartSample", "language", "maxNewTokens"))
        ticket = request["ticket"]
        fields(ticket, ("id", "sequence", "kind", "identity", "segment", "range", "sourceRevision"))
        identifier(ticket["id"])
        integer(ticket["sequence"], 1)
        integer(ticket["kind"], 0, 0)
        integer(ticket["sourceRevision"])
        identity = ticket["identity"]
        fields(identity, ("sessionId", "captureEpoch", "workerEpoch", "configEpoch"))
        identifier(identity["sessionId"])
        for name in ("captureEpoch", "workerEpoch", "configEpoch"):
            integer(identity[name], 1)
        if identity["workerEpoch"] != epoch:
            raise ValueError("ipc_worker_epoch")
        fields(ticket["segment"], ("id", "generation"))
        identifier(ticket["segment"]["id"])
        integer(ticket["segment"]["generation"], 1)
        start, end = sample_range(ticket["range"])
        window_start, window_end = sample_range(request["windowRange"])
        if not start <= window_start < window_end <= end or window_end - window_start > 1920000:
            raise ValueError("ipc_window_range")
        integer(request["bodyStartSample"], window_start, window_end - 1)
        integer(request["maxNewTokens"], 1, 2048)
        if request["language"] not in ("auto", "Japanese"):
            raise ValueError("ipc_language")
    if kind == "Transcription":
        elapsed = header.get("elapsedSeconds")
        if (not isinstance(header.get("text"), str) or type(header.get("complete")) is not bool
                or not isinstance(header.get("reason"), str) or type(elapsed) not in (int, float)
                or not math.isfinite(elapsed) or elapsed < 0):
            raise ValueError("ipc_transcription_metadata")
    if kind in ("Hello", "Ready"):
        info = header.get("information")
        fields(info, ("python", "torch", "cuda", "modelRevision", "device"))
        if any(not isinstance(value, str) or not value or len(value) > 512 for value in info.values()):
            raise ValueError("ipc_information")
    if kind in ("Error", "Failed") and not header.get("reason", "").strip():
        raise ValueError("ipc_failure")
    if kind in ("Ping", "Pong") and not header.get("nonce", "").strip():
        raise ValueError("ipc_nonce")


def exact_read(stream, count):
    """Handle short pipe reads; any early EOF invalidates the worker stream."""
    parts = bytearray(count)
    offset = 0
    while offset < count:
        chunk = stream.read(count - offset)
        if not chunk:
            raise EOFError("ipc_eof")
        parts[offset:offset + len(chunk)] = chunk
        offset += len(chunk)
    return parts


def unique_object(pairs):
    """Reject duplicate keys instead of choosing a parser-specific winner."""
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("ipc_duplicate_property")
        result[key] = value
    return result


def check_depth(value, depth=0):
    """Match the host's finite JSON depth budget."""
    if depth > 16:
        raise ValueError("ipc_depth")
    for child in value.values() if isinstance(value, dict) else value if isinstance(value, list) else ():
        check_depth(child, depth + 1)


def pcm(payload):
    """Decode finite little-endian PCM while preserving quiet samples."""
    result = array.array("f")
    result.frombytes(payload)
    if sys.byteorder != "little":
        result.byteswap()
    if not all(math.isfinite(value) for value in result):
        raise ValueError("ipc_nonfinite_audio")
    return result


def read_frame(stream):
    """Read one complete validated frame without trusting advertised lengths."""
    header_length, payload_length = struct.unpack("<II", exact_read(stream, 8))
    if not 0 < header_length <= MAX_HEADER or payload_length > MAX_PAYLOAD:
        raise ValueError("ipc_frame_capacity")
    header = json.loads(exact_read(stream, header_length).decode("utf-8"), object_pairs_hook=unique_object)
    check_depth(header)
    validate(header, payload_length)
    payload = exact_read(stream, payload_length)
    if payload:
        pcm(payload)
    return header, payload


def write_all(stream, data):
    """Handle partial writes and fail on a closed or nonprogressing pipe."""
    view = memoryview(data)
    while view:
        count = stream.write(view)
        if count is None or count <= 0:
            raise BrokenPipeError("ipc_write_eof")
        view = view[count:]


def write_frame(stream, header, payload=b""):
    """Write a bounded frame; caller serializes concurrent control/model outputs."""
    validate(header, len(payload))
    if payload:
        pcm(payload)
    encoded = json.dumps(header, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")
    if len(encoded) > MAX_HEADER:
        raise ValueError("ipc_header_capacity")
    write_all(stream, struct.pack("<II", len(encoded), len(payload)))
    write_all(stream, encoded)
    write_all(stream, payload)
    stream.flush()
