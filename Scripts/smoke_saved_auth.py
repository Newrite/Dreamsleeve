#!/usr/bin/env python3
"""Real native Credential Manager login, restart, revoke and password recovery.
Uses unique temporary loopback endpoints and removes its credential on every exit.
"""
import ctypes
import json
import tomli_w
import os
import re
import tempfile
import threading
import urllib.request
from pathlib import Path
from smoke_chat import Child, ROOT, free_tcp_port, free_udp_port, check


class AuthChild(Child):
    def record(self, text):
        text = re.sub(r"(One-time reset code \(deliver privately\): ).*", r"\1<redacted>", text)
        text = re.sub(r"(>> reset-password ).*", r"\1<redacted>", text)
        super().record(text)


def run(directory, log):
    port, auth_port = free_udp_port(), free_tcp_port()
    origin = f"http://127.0.0.1:{auth_port}"
    credential_target = f"Dreamsleeve/Auth/v1/http/127.0.0.1/{auth_port}"
    client = ROOT / "build/windows/x64/releasedbg/Dreamsleeve.Client.Dev.exe"
    server_dll = ROOT / "src/Dreamsleeve.Server/bin/Release/net10.0/Dreamsleeve.Server.dll"
    server_config = directory / "server.toml"
    client_config = directory / "client.toml"
    server_config.write_text(tomli_w.dumps({
        "Server": {"Port": port}, "Database": {"DatabasePath": str(directory / "accounts.db")},
        "Authentication": {"ListenUrl": origin, "RequestsPerMinute": 1000},
        "Logging": {"FilePath": str(directory / "server-.json")}}), encoding="utf-8")
    client_config.write_text(tomli_w.dumps({"serverPort": port, "authUrl": origin}), encoding="utf-8")
    lock, children = threading.Lock(), []
    env = os.environ.copy()
    env.pop("DREAMSLEEVE_PASSWORD", None)

    def start(name, args, environment=env):
        child = AuthChild(name, args, log, lock, environment)
        children.append(child)
        return child

    def server():
        child = start("server", ["dotnet", str(server_dll), "--config", str(server_config)])
        child.wait_for(lambda lines: any("Listening on" in line for line in lines), 20)
        return child

    def client_start(name, password=None, register=False):
        args = [str(client), "--config", str(client_config)]
        environment = env.copy()
        if password:
            args += ["saved_player", "--remember"]
            environment["DREAMSLEEVE_PASSWORD"] = password
        if register: args += ["--register", "Saved Player"]
        return start(name, args, environment)

    def idle(child, operation, failure=0, start=0):
        child.wait_for(lambda lines: any(f"auth=Idle operation={operation} failure={failure}" in line for line in lines), 20, start, read=True)

    def stage(message): print("PASS " + message, flush=True)

    try:
        host = server()
        first = client_start("register", "Saved-password-2026!", True)
        first.phase("Ready", 20)
        first.stop()
        host.stop()
        host = server()
        restored = client_start("restored")
        restored.phase("Ready", 20)
        stage("native saved login survives both client and server restart without password or environment")

        mark = restored.mark()
        host.send("revoke-access saved_player")
        host.wait_for(lambda lines: "Account access revoked." in lines, 20)
        restored.phase("Disconnected", 20, mark)
        mark = restored.mark()
        restored.send("resume")
        idle(restored, 2, 1, mark)
        check(any("saved=0" in line for line in restored.output(mark)), "Rejected credential was retained")
        stage("administrative revocation closes the live client and prevents saved login")
        restored.stop()

        mark = host.mark()
        host.send("reset-password saved_player")
        prefix = "One-time reset code (deliver privately): "
        lines = host.wait_for(lambda lines: any(line.startswith(prefix) for line in lines), 20, mark)
        code = next(line[len(prefix):] for line in lines if line.startswith(prefix))
        reset_env = env.copy()
        reset_env["DREAMSLEEVE_PASSWORD"] = "Replacement-password-2026!"
        recovery = start("recovery", [str(client), "--config", str(client_config)], reset_env)
        idle(recovery, 2, 1)
        mark = recovery.mark()
        recovery.send("reset-password " + code)
        idle(recovery, 5, 0, mark)
        recovery.stop()
        replacement = client_start("new-password", "Replacement-password-2026!")
        replacement.phase("Ready", 20)
        stage("native reset operation accepts the administrator code and the new password opens the original account")

        mark = replacement.mark()
        replacement.send("signout")
        idle(replacement, 3, 0, mark)
        check(any("saved=0" in line for line in replacement.output(mark)), "Signout retained the local credential")
        replacement.stop()
        absent = client_start("signed-out")
        idle(absent, 2, 1)
        stage("signout revokes and removes the saved credential")
    finally:
        for child in reversed(children): child.stop()
        # Exact test-only target, never enumerates or deletes a user's other credentials.
        delete = ctypes.WinDLL("advapi32", use_last_error=True).CredDeleteW
        delete.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32]
        delete.restype = ctypes.c_int
        if not delete(credential_target, 1, 0) and ctypes.get_last_error() != 1168:
            raise OSError(ctypes.get_last_error(), "Could not clean test credential")


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="saved-auth-", dir=ROOT / "build") as temporary:
        with (ROOT / "build/smoke-saved-auth.log").open("w", encoding="utf-8") as log:
            run(Path(temporary), log)
