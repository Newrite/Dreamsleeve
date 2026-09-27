#!/usr/bin/env python3
"""Controlled same-machine, multiprocess ENet movement benchmark.

Total clients, sockets, per-socket buffers and server configuration stay fixed
when worker count changes. Worker barriers are outside the measured loop.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

from benchmark_enet import Child, ROOT, SERVER, CLIENT, aggregate, configuration, free_port


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig")) if path.exists() else None


def run(args, workers, repetition, destination):
    name = f"{args.scenario}-n{args.clients}-h{args.hosts}-p{workers}-run{repetition}"
    case = destination / name
    case.mkdir()
    group = case / "group"
    group.mkdir()
    config = configuration(args.clients, free_port(), "movement", case)
    config["Server"].update(ReceiveBufferBytes=args.server_buffer, SendBufferBytes=args.server_buffer, MovementPacketTargetBytes=0)
    config["Server"]["Worker"] = dict(
        QueueCapacity=65536, QueueBytes=16 * 1024 * 1024,
        SendCommandsPerPass=args.worker_send_budget, SendBytesPerPass=4 * 1024 * 1024,
        WorkBudgetMs=2, IdleWaitMs=1)
    config["Runtime"]["Presence"]["ReplicationIntervalMs"] = args.replication_ms
    config_path = case / "server.json"
    config_path.write_text(json.dumps(config, indent=2), encoding="utf-8")
    phase_path = case / "phase.txt"
    phase_path.write_text("startup", encoding="utf-8")
    env = os.environ.copy()
    env["DREAMSLEEVE_BENCH_PHASE"] = str(phase_path)
    env["DREAMSLEEVE_BENCH_CLIENT_BUFFER"] = str(args.client_buffer)
    env["DREAMSLEEVE_BENCH_GROUP"] = str(group)
    env["DREAMSLEEVE_BENCH_WORKERS"] = str(workers)
    env["DREAMSLEEVE_BENCH_TOTAL_HOSTS"] = str(args.hosts)
    if args.peer_trace:
        env["DREAMSLEEVE_ENET_TRACE_DIRECTORY"] = str(case / "peer-trace")
    trace_name = f"DreamsleeveUdp-{os.getpid()}"
    trace_started = False
    trace_attempted = False
    trace_log = []
    children, samples, errors, server_lines = [], [], [], []
    stages = ["startup"] * workers
    server = None
    started = time.monotonic()
    print("START", name, flush=True)
    try:
        server = Child(["dotnet", "exec", "--runtimeconfig", str(SERVER.with_suffix(".runtimeconfig.json")),
                        str(args.server_benchmark or CLIENT), "--server-config", str(config_path),
                        "--metrics-output", str(case / "metrics.json")], case / "server.log",
                       dict(env, DOTNET_STARTUP_HOOKS=str(args.enet_probe.resolve()),
                            DREAMSLEEVE_ENET_PROBE_OUTPUT=str(case / "enet-probe.json")) if args.enet_probe else env)
        ready = False
        while not ready:
            lines = list(server.output())
            server_lines.extend(lines)
            ready = any("Listening on" in line for line in lines)
            if server.process.poll() is not None or time.monotonic() - started > 20:
                raise RuntimeError("Server startup failed")
            time.sleep(.05)
        for worker in range(workers):
            worker_env = dict(env, DREAMSLEEVE_BENCH_WORKER=str(worker))
            children.append(Child(["dotnet", str(CLIENT), "--auth-url", config["Authentication"]["ListenUrl"],
                "--port", str(config["Server"]["Port"]), "--clients", str(args.clients // workers),
                "--hosts", str(args.hosts // workers), "--seconds", str(args.seconds), "--rate", str(args.rate),
                "--replication-ms", str(args.replication_ms), "--scenario", args.scenario,
                "--output", str(case / f"client-{worker}.json")], case / f"client-{worker}.log", worker_env))
        (case / "pids.json").write_text(json.dumps({"server": server.process.pid,
            "clients": [c.process.pid for c in children]}), encoding="utf-8")
        phase = "startup"
        failure_seen_at = None
        while True:
            for worker, child in enumerate(children):
                for line in child.output():
                    if line.startswith("STAGE "):
                        stages[worker] = line.split(" ", 1)[1]
                    print(f"{name} worker={worker}: {line}", flush=True)
            # Common measurement window; boundary uncertainty <= sampling interval.
            if all(stage == "load" for stage in stages):
                next_phase = "load"
            elif any(stage in ("drain", "disconnect", "done") for stage in stages):
                next_phase = "drain"
            else:
                next_phase = "setup"
            if next_phase != phase:
                phase = next_phase
                phase_path.write_text(phase, encoding="utf-8")
            if args.udp_trace and phase == "load" and not trace_attempted:
                trace_attempted = True
                providers = case / "udp-providers.txt"
                providers.write_text('"{E53C6823-7BB8-44BB-90DC-3F86090D48A6}" 0x10000000000 5\n'
                    '"{2F07E2EE-15DB-40F1-90EF-9D7BA282188A}" 0x10000000200 5\n', encoding="ascii")
                capture = subprocess.run(["logman", "start", trace_name, "-ets", "-o", str(case / "udp.etl"),
                    "-pf", str(providers), "-bs", "128", "-nb", "16", "64", "-f", "bincirc", "-max", "256"],
                    capture_output=True, text=True, errors="replace", creationflags=subprocess.CREATE_NO_WINDOW)
                trace_log.append(capture.stdout + capture.stderr)
                trace_started = capture.returncode == 0
                if not trace_started:
                    errors.append("ETW start failed; see udp-trace.log")
                pids = ",".join(str(c.process.pid) for c in [server, *children])
                endpoints = subprocess.run(["powershell", "-NoProfile", "-Command",
                    f"Get-NetUDPEndpoint | Where-Object {{ $_.OwningProcess -in @({pids}) }} | Select-Object LocalAddress,LocalPort,OwningProcess | ConvertTo-Json"],
                    capture_output=True, text=True, errors="replace", creationflags=subprocess.CREATE_NO_WINDOW)
                (case / "udp-endpoints.json").write_text(endpoints.stdout, encoding="utf-8")
                trace_log.append(endpoints.stderr)
            server_lines.extend(server.output())
            if server.process.poll() is not None:
                raise RuntimeError("Server exited during case")
            if any(child.process.poll() not in (None, 0) for child in children):
                if failure_seen_at is None:
                    failure_seen_at = time.monotonic()
                    errors.append("A load worker failed")
                # Peer departure normally wakes other workers. Allow their bounded
                # cleanup/report to finish before stopping a stuck barrier waiter.
                if time.monotonic() - failure_seen_at > 15:
                    raise RuntimeError("Other load workers did not finish after failure")
            if all(child.process.poll() is not None for child in children):
                break
            if time.monotonic() - started > args.timeout:
                raise TimeoutError("Case deadline exceeded")
            sample = {"seconds": time.monotonic() - started, "phase": phase, "server": server.metrics.sample()}
            for worker, child in enumerate(children):
                try:
                    sample[f"client{worker}"] = child.metrics.sample()
                except OSError:
                    if child.process.poll() is None:
                        raise
            if all(f"client{i}" in sample for i in range(workers)):
                sample["clients"] = {key: sum(sample[f"client{i}"][key] for i in range(workers))
                                     for key in ("cpuSeconds", "privateBytes", "workingSetBytes")}
            samples.append(sample)
            time.sleep(.1)
    except (OSError, RuntimeError, TimeoutError) as error:
        errors.append(str(error))
    finally:
        if trace_started:
            stopped = subprocess.run(["logman", "stop", trace_name, "-ets"], capture_output=True,
                text=True, errors="replace", creationflags=subprocess.CREATE_NO_WINDOW)
            trace_log.append(stopped.stdout + stopped.stderr)
            if stopped.returncode != 0:
                errors.append("ETW stop failed; see udp-trace.log")
        if args.udp_trace:
            (case / "udp-trace.log").write_text("\n".join(trace_log), encoding="utf-8")
        for child in children:
            child.stop(graceful=False)
        if server:
            server.stop()
            server_lines.extend(server.output())
            if server.process.returncode != 0:
                errors.append(f"Server exit code: {server.process.returncode}")
    deliveries = [read_json(case / f"client-{i}.json") for i in range(workers)]
    result = dict(name=name, clients=args.clients, hosts=args.hosts, workers=workers, rate=args.rate,
        replicationMs=args.replication_ms, seconds=args.seconds, scenario=args.scenario, config=config,
        success=not errors and all(d and d.get("success") for d in deliveries), errors=errors,
        delivery=deliveries, server=aggregate(samples, "server"), clientsProcess=aggregate(samples, "clients"),
        workerProcesses=[aggregate(samples, f"client{i}") for i in range(workers)],
        serverMeasurements=read_json(case / "metrics.json"), serverDiagnostics=server_lines,
        elapsedSeconds=time.monotonic() - started)
    (case / "samples.json").write_text(json.dumps(samples, indent=2), encoding="utf-8")
    (case / "result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print("PASS" if result["success"] else "FAIL", name, flush=True)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--clients", type=int, default=1000)
    parser.add_argument("--hosts", type=int, default=200)
    parser.add_argument("--workers", type=int, nargs="+", default=[1, 4, 10])
    parser.add_argument("--rate", type=float, default=20)
    parser.add_argument("--replication-ms", type=int, default=50)
    parser.add_argument("--seconds", type=int, default=30)
    parser.add_argument("--repetitions", type=int, default=1)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--udp-trace", action="store_true", help="Separate diagnostic run: ETW drops and OS endpoints")
    parser.add_argument("--peer-trace", action="store_true", help="Also record bounded per-peer ENet command snapshots; adds synchronous I/O")
    parser.add_argument("--server-benchmark", type=Path, help="Isolated server diagnostic benchmark DLL")
    parser.add_argument("--enet-probe", type=Path, help="Startup hook for instrumented server")
    parser.add_argument("--worker-send-budget", type=int, default=2048, help="Production Server.Worker.SendCommandsPerPass (inline baseline ignores it)")
    parser.add_argument("--server-buffer", type=int, default=262144)
    parser.add_argument("--client-buffer", type=int, default=262144)
    parser.add_argument("--scenario", choices=["sparse", "spaces", "dense", "boundaries"], default="sparse")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.enet_probe and not args.server_benchmark:
        parser.error("--enet-probe requires an isolated --server-benchmark")
    if not 1 <= args.clients <= 1000 or not 1 <= args.hosts <= args.clients:
        parser.error("Require 1 <= hosts <= clients <= 1000")
    if any(p < 1 or args.clients % p or args.hosts % p for p in args.workers):
        parser.error("Client and host counts must be divisible by each worker count")
    if not 0 < args.rate <= 1000 or not 1 <= args.seconds <= 300 or args.replication_ms < 1 or args.client_buffer < 1 or args.server_buffer < 1 or args.repetitions < 1 or args.worker_send_budget < 1:
        parser.error("Invalid rate, duration, replication interval, buffer or repetition count")
    destination = args.output.resolve()
    destination.mkdir(parents=True, exist_ok=False)
    sources = [Path(__file__), ROOT / "Scripts/benchmark_enet.py",
               ROOT / "Scripts/build_enet_worker_experiment.py",
               ROOT / "src/Dreamsleeve.Server.Infrastructure/TransportOwner.fs",
               *sorted((ROOT / "tests/Dreamsleeve.Server.NetworkBenchmarks").glob("*.fs"))]
    result = dict(measuredAtUtc=datetime.now(timezone.utc).isoformat(), args={k: str(v) if isinstance(v, Path) else v for k,v in vars(args).items()},
        head=subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
        worktree=subprocess.check_output(["git", "status", "--short"], cwd=ROOT, text=True).splitlines(),
        diagnosticBinarySha256={str(p): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in ([args.server_benchmark.resolve(), args.server_benchmark.resolve().parent / "xENet.dll",
                       args.server_benchmark.resolve().parent / "Dreamsleeve.Server.Infrastructure.dll"] if args.server_benchmark else [])
            + ([args.enet_probe.resolve()] if args.enet_probe else [])},
        logicalProcessors=os.cpu_count(), clock="same-machine Stopwatch monotonic; not a distributed-host runner",
        sourceSha256={str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sources},
        binarySha256={str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(CLIENT.parent.glob("Dreamsleeve.*.dll"))}, runs=[])
    for repetition in range(1, args.repetitions + 1):
        # Alternate direction on repeats to reduce systematic warmup/order bias.
        order = args.workers if repetition % 2 else list(reversed(args.workers))
        for workers in order:
            result["runs"].append(run(args, workers, repetition, destination))
            (destination / "results.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    return 0 if all(r["success"] for r in result["runs"]) else 1


if __name__ == "__main__":
    raise SystemExit(main())
