#!/usr/bin/env python3
"""Windows loopback benchmark: real server and independent ENet load process.

No production defaults are edited. Every run saves its complete configuration,
logs, client delivery checks and sampled CPU/private bytes for both processes.
"""
from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import hashlib
import json
import os
import platform
from pathlib import Path
import queue
import socket
import statistics
import subprocess
import threading
import time

ROOT = Path(__file__).resolve().parents[1]
SERVER = ROOT / "src/Dreamsleeve.Server/bin/Release/net10.0/Dreamsleeve.Server.dll"
CLIENT = ROOT / "tests/Dreamsleeve.Server.NetworkBenchmarks/bin/Release/net10.0/Dreamsleeve.Server.NetworkBenchmarks.dll"


class MemoryCounters(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD)] + [
        (name, ctypes.c_size_t) for name in (
            "PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage",
            "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage",
            "PagefileUsage", "PeakPagefileUsage", "PrivateUsage",
        )
    ]


class ProcessMetrics:
    def __init__(self, pid: int):
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.psapi = ctypes.WinDLL("psapi", use_last_error=True)
        self.kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.kernel.OpenProcess.restype = wintypes.HANDLE
        self.kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        self.kernel.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
        self.psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(MemoryCounters), wintypes.DWORD]
        self.handle = self.kernel.OpenProcess(0x0400 | 0x0010, False, pid)
        if not self.handle:
            raise ctypes.WinError(ctypes.get_last_error())

    def sample(self):
        memory = MemoryCounters()
        memory.cb = ctypes.sizeof(memory)
        times = [wintypes.FILETIME() for _ in range(4)]
        if not self.psapi.GetProcessMemoryInfo(self.handle, ctypes.byref(memory), memory.cb):
            raise ctypes.WinError(ctypes.get_last_error())
        if not self.kernel.GetProcessTimes(self.handle, *(ctypes.byref(t) for t in times)):
            raise ctypes.WinError(ctypes.get_last_error())
        ticks = sum((t.dwHighDateTime << 32) | t.dwLowDateTime for t in times[2:])
        return {"privateBytes": memory.PrivateUsage, "workingSetBytes": memory.WorkingSetSize,
                "cpuSeconds": ticks / 10_000_000}

    def close(self):
        if self.handle:
            self.kernel.CloseHandle(self.handle)
            self.handle = None


