#!/usr/bin/env python3
r"""Build the GitHub release archives in build/release/<version>/.

  Dreamsleeve-<v>-client.zip               the SKSE mod (dist/Client): extract into Skyrim Data
  Dreamsleeve-<v>-server-win-x64.zip       self-contained single-file server for Windows x64
  Dreamsleeve-<v>-server-linux-x64.tar.gz  the same for Linux x64; ICU and OpenSSL come from the OS
  SHA256SUMS.txt

Each server is one executable with the .NET runtime and every library inside;
native ones (SQLite and what other packages bring) unpack on the first start
into DOTNET_BUNDLE_EXTRACT_BASE_DIR, by default %TEMP%\.net or ~/.net. Next to it
stay db/migrations, the example configs, the word lists and the license. The version is the one in Directory.Build.props.
"""
from __future__ import annotations

import argparse
import hashlib
import re
import shutil
import sys
import tarfile
import zipfile
from pathlib import Path

import package_dist as dist

ROOT = dist.ROOT
SERVER = dist.SERVER
EXECUTABLES = {"win-x64": "Dreamsleeve.Server.exe", "linux-x64": "Dreamsleeve.Server"}


def version() -> str:
    found = re.search(r"<Version>([^<]+)</Version>", (ROOT / "Directory.Build.props").read_text(encoding="utf-8"))
    if not found:
        raise SystemExit("No <Version> in Directory.Build.props")
    return found.group(1)


def publish_server(rid: str, target: Path) -> None:
    dist.run(["dotnet", "publish", str(SERVER), "-c", "Release", "-r", rid, "--self-contained", "true",
              "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
              "-p:EnableCompressionInSingleFile=true", "-p:DebugType=embedded",
              "-o", str(target), "--nologo"], ROOT)
    # Documentation and IIS files are not used at run time.
    for item in [*target.glob("*.xml"), target / "web.config"]:
        item.unlink(missing_ok=True)
    for name in ("server.example.toml", "moderation.example.toml", "pseudonyms.example.toml"):
        shutil.copy2(SERVER / name, target / name)
    # Enabled by default: a fresh server starts with the example lists.
    shutil.copy2(SERVER / "moderation.example.toml", target / "moderation.toml")
    shutil.copy2(SERVER / "pseudonyms.example.toml", target / "pseudonyms.toml")
    (target / "README.md").write_text(dist.server_readme(rid), encoding="utf-8")
    (target / "THIRD_PARTY_NOTICES.md").write_text(dist.server_notices(), encoding="utf-8")
    dist.copy_licenses(target)

    if not (target / EXECUTABLES[rid]).is_file():
        raise SystemExit(f"Missing {target / EXECUTABLES[rid]}")
    native = [item.name for item in target.iterdir() if item.suffix in (".dll", ".so")]
    if native:
        raise SystemExit(f"Native libraries outside the {rid} executable: {', '.join(native)}")
    if not any((target / "db" / "migrations").glob("*.sql")):
        raise SystemExit(f"Missing migrations in {target}")
    for item in target.rglob("*"):
        if any(marker in item.name for marker in dist.SERVER_FORBIDDEN) or item.name == "server.toml" or item.suffix == ".pdb":
            raise SystemExit(f"Unexpected file in the {rid} server: {item}")


def files(source: Path) -> list[Path]:
    return sorted(item for item in source.rglob("*") if item.is_file())


def zip_tree(source: Path, archive: Path, prefix: str = "") -> None:
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as output:
        for item in files(source):
            output.write(item, prefix + item.relative_to(source).as_posix())


def tar_tree(source: Path, archive: Path, prefix: str, executable: str) -> None:
    # Built on Windows: the archive, not the file system, carries the modes.
    with tarfile.open(archive, "w:gz") as output:
        for item in files(source):
            info = output.gettarinfo(str(item), prefix + item.relative_to(source).as_posix())
            info.mode = 0o755 if item.name == executable else 0o644
            info.uid = info.gid = 0
            info.uname = info.gname = ""
            with item.open("rb") as handle:
                output.addfile(info, handle)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--skip-client-build", action="store_true", help="reuse the existing DLL and UI bundle")
    parser.add_argument("--output", default=str(ROOT / "build" / "release"))
    args = parser.parse_args()

    release = version()
    output = Path(args.output) / release
    if output.exists():
        shutil.rmtree(output)
    stage = output / "stage"

    client = stage / "dist"
    dist.run([sys.executable, str(Path(__file__).with_name("package_dist.py")), "--no-server", "--output", str(client),
              *(["--skip-build"] if args.skip_client_build else [])], ROOT)
    archives = [output / f"Dreamsleeve-{release}-client.zip"]
    zip_tree(client / "Client", archives[0])

    for rid, executable in EXECUTABLES.items():
        server = stage / rid
        publish_server(rid, server)
        name = f"Dreamsleeve-{release}-server-{rid}"
        if rid.startswith("win"):
            archives.append(output / f"{name}.zip")
            zip_tree(server, archives[-1], f"{name}/")
        else:
            archives.append(output / f"{name}.tar.gz")
            tar_tree(server, archives[-1], f"{name}/", executable)

    sums = [f"{hashlib.sha256(archive.read_bytes()).hexdigest()}  {archive.name}" for archive in archives]
    (output / "SHA256SUMS.txt").write_text("\n".join(sums) + "\n", encoding="utf-8")
    for archive in archives:
        print(f"{archive.name}: {archive.stat().st_size / 2**20:.1f} MiB")
    print(f"Release {release} in {output}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
