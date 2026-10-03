#!/usr/bin/env python3
"""A proxy of the server over the real server and a native Dev client: the
client's main route does not answer, so it signs in and plays through the
proxy route of its client.toml. The proxy is a small stand-in for nginx
(docs/DeploymentRu.md, «Прокси»): it forwards UDP to the game port and HTTP
to the authentication host with X-Forwarded-For, from 127.0.0.2, which the
server trusts as [Proxies]. The server takes the forwarded address for the
sign-in and for the game connection, and logs the proxy.
"""
import argparse
import http.client
import ipaddress
import json
import os
import re
import select
import socket
import sqlite3
import subprocess
import tempfile
import threading
import time
import uuid
from contextlib import closing
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import tomli_w

from smoke_chat import ROOT, Child, check, free_tcp_port, free_udp_port

PROXY = "127.0.0.2"
# The address the proxy reports for its player; what the server must use.
PLAYER = "198.51.100.77"


class UdpRelay(threading.Thread):
    """Each client address gets its own socket from the proxy address to the
    game port; replies go back to that client."""

    def __init__(self, listen_port: int, server_port: int):
        super().__init__(daemon=True)
        self.front = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.front.bind(("127.0.0.1", listen_port))
        self.server = ("127.0.0.1", server_port)
        self.upstream: dict[tuple, socket.socket] = {}
        self.clients: dict[socket.socket, tuple] = {}
        self.stopped = threading.Event()

    def run(self):
        while not self.stopped.is_set():
            readable, _, _ = select.select([self.front, *self.clients], [], [], 0.1)
            for sock in readable:
                try:
                    data, origin = sock.recvfrom(65536)
                except OSError:
                    continue
                if sock is self.front:
                    link = self.upstream.get(origin)
                    if link is None:
                        link = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                        link.bind((PROXY, 0))
                        self.upstream[origin] = link
                        self.clients[link] = origin
                    link.sendto(data, self.server)
                else:
                    self.front.sendto(data, self.clients[sock])

    def stop(self):
        self.stopped.set()
        self.join(timeout=2)
        for sock in [self.front, *self.clients]:
            sock.close()


