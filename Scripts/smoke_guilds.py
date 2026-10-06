#!/usr/bin/env python3
"""Guilds over the real server and three native Dev clients: creation and the
name rules, an invitation and its acceptance, a rename from the admin panel
that members and the invited player see at once, guild chat with the real name
of a member who hides it elsewhere, a guild mute and its lift, an officer
who removes a member's message and excludes them, the master who cannot
leave and hands the guild over, the stored rows, disbanding and the name
free again.
"""
import argparse
import os
import re
import sqlite3
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from contextlib import closing
from http.cookiejar import CookieJar
from pathlib import Path

import tomli_w

from smoke_chat import ROOT, Child, check, free_tcp_port, free_udp_port

# Protocol::Chat::RequestRejectionCode values the checks name.
INVALID_REQUEST, MUTED, GUILD_NAME_TAKEN, GUILD_MASTER_STAYS = 1, 16, 19, 26


class _Stay(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


class Panel:
    """The admin panel as a browser of the same origin, with its session cookie."""

    def __init__(self, url: str):
        self.url = url
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(CookieJar()), _Stay())

    def post(self, path: str, fields: dict) -> tuple[int, str | None]:
        request = urllib.request.Request(self.url + path, data=urllib.parse.urlencode(fields).encode(),
                                         headers={"Origin": self.url})
        try:
            with self.opener.open(request, timeout=10) as response:
                return response.status, response.headers.get("Location")
        except urllib.error.HTTPError as error:
            return error.code, error.headers.get("Location")


