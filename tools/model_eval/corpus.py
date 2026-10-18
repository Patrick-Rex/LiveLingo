"""Prepare disposable official listening assets with a verifiable deletion manifest."""

import argparse
import pathlib
import urllib.request

from common import new_output, sha256, utc_now, write_json

ASSETS = {
    "N2Q1.mp3": "https://www.jlpt.jp/samples/sample2018/mp3/N2Q1.mp3",
    "N2script.pdf": "https://www.jlpt.jp/samples/sample2018/pdf/N2script.pdf",
}
SOURCES = {
    "qwen-music": ("https://github.com/QwenLM/Qwen-Audio/blob/main/TUTORIAL.md", {
        "music.wav": "https://raw.githubusercontent.com/QwenLM/Qwen-Audio/main/assets/audio/music.wav",
    }),
    "music": ("https://commons.wikimedia.org/wiki/File:Air_(Bach).ogg", {
        "air-bach.ogg": "https://upload.wikimedia.org/wikipedia/commons/1/1e/Air_%28Bach%29.ogg",
    }),
    "jlpt": ("https://www.jlpt.jp/samples/sampleindex.html", ASSETS),
    "dnc": ("https://www.dnc.ac.jp/kyotsu/listening.html", {
        "volume-check.mp3": "https://www.dnc.ac.jp/albums/51/abm00001504.mp3",
        "questions.mp3": "https://www.dnc.ac.jp/albums/51/abm00001507.mp3",
        "script.pdf": "https://www.dnc.ac.jp/albums/abm.php?d=51&f=abm00001505.pdf&n=script.pdf",
    }),
}


def main():
    """Download a matched official audio/script pair, bounded to 32MiB per asset."""
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--source", choices=tuple(SOURCES), default="jlpt")
    args = parser.parse_args()
    output = new_output(args.output)
    allowed = pathlib.Path(r"D:\Soft Ware\LiveLingo\data\evaluation\corpora").resolve()
    if output.parent != allowed:
        output.rmdir()
        raise ValueError("Corpus must be a dedicated child of the evaluation corpora directory")
    files = []
    source_page, assets = SOURCES[args.source]
    manifest = {"schemaVersion": 1, "kind": "disposable-evaluation-corpus", "root": str(output),
                "createdUtc": utc_now(), "sourcePage": source_page,
                "humanVerified": False, "files": files, "completed": False}
    try:
        for name, url in assets.items():
            path = output / name
            size = 0
            try:
                with urllib.request.urlopen(url, timeout=60) as response, path.open("xb") as destination:
                    while chunk := response.read(1024 * 1024):
                        size += len(chunk)
                        if size > 32 * 1024 * 1024:
                            raise ValueError("Corpus asset exceeds download budget")
                        destination.write(chunk)
            finally:
                if path.exists():
                    files.append({"path": name, "url": url, "bytes": path.stat().st_size, "sha256": sha256(path)})
        manifest["completed"] = True
    except Exception as error:
        manifest["error"] = {"type": type(error).__name__, "message": str(error)}
    finally:
        write_json(output / "assets.json", manifest)
    print(f"Corpus complete={manifest['completed']}; path={output}; references still need alignment")
    return 0 if manifest["completed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
