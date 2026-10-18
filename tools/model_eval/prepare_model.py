"""Explicitly prepare the pinned model; inference never downloads assets."""

import argparse
import json
import pathlib
import hashlib
import urllib.request

from common import configure_cache, new_output, non_system_path, sha256, utc_now, write_json
from download import download_large

REPOSITORY = "Qwen/Qwen3-ASR-1.7B"
REVISION = "7278e1e70fe206f11671096ffdd38061171dd6e5"


def verify_model(directory):
    """Reject missing, changed, linked-out or unexpected model assets before load."""
    directory = pathlib.Path(directory).resolve()
    manifest = json.loads((directory / "asset-manifest.json").read_text(encoding="utf-8"))
    if manifest["repository"] != REPOSITORY or manifest["revision"] != REVISION:
        raise ValueError("Unexpected model revision")
    if not manifest["files"]:
        raise ValueError("Empty model manifest")
    expected = set()
    for item in manifest["files"]:
        file = (directory / item["path"]).resolve()
        if not file.is_relative_to(directory) or file == directory:
            raise ValueError("Model asset escapes its root")
        if file.stat().st_size != item["bytes"] or sha256(file) != item["sha256"]:
            raise ValueError("Model asset fingerprint mismatch: " + item["path"])
        expected.add(item["path"])
    actual = {p.relative_to(directory).as_posix() for p in directory.rglob("*")
              if p.is_file() and ".cache" not in p.relative_to(directory).parts
              and p.name not in ("asset-manifest.json", "preparation.json")}
    if actual != expected:
        raise ValueError("Model directory contains unexpected assets")
    return manifest


def main():
    """Download only the fixed snapshot and hash all loadable assets."""
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--resume", action="store_true")
    args = parser.parse_args()
    if args.resume:
        output = non_system_path(args.output)
        ownership = json.loads((output / "preparation.json").read_text(encoding="utf-8"))
        if ownership != {"repository": REPOSITORY, "revision": REVISION, "root": str(output)}:
            raise ValueError("Preparation ownership mismatch")
        if (output / "asset-manifest.json").exists():
            raise FileExistsError("Model already finalized")
    else:
        output = new_output(args.output)
        write_json(output / "preparation.json", {"repository": REPOSITORY, "revision": REVISION, "root": str(output)})
    configure_cache(offline=False)
    url = f"https://huggingface.co/api/models/{REPOSITORY}/revision/{REVISION}?blobs=true"
    with urllib.request.urlopen(url, timeout=30) as response:
        metadata = json.load(response)
    if metadata["sha"] != REVISION:
        raise ValueError("Remote model revision mismatch")
    for item in metadata["siblings"]:
        name = item["rfilename"]
        if pathlib.PurePosixPath(name).name != name or not name.endswith((".json", ".safetensors", ".txt", ".model", ".jinja")):
            continue
        destination = output / name
        download_url = f"https://huggingface.co/{REPOSITORY}/resolve/{REVISION}/{name}"
        if not destination.exists():
            if item["size"] > 32 * 1024 * 1024:
                download_large(download_url, destination, item["size"], item["lfs"]["sha256"])
            else:
                with urllib.request.urlopen(download_url, timeout=30) as response:
                    data = response.read(item["size"] + 1)
                if len(data) != item["size"]:
                    raise ValueError("Model file length mismatch")
                with destination.open("xb") as target:
                    target.write(data)
        if destination.stat().st_size != item["size"]:
            raise ValueError("Existing asset length mismatch")
        if "lfs" in item:
            if sha256(destination) != item["lfs"]["sha256"]:
                raise ValueError("Official LFS SHA-256 mismatch")
        else:
            data = destination.read_bytes()
            git_hash = hashlib.sha1(f"blob {len(data)}\0".encode() + data).hexdigest()
            if git_hash != item["blobId"]:
                raise ValueError("Official Git blob identity mismatch")
    files = [{"path": p.relative_to(output).as_posix(), "bytes": p.stat().st_size, "sha256": sha256(p)}
             for p in sorted(output.rglob("*")) if p.is_file() and ".cache" not in p.relative_to(output).parts
             and p.name != "preparation.json"]
    write_json(output / "asset-manifest.json", {"schemaVersion": 1, "repository": REPOSITORY,
               "revision": REVISION, "preparedUtc": utc_now(), "files": files})
    verify_model(output)
    print(f"Verified {len(files)} model files at {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