def smoke(args, log, directory: Path):
    children: list[Child] = []
    lock = threading.Lock()
    port, auth_url, panel_url = free_udp_port(), f"http://127.0.0.1:{free_tcp_port()}", f"http://127.0.0.1:{free_tcp_port()}"
    database = directory / "accounts.sqlite"
    config = directory / "server.toml"
    config.write_text(tomli_w.dumps({
        "Database": {"DatabasePath": str(database), "BusyTimeoutSeconds": 5},
        "Authentication": {"Listener": {"ListenUrl": auth_url, "AllowInsecureLoopback": True}},
        "Admin": {"Listener": {"ListenUrl": panel_url}},
        "Identity": {"ToggleIntervalMs": 0},
        "Logging": {"MinimumLevel": "Debug", "FilePath": str(directory / "server-.json")},
    }), encoding="utf-8")
    env = dict(os.environ, DREAMSLEEVE_PASSWORD="smoke-" + uuid.uuid4().hex)
    nonce = uuid.uuid4().hex[:8]

    def stage(message: str):
        print("PASS " + message, flush=True)
        with lock:
            log.write("CHECK " + message + "\n")
            log.flush()

    def start(name: str, command: list[str], environment=None) -> Child:
        child = Child(name, command, log, lock, environment)
        children.append(child)
        return child

    def client(name: str, username: str, display: str) -> tuple[Child, str]:
        child = start(name, [str(args.client), "--connect", "127.0.0.1", str(port), username,
                             "--auth-url", auth_url, "--register", display], env)
        child.phase("Ready", args.timeout)
        lines = child.wait_for(lambda out: any(re.fullmatch(rf"\d+: {display}", line) for line in out),
                               args.timeout, read=True)
        return child, next(line.split(":", 1)[0] for line in lines if re.fullmatch(rf"\d+: {display}", line))

    def expect(child: Child, pattern: str, start: int) -> re.Match:
        lines = child.wait_for(lambda out: any(re.search(pattern, line) for line in out), args.timeout, start, read=True)
        return next(match for line in lines if (match := re.search(pattern, line)))

    def command(child: Child, text: str, pattern: str) -> re.Match:
        start = child.mark()
        child.send(text)
        return expect(child, pattern, start)

    def done(child: Child, text: str) -> str:
        return command(child, text, r"guild done (\d+)$").group(1)

    # The guild owner writes through its own writer: the rows follow shortly.
    def stored(query: str, parameters: tuple, expected, what: str):
        deadline = time.monotonic() + args.timeout
        while True:
            with closing(sqlite3.connect(database)) as db:
                rows = sorted(db.execute(query, parameters).fetchall())
            if rows == expected:
                return
            if time.monotonic() >= deadline:
                raise AssertionError(f"{what}: {rows}")
            time.sleep(0.1)

    try:
        server = start("server", ["dotnet", str(args.server), "--port", str(port), "--config", str(config)])
        lines = server.wait_for(lambda lines: any(f"Listening on 127.0.0.1:{port}" in line for line in lines), args.timeout, read=True)
        setup = next(match.group(1) for line in lines if (match := re.search(r"setup code .*: (\S+)$", line)))
        panel = Panel(panel_url)
        password = "smoke-" + uuid.uuid4().hex
        status, _ = panel.post("/setup", {"code": setup, "username": "root", "password": password, "password2": password})
        check(status == 303, f"The panel setup answered {status}")
        alice, alice_id = client("alice", "smoke_alice", "Smoke Alice")
        bob, bob_id = client("bob", "smoke_bob", "Smoke Bob")
        carol, carol_id = client("carol", "smoke_carol", "Smoke Carol")

        name = f"Вороны{nonce}"
        expect(alice, r"^guilds 0 invites 0 per-player=3 members=64 name=3\.\.24$", 0)
        start_alice = alice.mark()
        guild = done(alice, f"guild create {name}")
        channel = expect(alice, rf"^guild {guild} name={name} channel=(\d+) members=1$", start_alice).group(1)
        check(int(channel) == 4294967296 + int(guild), "The guild channel does not follow from the guild")
        expect(alice, rf"^member {guild} {alice_id} master online=1 name=Smoke Alice$", start_alice)
        command(alice, "guild create Два-слова", rf"rejected \({INVALID_REQUEST}\)")
        command(bob, f"guild create {name.upper()}", rf"rejected \({GUILD_NAME_TAKEN}\)")
        stage("a guild is created with its master and channel; punctuation and the same name in other case are refused")

        start_bob = bob.mark()
        done(alice, f"guild invite {guild} {bob_id}")
        expect(bob, rf"^invite {guild} name={name} by={alice_id} expires=\d+$", start_bob)
        start_alice = alice.mark()
        done(bob, f"guild accept {guild}")
        expect(bob, rf"^guild {guild} name={name} channel={channel} members=2$", start_bob)
        expect(alice, rf"^member {guild} {bob_id} member online=1 name=Smoke Bob$", start_alice)
        stage("an invitation waits for the online player; accepting brings them in and the members hear of it")

        old_name, name = name, f"Совы{nonce}"
        done(alice, f"guild invite {guild} {carol_id}")
        start_alice, start_bob, start_carol = alice.mark(), bob.mark(), carol.mark()
        status, location = panel.post(f"/guilds/{guild}/rename", {"name": f" {name} "})
        check((status, location) == (303, f"/guilds/{guild}?done=guild-renamed"), f"The panel rename answered {status} {location}")
        for member in (alice, bob):
            expect(member, rf"^guild {guild} name={name} channel={channel} members=2$", start_alice if member is alice else start_bob)
        expect(carol, rf"^invite {guild} name={name} by={alice_id} expires=\d+$", start_carol)
        stored("SELECT name FROM guilds WHERE id = ?", (int(guild),), [(name,)], "The new guild name is not stored")
        start_alice = alice.mark()
        command(bob, f"guild say {guild} renamed-{nonce}", r"published message \d+")
        expect(alice, rf"^\[{channel}\] Smoke Bob: renamed-{nonce}$", start_alice)
        done(carol, f"guild decline {guild}")
        freed = done(carol, f"guild create {old_name}")
        done(carol, f"guild disband {freed}")
        stage("the panel renames the guild: members and the invited player see the new name at once, the chat goes on, the old name is free")

        command(bob, "hide on", r"identity hidden as .+ everywhere")
        start_alice = alice.mark()
        command(bob, f"send global-{nonce}", r"published message \d+")
        expect(alice, rf"^\[1\] .+ \[pseudonymous\]: global-{nonce}$", start_alice)
        start_alice = alice.mark()
        command(bob, f"guild say {guild} guild-{nonce}", r"published message \d+")
        expect(alice, rf"^\[{channel}\] Smoke Bob: guild-{nonce}$", start_alice)
        stage("guildmates see the real name in guild chat while the global chat shows the pseudonym")

        start_bob = bob.mark()
        done(alice, f"guild mute {guild} {bob_id} 15 Флуд")
        expect(bob, rf"^member {guild} {bob_id} member online=1 muted name=Smoke Bob$", start_bob)
        command(bob, f"guild say {guild} muted-{nonce}", rf"rejected \({MUTED}\)")
        command(bob, f"send still-global-{nonce}", r"published message \d+")
        done(alice, f"guild unmute {guild} {bob_id}")
        command(bob, f"guild say {guild} free-{nonce}", r"published message \d+")
        stage("a guild mute closes only the guild's chat and lifts")

        start_bob = bob.mark()
        done(alice, f"guild role {guild} {bob_id} officer")
        expect(bob, rf"^member {guild} {bob_id} officer ", start_bob)
        done(alice, f"guild invite {guild} {carol_id}")
        done(carol, f"guild accept {guild}")
        spam = command(carol, f"guild say {guild} spam-{nonce}", r"published message (\d+)").group(1)
        start_alice = alice.mark()
        command(bob, f"guild delete {guild} {spam}", rf"deleted message {spam}$")
        expect(alice, rf"^message-deleted {spam}$", start_alice)
        start_carol = carol.mark()
        done(bob, f"guild exclude {guild} {carol_id}")
        expect(carol, r"^guilds 0 invites 0 ", start_carol)
        stage("an officer removes a member's message and excludes the member")

        command(alice, f"guild leave {guild}", rf"rejected \({GUILD_MASTER_STAYS}\)")
        start_alice = alice.mark()
        done(alice, f"guild transfer {guild} {bob_id}")
        expect(alice, rf"^member {guild} {alice_id} officer ", start_alice)
        expect(alice, rf"^member {guild} {bob_id} master ", start_alice)
        stored("SELECT player_id, role FROM guild_members WHERE guild_id = ?", (int(guild),),
               sorted([(int(alice_id), 1), (int(bob_id), 2)]), "Unexpected stored members")
        stage("the master cannot leave but hands the guild over and becomes an officer; the rows follow")

        start_alice = alice.mark()
        done(bob, f"guild disband {guild}")
        expect(alice, r"^guilds 0 invites 0 ", start_alice)
        stored("SELECT player_id FROM guild_members WHERE guild_id = ?", (int(guild),), [],
               "Members of a disbanded guild are still stored")
        done(alice, f"guild create {name.upper()}")
        stage("a disbanded guild leaves its members and frees its name")

        spaced = done(bob, f"guild create  Два   слова {nonce} ")
        stored("SELECT name FROM guilds WHERE id = ?", (int(spaced),), [(f"Два слова {nonce}",)],
               "A spaced guild name is not stored trimmed with single spaces")
        stage("a name with spaces is created trimmed, with runs of spaces as one")
    finally:
        for child in reversed(children):
            child.stop()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, default=ROOT / "src/Dreamsleeve.Server/bin/Release/net10.0/Dreamsleeve.Server.dll")
    parser.add_argument("--client", type=Path, default=ROOT / "build/windows/x64/releasedbg/Dreamsleeve.Client.Dev.exe")
    parser.add_argument("--timeout", type=float, default=15.0)
    parser.add_argument("--log", type=Path, default=ROOT / "build/smoke-guilds.log")
    args = parser.parse_args()
    for artifact in (args.server, args.client):
        if not artifact.is_file():
            parser.error(f"Build the server and native Dev first; missing {artifact}")
    args.log.parent.mkdir(parents=True, exist_ok=True)
    try:
        with tempfile.TemporaryDirectory(prefix="smoke-guilds-", dir=ROOT / "build") as directory:
            with args.log.open("w", encoding="utf-8") as log:
                smoke(args, log, Path(directory))
    except (AssertionError, OSError, RuntimeError, TimeoutError, subprocess.TimeoutExpired) as error:
        print(f"FAIL {error}\nLog: {args.log}", flush=True)
        return 1
    print(f"Log: {args.log}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
