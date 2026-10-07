"""Production UDP smoke through a loopback delay/loss relay (no game assets bundled).
Build the Release smoke server and native tests first; --model optionally reads a local .zst cache.
"""
import argparse
import heapq
import json
import os
from pathlib import Path
import random
import selectors
import socket
import subprocess
import threading
import time


def run():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--model", type=Path)
    parser.add_argument("--server", type=Path, default=Path("tests/Dreamsleeve.Phantom.Smoke.Server/bin/Release/net10.0/Dreamsleeve.Phantom.Smoke.Server.dll"))
    parser.add_argument("--rtt-ms", type=float, default=100)
    parser.add_argument("--jitter-ms", type=float, default=0)
    parser.add_argument("--loss-every", type=int, default=0)
    args = parser.parse_args()
    if args.rtt_ms < 0 or args.jitter_ms < 0 or args.loss_every < 0:
        parser.error("Network parameters must be non-negative")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    relay = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    relay.bind(("127.0.0.1", 0))
    relay.setblocking(False)
    stop = threading.Event()
    stats = {"datagrams": 0, "dropped": 0, "icmpResets": 0, "rttMs": args.rtt_ms, "jitterMs": args.jitter_ms}
    errors = []

    def forward():
        peers, pending = {}, []
        rng = random.Random(73)
        with selectors.DefaultSelector() as selector:
            selector.register(relay, selectors.EVENT_READ, None)
            try:
                while not stop.is_set():
                    for key, _ in selector.select(.001):
                        try:
                            data, address = key.fileobj.recvfrom(65535)
                        except ConnectionResetError:
                            # Windows reports ICMP for delayed packets sent after
                            # the test client closes. It is not a relay failure;
                            # a dead endpoint still fails the client's deadline.
                            stats["icmpResets"] += 1
                            continue
                        if key.fileobj is relay:
                            if address not in peers:
                                peer = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                                peer.bind(("127.0.0.1", 0))
                                peer.setblocking(False)
                                peers[address] = peer
                                selector.register(peer, selectors.EVENT_READ, address)
                            target, endpoint = peers[address], ("127.0.0.1", port)
                        else:
                            target, endpoint = relay, key.data
                        stats["datagrams"] += 1
                        serial = stats["datagrams"]
                        if args.loss_every and serial % args.loss_every == 0:
                            stats["dropped"] += 1
                            continue
                        delay = max(0, args.rtt_ms / 2 + rng.uniform(-args.jitter_ms, args.jitter_ms)) / 1000
                        heapq.heappush(pending, (time.monotonic() + delay, serial, target, endpoint, data))
                    while pending and pending[0][0] <= time.monotonic():
                        _, _, target, endpoint, data = heapq.heappop(pending)
                        target.sendto(data, endpoint)
            except Exception as error:
                errors.append(repr(error))
            finally:
                for peer in peers.values():
                    peer.close()

    thread = threading.Thread(target=forward, daemon=True)
    thread.start()
    ready = output / "ready.json"
    with (output / "server.log").open("w") as log:
        server = subprocess.Popen(["dotnet", str(args.server.resolve()), "--port", str(port), "--state-dir", str(output),
                                   "--ready-file", str(ready)], stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT,
                                  text=True, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        try:
            deadline = time.monotonic() + 30
            while not ready.exists() and time.monotonic() < deadline and server.poll() is None:
                time.sleep(.05)
            if not ready.exists():
                raise RuntimeError("Smoke server did not become ready")
            env = dict(os.environ, DREAMSLEEVE_PHANTOM_SMOKE_PORT=str(relay.getsockname()[1]),
                       DREAMSLEEVE_PHANTOM_SMOKE_STATE=str(output))
            env.pop("DREAMSLEEVE_PHANTOM_SMOKE_MODEL", None)
            if args.model:
                env["DREAMSLEEVE_PHANTOM_SMOKE_MODEL"] = str(args.model.resolve())
            with (output / "client.log").open("w") as client_log:
                result = subprocess.run([str(args.client.resolve()), "--test-case=Phantom production Streaming real UDP smoke"],
                                        env=env, stdout=client_log, stderr=subprocess.STDOUT, timeout=150)
            text = (output / "client.log").read_text()
            print(text)
            sentinel = "PHANTOM_NATIVE_ASSET_UDP_PASS" if args.model else "PHANTOM_NATIVE_UDP_PASS"
            if result.returncode or sentinel not in text:
                raise RuntimeError("Production UDP smoke failed; inspect client.log")
        finally:
            stop.set()
            thread.join()
            relay.close()
            if server.poll() is None:
                try:
                    server.communicate("stop\n", timeout=25)
                except subprocess.TimeoutExpired:
                    server.kill()
                    server.wait()
            stats["relayErrors"] = errors
            (output / "network.json").write_text(json.dumps(stats, indent=2))
        if errors:
            raise RuntimeError(str(errors))


if __name__ == "__main__":
    run()
