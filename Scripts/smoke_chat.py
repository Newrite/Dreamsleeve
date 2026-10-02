#!/usr/bin/env python3
"""Exercise the real F# ENet server with two native Client.Dev processes.

Build Dreamsleeve.Server in Release and Dreamsleeve.Client.Dev before running.
Every child is started without a visible console and is stopped on every exit path.
"""
from __future__ import annotations

import argparse
import copy
import json
import tomli_w
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
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
    config = directory / "server.toml"
    database = directory / "accounts.sqlite"
    secrets = [client_env["DREAMSLEEVE_PASSWORD"]]
    moderation = directory / "moderation.toml"
    moderation.write_text(tomli_w.dumps({"words": ["forbiddenword"], "exceptions": []}), encoding="utf-8")
    # One pseudonym makes the hidden player's name predictable.
    pseudonyms = directory / "pseudonyms.toml"
    pseudonyms.write_text(tomli_w.dumps({"version": 1, "names": ["Тень"]}), encoding="utf-8")
    settings = {
        "Moderation": {"Enabled": True, "RulesPath": str(moderation)},
        "Identity": {"AllowHiddenIdentity": True, "ToggleIntervalMs": 30000, "PseudonymsPath": str(pseudonyms)},
        "Database": {"DatabasePath": str(database), "BusyTimeoutSeconds": 5},
        "Authentication": {"Listener": {"ListenUrl": auth_url, "AllowInsecureLoopback": True}},
        # The admin panel runs in the same process; a free port keeps it off the default 8780.
        "Admin": {"Listener": {"ListenUrl": f"http://127.0.0.1:{free_tcp_port()}"}},
        "Logging": {"MinimumLevel": "Debug", "FilePath": str(directory / "server-.json")},
        # Own client disabled to see a type refusal; two mod announcements per minute.
        "Announcements": {
            "Rate": {"Burst": 2, "RefillMs": 60000, "DuplicateWindowMs": 0},
            "TrustedClient": {"Enabled": False}, "ThirdParty": {"Enabled": True},
            "Scheduled": [{"Text": f"smoke-welcome-{nonce}", "Kind": "Announcement", "DelaySeconds": 0, "IntervalSeconds": 0}],
        },
        # Two notes per player and four marks per cell make eviction and density observable;
        # a long death interval makes the second death refusal deterministic.
        "GroundMarks": {"MaxNotesPerPlayer": 2, "MaxPerIndexCell": 4, "DeathMinIntervalMs": 60000,
                        "NoteRate": {"Burst": 10, "DuplicateWindowMs": 0}},
    }
    config.write_text(tomli_w.dumps(settings), encoding="utf-8")

    def stage(message: str):
        print("PASS " + message, flush=True)
        with log_lock:
            log.write("CHECK " + message + "\n")
            log.flush()

    def start(name: str, command: list[str], env=None):
        child = Child(name, command, log, log_lock, env)
        children.append(child)
        return child

    def start_server(name: str, path: Path = config):
        server = start(name, ["dotnet", str(args.server), "--port", str(port), "--config", str(path)])
        server.wait_for(lambda lines: any(f"Listening on 127.0.0.1:{port}" in line for line in lines), args.timeout)
        return server

    def start_client(name: str, username: str, display_name: str | None = None, hidden: str | None = None):
        if name == "alice":
            client_config = directory / "client settings.toml"
            # A DNS name rather than the literal: the client resolves it when it connects.
            client_config.write_text(tomli_w.dumps({"version": 1, "serverHost": "localhost", "serverPort": port,
                                                  "authUrl": auth_url}), encoding="utf-8")
            command = [str(args.client), "--config", str(client_config), username]
        else:
            command = [str(args.client), "--connect", "127.0.0.1", str(port), username, "--auth-url", auth_url]
        if display_name is not None:
            command += ["--register", display_name]
        if hidden:
            command += [hidden]
        return start(name, command, client_env)

    def check_canceled_registration():
        entered, release = threading.Event(), threading.Event()
        requests = []

        class DelayedAuth(BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def do_POST(self):
                self.rfile.read(int(self.headers["Content-Length"]))
                requests.append(self.path)
                entered.set()
                release.wait(args.timeout)
                # Even a late failure belongs to the canceled operation.
                self.send_response(500)
                self.send_header("Content-Length", "0")
                self.end_headers()

        endpoint = ThreadingHTTPServer(("127.0.0.1", 0), DelayedAuth)
        thread = threading.Thread(target=endpoint.serve_forever, daemon=True)
        thread.start()
        canceled = None
        try:
            canceled = start("canceled-auth", [str(args.client), "--connect", "127.0.0.1", str(port),
                "cancel_test", "--auth-url", f"http://127.0.0.1:{endpoint.server_port}", "--register", "Canceled"], client_env)
            check(entered.wait(args.timeout), "Client did not start registration")
            mark = canceled.mark()
            canceled.send("disconnect")
            # A subsequent read proves the consumer processed disconnect while HTTP is blocked.
            canceled.send("read")
            canceled.phase("Disconnected", args.timeout, mark)
            mark = canceled.mark()
            release.set()
            canceled.phase("Disconnected", args.timeout, mark)
            canceled.send("quit")
            canceled.process.wait(timeout=args.timeout)
            check(canceled.process.returncode == 0, "Canceled auth did not stop cleanly")
            check(requests == ["/auth/register"], "Canceled registration proceeded to login")
            check("session=Connecting" not in canceled.output(), "Canceled auth opened an ENet connection")
            stage("disconnect during HTTP registration suppresses subsequent login and ENet connect")
        finally:
            release.set()
            if canceled is not None:
                canceled.stop()
            endpoint.shutdown()
            endpoint.server_close()
            thread.join(timeout=args.timeout)

    try:
        check_canceled_registration()
        server = start_server("server")
        alice = start_client("alice", "smoke_alice", "Smoke Alice")
        alice.phase("Ready", args.timeout)
        alice.wait_for(lambda lines: any(re.fullmatch(r"\d+: Smoke Alice", line) for line in lines), args.timeout, read=True)
        alice_id = next(line.split(":", 1)[0] for line in alice.output() if re.fullmatch(r"\d+: Smoke Alice", line))
        stage("native client loaded a caller-provided configuration path and opened a real server session")
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
        alice.send("move " + json.dumps({"location": sample["location"]}))
        alice.send("values " + json.dumps(sample["actorValues"]))
        alice.send("details " + json.dumps(details))
        authoritative = wait_player(alice, alice_id,
            lambda state: state.get("characterName") == "Nerevar" and state.get("location") is not None
                and len(state["actorValues"]) == 2 and state["details"].get("level") == 25,
            args.timeout, alice_start)
        check(authoritative["location"]["sampledAtUs"] > 0, "Source measurement timestamp was lost")
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
        alice.wait_for(lambda lines: bool(player_states(lines, bob_id)), args.timeout, read=True)
        late_join = wait_player(bob, alice_id, lambda state: state.get("characterName") == "Nerevar", args.timeout)
        def metadata(state):
            return {key: value for key, value in state.items()
                    if key not in ("location", "viewRevision", "movementSequence")}

        def pose(state):
            return {key: value for key, value in state["location"].items() if key != "sampledAtUs"}

        check(metadata(late_join) == metadata(authoritative) and not late_join.get("location"),
              "Late-join metadata changed or location leaked to an observer without position")
        check(all(not state.get("location") for state in player_states(bob.output(), alice_id)),
              "Bootstrap exposed location before the observer had a position")
        stage("late join receives global PlayerInfo metadata without leaking positions to an unlocated observer")

        bob_start, alice_start = bob.mark(), alice.mark()
        bob.send("begin Observer")
        observer_sample = {"location": copy.deepcopy(sample["location"]), "actorValues": {}}
        observer_sample["location"]["position"]["X"] = 0
        bob.send("move " + json.dumps({"location": observer_sample["location"]}))
        visible = wait_player(bob, alice_id, lambda state: state.get("location") is not None, args.timeout, bob_start)
        check(metadata(visible) == metadata(authoritative) and pose(visible) == pose(authoritative),
              "Entering range missed the stationary player's full game state")
        check(visible.get("viewRevision", 0) > 0, "Visible position has no reliable baseline revision")
        wait_player(alice, bob_id, lambda state: state.get("location") is not None, args.timeout, alice_start)
        stage("an observer entering the same space receives the stationary player's location")

        alice_start, bob_start = alice.mark(), bob.mark()
        sample["location"]["position"]["X"] = 42
        sample["location"]["rotation"]["Z"] = 2.5
        alice.send("move " + json.dumps({"location": sample["location"]}))
        for child, start_at in ((alice, alice_start), (bob, bob_start)):
            moved = wait_player(child, alice_id,
                lambda state: state.get("location") is not None and state["location"]["position"]["X"] == 42,
                args.timeout, start_at)
            check(moved["details"] == authoritative["details"] and moved["actorValues"] == authoritative["actorValues"],
                  "Movement changed character metadata or actor values")
            check(moved["location"]["rotation"]["Z"] == 2.5, "Rotation did not replicate")
            check(moved["location"]["sampledAtUs"] > authoritative["location"]["sampledAtUs"],
                  "Movement timestamp did not advance across the real transport")
        stage("periodic compact movement reaches author and peer without losing unchanged details")

        pose_start = bob.mark()
        bob.send(f"watch {alice_id} 400")
        poses = bob.wait_for(lambda lines: any(line.startswith(f"pose {alice_id} 42 ") for line in lines),
                             args.timeout, pose_start)
        check(any(line.startswith(f"pose {alice_id} 42 ") for line in poses), "Live movement view did not settle")
        stage("native movement consumer samples the live ENet stream and holds its final position")

        alice_start, bob_start = alice.mark(), bob.mark()
        observer_sample["location"]["position"]["X"] = 20000
        bob.send("move " + json.dumps({"location": observer_sample["location"]}))
        hidden = wait_player(bob, alice_id, lambda state: not state.get("location"), args.timeout, bob_start)
        check(hidden["details"] == authoritative["details"] and hidden["actorValues"] == authoritative["actorValues"],
              "Leaving range removed global player metadata")
        wait_player(alice, bob_id, lambda state: not state.get("location"), args.timeout, alice_start)
        wait_player(bob, bob_id,
                    lambda state: state.get("location") is not None and state["location"]["position"]["X"] == 20000,
                    args.timeout, bob_start)

        bob_start = bob.mark()
        alice.send("rename Nerevar Outside")
        hidden = wait_player(bob, alice_id, lambda state: state.get("characterName") == "Nerevar Outside",
                             args.timeout, bob_start)
        check(not hidden.get("location"), "Full PlayerInfo leaked an out-of-range location")
        stage("leaving radius clears both remote positions while self and global metadata remain available")

        bob_start = bob.mark()
        observer_sample["location"]["position"]["X"] = 0
        bob.send("move " + json.dumps({"location": observer_sample["location"]}))
        stationary = wait_player(bob, alice_id,
            lambda state: state.get("location") is not None and state["location"]["position"]["X"] == 42,
            args.timeout, bob_start)
        check(stationary["characterName"] == "Nerevar Outside", "Reentry lost global metadata")
        stage("returning inside the radius restores a stationary source without a new source sample")

        bob_start = bob.mark()
        observer_sample["location"]["location"]["locationId"]["localFormId"] = 292
        bob.send("move " + json.dumps({"location": observer_sample["location"]}))
        wait_player(bob, alice_id, lambda state: not state.get("location"), args.timeout, bob_start)
        bob_start = bob.mark()
        observer_sample["location"]["location"]["locationId"]["localFormId"] = 291
        bob.send("move " + json.dumps({"location": observer_sample["location"]}))
        wait_player(bob, alice_id,
            lambda state: state.get("location") is not None and state["location"]["position"]["X"] == 42,
            args.timeout, bob_start)
        stage("different WRLD/CELL clears location and returning restores the stationary source")

        alice_start, bob_start = alice.mark(), bob.mark()
        alice.send("values {}")
        for child, start_at in ((alice, alice_start), (bob, bob_start)):
            cleared = wait_player(child, alice_id, lambda state: not state["actorValues"], args.timeout, start_at)
            check(cleared["location"]["position"]["X"] == 42 and cleared["details"] == authoritative["details"],
                  "Actor values replacement changed movement or details")
        alice_start, bob_start = alice.mark(), bob.mark()
        details["level"] = 0
        alice.send("details " + json.dumps(details))
        for child, start_at in ((alice, alice_start), (bob, bob_start)):
            updated = wait_player(child, alice_id, lambda state: state["details"].get("level") == 0, args.timeout, start_at)
            check(not updated["actorValues"] and updated["location"]["position"]["X"] == 42,
                  "Details replacement changed movement or actor values")
        stage("independent metadata patches clear actor values and update details without touching movement")


        alice_start, bob_start = alice.mark(), bob.mark()
        alice.send("rename Nerevar Renamed")
        for child, start_at in ((alice, alice_start), (bob, bob_start)):
            renamed = wait_player(child, alice_id, lambda state: state.get("characterName") == "Nerevar Renamed",
                                  args.timeout, start_at)
            check(renamed["characterGeneration"] == 1 and not renamed["actorValues"] and renamed["details"]["level"] == 0,
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

        def refused(text: str, code: int):
            alice_start, bob_start = alice.mark(), bob.mark()
            alice.send("send " + text)
            alice.wait_for(lambda lines: any(f"rejected ({code})" in line for line in lines),
                           args.timeout, alice_start, read=True)
            settle_reads(bob, args.timeout)
            check(not any(text in line for line in bob.output(bob_start)), f"Refused text reached the peer: {text}")
            check(not any(text in line and line.startswith("[1] ") for line in alice.output(alice_start)),
                  f"Refused text was published to its author: {text}")

        refused(f"you F0RB1DDENW0RD {nonce}", 9)
        repeat = f"smoke-repeat-{nonce}"
        message_once(alice, bob, repeat, args.timeout)
        refused(repeat.upper(), 10)
        stage("word list and repeated-text limit refuse messages before storage and relay")

        # The system channel (2): "[2] announcement source=S kind=K signature=L author: text";
        # a server announcement has no author and prints as <system>.
        def announced(child: Child, marker: str, prefix: str, start: int):
            child.wait_for(lambda lines: any(marker in line and line.startswith(prefix) for line in lines),
                           args.timeout, start, read=True)

        welcome = f"smoke-welcome-{nonce}"
        for child in (alice, bob):
            announced(child, welcome, "[2] announcement source=1 kind=1 signature= <system>: ", 0)
        stage("a scheduled server announcement reached the system channel history without an author")

        admin = f"smoke-admin-{nonce}"
        alice_start, bob_start = alice.mark(), bob.mark()
        server.send("announce " + admin)
        for child, start_at in ((alice, alice_start), (bob, bob_start)):
            announced(child, admin, "[2] announcement source=1 kind=3 signature= <system>: ", start_at)
        stage("an administrator console announcement reached every client")

        # refusal: "rejected (<server code>)" or "not sent (local <code>)".
        def announce_refused(command: str, marker: str, refusal: str):
            alice_start, bob_start = alice.mark(), bob.mark()
            alice.send(command)
            alice.wait_for(lambda lines: any(refusal in line for line in lines),
                           args.timeout, alice_start, read=True)
            settle_reads(bob, args.timeout)
            check(not any(marker in line for line in bob.output(bob_start)), f"Refused announcement reached the peer: {marker}")

        # The welcome policy disallows it, so Core refuses it before sending.
        announce_refused(f"announce trusted announcement - smoke-trusted-{nonce}", f"smoke-trusted-{nonce}", "not sent (local 3)")
        stage("a source the welcome policy disables was refused locally and never sent")

        announce_refused(f"announce third admin SmokeMod smoke-admin-kind-{nonce}", f"smoke-admin-kind-{nonce}", "rejected (1)")
        stage("a server-only announcement kind requested by a client was refused as invalid")

        for index in (1, 2):
            marker = f"smoke-event-{index}-{nonce}"
            alice_start, bob_start = alice.mark(), bob.mark()
            alice.send(f"announce third event SmokeMod {marker}")
            for child, start_at in ((alice, alice_start), (bob, bob_start)):
                announced(child, marker, "[2] announcement source=3 kind=2 signature=SmokeMod Smoke Alice: ", start_at)
        stage("third-party announcements were published with origin, kind, label and author")

        announce_refused(f"announce third event SmokeMod smoke-event-3-{nonce}", f"smoke-event-3-{nonce}", "rejected (10)")
        message_once(alice, bob, f"smoke-after-announcements-{nonce}", args.timeout)
        stage("the announcement rate limit refused the third one while chat stayed available")

        # Marks on the ground: "mark <id> kind=<1|2> author=<name> x=<x> character=<name> date=<date> text=<text>", "mark-removed <id>",
        # "marks-cleared", "request <n> placed mark <id>[ evicted <id>]", "request <n> removed mark <id>".
        mark_line = re.compile(r"mark (\d+) kind=(\d) author=(.+?) x=(\S+) character=(.*?) date=(\S+) text=(.*)")
        # Client.Dev dates every mark it places with the same game date.
        console_date = "4E201-08-17T14:05"
        placed_line = re.compile(r"request \d+ placed mark (\d+)(?: evicted (\d+))?")

        def placed(child: Child, start: int):
            lines = child.wait_for(lambda lines: any(placed_line.fullmatch(line) for line in lines), args.timeout, start, read=True)
            match = next(placed_line.fullmatch(line) for line in lines if placed_line.fullmatch(line))
            return match.group(1), match.group(2)

        def saw_mark(child: Child, mark_id: str, start: int, text=None, kind="1"):
            def found(lines):
                for line in lines:
                    match = mark_line.fullmatch(line)
                    if match and match.group(1) == mark_id and match.group(2) == kind and (text is None or match.group(7) == text):
                        return True
                return False
            child.wait_for(found, args.timeout, start, read=True)

        # The bounded state queue may replace deltas with a snapshot ("marks N revision=R" and
        # its mark lines), so absence is also proven by a snapshot that lacks the mark.
        def snapshot_blocks(lines: list[str]):
            blocks, current = [], None
            for line in lines:
                if line.startswith("marks "):
                    current = []
                    blocks.append(current)
                elif current is not None and line.startswith("session="):
                    current = None
                elif current is not None and mark_line.fullmatch(line):
                    current.append(line)
            return blocks

        def mark_gone(child: Child, mark_id: str, start: int):
            def gone(lines):
                if f"mark-removed {mark_id}" in lines:
                    return True
                return any(all(not line.startswith(f"mark {mark_id} ") for line in block) for block in snapshot_blocks(lines))
            child.wait_for(gone, args.timeout, start, read=True)

        def marks_cleared(child: Child, start: int):
            child.wait_for(lambda lines: "marks-cleared" in lines or any(line.startswith("marks 0 ") for line in lines),
                           args.timeout, start, read=True)

        def mark_refused(child: Child, command: str, code: int):
            start = child.mark()
            child.send(command)
            child.wait_for(lambda lines: any(f"rejected ({code})" in line for line in lines), args.timeout, start, read=True)

        alice_start = alice.mark()
        alice.send("begin Marker")
        alice.send("move " + json.dumps({"location": sample["location"]}))
        wait_player(alice, alice_id, lambda state: state.get("location") is not None and state["location"]["position"]["X"] == 42,
                    args.timeout, alice_start)
        note1 = f"smoke-note-1-{nonce}"
        alice_start, bob_start = alice.mark(), bob.mark()
        alice.send("note " + note1)
        first_id, evicted = placed(alice, alice_start)
        check(evicted is None, "The first note evicted something")
        saw_mark(alice, first_id, alice_start, note1)
        saw_mark(bob, first_id, bob_start, note1)
        check(any(mark_line.fullmatch(line) and mark_line.fullmatch(line).group(3) == "Smoke Alice"
                  and mark_line.fullmatch(line).group(5) == "Marker" for line in bob.output(bob_start)),
              "The observer did not receive the author's profile and character name with the mark")
        stage("a note is confirmed to its author and appears with the author's name and character for a nearby client")

        bob_start = bob.mark()
        observer_sample["location"]["position"]["X"] = 20000
        bob.send("move " + json.dumps({"location": observer_sample["location"]}))
        mark_gone(bob, first_id, bob_start)
        note2 = f"smoke-note-2-{nonce}"
        alice_start, bob_start = alice.mark(), bob.mark()
        alice.send("note " + note2)
        second_id, _ = placed(alice, alice_start)
        saw_mark(alice, second_id, alice_start, note2)
        settle_reads(bob, args.timeout)
        check(not any(note2 in line for line in bob.output(bob_start)), "A far observer received a new mark")
        stage("leaving the radius removes marks and a far observer does not receive new ones")

        bob_start = bob.mark()
        observer_sample["location"]["position"]["X"] = 0
        bob.send("move " + json.dumps({"location": observer_sample["location"]}))
        saw_mark(bob, first_id, bob_start)
        saw_mark(bob, second_id, bob_start)
        bob_start = bob.mark()
        observer_sample["location"]["location"]["locationId"]["localFormId"] = 292
        bob.send("move " + json.dumps({"location": observer_sample["location"]}))
        marks_cleared(bob, bob_start)
        bob_start = bob.mark()
        observer_sample["location"]["location"]["locationId"]["localFormId"] = 291
        bob.send("move " + json.dumps({"location": observer_sample["location"]}))
        saw_mark(bob, first_id, bob_start)
        saw_mark(bob, second_id, bob_start)
        stage("another WRLD/CELL clears visible marks and returning restores them as a new baseline")

        alice_start, bob_start = alice.mark(), bob.mark()
        alice.send("death Alduin")
        death_id, _ = placed(alice, alice_start)
        saw_mark(alice, death_id, alice_start, "Alduin", kind="2")
        saw_mark(bob, death_id, bob_start, "Alduin", kind="2")
        mark_refused(alice, "death again", 10)
        stage("a death mark carries its label to author and observer; a second death too soon is rate limited")

        note3 = f"smoke-note-3-{nonce}"
        alice_start, bob_start = alice.mark(), bob.mark()
        alice.send("note " + note3)
        third_id, evicted = placed(alice, alice_start)
        check(evicted == first_id, f"Expected eviction of {first_id}, got {evicted}")
        mark_gone(bob, first_id, bob_start)
        saw_mark(bob, third_id, bob_start, note3)
        stage("the note quota evicts the author's oldest note and reports its id to the author")

        bob_note = f"smoke-bob-note-{nonce}"
        bob_start, alice_start = bob.mark(), alice.mark()
        bob.send("note " + bob_note)
        bob_mark_id, _ = placed(bob, bob_start)
        saw_mark(alice, bob_mark_id, alice_start, bob_note)
        mark_refused(bob, f"note smoke-overflow-{nonce}", 12)
        mark_refused(alice, f"note you forbiddenword {nonce}", 9)
        stage("a full index cell refuses new marks without evicting others; the word list refuses a note")

        alice_start, bob_start = alice.mark(), bob.mark()
        alice.send(f"unmark {third_id}")
        alice.wait_for(lambda lines: any(re.fullmatch(rf"request \d+ removed mark {third_id}", line) for line in lines),
                       args.timeout, alice_start, read=True)
        mark_gone(bob, third_id, bob_start)
        mark_refused(bob, f"unmark {second_id}", 13)
        stage("only the author removes a mark; observers see the removal")

        # Hidden names: the session opens hidden from the first packet; others see only the
        # server pseudonym ("[1] Тень [pseudonymous]: ...", "mark ... author=Тень [pseudonymous] x=...").
        real_names = ("Smoke Hidden", "smoke_hidden", "Secret Hero")
        bob_start = bob.mark()
        hidden = start_client("hidden", "smoke_hidden", "Smoke Hidden", hidden="--hide")
        hidden.phase("Ready", args.timeout)
        hidden.wait_for(lambda lines: "pseudonym=Тень" in lines, args.timeout, read=True)
        hidden.wait_for(lambda lines: any(re.fullmatch(r"\d+: Smoke Hidden", line) for line in lines), args.timeout, read=True)
        hidden_id = next(line.split(":", 1)[0] for line in hidden.output() if re.fullmatch(r"\d+: Smoke Hidden", line))
        shown_as = wait_player(bob, hidden_id, lambda state: state["data"].get("pseudonymous"), args.timeout, bob_start)
        check(shown_as["data"]["username"] == "" and shown_as["data"]["displayName"] == "Тень",
              "The observer received something other than the pseudonym")
        check("nameColor" not in shown_as["data"], "The pseudonym carried the player's name color")
        stage("a session opened hidden shows the pseudonym with an empty username online; its owner sees the real profile")

        hidden_start, bob_start = hidden.mark(), bob.mark()
        hidden.send("begin Secret Hero")
        hidden.send("move " + json.dumps({"location": sample["location"]}))
        wait_player(hidden, hidden_id, lambda state: state.get("characterName") == "Secret Hero" and state.get("location") is not None,
                    args.timeout, hidden_start)
        wait_player(bob, hidden_id, lambda state: state["characterGeneration"] >= 1 and state.get("location") is not None,
                    args.timeout, bob_start)
        hidden_message = f"smoke-hidden-{nonce}"
        hidden.send("send " + hidden_message)
        bob.wait_for(lambda lines: f"[1] Тень [pseudonymous]: {hidden_message}" in lines, args.timeout, bob_start, read=True)
        hidden_note = f"smoke-hidden-note-{nonce}"
        hidden.send("note " + hidden_note)
        hidden_mark, _ = placed(hidden, hidden_start)
        bob.wait_for(lambda lines: any(line.startswith(f"mark {hidden_mark} kind=1 author=Тень [pseudonymous] x=")
                                       and line.endswith(f"character= date={console_date} text={hidden_note}") for line in lines),
                     args.timeout, bob_start, read=True)
        settle_reads(bob, args.timeout)
        for name in real_names:
            check(not any(name in line for line in bob.output(bob_start)), f"A real name reached the observer: {name}")
        stage("while hidden the observer sees only the pseudonym in presence, the message and the mark")

        hidden_start, bob_start = hidden.mark(), bob.mark()
        hidden.send("hide off")
        hidden.wait_for(lambda lines: any(re.fullmatch(r"request \d+ identity shown", line) for line in lines),
                        args.timeout, hidden_start, read=True)
        wait_player(bob, hidden_id, lambda state: not state["data"].get("pseudonymous")
                    and state["data"]["displayName"] == "Smoke Hidden" and state.get("characterName") == "Secret Hero",
                    args.timeout, bob_start)
        late = start_client("late", "smoke_late", "Smoke Late")
        late.phase("Ready", args.timeout)
        late.wait_for(lambda lines: f"[1] Тень [pseudonymous]: {hidden_message}" in lines, args.timeout, read=True)
        check(not any(hidden_message in line and "Smoke Hidden" in line for line in late.output()),
              "History rewrote the author of a message sent while hidden")
        stage("showing the names again reaches the observer; a message sent while hidden keeps its pseudonym in history")

        mark_refused(hidden, "hide on", 10)
        stage("a second switch within the interval is rate limited and changes nothing")

        # The own display name: confirmed to the author, spread to the others through
        # presence; the word list and the change interval (a minute by default) refuse more.
        hidden_start, bob_start = hidden.mark(), bob.mark()
        hidden.send("name Smoke Renamed")
        hidden.wait_for(lambda lines: any(re.fullmatch(r"request \d+ display name Smoke Renamed", line) for line in lines),
                        args.timeout, hidden_start, read=True)
        wait_player(bob, hidden_id, lambda state: state["data"]["displayName"] == "Smoke Renamed"
                    and state["data"]["username"] == "smoke_hidden", args.timeout, bob_start)
        stage("an own display name change is confirmed and reaches the other client")
        mark_refused(hidden, "name forbiddenword", 9)
        hidden_start = hidden.mark()
        hidden.send("name Smoke Again")
        hidden.wait_for(lambda lines: any("rejected (10): The display name can be changed again in" in line for line in lines),
                        args.timeout, hidden_start, read=True)
        stage("a listed word and a second change within the interval are refused")

        # The name color: a palette color from registration, a dark one refused,
        # a chosen one stored and spread through presence, the next one too soon.
        palette = {0xE57373, 0xF06292, 0xBA68C8, 0x9575CD, 0x7986CB, 0x64B5F6, 0x4FC3F7, 0x4DD0E1,
                   0x4DB6AC, 0x81C784, 0xAED581, 0xDCE775, 0xFFF176, 0xFFD54F, 0xFFB74D, 0xFF8A65}
        registered = next(state for state in player_states(bob.output(), hidden_id) if not state["data"].get("pseudonymous"))
        check(registered["data"].get("nameColor") in palette, f"A new account has no palette color: {registered['data']}")
        mark_refused(hidden, "color #101010", 27)
        hidden_start, bob_start = hidden.mark(), bob.mark()
        hidden.send("color #FFFFFF")
        hidden.wait_for(lambda lines: any(re.fullmatch(r"request \d+ name color #FFFFFF", line) for line in lines),
                        args.timeout, hidden_start, read=True)
        wait_player(bob, hidden_id, lambda state: state["data"].get("nameColor") == 0xFFFFFF, args.timeout, bob_start)
        mark_refused(hidden, "color #4FC3F7", 10)
        stage("a new account has a palette color; a dark one is refused, a chosen one reaches others, the next waits")
        for child in (hidden, late):
            child.send("quit")
            child.process.wait(timeout=args.timeout)
            check(child.process.returncode == 0, f"{child.name} exited unsuccessfully")
        settle_reads(bob, args.timeout)

        # Hidden everywhere except ground marks: presence and chat carry the pseudonym,
        # the mark shows the real profile and character. Another index cell keeps room.
        bob_start = bob.mark()
        partial = start_client("partial", "smoke_partial", "Smoke Partial", hidden="--hide-except-marks")
        partial.phase("Ready", args.timeout)
        partial.wait_for(lambda lines: "pseudonym=Тень" in lines, args.timeout, read=True)
        partial.wait_for(lambda lines: any(re.fullmatch(r"\d+: Smoke Partial", line) for line in lines), args.timeout, read=True)
        partial_id = next(line.split(":", 1)[0] for line in partial.output() if re.fullmatch(r"\d+: Smoke Partial", line))
        partial_start = partial.mark()
        partial.send("begin Partial Hero")
        nearby = copy.deepcopy(sample["location"])
        nearby["position"]["X"] = -100
        partial.send("move " + json.dumps({"location": nearby}))
        wait_player(partial, partial_id, lambda state: state.get("location") is not None, args.timeout, partial_start)
        wait_player(bob, partial_id, lambda state: state["data"].get("pseudonymous") and state["characterGeneration"] >= 1
                    and not state.get("characterName"), args.timeout, bob_start)
        partial_message = f"smoke-partial-{nonce}"
        partial.send("send " + partial_message)
        bob.wait_for(lambda lines: f"[1] Тень [pseudonymous]: {partial_message}" in lines, args.timeout, bob_start, read=True)
        partial_note = f"smoke-partial-note-{nonce}"
        partial.send("note " + partial_note)
        partial_mark, _ = placed(partial, partial_start)
        bob.wait_for(lambda lines: any(line.startswith(f"mark {partial_mark} kind=1 author=Smoke Partial x=")
                                       and line.endswith(f"character=Partial Hero date={console_date} text={partial_note}") for line in lines),
                     args.timeout, bob_start, read=True)
        settle_reads(bob, args.timeout)
        check(not any("Smoke Partial" in line and "mark " not in line for line in bob.output(bob_start)),
              "The real name left the ground mark for presence or chat")
        stage("hidden except marks: presence and chat show the pseudonym, the ground mark the real profile")
        partial.send("quit")
        partial.process.wait(timeout=args.timeout)
        check(partial.process.returncode == 0, "partial exited unsuccessfully")
        settle_reads(bob, args.timeout)

        # The bounded state queue may replace an "offline" delta with a snapshot
        # ("snapshot generation=G players=N" and "<id>: <name>" rows) that lacks the player.
        def went_offline(player_id: str):
            def gone(lines):
                if f"offline {player_id}" in lines:
                    return True
                for index, line in enumerate(lines):
                    if line.startswith("snapshot generation="):
                        listed = lines[index + 1:]
                        ends = next((at for at, row in enumerate(listed)
                                     if not (re.fullmatch(r"\d+: .*", row) or row.startswith("player {"))), len(listed))
                        rows = [row.split(":", 1)[0] for row in listed[:ends] if re.fullmatch(r"\d+: .*", row)]
                        if player_id not in rows:
                            return True
                return False
            return gone

        alice_start, bob_start = alice.mark(), bob.mark()
        alice.send("disconnect")
        alice.phase("Disconnected", args.timeout, alice_start)
        bob.wait_for(went_offline(alice_id), args.timeout, bob_start, read=True)
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
        bob.wait_for(lambda lines: bool(player_states(lines, alice_id)), args.timeout, bob_start, read=True)
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

        # "own-marks <n>" then "own <id> kind=<k> text=<t>": the server's complete list of the
        # player's marks arrives right after the session opened, before any position is sent.
        def own_marks(child: Child, start: int):
            # The snapshot taken before the list arrives prints "own-marks 0"; the newest
            # complete list counts, and only a non-empty one satisfies the wait.
            def listed(lines):
                for index in range(len(lines) - 1, -1, -1):
                    match = re.fullmatch(r"own-marks (\d+)", lines[index])
                    if match:
                        count = int(match.group(1))
                        rows = lines[index + 1:index + 1 + count]
                        if len(rows) == count and all(row.startswith("own ") for row in rows):
                            return [re.fullmatch(r"own (\d+) kind=(\d) date=(\S+) text=(.*)", row).groups() for row in rows]
                        return False
                return False
            return listed(child.wait_for(listed, args.timeout, start, read=True))

        own = own_marks(alice, alice_start)
        check(sorted(row[0] for row in own) == sorted([second_id, death_id]),
              f"Own list after restart must hold {second_id} and {death_id}, got {[row[0] for row in own]}")
        check(any(row[0] == death_id and row[1] == "2" and row[3] == "Alduin" for row in own), "Own death mark lost its label")
        check(all(row[2] == console_date for row in own), f"Own marks must keep their game date across the restart, got {own}")
        own_bob = own_marks(bob, bob_start)
        check([row[0] for row in own_bob] == [bob_mark_id], f"Bob's own list must hold only {bob_mark_id}, got {own_bob}")
        stage("after a restart every own mark is listed to its author on connect, with far marks included")

        bob_start = bob.mark()
        bob.send("begin Observer")
        bob.send("move " + json.dumps({"location": observer_sample["location"]}))
        for mark_id in (second_id, death_id, bob_mark_id):
            saw_mark(bob, mark_id, bob_start, kind="2" if mark_id == death_id else "1")
        settle_reads(bob, args.timeout)
        check(not any(f"mark {third_id} " in line for line in bob.output(bob_start)), "A removed mark came back after restart")
        check(any(mark_line.fullmatch(line) and mark_line.fullmatch(line).group(1) == second_id
                  and mark_line.fullmatch(line).group(5) == "Marker" for line in bob.output(bob_start)),
              "The character name of a stored mark was lost across restart")
        stage("server restart preserved the remaining marks and a reconnected client receives them near its position")
        # Its author shows the real names now, yet the mark placed while hidden keeps the pseudonym.
        bob.wait_for(lambda lines: any(line.startswith(f"mark {hidden_mark} kind=1 author=Тень [pseudonymous] x=") for line in lines),
                     args.timeout, bob_start, read=True)
        check(not any(line.startswith(f"mark {hidden_mark} ") and ("Smoke Hidden" in line or "Smoke Renamed" in line)
                      for line in bob.output(bob_start)),
              "A stored mark placed while hidden revealed its author after restart")
        stage("a mark placed while hidden keeps its pseudonym across a server restart")

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

        strict = directory / "server-strict.toml"
        settings["Identity"]["AllowHiddenIdentity"] = False
        strict.write_text(tomli_w.dumps(settings), encoding="utf-8")
        server = start_server("server-strict", strict)
        refused_client = start_client("refused-hidden", "smoke_refused", "Smoke Refused", hidden="--hide")
        refused_client.wait_for(lambda lines: any("rejected (14)" in line for line in lines), args.timeout, read=True)
        refused_client.phase("Disconnected", args.timeout)
        check("session=Ready" not in refused_client.output(), "A refused hidden session became ready")
        refused_client.send("quit")
        refused_client.process.wait(timeout=args.timeout)
        server.send("quit")
        server.process.wait(timeout=args.timeout)
        check(server.process.returncode == 0, "Strict server did not shut down successfully")
        stage("a server that does not allow hidden names refuses such an opening with HIDDEN_IDENTITY_NOT_ALLOWED")
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
