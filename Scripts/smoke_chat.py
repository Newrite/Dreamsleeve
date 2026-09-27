#!/usr/bin/env python3
"""Exercise the real F# ENet server with two native Client.Dev processes.

Build Dreamsleeve.Server in Release and Dreamsleeve.Client.Dev before running.
Every child is started without a visible console and is stopped on every exit path.
"""
from __future__ import annotations

import argparse
import json
import os
import tempfile
import re
import socket
import subprocess
import threading
import time
import uuid
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class Child:
    def __init__(self, name: str, command: list[str], log, log_lock: threading.Lock, env=None):
        self.name = name
        self.lines: list[str] = []
        self.changed = threading.Condition()
        self.log = log
        self.log_lock = log_lock
        flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        self.process = subprocess.Popen(
            command, cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace",
            bufsize=1, creationflags=flags, env=env,
        )
        self.record("START " + " ".join(command))
        self.reader = threading.Thread(target=self.read_output, name=f"smoke-{name}", daemon=True)
        self.reader.start()

    def record(self, text: str):
        with self.log_lock:
            self.log.write(f"[{self.name}] {text}\n")
            self.log.flush()

    def read_output(self):
        assert self.process.stdout is not None
        for raw in self.process.stdout:
            line = raw.rstrip("\r\n")
            self.record(line)
            with self.changed:
                self.lines.append(line)
                self.changed.notify_all()
        with self.changed:
            self.changed.notify_all()

    def mark(self) -> int:
        with self.changed:
            return len(self.lines)

    def output(self, start: int = 0) -> list[str]:
        with self.changed:
            return self.lines[start:].copy()

    def send(self, command: str):
        assert self.process.stdin is not None
        self.record(">> " + command)
        self.process.stdin.write(command + "\n")
        self.process.stdin.flush()

    def wait_for(self, predicate, timeout: float, start: int = 0, read: bool = False):
        deadline = time.monotonic() + timeout
        next_read = 0.0
        while True:
            with self.changed:
                lines = self.lines[start:].copy()
                if predicate(lines):
                    return lines
                if self.process.poll() is not None:
                    raise RuntimeError(f"{self.name} exited {self.process.returncode}: " + " | ".join(lines[-12:]))
                left = deadline - time.monotonic()
                if left <= 0:
                    raise TimeoutError(f"{self.name} output timeout: " + " | ".join(lines[-12:]))
            # `read` is the supported consumer pump. Polling it also flushes redirected
            # native console output; readiness is always determined by observed state.
            if read and time.monotonic() >= next_read:
                self.send("read")
                next_read = time.monotonic() + 0.15
            with self.changed:
                self.changed.wait(min(left, 0.1))

    def phase(self, value: str, timeout: float, start: int = 0):
        return self.wait_for(lambda lines: f"session={value}" in lines, timeout, start, read=True)

    def stop(self, timeout: float = 5.0):
        if self.process.poll() is None:
            try:
                self.send("quit")
                self.process.wait(timeout=timeout)
            except (BrokenPipeError, OSError, subprocess.TimeoutExpired):
                self.process.kill()  # This exact child only; no name-based process lookup.
                self.process.wait(timeout=timeout)
        self.reader.join(timeout=1)
        for pipe in (self.process.stdin, self.process.stdout):
            if pipe is not None:
                pipe.close()


def free_udp_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def free_tcp_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def check(condition: bool, message: str):
    if not condition:
        raise AssertionError(message)


def settle_reads(child: Child, timeout: float):
    # A later read response is an output barrier for the preceding console drain.
    for _ in range(2):
        start = child.mark()
        child.send("read")
        child.wait_for(lambda lines: any(line.startswith("session=") for line in lines), timeout, start)


def message_once(sender: Child, receiver: Child, marker: str, timeout: float):
    sender_start, receiver_start = sender.mark(), receiver.mark()
    sender.send("send " + marker)
    for child, start in ((sender, sender_start), (receiver, receiver_start)):
        child.wait_for(lambda lines: any(marker in line and line.startswith("[1] ") for line in lines),
                       timeout, start, read=True)
        settle_reads(child, timeout)
        count = sum(marker in line and line.startswith("[1] ") for line in child.output(start))
        check(count == 1, f"{child.name}: expected one publication for {marker}, got {count}")


def player_states(lines: list[str], player_id: str):
    for line in lines:
        if line.startswith("player {"):
            state = json.loads(line[7:])
            if state["data"]["playerId"] == int(player_id):
                yield state