def http_relay(listen_port: int, auth_port: int) -> ThreadingHTTPServer:
    class Forward(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def forward(self):
            length = int(self.headers.get("Content-Length") or 0)
            body = self.rfile.read(length) if length else None
            upstream = http.client.HTTPConnection("127.0.0.1", auth_port, timeout=10, source_address=(PROXY, 0))
            headers = {key: value for key, value in self.headers.items() if key.lower() not in ("host", "x-forwarded-for")}
            headers["X-Forwarded-For"] = PLAYER
            headers["Host"] = self.headers.get("Host", "")
            upstream.request(self.command, self.path, body=body, headers=headers)
            answer = upstream.getresponse()
            payload = answer.read()
            self.send_response(answer.status)
            for key, value in answer.getheaders():
                if key.lower() not in ("transfer-encoding", "connection", "content-length"):
                    self.send_header(key, value)
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
            upstream.close()

        do_GET = forward
        do_POST = forward

    server = ThreadingHTTPServer(("127.0.0.1", listen_port), Forward)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


def smoke(args, log, directory: Path):
    children: list[Child] = []
    lock = threading.Lock()
    port, auth_port = free_udp_port(), free_tcp_port()
    auth_url = f"http://127.0.0.1:{auth_port}"
    database = directory / "accounts.sqlite"
    config = directory / "server.toml"
    config.write_text(tomli_w.dumps({
        "Database": {"DatabasePath": str(database), "BusyTimeoutSeconds": 5},
        "Authentication": {"Listener": {"ListenUrl": auth_url, "AllowInsecureLoopback": True}},
        "Admin": {"Listener": {"ListenUrl": f"http://127.0.0.1:{free_tcp_port()}"}},
        "Logging": {"MinimumLevel": "Debug", "FilePath": str(directory / "server-.json")},
        "Proxies": {"Trusted": [PROXY]},
    }), encoding="utf-8")
    password = "smoke-" + uuid.uuid4().hex
    env = dict(os.environ, DREAMSLEEVE_PASSWORD=password)

    # The client's main route goes nowhere; its proxy route goes through the relay.
    relay_udp, relay_http = free_udp_port(), free_tcp_port()
    client_config = directory / "client.toml"
    client_config.write_text(tomli_w.dumps({
        "version": 1,
        "serverHost": "127.0.0.1", "serverPort": free_udp_port(), "authUrl": f"http://127.0.0.1:{free_tcp_port()}",
        "client": {"connectTimeoutMs": 1000},
        "routes": [{"name": "Прокси", "serverHost": "127.0.0.1", "serverPort": relay_udp,
                    "authUrl": f"http://127.0.0.1:{relay_http}"}],
    }), encoding="utf-8")

    def stage(message: str):
        print("PASS " + message, flush=True)
        with lock:
            log.write("CHECK " + message + "\n")
            log.flush()

    def start(name: str, command: list[str], environment=None) -> Child:
        child = Child(name, command, log, lock, environment)
        children.append(child)
        return child

    udp = UdpRelay(relay_udp, port)
    web = None
    try:
        server = start("server", ["dotnet", str(args.server), "--port", str(port), "--config", str(config)])
        server.wait_for(lambda lines: any(f"Listening on 127.0.0.1:{port}" in line for line in lines), args.timeout)
        udp.start()
        web = http_relay(relay_http, auth_port)

        # The account exists already; registering is not repeated by another route.
        with closing(http.client.HTTPConnection("127.0.0.1", auth_port, timeout=10)) as direct:
            direct.request("POST", "/auth/register", body=json.dumps({"username": "smoke_proxy", "password": password,
                                                                       "displayName": "Smoke Proxy"}),
                           headers={"Content-Type": "application/json"})
            check(direct.getresponse().status == 201, "The account was not registered")

        player = start("player", [str(args.client), "--config", str(client_config), "smoke_proxy"], env)
        player.phase("Ready", args.timeout)
        player.send("read")
        player.wait_for(lambda lines: "route=Прокси reached" in lines, args.timeout, read=True)
        stage("the main route does not answer: the client signs in and opens its session through the proxy route")

        server.wait_for(lambda lines: any(re.search(rf"smoke_proxy signed in \(password\) through proxy {re.escape(PROXY)}", line)
                                          for line in lines), args.timeout)
        server.wait_for(lambda lines: any(re.search(rf"comes through proxy {re.escape(PROXY)} from {re.escape(PLAYER)}", line)
                                          for line in lines), args.timeout)
        with closing(sqlite3.connect(database)) as db:
            addresses = [bytes(row[0]) for row in db.execute("SELECT address FROM sign_in_addresses")]
        # The registration came directly, the sign-in through the proxy.
        expected = sorted(ipaddress.IPv6Address(f"::ffff:{address}").packed for address in ("127.0.0.1", PLAYER))
        check(sorted(addresses) == expected, f"Sign-in addresses: {addresses}")
        stage("the server takes the forwarded address for the sign-in and the game connection and names the proxy")

        start_player = player.mark()
        player.send("send through-proxy")
        player.wait_for(lambda lines: any("published message" in line for line in lines), args.timeout, start_player, read=True)
        stage("chat works through the proxy")

        player.send("quit")
        player.process.wait(timeout=args.timeout)
        check(player.process.returncode == 0, "The client exited unsuccessfully")
    finally:
        for child in reversed(children):
            child.stop()
        udp.stop()
        if web:
            web.shutdown()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, default=ROOT / "src/Dreamsleeve.Server/bin/Release/net10.0/Dreamsleeve.Server.dll")
    parser.add_argument("--client", type=Path, default=ROOT / "build/windows/x64/releasedbg/Dreamsleeve.Client.Dev.exe")
    parser.add_argument("--timeout", type=float, default=20.0)
    parser.add_argument("--log", type=Path, default=ROOT / "build/smoke-proxy.log")
    args = parser.parse_args()
    for artifact in (args.server, args.client):
        if not artifact.is_file():
            parser.error(f"Build the server and native Dev first; missing {artifact}")
    args.log.parent.mkdir(parents=True, exist_ok=True)
    try:
        with tempfile.TemporaryDirectory(prefix="smoke-proxy-", dir=ROOT / "build") as directory:
            with args.log.open("w", encoding="utf-8") as log:
                smoke(args, log, Path(directory))
    except (AssertionError, OSError, RuntimeError, TimeoutError, subprocess.TimeoutExpired) as error:
        print(f"FAIL {error}\nLog: {args.log}", flush=True)
        return 1
    print(f"Log: {args.log}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
