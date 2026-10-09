# Protocol 20 phantom BEFORE baseline — 6 October 2026

The selected loopback matrix completed after a **13-line benchmark-client lifecycle
fix**: four movement/resource cases, two chat cases, and one identical sparse32
repeat. All seven runs passed delivery/state correctness with zero generator errors,
server request rejections or unexpected disconnects. This is the existing protocol
20 server, before phantom replication, with its inherited optimizations unchanged.

The first sparse32 run reached only 15.53–15.73 Hz and missed 528 generator intervals.
It is retained as a real load result, not discarded. Its exact repeat and the other
movement rows disclose their achieved rates below. Correctness success is distinct
from attaining the requested 20 Hz. The earlier rejected bootstrap attempts are
listed separately and are **not baseline load measurements**.

## Source, machine and ownership

- Source commit: `3885afa0232de40ac6673a818c2da4ce5034df51`, on
  `codex/phantom-replication`; production matches master
  `7772d170f5586aba8167bb4777a5f112862ef056`.
  `git diff --quiet 7772d17 3885afa -- src tests Scripts Directory.Build.props` returned 0.
- Full Git archive extracted under ignored `build/phantom-baseline/source`.
  Every build and runner import used this snapshot. Concurrent parent implementation
  edits, including future protocol 21 lanes, were not included.
- Windows 10 build 19045 x64; Ryzen 7 5800X3D; 16 logical processors;
  34,278,273,024 bytes physical RAM (31.924 GiB).
- .NET SDK 10.0.401; selected .NET/ASP.NET runtime 10.0.12; Python 3.12.10.
  Existing package cache and `tomli_w`; no tools/dependencies were installed.
- IPv4 loopback only, one unchanged server, fresh database and random HTTP/UDP ports
  per case. No WAN injection, game client, manual test or phantom traffic.
- Tiered JIT defaults; no invoking `DOTNET_`, `COMPlus_` or `DREAMSLEEVE_` overrides.
  Production runtimeconfig was used for all measured server hosts. All logged
  **`BENCHMARK_HOST server_gc=false`**: the actual mode is workstation GC, despite
  the older benchmark README's Server GC description. No forced GC or profiler.
- The baseline scope changed only this new report, ignored outputs and the explicitly
  authorized benchmark generator `tests/Dreamsleeve.Server.NetworkBenchmarks/Program.fs`.
  No server source/configuration or existing documentation was edited for the baseline.
  Subsequent, separately scoped UI work is absent from the frozen source and executables.
  No commit, history change or extra agent.

## Workload and measurement boundary

Four movement workers; N sockets for N clients (8/32 clients per worker at N=32/128).
Source motion 20 Hz; replication interval 50 ms; three resource values per client
at 4 Hz. Resource timestamps use the existing health-field encoding. Sparse groups
of 25 are separated by 20,000 units in one space, radius 8,192; dense is one shared
space/radius. Visible pairs include self: 674/1,024 at N=32; 3,134/16,384 at N=128.
Resource metadata is global fanout, independent of motion AOI.

Each case has a fresh server/database, bounded registration/login, exact online
convergence, all N² character projections and warmed AOI positions before the
15-second measured interval. Position warmup and the one-second movement idle are
excluded. Final motion/AOI convergence, drained resource command ACKs and graceful
cleanup follow. Chat is separate (no combined motion/chat generator mode): aggregate
10 requests/s, one generator process, N sockets, three-second idle before load,
exactly-once checks for every publication/recipient and request correlation.

`minimal` retains the example capacities: runtime mailbox 32,768; player mailbox/output
1,152; presence mailbox 4,096; transport handoff 65,536 packets/16 MiB; global outgoing
262,144 packets/256 MiB; per-peer 4,096 packets/16 MiB. Only session/peer slots become
N, with generated five-owner cleanup reserve 165/645. Event budget 256, send budget
2,048/pass, negotiated-MTU movement target 0, server socket buffers 4 MiB and each
client socket buffer 1 MiB. No raised queue experiment or optimization between rows.