def wait_player(child: Child, player_id: str, predicate, timeout: float, start: int = 0):
    lines = child.wait_for(lambda lines: any(predicate(state) for state in player_states(lines, player_id)),
                           timeout, start, read=True)
    return next(state for state in player_states(lines, player_id) if predicate(state))


def game_empty(state):
    return not state.get("location") and not state["actorValues"] and not state["details"].get("level")


def smoke(args, log, directory: Path):
    children: list[Child] = []
    log_lock = threading.Lock()
    port = args.port or free_udp_port()
    nonce = uuid.uuid4().hex[:10]
    auth_url = f"http://127.0.0.1:{free_tcp_port()}"
    client_env = dict(os.environ, DREAMSLEEVE_PASSWORD="smoke-" + uuid.uuid4().hex)
    config = directory / "server.json"
    database = directory / "accounts.sqlite"
    secrets = [client_env["DREAMSLEEVE_PASSWORD"]]
    config.write_text(json.dumps({
        "Database": {"DatabasePath": str(database), "BusyTimeoutSeconds": 5},
        "Authentication": {"ListenUrl": auth_url, "AllowInsecureLoopback": True, "AllowRegistration": True},
        "Logging": {"MinimumLevel": "Debug", "FilePath": str(directory / "server-.json")},
    }), encoding="utf-8")

    def stage(message: str):
        print("PASS " + message, flush=True)
        with log_lock:
            log.write("CHECK " + message + "\n")
            log.flush()

    def start(name: str, command: list[str], env=None):
        child = Child(name, command, log, log_lock, env)
        children.append(child)
        return child

    def start_server(name: str):
        server = start(name, ["dotnet", str(args.server), "--port", str(port), "--config", str(config)])
        server.wait_for(lambda lines: any(f"Listening on 127.0.0.1:{port}" in line for line in lines), args.timeout)
        return server

    def start_client(name: str, username: str, display_name: str | None = None):
        command = [str(args.client), "--connect", "127.0.0.1", str(port), username, "--auth-url", auth_url]
        if display_name is not None:
            command += ["--register", display_name]
        return start(name, command, client_env)

    try:
        server = start_server("server")
        alice = start_client("alice", "smoke_alice", "Smoke Alice")
        alice.phase("Ready", args.timeout)
        alice.wait_for(lambda lines: any(re.fullmatch(r"\d+: Smoke Alice", line) for line in lines), args.timeout, read=True)
        alice_id = next(line.split(":", 1)[0] for line in alice.output() if re.fullmatch(r"\d+: Smoke Alice", line))
        stage("first native client opened a real server session")
        # A known additional credential lets us assert HTTP ticket log redaction.
        # It remains unused and is discarded when the server process restarts.
        request = urllib.request.Request(auth_url + "/auth/login", method="POST",
            data=json.dumps({"username": "smoke_alice", "password": client_env["DREAMSLEEVE_PASSWORD"]}).encode(),
            headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=args.timeout) as response:
            grant = json.load(response)
        check(len(grant["sessionTicket"]) == 43, "Login did not issue a valid ticket")
        secrets.append(grant["sessionTicket"])

        alice_start = alice.mark()
        alice.send("begin Nerevar")
        sample = {
            "location": {"location": {"locationId": {"pluginName": "Skyrim.esm", "localFormId": 291},
                                      "locationName": "Whiterun"},
                         "position": {"X": 1, "Y": 2, "Z": 3}, "rotation": {"X": 0, "Y": 0, "Z": 1.5}},
            "actorValues": {"skyrim:speed": {"displayName": "Speed", "state": {"value": 0}},
                            "skyrim:health": {"displayName": "Health", "state": {"current": 150, "maximum": 100}}},
        }
        details = {"race": {"form": {"pluginName": "Skyrim.esm", "localFormId": 79686}, "name": "Nord"},
                   "level": 25, "activity": {"kind": 2, "targetName": "Dragon"},
                   "place": {"worldspaceName": "Tamriel", "locationName": "Whiterun",
                             "nearbyMarkerName": "Dragonsreach", "markerKind": "castle", "isInterior": False},
                   "gameStartedAtUnixMs": 1700000000000}
        alice.send("sample " + json.dumps(sample))
        alice.send("details " + json.dumps(details))
        authoritative = wait_player(alice, alice_id,
            lambda state: state.get("characterName") == "Nerevar" and state.get("location") is not None
                and len(state["actorValues"]) == 2 and state["details"].get("level") == 25,
            args.timeout, alice_start)
        check(authoritative["characterGeneration"] == 1, "First character generation did not advance")
        check(authoritative["actorValues"]["skyrim:speed"]["state"]["value"] == 0, "Zero scalar was lost")
        check(authoritative["actorValues"]["skyrim:health"]["state"]["current"] == 150, "Resource value was clamped")
        check(authoritative["details"]["activity"]["targetName"] == "Dragon", "Structured activity context was lost")
        stage("author received authoritative character, XYZ/radians, scalar/resource values and rich details")

        bob = start_client("bob", "smoke_bob", "Smoke Bob")
        bob.phase("Ready", args.timeout)
        bob.wait_for(lambda lines: any(re.fullmatch(r"\d+: Smoke Bob", line) for line in lines), args.timeout, read=True)
        bob_id = next(line.split(":", 1)[0] for line in bob.output() if re.fullmatch(r"\d+: Smoke Bob", line))
        check(f"{alice_id}: Smoke Alice" in bob.output(), "Second client bootstrap missed the first player")
        alice.wait_for(lambda lines: f"online {bob_id}: Smoke Bob" in lines, args.timeout, read=True)
        late_join = wait_player(bob, alice_id, lambda state: state.get("characterName") == "Nerevar", args.timeout)
        check(late_join == authoritative, "Late-join PlayerInfo missed the current game state")
        stage("both clients see the same online players and late join receives complete PlayerInfo")

        alice_start, bob_start = alice.mark(), bob.mark()
        sample["location"]["position"]["X"] = 42
        sample["location"]["rotation"]["Z"] = 2.5
        alice.send("sample " + json.dumps(sample))
        for child, start_at in ((alice, alice_start), (bob, bob_start)):
            moved = wait_player(child, alice_id,
                lambda state: state.get("location") is not None and state["location"]["position"]["X"] == 42,
                args.timeout, start_at)
            check(moved["details"] == authoritative["details"] and moved["actorValues"] == authoritative["actorValues"],
                  "Movement changed character metadata or actor values")
            check(moved["location"]["rotation"]["Z"] == 2.5, "Rotation did not replicate")
        stage("periodic compact movement reaches author and peer without losing unchanged details")

        alice_start, bob_start = alice.mark(), bob.mark()
        alice.send("rename Nerevar Renamed")
        for child, start_at in ((alice, alice_start), (bob, bob_start)):
            renamed = wait_player(child, alice_id, lambda state: state.get("characterName") == "Nerevar Renamed",
                                  args.timeout, start_at)
            check(renamed["characterGeneration"] == 1 and renamed["actorValues"] == authoritative["actorValues"],
                  "Rename unexpectedly reset the character")
        alice_start, bob_start = alice.mark(), bob.mark()
        alice.send("begin Nerevar Renamed")
        for child, start_at in ((alice, alice_start), (bob, bob_start)):
            reset = wait_player(child, alice_id, lambda state: state["characterGeneration"] == 2,
                                args.timeout, start_at)
            check(reset.get("characterName") == "Nerevar Renamed" and game_empty(reset),
                  "Same-name BeginCharacter retained prior game state")
        alice_start, bob_start = alice.mark(), bob.mark()
        alice.send("leave")
        for child, start_at in ((alice, alice_start), (bob, bob_start)):
            left = wait_player(child, alice_id, lambda state: state["characterGeneration"] == 3,
                               args.timeout, start_at)
            check(not left.get("characterName") and game_empty(left), "LeaveGame retained character state")
        stage("rename preserves state; same-name begin and leave advance generation and reset game state")

        duplicate = start_client("duplicate", "smoke_alice")
        duplicate.wait_for(lambda lines: any("rejected (3):" in line for line in lines), args.timeout, read=True)
        duplicate_start = duplicate.mark()
        duplicate.phase("Disconnected", args.timeout, duplicate_start)
        check("session=Ready" not in duplicate.output(), "Duplicate username became ready")
        duplicate.send("quit")
        duplicate.process.wait(timeout=args.timeout)
        check(duplicate.process.returncode == 0, "Rejected duplicate client did not exit cleanly")
        stage("duplicate username received SessionAlreadyOpen before transport closure")

        first, second = f"smoke-A-{nonce}", f"smoke-B-{nonce}"
        message_once(alice, bob, first, args.timeout)
        message_once(bob, alice, second, args.timeout)
        stage("both directions delivered exactly one authoritative publication to author and peer")

        alice_start, bob_start = alice.mark(), bob.mark()
        alice.send("disconnect")
        alice.phase("Disconnected", args.timeout, alice_start)
        bob.wait_for(lambda lines: f"offline {alice_id}" in lines, args.timeout, bob_start, read=True)
        stage("disconnect removed the player from the other client's online view")

        alice_start, bob_start = alice.mark(), bob.mark()
        alice.send("connect")
        alice.phase("Ready", args.timeout, alice_start)
        alice.wait_for(lambda lines: any(second in line and line.startswith("[1] ") for line in lines),
                       args.timeout, alice_start, read=True)
        reopened = alice.output(alice_start)
        check(f"{alice_id}: Smoke Alice" in reopened, "Reconnect changed the persisted profile ID")
        check(f"{bob_id}: Smoke Bob" in reopened, "Reconnect missed the other player")
        for marker in (first, second):
            count = sum(marker in line and line.startswith("[1] ") for line in reopened)
            check(count == 1, f"Reconnect history must contain {marker} once; got {count}")
        bob.wait_for(lambda lines: f"online {alice_id}: Smoke Alice" in lines, args.timeout, bob_start, read=True)
        message_once(alice, bob, f"smoke-reconnected-{nonce}", args.timeout)
        stage("same-username reconnect retained profile/history and can publish again")

        alice_start, bob_start = alice.mark(), bob.mark()
        server.send("quit")
        server.process.wait(timeout=args.timeout)
        check(server.process.returncode == 0, "Server did not shut down successfully")
        alice.phase("Disconnected", args.timeout, alice_start)
        bob.phase("Disconnected", args.timeout, bob_start)
        stage("server shutdown disconnected both clients")
        check(database.is_file(), "SQLite database was not created in the temporary directory")
        server = start_server("server-restarted")
        alice_start, bob_start = alice.mark(), bob.mark()
        alice.send("connect")
        alice.phase("Ready", args.timeout, alice_start)
        bob.send("connect")
        bob.phase("Ready", args.timeout, bob_start)
        alice.wait_for(lambda lines: f"{alice_id}: Smoke Alice" in lines, args.timeout, alice_start, read=True)
        bob.wait_for(lambda lines: f"{bob_id}: Smoke Bob" in lines, args.timeout, bob_start, read=True)
        check(f"{alice_id}: Smoke Alice" in bob.output(bob_start), "Restart changed Alice's stored identity")
        message_once(alice, bob, f"smoke-persisted-{nonce}", args.timeout)
        stage("server restart retained SQLite identities and fresh login tickets reopen working sessions")

        alice_start, bob_start = alice.mark(), bob.mark()
        server.send("quit")
        server.process.wait(timeout=args.timeout)
        check(server.process.returncode == 0, "Restarted server did not shut down successfully")
        alice.phase("Disconnected", args.timeout, alice_start)
        bob.phase("Disconnected", args.timeout, bob_start)
        for child in (alice, bob):
            child.send("quit")
            child.process.wait(timeout=args.timeout)
            check(child.process.returncode == 0, f"{child.name} exited unsuccessfully")
            check(not any("session=Faulted" in line or "Protocol error" in line for line in child.output()),
                  f"{child.name} observed a client protocol fault")
        for child in children:
            check(not any(secret in line for secret in secrets for line in child.output()),
                  f"{child.name} exposed an authentication secret in process output")
        files = list(directory.glob("server-*.json"))
        check(bool(files), "Structured server log files were not created")
        for path in files:
            text = path.read_text(encoding="utf-8-sig")
            check(not any(secret in text for secret in secrets), "Server file logs exposed an authentication secret")
            for line in text.splitlines():
                json.loads(line)
        stage("structured server logs and process output contain no test password or issued ticket")
        stage("server shutdown disconnected both clients; all processes exited cleanly")
    finally:
        for child in reversed(children):
            child.stop()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, default=ROOT / "src/Dreamsleeve.Server/bin/Release/net10.0/Dreamsleeve.Server.dll")
    parser.add_argument("--client", type=Path, default=ROOT / "build/windows/x64/releasedbg/Dreamsleeve.Client.Dev.exe")
    parser.add_argument("--port", type=int, default=0, help="0 chooses a free local UDP port")
    parser.add_argument("--timeout", type=float, default=15.0)
    parser.add_argument("--log", type=Path, default=ROOT / "build/smoke-chat.log")
    args = parser.parse_args()
    for artifact in (args.server, args.client):
        if not artifact.is_file():
            parser.error(f"Build the server and native Dev first; missing {artifact}")
    args.log.parent.mkdir(parents=True, exist_ok=True)
    try:
        with tempfile.TemporaryDirectory(prefix="smoke-auth-", dir=ROOT / "build") as directory:
            with args.log.open("w", encoding="utf-8") as log:
                smoke(args, log, Path(directory))
    except (AssertionError, OSError, RuntimeError, subprocess.TimeoutExpired) as error:
        print(f"FAIL {error}\nLog: {args.log}", flush=True)
        return 1
    print(f"Log: {args.log}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
