#!/usr/bin/env python3
from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
SOLUTION_EXTENSIONS = (".sln", ".slnx")


def iter_ancestor_dirs(start: Path):
    current = start.resolve()
    if current.is_file():
        current = current.parent

    while True:
        yield current
        if current.parent == current:
            break
        current = current.parent


def find_repo_root(explicit: str | None) -> Path:
    if explicit:
        repo_root = Path(explicit).expanduser().resolve()
        if not (repo_root / "xmake.lua").is_file():
            raise FileNotFoundError(f"xmake.lua not found in repo root: {repo_root}")
        return repo_root

    seen: set[Path] = set()
    for start in (Path.cwd(), SCRIPT_DIR):
        for candidate in iter_ancestor_dirs(start):
            if candidate in seen:
                continue
            seen.add(candidate)
            if (candidate / "xmake.lua").is_file():
                return candidate

    raise FileNotFoundError(
        "Could not find repo root.\n"
        "Expected to find xmake.lua in the current directory, one of its parents,\n"
        "or one of the parent directories of this script.\n"
        "Use --repo-root to specify it explicitly."
    )


def run_command(command: list[str], cwd: Path) -> None:
    printable = " ".join(f'"{arg}"' if " " in arg else arg for arg in command)
    print(f"> {printable}")
    result = subprocess.run(command, cwd=str(cwd))
    if result.returncode != 0:
        raise RuntimeError(f"Command failed with exit code {result.returncode}")


def gather_solution_candidates(repo_root: Path) -> list[Path]:
    directories = [repo_root]
    directories.extend(
        sorted(
            path for path in repo_root.glob("vsxmake*") if path.is_dir()
        )
    )

    dot_vs_dir = repo_root / ".vs"
    if dot_vs_dir.is_dir():
        directories.append(dot_vs_dir)

    candidates: dict[Path, Path] = {}
    for directory in directories:
        for extension in SOLUTION_EXTENSIONS:
            for path in directory.rglob(f"*{extension}"):
                if path.is_file():
                    candidates[path.resolve()] = path.resolve()

    return sorted(candidates.values())


def snapshot_solution_times(repo_root: Path) -> dict[Path, int]:
    return {
        path: path.stat().st_mtime_ns
        for path in gather_solution_candidates(repo_root)
    }


@dataclass(frozen=True)
class SolutionCandidate:
    path: Path
    changed: bool
    mtime_ns: int


def find_solution_path(repo_root: Path, before: dict[Path, int]) -> Path:
    candidates: list[SolutionCandidate] = []
    for path in gather_solution_candidates(repo_root):
        mtime_ns = path.stat().st_mtime_ns
        changed = before.get(path) != mtime_ns
        candidates.append(SolutionCandidate(path=path, changed=changed, mtime_ns=mtime_ns))

    if not candidates:
        raise FileNotFoundError(
            "Solution file was not generated.\n"
            "Expected xmake to produce a .sln/.slnx under the repo root, a vsxmake* directory, or .vs."
        )

    changed_candidates = [item for item in candidates if item.changed]
    preferred = changed_candidates or candidates
    preferred.sort(
        key=lambda item: (
            0 if item.path.suffix.lower() == ".sln" else 1,
            -item.mtime_ns,
            len(item.path.parts),
        )
    )
    return preferred[0].path


def collect_managed_projects(repo_root: Path) -> list[Path]:
    src_root = repo_root / "src"
    if not src_root.is_dir():
        return []

    projects = [
        path.resolve()
        for path in src_root.rglob("*")
        if path.is_file() and path.suffix.lower() in {".csproj", ".fsproj"}
    ]
    return sorted(projects)


def list_solution_projects(solution_path: Path) -> set[Path]:
    result = subprocess.run(
        ["dotnet", "sln", str(solution_path), "list"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        cwd=str(solution_path.parent),
    )
    if result.returncode != 0:
        return set()

    existing: set[Path] = set()
    for line in result.stdout.splitlines():
        trimmed = line.strip()
        lower = trimmed.lower()
        if not lower.endswith((".csproj", ".fsproj")):
            continue
        existing.add((solution_path.parent / trimmed).resolve())

    return existing


def add_projects_to_solution(
    solution_path: Path,
    projects: list[Path],
    solution_folder: str,
) -> tuple[int, int]:
    existing = list_solution_projects(solution_path)
    added = 0
    skipped = 0

    for project in projects:
        if project in existing:
            print(f"Already in solution: {project}")
            skipped += 1
            continue

        command = [
            "dotnet",
            "sln",
            str(solution_path),
            "add",
            str(project),
            "--solution-folder",
            solution_folder,
        ]
        run_command(command, cwd=solution_path.parent)
        existing = list_solution_projects(solution_path)
        added += 1

    return added, skipped


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Generate a vsxmake Visual Studio solution and add managed projects."
    )
    parser.add_argument(
        "--repo-root",
        default=None,
        help="Repository root that contains xmake.lua. Default: auto-detect from cwd or script path",
    )
    parser.add_argument(
        "--solution-folder",
        default="Managed",
        help="Solution folder for managed projects. Default: Managed",
    )
    parser.add_argument(
        "--xmake-kind",
        default="vsxmake",
        help="Project kind passed to 'xmake project -k'. Default: vsxmake",
    )
    args = parser.parse_args()

    try:
        if shutil.which("xmake") is None:
            raise FileNotFoundError("xmake was not found in PATH")
        if shutil.which("dotnet") is None:
            raise FileNotFoundError("dotnet was not found in PATH")

        repo_root = find_repo_root(args.repo_root)
        print(f"Repo root: {repo_root}")

        before = snapshot_solution_times(repo_root)
        run_command(
            ["xmake", "project", "-k", args.xmake_kind, "-y"],
            cwd=repo_root,
        )

        solution_path = find_solution_path(repo_root, before)
        print(f"Solution: {solution_path}")

        managed_projects = collect_managed_projects(repo_root)
        if not managed_projects:
            print("No managed projects found under src.")
            return 0

        added, skipped = add_projects_to_solution(
            solution_path=solution_path,
            projects=managed_projects,
            solution_folder=args.solution_folder,
        )
        print(f"Managed projects added: {added}, skipped: {skipped}")
        return 0

    except Exception as ex:
        print(f"Error: {ex}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
