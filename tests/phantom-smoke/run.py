"""Cross-language phantom smoke: the controlled-auth production server and native Streaming over real UDP.

The parent builds the native tests; this runner builds only the server fixture (unless --no-build),
starts it on an ephemeral loopback port, runs one opt-in native test case against it, requires the
success sentinel and stops the server gracefully. Python standard library only.
"""
import argparse
import datetime
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

REPO = Path(__file__).resolve().parents[2]
FIXTURE_PROJECT = REPO / "tests/Dreamsleeve.Phantom.Smoke.Server/Dreamsleeve.Phantom.Smoke.Server.fsproj"
FIXTURE_DLL = REPO / "tests/Dreamsleeve.Phantom.Smoke.Server/bin/Debug/net10.0/Dreamsleeve.Phantom.Smoke.Server.dll"
HIDDEN = getattr(subprocess, "CREATE_NO_WINDOW", 0)

# How long the server may take to become ready, and to drain after "stop".
READY_SECONDS = 15
SHUTDOWN_SECONDS = 25
PROXY_READY_SECONDS = 10

SCENARIOS = {
    # The two-party contract smoke.
    "Pair": ("Phantom production Streaming real UDP smoke", r"PHANTOM_NATIVE_(ASSET_)?UDP_PASS"),
    # N players publish and receive at once.
    "Mutual": ("Phantom production Streaming concurrent mutual smoke", r"PHANTOM_MUTUAL_UDP_PASS"),
}


class SmokeFailure(Exception):
    pass


def bounded(low, high):
    def parse(text):
        value = int(text)
        if not low <= value <= high:
            raise argparse.ArgumentTypeError(f"must be {low}..{high}")
        return value
    return parse


def arguments():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--native-tests", type=Path, required=True, help="Dreamsleeve.Client.Tests.exe built by the parent")
    parser.add_argument("--no-build", action="store_true", help="Skip the server fixture build")
    parser.add_argument("--nginx", type=Path, help="Route HTTP through the documented nginx servers")
    parser.add_argument("--artifacts", type=Path)
    parser.add_argument("--timeout-seconds", type=bounded(30, 300), default=120)
    parser.add_argument("--scenario", choices=SCENARIOS, default="Pair")
    parser.add_argument("--players", type=bounded(2, 16), default=6)
    parser.add_argument("--heavy-vertices", type=bounded(0, 4096), default=0)
    parser.add_argument("--model-bytes-per-second", type=int, default=0)
    return parser.parse_args()


def free_port(kind):
    with socket.socket(socket.AF_INET, kind) as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def start(command, log_name, artifacts, env=None):
    log = (artifacts / log_name).open("w", encoding="utf-8", errors="replace")
    process = subprocess.Popen(
        [str(part) for part in command], cwd=REPO, env=env, stdin=subprocess.PIPE, stdout=log,
        stderr=subprocess.STDOUT, text=True, creationflags=HIDDEN)
    return process, log


def finish(process, log, stop):
    """Stops a child that is still running and closes its log."""
    if process.poll() is None:
        stop()
        try:
            process.wait(SHUTDOWN_SECONDS)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
    log.close()


def request_stop(server):
    try:
        server.stdin.write("stop\n")
        server.stdin.close()
    except OSError:
        pass


def await_ready(server, path):
    deadline = time.monotonic() + READY_SECONDS
    while not path.exists():
        if server.poll() is not None:
            raise SmokeFailure("Controlled-auth server exited before readiness.")
        if time.monotonic() >= deadline:
            raise SmokeFailure("Controlled-auth server readiness timed out.")
        time.sleep(0.025)
    return json.loads(path.read_text(encoding="utf-8"))


def nginx_configuration(server_port, inner_port, outer_port):
    """Both documented server blocks, with only TLS, listeners and upstreams adapted to loopback."""
    documentation = (REPO / "docs/DeploymentRu.md").read_text(encoding="utf-8")
    blocks = re.findall(r"```nginx\s*(.*?)```", documentation, re.S)
    servers = []
    for host, port in (("auth.example.org", inner_port), ("proxy.example.org", outer_port)):
        matching = [block for block in blocks if f"server_name {host};" in block]
        if len(matching) != 1:
            raise SmokeFailure(f"Expected one documented server for {host}")
        config = matching[0].replace("listen 443 ssl;", f"listen 127.0.0.1:{port};")
        config = re.sub(r"(?m)^\s*(listen \[::\]|ssl_|proxy_ssl_).*\r?\n", "", config)
        config = config.replace("http://127.0.0.1:8779", f"http://127.0.0.1:{server_port}")
        config = config.replace("https://auth.example.org", f"http://127.0.0.1:{inner_port}")
        servers.append(config)
    return "worker_processes 1;\nevents { worker_connections 128; }\nhttp {\n" + "\n".join(servers) + "\n}"


def await_proxy(proxy, outer_port):
    """The content route must reach production capability validation through both proxies."""
    deadline = time.monotonic() + PROXY_READY_SECONDS
    while True:
        try:
            urllib.request.urlopen(f"http://127.0.0.1:{outer_port}/phantoms/content", timeout=2)
            raise SmokeFailure("Content route answered without a capability.")
        except urllib.error.HTTPError as error:
            if error.code != 403:
                raise SmokeFailure(f"Content route returned {error.code}, not production capability validation (403).")
            return
        except (urllib.error.URLError, ConnectionError, TimeoutError):
            if proxy.poll() is not None or time.monotonic() >= deadline:
                raise SmokeFailure("nginx did not start serving the content route.")
            time.sleep(0.05)