Generated config also uses fresh database/log paths, disabled admin panel, loopback
HTTP and benchmark auth limits (6,000 requests/minute, concurrency 4, 210,000 password
iterations), and disables chat anti-spam. Full `server.toml` is saved for each case.
This is not a claim that every setting equals production defaults.

Both processes use the existing diagnostic recorder; the server host calls unmodified
`Program.main` under production runtimeconfig. CPU is accumulated user+kernel time
per wall second in **one-core equivalents**; server PID is separate from generators.
Process sampling is nominally 100 ms. GC/allocation deltas subtract the first/last
server **load** runtime samples, with phase-boundary uncertainty about 100 ms. Heap
and private bytes are different measures; no forced collection was performed.

Delivery age includes replication, ENet and generator receive processing, not just
server latency. Worker histograms cannot be pooled from saved quantiles: movement
percentiles below are **ranges across four worker quantiles**, not global pooled
p50/p95/p99. The fixed logarithmic histogram reports upper bounds within 1% + 0.01 ms.
Fresh Hz/pair is sum(received movements / each worker's load seconds) / visible pairs.
Resource receive counts cover load only; drained ACKs do not prove every resource
version reached every recipient, and final-state convergence is a motion/AOI oracle.

## Actual load results

All rows opened exactly N sessions. Movement rows initialized N² character pairs,
finished with zero pending commands and converged final positions/hidden pairs.
Every listed case has `success=true`, zero rejections, disconnects and client errors.
The sparse32 repeat uses a fresh server/database with identical options and binaries.

| Case | Motion sent / fresh received | Source Hz (worker range) | Fresh Hz/pair | Resource updates / load deliveries | Resource Hz | Missed intervals |
|---|---|---|---|---|---|---|
| sparse-32 | 7,497 / 149,748 | 15.53–15.73 | 14.80 | 1,791 / 57,312 | 3.720–3.736 | 528 |
| dense-32 | 9,407 / 299,188 | 19.57–19.62 | 19.48 | 1,913 / 61,056 | 3.975–3.992 | 0 |
| sparse-128 | 37,738 / 922,421 | 19.64–19.67 | 19.62 | 7,655 / 978,176 | 3.985–3.989 | 0 |
| dense-128 | 37,778 / 4,817,866 | 19.67–19.68 | 19.60 | 7,656 / 977,408 | 3.985–3.989 | 0 |
| sparse-32-repeat | 9,444 / 198,124 | 19.67–19.68 | 19.60 | 1,915 / 61,056 | 3.983–3.992 | 0 |

Motion age, resource age and receive gap are milliseconds. Each entry is a worker-quantile range.


| Case | Motion age p50 / p95 / p99 | Resource age p50 / p95 / p99 | Gap p95 | Worker cycle p95 |
|---|---|---|---|---|
| sparse-32 | 39.1–39.9 / 148.2–170.5 / 237.1–353.5 | 46.5–48.4 / 154.2–244.3 / 246.8–357.0 | 123.7–127.5 | 17.3–21.3 |
| dense-32 | 27.3–27.6 / 50.4 / 53.1–54.1 | 27.3–27.6 / 50.9–51.5 / 54.1–54.7 | 52.5–53.1 | 2.0 |
| sparse-128 | 29.1–30.6 / 50.9–52.5 / 53.6–54.7 | 29.1–30.3 / 51.5–53.1 / 54.1–55.3 | 52.0–52.5 | 2.0 |
| dense-128 | 29.1–32.9 / 51.5–55.3 / 53.6–57.5 | 29.4–32.9 / 52.0–55.8 / 54.1–58.1 | 52.5–53.1 | 2.0 |
| sparse-32-repeat | 27.9–28.5 / 50.4–50.9 / 52.5–53.1 | 28.2–28.5 / 50.9 / 53.1–53.6 | 52.0–52.5 | 2.0 |

Chat latencies use the generator's monotonic clock and nearest-rank percentiles, including delivery drain.


| Case | Sent / copies received / expected | Achieved aggregate requests/s | Author ACK p50 / p95 / p99 ms | All recipients p50 / p95 / p99 ms |
|---|---|---|---|---|
| chat-32 | 150 / 4,800 / 4,800 | 9.999 | 1.89 / 4.14 / 9.46 | 2.05 / 4.44 / 9.60 |
| chat-128 | 150 / 19,200 / 19,200 | 10.000 | 2.79 / 4.72 / 5.51 | 3.54 / 5.66 / 6.06 |

Server process and GC metrics use load samples only. Memory is private median / sampled peak; GC pause is cumulative pause-time delta in the sampled interval.


| Case | CPU cores | Private MiB median / peak | Allocation MiB/s | GC pause ms | GC count Δ 0/1/2 | ThreadPool pending max |
|---|---|---|---|---|---|---|
| sparse-32 | 0.051 | 83.4 / 83.5 | 9.96 | 18.12 | 4/1/0 | 3 |
| dense-32 | 0.018 | 79.7 / 81.0 | 13.54 | 6.27 | 4/1/0 | 4 |
| sparse-128 | 0.103 | 81.3 / 81.8 | 60.57 | 25.95 | 19/1/0 | 0 |
| dense-128 | 0.386 | 84.9 / 87.1 | 148.97 | 73.52 | 50/2/1 | 0 |
| chat-32 | 0.032 | 70.6 / 75.1 | 1.01 | 6.25 | 1/0/0 | 0 |
| chat-128 | 0.030 | 79.4 / 82.1 | 2.32 | 0.00 | 0/0/0 | 0 |
| sparse-32-repeat | 0.039 | 81.4 / 82.0 | 11.45 | 15.94 | 4/1/0 | 1 |

Server queue age p50/p95/p99 in ms, and sampled queue/transport pressure. Empty presence histograms in chat are marked n/a.


| Case | Incoming age p50 / p95 / p99 | Outgoing age p50 / p95 / p99 | Presence flush p95 ms | Runtime tick queue p95 ms | Outgoing packets max | Dropped realtime / native send failures (max observed counters) |
|---|---|---|---|---|---|---|
| sparse-32 | 0.010 / 1.449 / 6.689 | 0.010 / 0.010 / 0.010 | 0.403 | 0.105 | 8.000 | 0.000 / 0.000 |
| dense-32 | 0.008 / 0.008 / 0.008 | 0.010 / 0.010 / 0.010 | 0.361 | 0.020 | 19.000 | 0.000 / 0.000 |
| sparse-128 | 0.008 / 0.008 / 0.008 | 0.010 / 0.010 / 0.010 | 0.872 | 0.030 | 51.000 | 0.000 / 0.000 |
| dense-128 | 0.010 / 0.010 / 0.010 | 0.010 / 0.549 / 1.261 | 3.722 | 0.196 | 254.000 | 0.000 / 0.000 |
| chat-32 | 0.002 / 0.002 / 0.002 | 0.000 / 0.000 / 0.000 | 0.020 | 0.020 | 0.000 | 0.000 / 0.000 |
| chat-128 | 0.005 / 0.005 / 0.005 | 0.000 / 0.000 / 0.000 | 0.030 | 0.020 | 0.000 | 0.000 / 0.000 |
| sparse-32-repeat | 0.010 / 0.010 / 0.010 | 0.000 / 0.000 / 0.000 | 0.282 | 0.020 | 0.000 | 0.000 / 0.000 |

System-wide IPv4 UDP receive-error deltas by row: sparse-32=0; dense-32=0; sparse-128=12; dense-128=6; chat-32=0; chat-128=0; sparse-32-repeat=0. These counters cover the whole machine and cannot be attributed to this server socket.

The exploratory movement thresholds from the benchmark README are source >=18 Hz,
movement-age p95 <=200 ms, gap p95 <=100 ms, final convergence and no unexpected
disconnects. Check all three cadence/age columns, not only `success`. The first
sparse32 fails source cadence and gap bounds despite correctness; its queue ages and
low generator CPU do not prove a particular scheduling cause. The identical repeat
is disclosed alongside it, without code/config changes. This single short matrix
is a local diagnostic, not a saturation/capacity guarantee; machine scheduling and
concurrent desktop activity were not controlled, and the first run was not excused
by substituting later values. No server optimization delta is claimed.

## Exact generator patch and verification

The defect was reproduced on the unmodified client before changing source. Empty
`GuildsSnapshot` is valid mandatory protocol 19/20 bootstrap traffic. The patch
accepts only an opened client's uncorrelated empty guild/invitation snapshot, known
role notification, and mute-clear notification. Nonempty guild state, active mute,
invalid role/request correlation and every other unexpected payload still fail.
Version/channel/reliability, chat exactness and motion sequence/baseline checks stay
in place. Role/mute branches were compiled; the real matrix directly exercises guild
bootstrap, not injected role/mute error cases.

The same logical 13-line delta is present in the frozen generator and in working
`tests/Dreamsleeve.Server.NetworkBenchmarks/Program.fs`. Server/project references
were not rebuilt for the fix (`BuildProjectReferences=false`). The rebuilt generator
and movement oracle passed. Against the pre-fix manifest only the generator DLL/PDB
changed; every server/dependency binary remained byte-identical. The patched source
and all binaries were checked unchanged again after the matrix.

Patch artifact: [`generator-protocol20.patch`](../../build/phantom-baseline/generator-protocol20.patch).
Exact diff:

```diff
diff --git a/tests/Dreamsleeve.Server.NetworkBenchmarks/Program.fs b/tests/Dreamsleeve.Server.NetworkBenchmarks/Program.fs
--- a/tests/Dreamsleeve.Server.NetworkBenchmarks/Program.fs
+++ b/tests/Dreamsleeve.Server.NetworkBenchmarks/Program.fs
@@ -256,6 +256,19 @@
             | ServerPacket.PayloadOneofCase.GroundMarksChanged ->
                 if response.GroundMarksChanged.Added.Count <> 0 || response.GroundMarksChanged.RemovedIds.Count <> 0 then
                     fail state (sprintf "Client %d received ground marks" client.Index)
+            // Fresh benchmark accounts have no guilds or invitations. These
+            // uncorrelated lifecycle notifications do not settle load requests.
+            | ServerPacket.PayloadOneofCase.GuildsSnapshot ->
+                if not client.Ready || response.HasRequestId || response.GuildsSnapshot.Guilds.Count <> 0
+                   || response.GuildsSnapshot.Invites.Count <> 0 then
+                    fail state (sprintf "Client %d received unexpected guild state" client.Index)
+            | ServerPacket.PayloadOneofCase.RoleChanged ->
+                if not client.Ready || response.HasRequestId
+                   || (response.RoleChanged.Role <> PlayerRole.Player && response.RoleChanged.Role <> PlayerRole.Moderator) then
+                    fail state (sprintf "Client %d received invalid role notification" client.Index)
+            | ServerPacket.PayloadOneofCase.MuteChanged ->
+                if not client.Ready || response.HasRequestId || not (isNull response.MuteChanged.Mute) then
+                    fail state (sprintf "Client %d received unexpected mute state" client.Index)
             | unknown -> fail state (sprintf "Unknown server packet payload: %A" unknown)

 let private handle state hostIndex (event: EnetEvent) =
```
Saved chat diagnostic adapter (same bytes in every rejected/corrected chat attempt):

```python
"""Ignored adapter: unchanged protocol20 server and existing chat runner.

Correct the generated cleanup reserve for five protocol20 owners. Use the
existing benchmark server host to expose the same diagnostic recorder as the
movement cases, with the production server runtimeconfig (including Server GC).
No production source or checked-in benchmark script is modified.
"""
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / "source" / "Scripts"))
import benchmark_enet as benchmark

benchmark.CLEANUP_SOURCES = 5
OriginalChild = benchmark.Child


class MeasuredChild(OriginalChild):
    def __init__(self, command, log_path, env):
        if len(command) == 4 and command[:3] == ["dotnet", str(benchmark.SERVER), "--config"]:
            command = [
                "dotnet", "exec", "--runtimeconfig",
                str(benchmark.SERVER.with_suffix(".runtimeconfig.json")),
                str(benchmark.CLIENT), "--server-config", command[3],
                "--metrics-output", str(log_path.parent / "metrics.json"),
            ]
        super().__init__(command, log_path, env)


benchmark.Child = MeasuredChild
if __name__ == "__main__":
    raise SystemExit(benchmark.main())
```


Generated-only runtime overlays correct the stale runner's four-owner formula to
five (`[Runtime] ControlReserve = 165` / `645`). The ignored `run_chat.py` imports
unchanged snapshot runner code, sets its generated cleanup count to five and uses
the existing diagnostic host for chat, with production runtimeconfig. It does not
filter wire traffic or bypass client verification. Those Python adaptations were
already present in the rejected preflight and are not server optimizations.

## Exact commands and reproduction

PowerShell, repository `S:/Programming/Dreamsleeve`; all paths below refer to the
frozen source or ignored outputs. Output directories must be new. To reproduce from
a fresh directory, create the archive, restore the exact patch below/linked above,
apply it to the frozen generator, and create the two one-key overlays plus saved
chat adapter. `source.zip` itself remains the unpatched archive.

```powershell
New-Item -ItemType Directory -Path build/phantom-baseline
git archive --format=zip --output=build/phantom-baseline/source.zip 3885afa
Expand-Archive -LiteralPath build/phantom-baseline/source.zip -DestinationPath build/phantom-baseline/source
Set-Location build/phantom-baseline/source
dotnet build src/Dreamsleeve.Server -c Release -p:NuGetAudit=false -p:RestorePackagesPath=C:/Users/newri/.nuget/packages --source C:/Users/newri/.nuget/packages
dotnet build tests/Dreamsleeve.Server.NetworkBenchmarks -c Release -p:NuGetAudit=false -p:RestorePackagesPath=C:/Users/newri/.nuget/packages --source C:/Users/newri/.nuget/packages
# Apply the exact patch from the repository root to the frozen generator:
Set-Location S:/Programming/Dreamsleeve
git apply --directory=build/phantom-baseline/source build/phantom-baseline/generator-protocol20.patch
Set-Location build/phantom-baseline/source
dotnet build tests/Dreamsleeve.Server.NetworkBenchmarks -c Release --no-restore -p:BuildProjectReferences=false
dotnet tests/Dreamsleeve.Server.NetworkBenchmarks/bin/Release/net10.0/Dreamsleeve.Server.NetworkBenchmarks.dll --verify-movement-oracle
```

The patch was actually applied directly to the two declared files; the `git apply`
line above is a reproduction equivalent. `git apply --check --reverse
--directory=build/phantom-baseline/source build/phantom-baseline/generator-protocol20.patch`
confirmed the saved patch matches the frozen source. The initial online restore
attempt was interrupted after NU1801 network warnings; final local-cache builds
succeeded with zero warnings/errors. All build logs remain in the output root.

Executed load commands, in order (stdout/stderr retained in matching `.log` files):

```powershell
# Working directory: build/phantom-baseline/source
python -u Scripts/benchmark_enet_workers.py --clients 32 --hosts 32 --workers 4 --scenario sparse --rate 20 --seconds 15 --warm-positions --actor-values-hz 4 --replication-ms 50 --profile minimal --server-buffer 4194304 --client-buffer 1048576 --server-overlay S:/Programming/Dreamsleeve/build/phantom-baseline/runtime-32.toml --timeout 360 --output S:/Programming/Dreamsleeve/build/phantom-baseline/measured-sparse-32
python -u Scripts/benchmark_enet_workers.py --clients 32 --hosts 32 --workers 4 --scenario dense --rate 20 --seconds 15 --warm-positions --actor-values-hz 4 --replication-ms 50 --profile minimal --server-buffer 4194304 --client-buffer 1048576 --server-overlay S:/Programming/Dreamsleeve/build/phantom-baseline/runtime-32.toml --timeout 360 --output S:/Programming/Dreamsleeve/build/phantom-baseline/measured-dense-32
python -u Scripts/benchmark_enet_workers.py --clients 128 --hosts 128 --workers 4 --scenario sparse --rate 20 --seconds 15 --warm-positions --actor-values-hz 4 --replication-ms 50 --profile minimal --server-buffer 4194304 --client-buffer 1048576 --server-overlay S:/Programming/Dreamsleeve/build/phantom-baseline/runtime-128.toml --timeout 360 --output S:/Programming/Dreamsleeve/build/phantom-baseline/measured-sparse-128
python -u Scripts/benchmark_enet_workers.py --clients 128 --hosts 128 --workers 4 --scenario dense --rate 20 --seconds 15 --warm-positions --actor-values-hz 4 --replication-ms 50 --profile minimal --server-buffer 4194304 --client-buffer 1048576 --server-overlay S:/Programming/Dreamsleeve/build/phantom-baseline/runtime-128.toml --timeout 360 --output S:/Programming/Dreamsleeve/build/phantom-baseline/measured-dense-128
# Working directory: repository root
python -u build/phantom-baseline/run_chat.py --clients 32 --client-hosts 32 --rates 10 --scenarios chat --seconds 15 --repetitions 1 --profile minimal --replication-ms 50 --server-buffer 4194304 --client-buffer 1048576 --timeout 360 --output S:/Programming/Dreamsleeve/build/phantom-baseline/measured-chat-32
python -u build/phantom-baseline/run_chat.py --clients 128 --client-hosts 128 --rates 10 --scenarios chat --seconds 15 --repetitions 1 --profile minimal --replication-ms 50 --server-buffer 4194304 --client-buffer 1048576 --timeout 360 --output S:/Programming/Dreamsleeve/build/phantom-baseline/measured-chat-128
# Working directory: build/phantom-baseline/source; identical sparse32 repeat
python -u Scripts/benchmark_enet_workers.py --clients 32 --hosts 32 --workers 4 --scenario sparse --rate 20 --seconds 15 --warm-positions --actor-values-hz 4 --replication-ms 50 --profile minimal --server-buffer 4194304 --client-buffer 1048576 --server-overlay S:/Programming/Dreamsleeve/build/phantom-baseline/runtime-32.toml --timeout 360 --output S:/Programming/Dreamsleeve/build/phantom-baseline/measured-sparse-32-repeat
# Working directory: repository root
python build/phantom-baseline/capture_manifest.py manifest-patched-before.json
python build/phantom-baseline/capture_manifest.py manifest-patched-after.json
python build/phantom-baseline/summarize.py
```

The patched-before manifest was captured after the generator build, before load;
the patched-after manifest after all seven cases. `summary.json` records exact
counts/quantiles, load/drain durations and first/last-load runtime deltas. Re-running
against protocol 21 requires a new, independently identified baseline; these frozen
runs contain no new model/pose lanes.

## Raw results, failed preflight and provenance

All raw configurations, server/client logs, verification reports, process samples,
phase/barrier files, runtime histograms and databases remain ignored/local under
`build/phantom-baseline`. Summary: [`summary.json`](../../build/phantom-baseline/summary.json).
No credentials/tickets or unrelated desktop/network information are reproduced here.


| Case | Start UTC | Load ms range | Drain ms range | Result artifact |
|---|---|---|---|---|
| sparse-32 | 2026-10-05T23:03:11.602220+00:00 | 15008.65–15023.50 | 85.14–95.54 | [measured-sparse-32/results.json](../../build/phantom-baseline/measured-sparse-32/results.json) |
| dense-32 | 2026-10-05T23:03:58.916753+00:00 | 15000.44–15000.60 | 60.26–61.03 | [measured-dense-32/results.json](../../build/phantom-baseline/measured-dense-32/results.json) |
| sparse-128 | 2026-10-05T23:04:52.968867+00:00 | 15000.00–15001.00 | 62.38–63.47 | [measured-sparse-128/results.json](../../build/phantom-baseline/measured-sparse-128/results.json) |
| dense-128 | 2026-10-05T23:06:01.644883+00:00 | 15000.29–15001.34 | 60.55–61.60 | [measured-dense-128/results.json](../../build/phantom-baseline/measured-dense-128/results.json) |
| chat-32 | 2026-10-05T23:07:00.971255+00:00 | 15001.57 | 0.00 | [measured-chat-32/results.json](../../build/phantom-baseline/measured-chat-32/results.json) |
| chat-128 | 2026-10-05T23:07:39.794160+00:00 | 15000.66 | 0.00 | [measured-chat-128/results.json](../../build/phantom-baseline/measured-chat-128/results.json) |
| sparse-32-repeat | 2026-10-05T23:10:47.992486+00:00 | 15000.05–15000.63 | 58.46–58.91 | [measured-sparse-32-repeat/results.json](../../build/phantom-baseline/measured-sparse-32-repeat/results.json) |

Rejected runs retained separately (all runner exit 1, no load phase):

- `preflight-unmodified`: generated reserve 132 for 32 runtime slots; server exit 2,
  `Runtime.ControlReserve must allow 5 * MaxSessions + 5 cleanup acknowledgements`.
- `sparse-32`: after overlay, 16/32 sessions opened; four workers each rejected four
  `GuildsSnapshot` packets and failed a barrier (20 generator errors total),
  `loadMs=0`, `movement=null`. No samples/resources sent.
- `chat-confirmation`: 2/32 sessions opened; two guild errors plus incomplete online
  convergence (3 errors), `loadMs=0`, chat sent/received=0.

Their full commands/evidence are retained in
[`report-preflight.md`](../../build/phantom-baseline/report-preflight.md), and their
own `results.json`/logs remain in those three directories. Their startup/ramp CPU,
empty histograms and zero rates are excluded from the measured tables.

Hash manifests:
[`manifest-before.json`](../../build/phantom-baseline/manifest-before.json) /
[`manifest-after.json`](../../build/phantom-baseline/manifest-after.json) record the
unmodified preflight; [`manifest-patched-before.json`](../../build/phantom-baseline/manifest-patched-before.json) /
[`manifest-patched-after.json`](../../build/phantom-baseline/manifest-patched-after.json)
record the final generator and immutable matrix. `sourceSha256` is the original
archive; `actualSourceSha256` in patched manifests identifies the sole generator
source delta. `sourceSnapshotMismatches` contains only its `Program.fs`. Full hashes
cover 467 archive files and 309 build outputs.

Selected SHA-256 values:


| Artifact/source | SHA-256 |
|---|---|
| source.zip | `dcd27d1b000ba27691aa179c0757ab8ca5a080126c95f12c372a58a536c4d537` |
| generator-protocol20.patch | `233ecc9a3bba376473e57e68c9c1c29b69ad3477d6de94d872740fbdf87794bf` |
| Scripts/benchmark_enet.py | `59baa5ee99815cbe369f8588252d1ce689ede8dfa5cf7484d9a1d1bd24fe63c3` |
| Scripts/benchmark_enet_workers.py | `22856563920fb6bb77ff2b61572afe69d6291a87b54b8a87f8f2a62d31a853ef` |
| tests/Dreamsleeve.Server.NetworkBenchmarks/Movement.fs | `0be51f0ca6fb280d482c2fc7045f0355741e6b589b0d02b51cb92ea77e0e3c83` |
| tests/Dreamsleeve.Server.NetworkBenchmarks/Measurements.fs | `7b556d06a56b4eac307610714272eb1039192d5356dc0c696530dae1ae6731f2` |
| src/Dreamsleeve.Server/server.example.toml | `42316ded1c2775ec05ddc8b55ebe44cc03e94e489a24907327733d554bb5a8a9` |
| src/Dreamsleeve.Server.Core/Protocol/ProtocolCodec.fs | `182b2dd49add659891313cda519ebff3822adf6d2c2fd0e9a8b8306851d668b5` |
| src/Dreamsleeve.Server.Core/PresenceAgent.fs | `e8b1718c1f64152f98dd87df3b201e3dc62bf0befd33dcfa6250fced90de9557` |
| src/Dreamsleeve.Server.Infrastructure/TransportOwner.fs | `ffd1f51dcfac60777528c6414fbca8ef16ee52b92530d139673c33ab2686a870` |
| Generator Program.fs — original | `e100c97e5ac22dcc9c7731ef3875c9e304ff134f28d7d4c0d877896b27aa3166` |
| Generator Program.fs — patched snapshot | `7d61fadf2747ab647d89202a3aafe346adda743c50ee60116ba7c862d99bf843` |
| Generator Program.fs - working repository bytes | `6d57cc39e3b9ed80dbb9c18ce2247c5e669ba7a1a8fa4fde764e0a9d37a3c91d` |
| Dreamsleeve.Server.dll — matrix | `a2275ad5c3207b533a9de9034c4e72a5c006077b5361eaba21cc0830dc1c1d7f` |
| Dreamsleeve.Server.Core.dll — matrix | `2f8450330a69a776b2c34ef47e3a2d1a78a4254aecab47a198b4f8d91d04850e` |
| Dreamsleeve.Server.Infrastructure.dll — matrix | `e3744764c7f8676afc27f38e839a92573306724f454c426b9ea2fdf7ab965e75` |
| Dreamsleeve.Server.Infrastructure.Interop.dll — matrix | `83820a4bb503039bff15a892bf4a9e121605ff0985be8c45f4890feaba82c8d8` |
| Dreamsleeve.Protocol.Dotnet.dll — matrix | `c09b61cf0c0ddc36ca2a6acc75ecba77f9ba58e7a6eb91953bf14ac76b2f1443` |
| xENet.dll — matrix | `2921f17d511476ab287828c3a8e356818023adebdd9a5c3ac82b4aed46951ad8` |
| yENet.dll — matrix | `15b7aa88f6b83acd71f79f95cecf6754b8da654d71f12f3b13afc425febe0c7a` |
| Dreamsleeve.Server.NetworkBenchmarks.dll — matrix | `10704dbde10b7a5be41d41e65cab12ea35178f366d0b4ebaa7042b67525f60a1` |
| Dreamsleeve.Server.NetworkBenchmarks.dll — rejected preflight | `fc9694430e44210a44c9abd98b712041d8299e909229f6f91e0cef53fbb8240c` |
| run_chat.py | `ac7699880fa3118005bbe009b913f2ae070bf28a1b322edb5fc1ce97fed6b6f1` |
| runtime-32.toml | `5e09c4affefa8bd2fd8b7e95278c3126ec433f988b02cc4c67850347081a024c` |
| runtime-128.toml | `72096906bcfccee098ddf587e2820a89a2e4e384e424bb7af4600abbae1f338e` |

This is **BEFORE phantom, AFTER inherited movement/presence optimizations**. Relevant
commits: `bdb3c38` (visibility index and movement batches), `aaef43d` (detached arrays,
reusable movement scratch), `6affcb0` (bounded ENet owner/reliable lifecycle),
`f1a43f918ee9b3b6d14914a5284f3636c51fa202` (protocol 16 packet optimization), followed
by protocol 19/20 guild/color work. The benchmark-only payload handling repair
changes the generator's acceptance of lifecycle traffic, not server delivery logic,
rate scheduling, resource payloads or production capacities. The sparse32 repeat
used the same patched generator, not a second optimization.

The historical [512-client report](load-512-2026-10-01.md) and
[summary](load-512-2026-10-01.json) describe protocol 15/16/v16b and 512-client,
30-second workloads; the summary's original head is
`5b515e00a005550f951e9819c6e1385c9f23ffef`. Their results and mutable experimental
provenance are not substituted for this current protocol 20 baseline and must not
be used as a phantom cost A/B comparison. A future AFTER measurement needs matching
socket topology, density, motion/resource/chat rates, warmup, configuration, GC/JIT
and explicit source/binary hashes.
