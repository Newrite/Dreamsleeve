"""Orchestrate production UDP smoke; .NET owns all packet timing and forwarding.
Build the Release smoke server, Network.Relay and native tests first; --model optionally reads a local .zst cache.
"""
import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import time


def run():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--model", type=Path)
    parser.add_argument("--server", type=Path, default=Path("tests/Dreamsleeve.Phantom.Smoke.Server/bin/Release/net10.0/Dreamsleeve.Phantom.Smoke.Server.dll"))
    parser.add_argument("--relay", type=Path, default=Path("tests/Dreamsleeve.Network.Relay/bin/Release/net10.0/Dreamsleeve.Network.Relay.dll"))
    parser.add_argument("--rtt-ms", type=float, default=100)
    parser.add_argument("--jitter-ms", type=float, default=0)
    parser.add_argument("--loss-every", type=int, default=0)
    parser.add_argument("--link-mib", type=float, default=0, help="Per-direction IPv4 byte rate (UDP payload plus 28 header bytes); zero disables shaping")
    parser.add_argument("--queue-kib", type=float, default=64, help="Tail-drop queue before link serialization")
    parser.add_argument("--deadline-seconds", type=int, default=90, help="Per-phase deadline for slow shaped links")
    args = parser.parse_args()
    if not 1 <= args.deadline_seconds <= 600:
        parser.error("Deadline must be 1..600 seconds")
    if args.rtt_ms < 0 or args.jitter_ms < 0 or args.loss_every < 0 or args.link_mib < 0 or args.queue_kib <= 0:
        parser.error("Rates, delay and loss must be non-negative; queue must be positive")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    ready = output / "ready.json"
    relay_ready = output / "relay-ready.json"
    relay_config = output / "relay-options.json"
    relay_config.write_text(json.dumps(dict(serverPort=port, rttMs=args.rtt_ms, jitterMs=args.jitter_ms,
        lossEvery=args.loss_every, linkMiBps=args.link_mib, queueKiB=args.queue_kib)), encoding="utf-8")

    def await_ready(process, path):
        deadline = time.perf_counter() + 30
        while not path.exists() and time.perf_counter() < deadline and process.poll() is None:
            time.sleep(.05)
        if not path.exists():
            raise RuntimeError(f"Process did not become ready: {path.name}")
        return json.loads(path.read_text())

    def stop(process):
        if process is not None and process.poll() is None:
            try:
                process.communicate("stop\n", timeout=25)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()

    relay = None
    with (output / "server.log").open("w") as log, (output / "relay.log").open("w") as relay_log:
        server = subprocess.Popen(["dotnet", str(args.server.resolve()), "--port", str(port), "--state-dir", str(output),
                                   "--ready-file", str(ready)], stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT,
                                  text=True, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        try:
            await_ready(server, ready)
            relay = subprocess.Popen(["dotnet", str(args.relay.resolve()), str(relay_config), str(output)],
                stdin=subprocess.PIPE, stdout=relay_log, stderr=subprocess.STDOUT, text=True,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            relay_port = await_ready(relay, relay_ready)["port"]
            env = dict(os.environ, DREAMSLEEVE_PHANTOM_SMOKE_PORT=str(relay_port),
                       DREAMSLEEVE_PHANTOM_SMOKE_STATE=str(output),
                       DREAMSLEEVE_PHANTOM_SMOKE_DEADLINE_SECONDS=str(args.deadline_seconds))
            env.pop("DREAMSLEEVE_PHANTOM_SMOKE_MODEL", None)
            if args.model:
                env["DREAMSLEEVE_PHANTOM_SMOKE_MODEL"] = str(args.model.resolve())
            with (output / "client.log").open("w") as client_log:
                result = subprocess.run([str(args.client.resolve()), "--test-case=Phantom production Streaming real UDP smoke"],
                    env=env, stdout=client_log, stderr=subprocess.STDOUT, timeout=args.deadline_seconds + 60)
            text = (output / "client.log").read_text()
            print(text)
            sentinel = "PHANTOM_NATIVE_ASSET_UDP_PASS" if args.model else "PHANTOM_NATIVE_UDP_PASS"
            if result.returncode or sentinel not in text:
                raise RuntimeError("Production UDP smoke failed; inspect client.log")
        finally:
            stop(relay)
            stop(server)
        if relay.returncode:
            raise RuntimeError(".NET relay failed; inspect relay.log and network.json")


if __name__ == "__main__":
    run()
