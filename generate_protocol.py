#!/usr/bin/env python3
from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path


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


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Generate C# and C++ protobuf files from ./proto into ./generated"
    )
    parser.add_argument(
        "--proto-dir",
        default="Protocol/proto",
        help="Directory containing .proto files. Default: Protocol/proto",
    )
    parser.add_argument(
        "--generated-dir",
        default="Protocol/generated",
        help="Output root directory. Default: Protocol/generated",
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
        help="Delete generated/dotnet and generated/native before generation",
    )

    args = parser.parse_args()

    repo_root = Path.cwd()
    proto_dir = (repo_root / args.proto_dir).resolve()
    generated_dir = (repo_root / args.generated_dir).resolve()
    dotnet_out = generated_dir / "dotnet"
    native_out = generated_dir / "native"

    try:
        protoc = find_protoc(repo_root, args.protoc)
        proto_files = collect_proto_files(proto_dir)

        if args.clean:
            shutil.rmtree(dotnet_out, ignore_errors=True)
            shutil.rmtree(native_out, ignore_errors=True)

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
        print(f"Proto root:    {proto_dir}")
        print(f"C# output:     {dotnet_out}")
        print(f"C++ output:    {native_out}")
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