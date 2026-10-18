"""Create a long-lived child so the real Job supervisor must clean a process tree."""

import argparse
import json
import pathlib
import subprocess
import sys
import time


def main():
    """Record the owned child identity and either exit or await the host timeout."""
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--wait", type=float, default=2)
    args = parser.parse_args()
    child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(120)"],
                             creationflags=subprocess.CREATE_NO_WINDOW)
    with pathlib.Path(args.output).open("x", encoding="utf-8", newline="\n") as target:
        json.dump({"ownedChildPid": child.pid}, target)
        target.write("\n")
    time.sleep(args.wait)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