def run(args):
    native = args.native_tests.resolve(strict=True)
    stamp = datetime.datetime.now(datetime.UTC).strftime("%Y%m%d-%H%M%S")
    artifacts = (args.artifacts or REPO / f"build/phantom-native-smoke/run-{stamp}-{uuid.uuid4().hex[:8]}").resolve()
    artifacts.mkdir(parents=True, exist_ok=True)

    if not args.no_build:
        with (artifacts / "server-build.log").open("w", encoding="utf-8") as log:
            if subprocess.run(["dotnet", "build", str(FIXTURE_PROJECT), "-v", "minimal"], cwd=REPO, stdout=log, stderr=subprocess.STDOUT).returncode:
                raise SmokeFailure("Controlled-auth server fixture build failed; see server-build.log.")
    if not FIXTURE_DLL.exists():
        raise SmokeFailure("Server fixture is missing; build it before using --no-build.")

    port = free_port(socket.SOCK_DGRAM)
    ready_path = artifacts / "ready.json"
    server_env = dict(os.environ)
    if args.model_bytes_per_second > 0:
        server_env["DREAMSLEEVE_PHANTOM_SMOKE_MODEL_BPS"] = str(args.model_bytes_per_second)

    children = []
    try:
        server, server_log = start(
            ["dotnet", FIXTURE_DLL, "--port", port, "--state-dir", artifacts, "--ready-file", ready_path],
            "server.log", artifacts, server_env)
        children.append((server, server_log, lambda: request_stop(server)))
        ready = await_ready(server, ready_path)
        if ready.get("port") != port or "protocolVersion" not in ready:
            raise SmokeFailure("Unexpected server fixture contract.")

        http = None
        if args.nginx:
            nginx = args.nginx.resolve(strict=True)
            inner_port, outer_port = free_port(socket.SOCK_STREAM), free_port(socket.SOCK_STREAM)
            proxy_directory = artifacts / "nginx"
            (proxy_directory / "logs").mkdir(parents=True, exist_ok=True)
            (proxy_directory / "temp").mkdir(exist_ok=True)
            (proxy_directory / "nginx.conf").write_text(nginx_configuration(port, inner_port, outer_port), encoding="utf-8")
            prefix = proxy_directory.as_posix() + "/"
            if subprocess.run([str(nginx), "-p", prefix, "-c", "nginx.conf", "-t"], cwd=REPO).returncode:
                raise SmokeFailure("Documented nginx configuration failed syntax validation.")
            proxy, proxy_log = start([nginx, "-p", prefix, "-c", "nginx.conf", "-g", "daemon off;"], "nginx.log", artifacts)
            children.append((proxy, proxy_log, lambda: subprocess.run([str(nginx), "-p", prefix, "-c", "nginx.conf", "-s", "quit"], cwd=REPO)))
            await_proxy(proxy, outer_port)
            http = f"http://127.0.0.1:{outer_port}"

        test, sentinel = SCENARIOS[args.scenario]
        native_env = dict(os.environ,
                          DREAMSLEEVE_PHANTOM_SMOKE_PLAYERS=str(args.players),
                          DREAMSLEEVE_PHANTOM_SMOKE_HEAVY_VERTICES=str(args.heavy_vertices),
                          DREAMSLEEVE_PHANTOM_SMOKE_PORT=str(port),
                          DREAMSLEEVE_PHANTOM_SMOKE_STATE=str(artifacts))
        if http:
            native_env["DREAMSLEEVE_PHANTOM_SMOKE_HTTP"] = http
        with (artifacts / "native.log").open("w", encoding="utf-8", errors="replace") as log:
            try:
                result = subprocess.run([str(native), f"--test-case={test}", "--no-colors=true"], cwd=REPO, env=native_env,
                                        stdout=log, stderr=subprocess.STDOUT, timeout=args.timeout_seconds, creationflags=HIDDEN)
            except subprocess.TimeoutExpired:
                raise SmokeFailure("Native Streaming smoke exceeded its time limit.")
        output = (artifacts / "native.log").read_text(encoding="utf-8", errors="replace")
        print(output)
        if result.returncode:
            raise SmokeFailure(f"Native smoke failed with exit code {result.returncode}. See {artifacts}")
        if not re.search(sentinel, output):
            raise SmokeFailure("Selected binary did not execute the opt-in native smoke (missing success sentinel).")
        if args.nginx and args.scenario == "Pair" and not ("PHANTOM_DELTA_PASS" in output and "PHANTOM_CONTEXT_DELTA_PASS" in output):
            raise SmokeFailure("Proxy smoke must execute both native delta and context-transition coverage.")

        if server.poll() is not None:
            raise SmokeFailure("Server exited before graceful smoke shutdown.")
        request_stop(server)
        try:
            server.wait(SHUTDOWN_SECONDS)
        except subprocess.TimeoutExpired:
            raise SmokeFailure("Server did not finish graceful smoke shutdown.")
        if server.returncode:
            raise SmokeFailure(f"Server fixture failed with exit code {server.returncode}.")

        (artifacts / "result.json").write_text(json.dumps({
            "status": "passed", "scenario": args.scenario, "test": test, "nginx": str(args.nginx or ""),
            "protocolVersion": ready["protocolVersion"], "nativeBinary": str(native),
            "utc": datetime.datetime.now(datetime.UTC).isoformat(), "artifacts": str(artifacts),
        }, indent=2), encoding="utf-8")
        print(f"Cross-language smoke passed; artifacts: {artifacts}")
    finally:
        # Proxy first, then the server: the reverse of start order.
        for process, log, stop in reversed(children):
            finish(process, log, stop)


if __name__ == "__main__":
    try:
        run(arguments())
    except SmokeFailure as failure:
        print(f"Phantom smoke failed: {failure}", file=sys.stderr)
        sys.exit(1)
