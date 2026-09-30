#!/usr/bin/env python3
"""A moderator's tools over the real server and two native Dev clients: the
role from the account, a refusal without it, message deletion, mute and
lift, the list of sanctions, a player's marks and their removal, a kick, a
ban refusing sign-in, and every action in the audit log under the moderator.
"""
import argparse
import json
import os
import re
import sqlite3
import subprocess
import tempfile
import threading
import uuid
from contextlib import closing
from pathlib import Path

import tomli_w

from smoke_chat import ROOT, Child, check, free_tcp_port, free_udp_port

# Protocol::Chat::RequestRejectionCode and Domain::SessionEndReason values the checks name.
NOT_PERMITTED, MUTED = 17, 16
BANNED_FAILURE = 11  # Auth::FailureCode::Banned
KICKED = 3


def smoke(args, log, directory: Path):
    children: list[Child] = []
    lock = threading.Lock()
    port, auth_url = free_udp_port(), f"http://127.0.0.1:{free_tcp_port()}"
    database = directory / "accounts.sqlite"
    config = directory / "server.toml"
    config.write_text(tomli_w.dumps({
        "Database": {"DatabasePath": str(database), "BusyTimeoutSeconds": 5},
        "Authentication": {"AllowRegistration": True, "Listener": {"ListenUrl": auth_url, "AllowInsecureLoopback": True}},
        "Admin": {"Listener": {"ListenUrl": f"http://127.0.0.1:{free_tcp_port()}"}},
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

    try:
        server = start("server", ["dotnet", str(args.server), "--port", str(port), "--config", str(config)])
        server.wait_for(lambda lines: any(f"Listening on 127.0.0.1:{port}" in line for line in lines), args.timeout)
        moderator, moderator_id = client("moderator", "smoke_moderator", "Smoke Moderator")
        player, player_id = client("player", "smoke_player", "Smoke Player")

        command(player, "mod sanctions", rf"rejected \({NOT_PERMITTED}\)")
        # The role is the account's: stored by the panel, carried by the next ticket.
        with closing(sqlite3.connect(database)) as db, db:
            db.execute("INSERT INTO player_roles(player_id, role, granted_at) VALUES (?, 1, 0)", (int(moderator_id),))
        start_at = moderator.mark()
        moderator.send("disconnect")
        moderator.phase("Disconnected", args.timeout, start_at)
        start_at = moderator.mark()
        moderator.send("connect")
        moderator.phase("Ready", args.timeout, start_at)
        expect(moderator, r"^role=moderator$", start_at)
        stage("a player is refused moderator requests; the stored role reaches the next session's welcome")

        spam = f"smoke-spam-{nonce}"
        message_id = command(player, "send " + spam, r"published message (\d+)").group(1)
        expect(moderator, re.escape(f"Smoke Player: {spam}"), 0)
        start_player = player.mark()
        command(moderator, f"mod delete {message_id}", rf"deleted message {message_id}$")
        expect(player, rf"^message-deleted {message_id}$", start_player)
        stage("a deleted message leaves the moderator's and the author's chat")

        start_player = player.mark()
        command(moderator, f"mod mute {player_id} 15 Флуд", rf"sanctioned {player_id} mute until=\d+: Флуд")
        expect(player, r"^muted: Флуд$", start_player)
        command(player, "send still here", rf"rejected \({MUTED}\)")
        listed = command(moderator, "mod sanctions", r"^request \d+ sanctions (\d+)")
        check(listed.group(1) == "1", "The sanction list did not hold the mute")
        command(moderator, f"mod mute {moderator_id} 15 Сам", rf"rejected \({NOT_PERMITTED}\)")
        command(moderator, f"mod lift mute {player_id}", rf"lifted mute of {player_id}")
        command(player, "send free again", r"published message \d+")
        stage("a mute silences the player at once, is listed, spares moderators and lifts")

        location = {"location": {"locationId": {"pluginName": "Skyrim.esm", "localFormId": 291}, "locationName": "Whiterun"},
                    "position": {"X": 1, "Y": 2, "Z": 3}, "rotation": {"X": 0, "Y": 0, "Z": 1.5}}
        player.send("begin Nerevar")
        player.send("move " + json.dumps({"location": location}))
        mark_id = command(player, f"note smoke-note-{nonce}", r"placed mark (\d+)").group(1)
        command(moderator, f"mod marks {player_id}", rf"player-marks {player_id} 1")
        expect(moderator, rf"^mark {mark_id} kind=1 text=smoke-note-{nonce}$", 0)
        start_player = player.mark()
        command(moderator, f"mod clear {player_id} notes", rf"cleared 1 marks of {player_id}")
        expect(player, r"^own-marks 0$", start_player)
        stage("a moderator lists a player's marks and removes their notes; the author's list follows")

        start_player = player.mark()
        command(moderator, f"mod kick {player_id} Остынь", rf"kicked {player_id}")
        expect(player, rf"^ended={KICKED}: Остынь$", start_player)
        player.phase("Disconnected", args.timeout, start_player)
        command(moderator, f"mod ban {player_id} forever Читы", rf"sanctioned {player_id} ban until=lifted: Читы")
        start_player = player.mark()
        player.send("connect")
        expect(player, rf"failure={BANNED_FAILURE}", start_player)
        stage("a kick ends the session with its reason; a ban refuses the next sign-in")

        with closing(sqlite3.connect(database)) as db:
            actions = [row[0] for row in db.execute(
                "SELECT action FROM admin_audit WHERE moderator_id = ? ORDER BY id", (int(moderator_id),))]
        check(actions == ["delete_chat_message", "sanction_player", "lift_sanction", "clear_ground_marks",
                          "kick_player", "sanction_player"], f"Unexpected audit lines: {actions}")
        stage("every moderator action is in the audit log under the moderator")
    finally:
        for child in reversed(children):
            child.stop()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, default=ROOT / "src/Dreamsleeve.Server/bin/Release/net10.0/Dreamsleeve.Server.dll")
    parser.add_argument("--client", type=Path, default=ROOT / "build/windows/x64/releasedbg/Dreamsleeve.Client.Dev.exe")
    parser.add_argument("--timeout", type=float, default=15.0)
    parser.add_argument("--log", type=Path, default=ROOT / "build/smoke-moderation.log")
    args = parser.parse_args()
    for artifact in (args.server, args.client):
        if not artifact.is_file():
            parser.error(f"Build the server and native Dev first; missing {artifact}")
    args.log.parent.mkdir(parents=True, exist_ok=True)
    try:
        with tempfile.TemporaryDirectory(prefix="smoke-moderation-", dir=ROOT / "build") as directory:
            with args.log.open("w", encoding="utf-8") as log:
                smoke(args, log, Path(directory))
    except (AssertionError, OSError, RuntimeError, TimeoutError, subprocess.TimeoutExpired) as error:
        print(f"FAIL {error}\nLog: {args.log}", flush=True)
        return 1
    print(f"Log: {args.log}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
