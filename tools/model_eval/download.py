"""Bounded official asset download with strict ranges and final integrity checks."""

import concurrent.futures
import hashlib
import pathlib
import time
import urllib.request


def fetch_range(url, start, end, total):
    """Reject proxy/full-file responses that do not match the requested byte range."""
    for attempt in range(3):
        try:
            request = urllib.request.Request(url, headers={"Range": f"bytes={start}-{end}"})
            with urllib.request.urlopen(request, timeout=30) as response:
                expected = f"bytes {start}-{end}/{total}"
                if response.status != 206 or response.headers.get("Content-Range") != expected:
                    raise ValueError("Server did not honor exact range")
                data = response.read(end - start + 2)
                if len(data) != end - start + 1:
                    raise ValueError("Range length mismatch")
                return data
        except (OSError, ValueError):
            if attempt == 2:
                raise
            time.sleep(attempt + 1)


def download_large(url, destination, size, expected_sha256):
    """Resume complete 8MiB chunks with bounded concurrency and final verification."""
    destination = pathlib.Path(destination)
    partial = destination.with_name(destination.name + ".download")
    if destination.exists() or partial.is_symlink():
        raise FileExistsError("Existing full/linked asset preserved")
    chunk = 8 * 1024 * 1024
    offset = partial.stat().st_size if partial.exists() else 0
    if offset > size:
        raise ValueError("Partial asset larger than official size")
    if offset != size:
        offset -= offset % chunk
    try:
        with partial.open("r+b" if partial.exists() else "xb") as output, concurrent.futures.ThreadPoolExecutor(max_workers=12) as pool:
            output.truncate(offset)
            output.seek(offset)
            for base in range(offset, size, chunk * 12):
                ranges = [(start, min(start + chunk, size) - 1) for start in range(base, min(base + chunk * 12, size), chunk)]
                futures = [pool.submit(fetch_range, url, start, end, size) for start, end in ranges]
                for future in futures:
                    output.write(future.result())
                print(f"Downloaded {min(base + chunk * 12, size)}/{size} bytes", flush=True)
        with partial.open("rb") as source:
            if hashlib.file_digest(source, "sha256").hexdigest() != expected_sha256:
                raise ValueError("Official asset SHA-256 mismatch")
        # rename refuses to replace an existing file on Windows.
        partial.rename(destination)
    except Exception:
        # Keep the partial bytes as failure evidence; never claim a complete asset.
        raise
