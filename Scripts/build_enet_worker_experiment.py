#!/usr/bin/env python3
"""Build protocol-identical owner/inline benchmark binaries; restore source bytes.

Run without concurrent builds. Production owner settings come from Server.Worker
in the supplied benchmark config; historical DREAMSLEEVE_ENET_WORKER_* variables
no longer configure a separate experimental transport.
"""
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
source = ROOT / "src/Dreamsleeve.Server.Infrastructure/EnetTransport.fs"
original = source.read_bytes()
text = original.decode("utf-8").replace("\r\n", "\n")
needle = "TransportOwner.create settings (fun () -> allocate settings)"
if text.count(needle) != 1:
    raise SystemExit("Transport factory changed; inspect the inline baseline target")


def build(directory):
    subprocess.run(
        ["dotnet", "build", "tests/Dreamsleeve.Server.NetworkBenchmarks", "-c", "Release",
         "--no-restore", "--output", str(ROOT / "build" / directory)], cwd=ROOT, check=True)


build("enet-worker-server")
try:
    source.write_text(text.replace(needle, "allocate settings"), encoding="utf-8")
    build("enet-inline-server")
finally:
    source.write_bytes(original)
    # Restore ordinary build outputs even when the inline build fails. If this
    # rebuild also fails, both exceptions remain visible in Python's traceback.
    build("enet-worker-server")
    subprocess.run(["dotnet", "build", "tests/Dreamsleeve.Server.NetworkBenchmarks",
                    "-c", "Release", "--no-restore"], cwd=ROOT, check=True)
print("Owner: build/enet-worker-server; inline baseline: build/enet-inline-server")
