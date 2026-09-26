#!/usr/bin/env python3
"""Build and run the native and managed suites from any working directory."""
from __future__ import annotations

import argparse
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parent.parent


def run(command: list[str]) -> int:
    executable = shutil.which(command[0])
    if executable is None:
        print(f"Required tool not found: {command[0]}", file=sys.stderr)
        return 127
    print("> " + subprocess.list2cmdline(command), flush=True)
    return subprocess.run([executable, *command[1:]], cwd=ROOT).returncode


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--suite", choices=("all", "native", "managed"), default="all")
    args = parser.parse_args()

    # Build before running: never silently test a stale executable after a failure.
    suites = {
        "native": [
            ["xmake", "build", "Dreamsleeve.Client.Tests"],
            ["xmake", "run", "Dreamsleeve.Client.Tests"],
        ],
        "managed": [
            ["dotnet", "run", "--project",
             "tests/Dreamsleeve.Server.Tests/Dreamsleeve.Server.Tests.fsproj",
             "--configuration", "Release"],
        ],
    }
    failed = []
    for name, commands in suites.items():
        if args.suite not in ("all", name):
            continue
        for command in commands:
            if run(command) != 0:
                failed.append(name)
                break
    if failed:
        print("Failed suites: " + ", ".join(failed), file=sys.stderr)
        return 1
    print("All selected suites passed.")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
