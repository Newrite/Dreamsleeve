#!/usr/bin/env python3
from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent


def iter_ancestor_dirs(start: Path):
    current = start.resolve()
    if current.is_file():
        current = current.parent

    while True:
        yield current
        if current.parent == current:
            break
        current = current.parent


def resolve_repo_path(repo_root: Path, value: str) -> Path:
    path = Path(value).expanduser()
    if path.is_absolute():
        return path.resolve()
    return (repo_root / path).resolve()


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


def find_protoc(repo_root: Path, explicit: str | None) -> Path:
    if explicit:
        path = Path(explicit).expanduser().resolve()
        if not path.is_file():
            raise FileNotFoundError(f"protoc not found: {path}")
        return path

    candidates = [
        repo_root / "tools" / "protoc" / "bin" / "protoc.exe",
        repo_root / "tools" / "protoc" / "bin" / "protoc",
    ]

    for candidate in candidates:
        if candidate.is_file():
            return candidate

    from_path = shutil.which("protoc")
    if from_path:
        return Path(from_path).resolve()

    raise FileNotFoundError(
        "Could not find protoc.\n"
        "Expected one of:\n"
        "  - tools/protoc/bin/protoc(.exe)\n"
        "  - protoc available in PATH"
    )


def find_default_proto_dir(repo_root: Path) -> Path:
    candidates = [
        repo_root / "Protocol",
    ]

    for candidate in candidates:
        if candidate.is_dir():
            return candidate.resolve()

    raise FileNotFoundError(
        "Could not find proto directory.\n"
        "Expected one of:\n"
        "  - Protocol\n"
        "Use --proto-dir to specify it explicitly."
    )


def collect_proto_files(proto_dir: Path) -> list[Path]:
    if not proto_dir.is_dir():
        raise FileNotFoundError(f"proto directory not found: {proto_dir}")

    files = sorted(p for p in proto_dir.rglob("*.proto") if p.is_file())
    if not files:
        raise FileNotFoundError(f"No .proto files found in: {proto_dir}")

    return files


def run_command(command: list[str], cwd: Path) -> None:
    printable = " ".join(f'"{x}"' if " " in x else x for x in command)
    print(f"> {printable}")

    result = subprocess.run(command, cwd=str(cwd))
    if result.returncode != 0:
        raise RuntimeError(f"Command failed with exit code {result.returncode}")


def clean_generated_files(root: Path, patterns: tuple[str, ...]) -> int:
    if not root.is_dir():
        return 0

    removed = 0
    for pattern in patterns:
        for path in sorted(root.rglob(pattern)):
            if not path.is_file():
                continue
            path.unlink()
            removed += 1

    for directory in sorted(
        (path for path in root.rglob("*") if path.is_dir()),
        key=lambda item: len(item.parts),
        reverse=True,
    ):
        if any(directory.iterdir()):
            continue
        directory.rmdir()

    return removed


def describe_path(repo_root: Path, path: Path) -> str:
    try:
        return str(path.relative_to(repo_root))
    except ValueError:
        return str(path)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Generate C# and C++ protobuf files into src/Dreamsleeve.Protocol.*"
    )
    parser.add_argument(
        "--repo-root",
        default=None,
        help="Repository root that contains xmake.lua. Default: auto-detect from cwd or script path",
    )
    parser.add_argument(
        "--proto-dir",
        default=None,
        help="Directory containing .proto files. Default: auto-detect Protocol",
    )
    parser.add_argument(
        "--native-out-dir",
        default="src/Dreamsleeve.Protocol.Native",
        help="Directory for generated C++ files. Default: src/Dreamsleeve.Protocol.Native",
    )
    parser.add_argument(
        "--dotnet-out-dir",
        default="src/Dreamsleeve.Protocol.Dotnet",
        help="Directory for generated C# files. Default: src/Dreamsleeve.Protocol.Dotnet",
    )
    parser.add_argument(
        "--protoc",
        default=None,
        help="Explicit path to protoc. Default: tools/protoc/bin/protoc(.exe) or PATH",
    )
    parser.add_argument(
        "--csharp-base-namespace",
        default="Dreamsleeve",
        help="Value for --csharp_opt=base_namespace=... Default: Dreamsleeve",
    )
    parser.add_argument(
        "--clean",
        action="store_true",
        help="Delete previously generated *.pb.h/*.pb.cc/*.g.cs files before generation",
    )

    args = parser.parse_args()

    try:
        repo_root = find_repo_root(args.repo_root)
        proto_dir = (
            resolve_repo_path(repo_root, args.proto_dir)
            if args.proto_dir
            else find_default_proto_dir(repo_root)
        )
        native_out = resolve_repo_path(repo_root, args.native_out_dir)
        dotnet_out = resolve_repo_path(repo_root, args.dotnet_out_dir)

        protoc = find_protoc(repo_root, args.protoc)
        proto_files = collect_proto_files(proto_dir)

        if args.clean:
            removed_native = clean_generated_files(native_out, ("*.pb.h", "*.pb.cc"))
            removed_dotnet = clean_generated_files(dotnet_out, ("*.g.cs",))
            print(
                f"Removed {removed_native} native file(s) and {removed_dotnet} dotnet file(s)"
            )

        dotnet_out.mkdir(parents=True, exist_ok=True)
        native_out.mkdir(parents=True, exist_ok=True)

        # protoc expects file paths relative to the import root or cwd.
        relative_proto_files = [str(p.relative_to(proto_dir)) for p in proto_files]

        common_args = [
            str(protoc),
            f"-I{proto_dir}",
        ]

        cpp_args = common_args + [
            f"--cpp_out={native_out}",
            *relative_proto_files,
        ]

        csharp_args = common_args + [
            f"--csharp_out={dotnet_out}",
            f"--csharp_opt=file_extension=.g.cs,base_namespace={args.csharp_base_namespace}",
            *relative_proto_files,
        ]

        print(f"Using protoc: {protoc}")
        print(f"Repo root:     {repo_root}")
        print(f"Proto root:    {describe_path(repo_root, proto_dir)}")
        print(f"C# output:     {describe_path(repo_root, dotnet_out)}")
        print(f"C++ output:    {describe_path(repo_root, native_out)}")
        print(f"Found {len(proto_files)} proto file(s)")

        run_command(cpp_args, cwd=proto_dir)
        run_command(csharp_args, cwd=proto_dir)

        print("Done.")
        return 0

    except Exception as ex:
        print(f"Error: {ex}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