class Child:
    def __init__(self, command, log_path, env):
        self.lines = queue.Queue()
        self.log = log_path.open("w", encoding="utf-8")
        self.process = subprocess.Popen(command, cwd=ROOT, env=env, stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8",
            errors="replace", creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            self.metrics = ProcessMetrics(self.process.pid)
        except OSError:
            self.process.kill()
            self.process.wait(timeout=5)
            self.process.stdin.close()
            self.process.stdout.close()
            self.log.close()
            raise
        self.reader = threading.Thread(target=self.read, daemon=True)
        self.reader.start()

    def read(self):
        for line in self.process.stdout:
            self.log.write(line)
            self.log.flush()
            self.lines.put(line.strip())

    def output(self):
        while True:
            try:
                yield self.lines.get_nowait()
            except queue.Empty:
                return

    def stop(self, graceful=True):
        if self.process.poll() is None:
            if graceful:
                try:
                    self.process.stdin.write("quit\n")
                    self.process.stdin.flush()
                    self.process.wait(timeout=15)
                except (BrokenPipeError, OSError, subprocess.TimeoutExpired):
                    self.process.kill()
                    self.process.wait(timeout=5)
            else:
                self.process.kill()
                self.process.wait(timeout=5)
        self.reader.join(timeout=2)
        self.metrics.close()
        self.log.close()
        self.process.stdin.close()
        self.process.stdout.close()


def free_port(kind=socket.SOCK_DGRAM):
    with socket.socket(socket.AF_INET, kind) as endpoint:
        endpoint.bind(("127.0.0.1", 0))
        return endpoint.getsockname()[1]


def configuration(clients, port, profile, case):
    config = json.loads((ROOT / "src/Dreamsleeve.Server/server.example.json").read_text(encoding="utf-8-sig"))
    config.pop("Profiles", None)
    config["Database"] = {"DatabasePath": str((case / "accounts.sqlite").resolve()), "BusyTimeoutSeconds": 5}
    config["Authentication"] = {
        "ListenUrl": f"http://127.0.0.1:{free_port(socket.SOCK_STREAM)}",
        "AllowInsecureLoopback": True, "AllowRegistration": True, "CertificatePath": "",
        "RequestsPerMinute": 6000, "RequestTimeoutSeconds": 15,
        "Service": {"MailboxCapacity": 64, "MaxConcurrentOperations": 4, "MaxTickets": 4096,
                    "TicketLifetimeSeconds": 60, "PasswordIterations": 210000},
    }
    config["Logging"]["FilePath"] = str((case / "server-.json").resolve())
    server, runtime = config["Server"], config["Runtime"]
    server.update(Port=port, PeerLimit=max(32, clients))
    runtime.update(MaxSessions=max(32, clients), ControlReserve=max(128, 3 * clients + 4))
    if profile in ("scaled", "movement"):
        server.update(PeerLimit=1000, ServiceTimeoutMs=0, EventBudget=512,
                      MaxOutgoingPackets=65536, MaxOutgoingBytes=64 * 1024 * 1024)
        runtime.update(MaxSessions=1000, ControlReserve=3004, MailboxCapacity=8192,
                       OpenTimeoutMs=30000, ShutdownTimeoutMs=10000)
    if profile == "movement":
        server.update(MaxOutgoingPacketsPerPeer=4096, MaxOutgoingBytesPerPeer=16 * 1024 * 1024,
                      MaxOutgoingPackets=262144, MaxOutgoingBytes=256 * 1024 * 1024)
        runtime.update(MailboxCapacity=65536)
        runtime["Player"].update(MailboxCapacity=max(256, 2 * clients + 128),
                                 MaxPendingOutput=max(256, 2 * clients + 128), MaxBootstrapEvents=max(128, clients))
        runtime["Presence"].update(MailboxCapacity=8192, ControlReserve=128, MaxControlDeliveries=1024)
    return config


def aggregate(samples, process):
    result = {}
    for phase in dict.fromkeys(s["phase"] for s in samples):
        points = [s for s in samples if s["phase"] == phase and process in s]
        if not points:
            continue
        elapsed = points[-1]["seconds"] - points[0]["seconds"]
        cpu = points[-1][process]["cpuSeconds"] - points[0][process]["cpuSeconds"]
        result[phase] = {
            "sampleCount": len(points), "sampleSpanSeconds": elapsed,
            "cpuCoreEquivalent": cpu / elapsed if elapsed > 0 else None,
            "privateMedianMiB": statistics.median(p[process]["privateBytes"] for p in points) / 2**20,
            "privatePeakMiB": max(p[process]["privateBytes"] for p in points) / 2**20,
            "workingSetPeakMiB": max(p[process]["workingSetBytes"] for p in points) / 2**20,
        }
    return result


def run_case(args, clients, rate, repetition, destination, scenario="chat"):
    movement = scenario != "chat"
    name = f"{scenario}-n{clients}-r{rate:g}-run{repetition}"
    case = destination / name
    case.mkdir()
    config = configuration(clients, free_port(), args.profile, case)
    config["Server"].update(ReceiveBufferBytes=args.server_buffer, SendBufferBytes=args.server_buffer)
    config["Runtime"]["Presence"]["ReplicationIntervalMs"] = args.replication_ms
    config_path = case / "server.json"
    config_path.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
    env = os.environ.copy()
    phase_path = case / "phase.txt"
    phase_path.write_text("startup", encoding="utf-8")
    env["DREAMSLEEVE_BENCH_PHASE"] = str(phase_path)
    env["DREAMSLEEVE_BENCH_CLIENT_BUFFER"] = str(args.client_buffer)
    trace = None
    trace_log = None
    # Preserve the normal JIT configuration, recording it in the run metadata.
    server = client = None
    samples, errors = [], []
    started = time.monotonic()
    phase = "startup"
    server_lines = []
    print(f"START {name} profile={args.profile}", flush=True)
    try:
        server_command = (["dotnet", "exec", "--runtimeconfig", str(SERVER.with_suffix(".runtimeconfig.json")),
                           str(CLIENT), "--server-config", str(config_path),
                           "--metrics-output", str(case / "metrics.json")] if movement else
                          ["dotnet", str(SERVER), "--config", str(config_path)])
        server = Child(server_command, case / "server.log", env)
        ready = False
        while not ready:
            lines = list(server.output())
            server_lines.extend(lines)
            ready = any("Listening on" in line for line in lines)
            if server.process.poll() is not None or time.monotonic() - started > 15:
                raise RuntimeError("Server did not become ready: " + " | ".join(server_lines[-10:]))
            time.sleep(0.05)
        for _ in range(10):
            samples.append({"seconds": time.monotonic() - started, "phase": phase, "server": server.metrics.sample()})
            time.sleep(0.1)
        client = Child(["dotnet", str(CLIENT), "--auth-url", config["Authentication"]["ListenUrl"], "--port", str(config["Server"]["Port"]),
            "--clients", str(clients), "--seconds", str(args.seconds), "--rate", str(rate),
            "--replication-ms", str(args.replication_ms), "--scenario", scenario, "--output", str(case / "client.json")], case / "client.log", env)
        while True:
            for line in client.output():
                if line.startswith("STAGE "):
                    phase = line.split(" ", 1)[1]
                    phase_path.write_text(phase, encoding="utf-8")
                    if phase == "load" and args.trace_server and trace is None:
                        trace_log = (case / "trace.log").open("w", encoding="utf-8")
                        trace = subprocess.Popen([str(args.trace_server.resolve()), "collect", "--process-id", str(server.process.pid),
                            "--profile", "dotnet-sampled-thread-time,gc-verbose", "--duration", f"00:{(args.seconds + 3) // 60:02d}:{(args.seconds + 3) % 60:02d}",
                            "--output", str(case / "server.nettrace")], stdout=trace_log, stderr=subprocess.STDOUT,
                            stdin=subprocess.PIPE, text=True, creationflags=subprocess.CREATE_NO_WINDOW)
                print(f"{name}: {line}", flush=True)
            server_lines.extend(server.output())
            if client.process.poll() is not None:
                break
            if server.process.poll() is not None:
                raise RuntimeError("Server exited during the measurement")
            if time.monotonic() - started > args.timeout:
                raise TimeoutError("Case deadline exceeded")
            try:
                client_sample = client.metrics.sample()
            except OSError:
                if client.process.poll() is not None:
                    continue
                raise
            samples.append({"seconds": time.monotonic() - started, "phase": phase,
                            "server": server.metrics.sample(), "client": client_sample})
            time.sleep(0.1)
        if client.process.returncode != 0:
            errors.append(f"Client exit code: {client.process.returncode}")
        else:
            for _ in range(20):
                if server.process.poll() is not None:
                    raise RuntimeError("Server exited after clients disconnected")
                samples.append({"seconds": time.monotonic() - started, "phase": "afterDisconnect",
                                "server": server.metrics.sample()})
                time.sleep(0.1)
    except (OSError, RuntimeError, TimeoutError) as error:
        errors.append(str(error))
    finally:
        if trace:
            try:
                trace.communicate(input="\n", timeout=30)
            except subprocess.TimeoutExpired:
                trace.kill()
                trace.wait()
                errors.append("Profiler stop timed out")
            if trace.returncode != 0:
                errors.append(f"Profiler exit code: {trace.returncode}")
        if trace_log:
            trace_log.close()
        if client:
            client.stop(graceful=False)
        if server:
            server.stop()
            server_lines.extend(server.output())
            if server.process.returncode != 0:
                errors.append(f"Server exit code: {server.process.returncode}")
    client_path = case / "client.json"
    delivery = json.loads(client_path.read_text(encoding="utf-8-sig")) if client_path.exists() else None
    success = not errors and delivery is not None and delivery.get("success") is True
    metrics_path = case / "metrics.json"
    metrics = json.loads(metrics_path.read_text(encoding="utf-8-sig")) if metrics_path.exists() else None
    client_metrics_path = case / "client.json.metrics.json"
    client_metrics = json.loads(client_metrics_path.read_text(encoding="utf-8")) if client_metrics_path.exists() else None
    result = {"scenario": scenario, "serverMeasurements": metrics, "clientMeasurements": client_metrics,
              "name": name, "clients": clients, "rate": rate, "repetition": repetition,
              "profile": args.profile, "success": success, "errors": errors,
              "config": config, "server": aggregate(samples, "server"),
              "clientProcess": aggregate(samples, "client"), "delivery": delivery,
              "serverDiagnostics": server_lines, "elapsedSeconds": time.monotonic() - started}
    (case / "samples.json").write_text(json.dumps(samples, indent=2), encoding="utf-8")
    (case / "result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(f"{'PASS' if success else 'FAIL'} {name}", flush=True)
    return result


def positive_int(text):
    value = int(text)
    if value <= 0:
        raise argparse.ArgumentTypeError("must be positive")
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--clients", nargs="+", type=positive_int, default=[100, 500, 1000])
    parser.add_argument("--scenarios", nargs="+", choices=["chat", "dense", "spaces", "sparse", "boundaries"], default=["chat"])
    parser.add_argument("--rates", nargs="+", type=float, default=[0, 10, 100])
    parser.add_argument("--seconds", type=positive_int, default=10)
    parser.add_argument("--repetitions", type=positive_int, default=1)
    parser.add_argument("--timeout", type=positive_int, default=180)
    parser.add_argument("--profile", choices=["minimal", "scaled", "movement"], default="scaled")
    parser.add_argument("--server-buffer", type=positive_int, default=262144)
    parser.add_argument("--client-buffer", type=positive_int, default=262144)
    parser.add_argument("--replication-ms", type=positive_int, default=100)
    parser.add_argument("--trace-server", type=Path, help="Path to dotnet-trace; separate profiled runs from baseline")
    parser.add_argument("--output", type=Path, default=ROOT / "build/benchmarks/enet" / datetime.now().strftime("%Y%m%d-%H%M%S"))
    args = parser.parse_args()
    if args.trace_server and not args.trace_server.is_file():
        parser.error("dotnet-trace executable not found")
    if os.name != "nt":
        parser.error("Process metrics currently require Windows")
    if any(n > 1000 for n in args.clients) or any(not 0 <= rate <= 1000 for rate in args.rates):
        parser.error("clients must be <=1000; rates must be finite and between 0 and 1000")
    if any(s != "chat" for s in args.scenarios) and any(r <= 0 for r in args.rates):
        parser.error("Movement rates must be positive per-client Hz")
    if args.seconds > 300 or any(rate * args.seconds > 100000 for rate in args.rates):
        parser.error("duration must be <=300 seconds; at most 100000 messages per case")
    if not SERVER.exists() or not CLIENT.exists():
        parser.error("Build Dreamsleeve.Server and Dreamsleeve.Server.NetworkBenchmarks in Release first")
    destination = args.output.resolve()
    destination.mkdir(parents=True, exist_ok=False)
    source = [Path(__file__), *sorted((ROOT / "tests/Dreamsleeve.Server.NetworkBenchmarks").glob("*.fs")),
              *sorted((ROOT / "src/Dreamsleeve.Server.Core").glob("*.fs")),
              ROOT / "src/Dreamsleeve.Server.Infrastructure/EnetTransport.fs",
              *sorted((ROOT / "src/Dreamsleeve.Server.Infrastructure.Interop").glob("*.cs"))]
    metadata = {
        "measuredAtUtc": datetime.now(timezone.utc).isoformat(),
        "head": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
        "worktree": subprocess.check_output(["git", "status", "--short"], cwd=ROOT, text=True).splitlines(),
        "dotnet": subprocess.check_output(["dotnet", "--version"], text=True).strip(),
        "serverRuntimeConfig": json.loads(SERVER.with_suffix(".runtimeconfig.json").read_text(encoding="utf-8")),
        "logicalProcessors": os.cpu_count(), "os": platform.platform(),
        "tieredCompilationOverride": os.environ.get("DOTNET_TieredCompilation"),
        "sourceSha256": {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in source},
        "binarySha256": {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
                         for p in [SERVER, CLIENT, *sorted(CLIENT.parent.glob("Dreamsleeve.*.dll"))]},
        "diagnostics": {"serverBuffer": args.server_buffer, "clientBuffer": args.client_buffer,
                        "replicationMs": args.replication_ms, "traceServer": str(args.trace_server) if args.trace_server else None},
        "sampleIntervalMs": 100, "durationSeconds": args.seconds, "runs": [],
    }
    for repetition in range(1, args.repetitions + 1):
        for clients in args.clients:
            for rate in args.rates:
                for scenario in args.scenarios:
                    metadata["runs"].append(run_case(args, clients, rate, repetition, destination, scenario))
                    (destination / "results.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    print(f"Results: {destination / 'results.json'}", flush=True)
    return 0 if all(r["success"] for r in metadata["runs"]) else 1


if __name__ == "__main__":
    raise SystemExit(main())
