# Protocol21 phantom AFTER loopback matrix — 6 October 2026

**Status: initial freeze preserved; one targeted disabled-dense128 postfix row completed below. Phantom-on postfix rows were not rerun.**

This report measures an isolated, hash-identified server21 snapshot against the
[frozen protocol20 BEFORE matrix](phantom-baseline-2026-10-06.md). Delivery/oracle
success, reaching requested rates, and completing all model downloads are separate
outcomes. Partial model readiness and lower pose receive rates are retained below.
No production server code/configuration, Git history or experimental branch was
changed by this benchmark task. No commits, extra agents or xmake runs.

## Snapshot and reproducibility

Anscombe's readiness file declared protocol21 ready at 2026-10-06T00:10:46Z:
591 server tests, zero build warnings/errors, and one real UDP E2E smoke passed.
Those are readiness evidence from the server owner, not repeated benchmark tests.
152 readiness source hashes were verified before copying. The ignored snapshot is
`build/phantom-after/source`: Git archive of
`3885afa0232de40ac6673a818c2da4ce5034df51` plus an exact 160-input readiness/benchmark
overlay recorded in `freeze.json`. All builds/runners used this snapshot, never
concurrent working-tree outputs. This is uncommitted implementation source over
production master `7772d170f5586aba8167bb4777a5f112862ef056`, not a protocol21 commit.

Machine/runtime match BEFORE: Windows10 build19045 x64, Ryzen5800X3D,
16 logical processors, 31.924GiB physical memory, Python3.12.10,
.NET SDK10.0.401/runtime10.0.12. No forced GC/JIT overrides. Every server host used
production runtimeconfig and logged **server_gc=false (workstation GC)**.

`Scripts/benchmark_enet.py`, `benchmark_enet_workers.py`, `Movement.fs` and
`Coordination.fs` remain byte-identical to frozen BEFORE. The inherited 13-line
guild/role/mute bootstrap repair remains. Protocol21 compatibility adds five ENet
channels, the new PacketDelivery enum mapping, Policy acceptance and the empty lane4
epoch marker. Optional `PhantomProbe.fs` and `benchmark_phantom.py` add the separate
phantom workload. The recorder additionally listens to `Dreamsleeve.Phantoms`;
phantoms-off rows have no phantom meter events. The complete exact benchmark diff
against patched BEFORE is [benchmark-after.patch](../../build/phantom-after/benchmark-after.patch).

## Workloads and boundaries

Phantoms off: four worker processes, N sockets for N clients; identical dense/sparse
32/128 geometry, motion20Hz, three actor resources4Hz, replication50ms, warmed AOI,
15-second load and final convergence oracle. Visible pairs (self included):
sparse32=674, dense32=1024, sparse128=3134, dense128=16384. Separate old chat rows:
one process/N sockets, aggregate10 requests/s,15 seconds, exactly-once copies and
request correlation. No combined chat traffic was added to these old movement rows.

Generated minimal configuration matches BEFORE capacities, five-owner cleanup
reserve165/645, socket buffers server4MiB/client1MiB, fresh auth/database/log paths,
loopback only,210000 password iterations, auth concurrency4, chat anti-spam disabled,
admin disabled. Server.ChannelLimit3→5 is required by protocol21; generated config comparison
found only this channel change, volatile ports/paths and the new Phantoms table.
Phantoms.Enabled=false and a fresh case-local cache are the new
phantoms-off settings. Full generated TOML is retained per case.

Phantoms on: one owner process/N sockets, the existing **dense** movement20Hz and
resources4Hz, plus aggregate chat10/s. Four publishers, all clients receive up to
four sources within4096 units; default MaxSubscribers64 per source remains active.
Cold loads60 seconds; warm loads30 seconds. The extra overload uses128 publishers,
128 clients and20 seconds. Phantom results are not an apples-to-apples CPU delta
against the four-worker off cases; source/socket topology is explicit.

Every publisher uses the same deterministic **13MiB (13631488-byte)** opaque model:
byte[i]=i%251, SHA256
`37e6d9d0dd6bce2040b399ff0aec7c27af3f5ffad97493e40fc46e0efc05fa99`.
Manifest generation1,format1,declared raw26MiB,channels256,geometry32; all envelope
limits are valid. Complete independent pose payloads are **6KiB (6144 bytes)**,
byte[i]=i%251, generation1, current dense context1, monotonically increasing sequence
and same-machine timestamp; requested20Hz per accepted-Ready publisher.
Lane3 is reliable bulk with16384-byte chunks/window4 and Progress ACKs. Lane4 uses
sequenced unreliable fragments through the production rollover-safe wrapper.

The model and pose bodies are deliberately **opaque**, not real engine/zstd assets.
The production server checks bounded envelope metadata/content hash and does not
decode them. The benchmark verifies every downloaded byte plus incremental SHA256,
and every received complete pose body, sequence and offered view. It cannot establish
engine decode/render readiness; native Streaming/triangle fixtures belong to parent.
One shared content hash measures deduplication, not many distinct large models.
Each observer requests one full copy once an eligible Offer arrives, despite publishers
already holding the same synthetic bytes; after SHA verification it reuses that hash
for other offers. MaxSubscribers64 may leave clients unoffered at128 peers;256 pairs
does not prove128 observers all receive two sources. We retain the connected-N denominator
for completion disclosure, not a claim that all N download requests were admitted.

Cold means empty **server disk** cache at process start. Warm/overload explicitly
preseed the exact hash-named13MiB file before startup; server still verifies its hash.
They are server disk warm starts, not naturally warmed clients or warm server RAM.
Every receiving client starts without a verified download. RAM cache warms during
real chunk reads. This distinction is essential to interpreting partial downloads.

Process sampling and runtime/GC sampling are nominally100ms. CPU is process user+
kernel seconds per wall second in one-core equivalents. Private bytes, working set
and managed heap are distinct. GC/allocation deltas use first/last load samples;
phase edges have roughly100ms uncertainty. Delivery ages include scheduling,
replication, transport and receive processing. Four-worker percentiles are ranges
of worker quantiles, never pooled. Phantom distributions are from one owner process;
pose receiveHz/pair is received/whole measured duration/distinct observed pairs,
so cold rows include the pre-Ready delay and overload churn can enlarge that denominator.
The corrected overload also integrates subscription-seconds across Offer/Remove
events; its active-view weighted receiveHz is the useful cadence denominator.
Overload receive-gap distributions retain gaps across view removal/reentry and must
not be read as continuously subscribed-pair stall measurements.
Rates while Ready use each publisher's Ready timestamp; first immediate sends can
make this finite-window estimate slightly greater than20Hz.

## Phantoms-off results versus BEFORE

BEFORE sparse32 has both an initial15.53–15.73Hz run and its unchanged19.67–19.68Hz
repeat. The table compares the disclosed repeat; the first failure remains in BEFORE.

| Case | BEFORE sourceHz | AFTER sourceHz | BEFORE freshHz/pair | AFTER freshHz/pair | Private peak MiB before/after | GC pause ms before/after |
|---|---|---|---|---|---|---|
| sparse-32 | 19.67–19.68 | 19.67–19.69 | 19.60 | 19.62 | 82.01 / 80.18 | 15.94 / 6.57 |
| dense-32 | 19.57–19.62 | 19.66–19.67 | 19.48 | 19.59 | 80.99 / 82.07 | 6.27 / 7.57 |
| sparse-128 | 19.64–19.67 | 19.65–19.66 | 19.62 | 19.57 | 81.83 / 86.79 | 25.95 / 39.97 |
| dense-128 | 19.67–19.68 | 19.65–19.67 | 19.60 | 19.49 | 87.15 / 115.06 | 73.52 / 740.23 |

The identical dense128 repeat retained GC pause772.31ms, private peak115.83MiB and allocation182.94MiB/s versus the first740.23ms/115.06MiB/182.84MiB/s. This confirms the observation in two short runs without establishing its cause. Anscombe is auditing disabled Observe allocations; future fixes need new rows/snapshots.
Actual AFTER movement/resource counts and timing (ms):

| Case | Motion sent | Fresh received | Actor updates | Metadata copies | ActorHz | Motion age p95 | Resource age p95 | Gap p95 | Missed source intervals | Final convergence |
|---|---|---|---|---|---|---|---|---|---|---|
| off-sparse-32 | 9446 | 198360 | 1915 | 61184 | 3.98–3.99 | 50.44–50.95 | 50.44–50.95 | 52.52–52.52 | 0 | True |
| off-dense-32 | 9441 | 300896 | 1913 | 61088 | 3.98–3.99 | 50.44–50.44 | 51.47–51.47 | 51.99–52.52 | 0 | True |
| off-sparse-128 | 37739 | 920243 | 7653 | 977792 | 3.99–3.99 | 50.95–53.06 | 51.99–53.60 | 52.52–52.52 | 0 | True |
| off-dense-128 | 37753 | 4790886 | 7653 | 977236 | 3.98–3.99 | 52.52–58.72 | 52.52–60.52 | 61.14–69.72 | 0 | True |
| cold-32 | 37770 | 1207296 | 7654 | 244832 | 3.99–3.99 | 50.95–50.95 | 51.99–51.99 | 52.52–52.52 | 0 | True |
| cold-128 | 150312 | 19070319 | 30588 | 3912576 | 3.98–3.98 | 59.31–59.31 | 58.72–58.72 | 69.72–69.72 | 0 | True |
| warm-32 | 18884 | 597894 | 3827 | 122272 | 3.99–3.99 | 51.47–51.47 | 52.52–52.52 | 52.52–52.52 | 0 | True |
| warm-128 | 66267 | 7470530 | 14817 | 1896576 | 3.86–3.86 | 190.29–190.29 | 239.48–239.48 | 146.68–146.68 | 2432 | True |
| overload-128 | 5645 | 546348 | 1178 | 147776 | 3.97–3.97 | 316.74–316.74 | 301.32–301.32 | 116.47–116.47 | 0 | False |
| overload-128-corrected | 49826 | 5768386 | 10199 | 1298432 | 3.98–3.98 | 77.12–77.12 | 84.44–84.44 | 111.89–111.89 | 0 | True |
| off-dense-128-repeat | 37762 | 4785324 | 7656 | 977792 | 3.99–3.99 | 52.52–59.92 | 53.06–61.14 | 62.39–71.14 | 0 | True |

Chat results include exact recipient checks and delivery drain. Phantom rows include concurrent movement/model/pose load.

| Case | Sent / received / expected | Requested / achieved req/s | Author ACK p50/p95/p99 ms | All recipients p50/p95/p99 ms |
|---|---|---|---|---|
| chat-32 | 150 / 4800 / 4800 | 10 / 9.999 | 2.17/4.29/4.46 | 2.41/4.45/4.65 |
| chat-128 | 150 / 19200 / 19200 | 10 / 9.999 | 2.96/5.58/9.75 | 3.74/5.97/11.53 |
| cold-32 | 600 / 19200 / 19200 | 10 / 9.999 | 2.08/4.49/5.57 | 2.38/4.71/5.98 |
| cold-128 | 600 / 76800 / 76800 | 10 / 9.999 | 3.68/18.17/23.71 | 4.46/19.59/27.04 |
| warm-32 | 300 / 9600 / 9600 | 10 / 9.999 | 2.16/4.48/5.71 | 2.45/6.45/7.80 |
| warm-128 | 300 / 38400 / 38400 | 10 / 9.999 | 18.30/174.00/290.97 | 21.17/952.46/1559.17 |
| overload-128 | 24 / 2944 / 3072 | 10 / 10.363 | 17.61/218.57/289.00 | 20.61/246.54/304.01 |
| overload-128-corrected | 200 / 25600 / 25600 | 10 / 9.996 | 6.72/30.58/213.66 | 9.73/31.43/216.15 |


## Phantom readiness, cadence and bounded incompletion

| Case | Publishers Ready/required | SHA-verified downloads/N | Load s | Ready p95 ms | RequestedHz | SourceHz whileReady min/max | SourceHz whole load | ReceivedHz/observed pair | Received poses | Observed pairs | Pose age p95/p99 ms | Gap p95 ms |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| cold-32 | 4/4 | 5/32 | 60.00 | 25178.26 | 20 | 20.00–20.02 | 11.62 | 9.29 | 69098 | 124 | 54.15/55.25 | 64.96 |
| cold-128 | 4/4 | 0/128 | 60.00 | 25225.41 | 20 | 20.01–20.02 | 11.60 | 9.24 | 141939 | 256 | 53.06/55.82 | 74.07 |
| warm-32 | 4/4 | 4/32 | 30.00 | 86.06 | 20 | 20.02–20.03 | 19.97 | 15.93 | 59270 | 124 | 49.42/50.95 | 64.96 |
| warm-128 | 4/4 | 0/128 | 30.00 | 186.19 | 20 | 19.19–19.25 | 19.12 | 13.22 | 101484 | 256 | 200.04/304.34 | 146.68 |
| overload-128 | 120/128 | 0/128 | 2.31 | 1566.83 | 20 | 19.98–20.18 | 11.92 | 2.96 | 13479 | 1967 | 85.29/310.48 | 371.58 |
| overload-128-corrected | 126/128 | 0/128 | 20.01 | 1607.55 | 20 | 20.00–20.04 | 18.84 | 1.75 | 112733 | 3213 | 83.59/130.06 | 808.63 |


| Case | Subscriptions at stop | Active subscription-seconds | ReceivedHz per active subscription-second | Total distinct pairs across churn |
|---|---|---|---|---|
| cold-32 | 124 | n/a | n/a | 124 |
| cold-128 | 256 | n/a | n/a | 256 |
| warm-32 | 124 | n/a | n/a | 124 |
| warm-128 | 256 | n/a | n/a | 256 |
| overload-128 | 508 | n/a | n/a | 1967 |
| overload-128-corrected | 507 | 10044.50 | 11.22 | 3213 |


| Case | Upload / download payload MiB | Combined model MiB/s | Received pose envelopes MiB/s | Unfinished downloads at stop | Pose missed source intervals | Completion/refusal reasons |
|---|---|---|---|---|---|---|
| cold-32 | 52.00 / 143.30 | 3.25 | 6.78 | 27 | 0 | {"cancelled": 27} |
| cold-128 | 52.00 / 142.88 | 3.25 | 13.92 | 64 | 0 | {"transfer limit": 33, "transfer timeout": 27, "cancelled": 64} |
| warm-32 | 0.00 / 123.44 | 4.11 | 11.63 | 28 | 0 | {"cancelled": 28} |
| warm-128 | 0.00 / 123.06 | 4.10 | 19.91 | 64 | 94 | {"transfer limit": 28, "cancelled": 64} |
| overload-128 | 0.75 / 12.02 | 5.52 | 34.28 | 7 | 0 | {"transfer limit": 184, "view removed": 97, "view unavailable": 32} |
| overload-128-corrected | 0.50 / 83.14 | 4.18 | 33.16 | 56 | 0 | {"transfer limit": 927, "view removed": 480, "view unavailable": 132, "cancelled": 54} |


A successful inherited runner exit proves its delivery/state oracle and chat copies,
not full phantom readiness. `boundsComplete` in summary.json separately requires all
publishers Ready and all clients SHA-verified. Explicit cancellation reasons at stop
are part of bounded cleanup, not counted as unrequested disconnects. Admission/timeout
refusals during the load remain in their own reason counts. Partial downloaded bytes
are real traffic, not successful complete model copies. Cold sourceHz across the
whole load includes upload time; while-Ready sourceHz must not replace that metric.

## RAM, GC, queues and caches


| Case | Server CPU cores | Private MiB median/peak | Working set peak MiB | Allocated MiB/s | GC pause ms | GC counts 0/1/2 | ThreadPool pending max |
|---|---|---|---|---|---|---|---|
| off-sparse-32 | 0.055 | 79.73/80.18 | 148.13 | 13.38 | 6.57 | 4/1/0 | 0 |
| off-dense-32 | 0.043 | 80.46/82.07 | 149.48 | 16.18 | 7.57 | 5/1/0 | 0 |
| off-sparse-128 | 0.151 | 84.18/86.79 | 156.30 | 68.23 | 39.97 | 21/1/1 | 0 |
| off-dense-128 | 0.483 | 107.43/115.06 | 183.74 | 182.84 | 740.23 | 76/61/18 | 1 |
| chat-32 | 0.019 | 73.32/77.50 | 143.32 | 1.33 | 6.05 | 1/0/0 | 1 |
| chat-128 | 0.019 | 80.18/82.70 | 150.89 | 3.03 | 1.55 | 1/0/0 | 0 |
| cold-32 | 0.027 | 94.97/97.77 | 166.91 | 30.35 | 41.38 | 39/4/1 | 9 |
| cold-128 | 0.501 | 126.83/132.12 | 203.64 | 207.42 | 3306.44 | 337/251/71 | 4 |
| warm-32 | 0.029 | 97.06/100.60 | 168.59 | 35.19 | 23.71 | 23/3/1 | 0 |
| warm-128 | 0.707 | 126.36/141.17 | 211.80 | 203.61 | 2608.86 | 162/128/38 | 130 |
| overload-128 | 1.233 | 119.57/129.28 | 201.07 | 513.40 | 199.48 | 29/16/5 | 0 |
| overload-128-corrected | 0.901 | 124.54/148.37 | 218.82 | 452.69 | 1696.92 | 233/140/43 | 92 |
| off-dense-128-repeat | 0.458 | 106.41/115.83 | 186.41 | 182.94 | 772.31 | 72/57/17 | 0 |

Transport pressure is sampled counters/gauges, not evidence of unbounded capacity:

| Case | Outgoing queued packets/bytes max | Outgoing age p95 ms | Presence flush p95 ms | Realtime dropped max | Native send failures max |
|---|---|---|---|---|---|
| off-sparse-32 | 2 / 415 | 0.01 | 0.27 | 0 | 0 |
| off-dense-32 | 0 / 0 | 0.00 | 0.32 | 0 | 0 |
| off-sparse-128 | 73 / 54502 | 0.01 | 1.42 | 0 | 0 |
| off-dense-128 | 179 / 181007 | 1.81 | 17.46 | 0 | 0 |
| chat-32 | 0 / 0 | 0.00 | 0.02 | 0 | 0 |
| chat-128 | 0 / 0 | 0.00 | 0.04 | 0 | 0 |
| cold-32 | 124 / 765080 | 0.03 | 0.31 | 0 | 0 |
| cold-128 | 643 / 1657575 | 10.45 | 17.09 | 0 | 0 |
| warm-32 | 8 / 131200 | 0.04 | 0.32 | 0 | 0 |
| warm-128 | 1997 / 8711989 | 216.70 | 22.91 | 0 | 0 |
| overload-128 | 1058 / 3225578 | 20.43 | 10.91 | 0 | 0 |
| overload-128-corrected | 912 / 2755688 | 16.04 | 13.68 | 0 | 0 |
| off-dense-128-repeat | 240 / 235536 | 11.52 | 18.59 | 0 | 0 |


| Case | Disk content sampled peak MiB | Partial .tmp sampled peak MiB | Content entries max | Disk headroom at sampled content peak MiB |
|---|---|---|---|---|
| cold-32 | 13.00 | 51.70 | 1 | 4083.00 |
| cold-128 | 13.00 | 51.94 | 1 | 4083.00 |
| warm-32 | 13.00 | 0.00 | 1 | 4083.00 |
| warm-128 | 13.00 | 0.00 | 1 | 4083.00 |
| overload-128 | 13.00 | 0.00 | 1 | 4083.00 |
| overload-128-corrected | 13.00 | 0.00 | 1 | 4083.00 |


Disk values are actual sampled file lengths, with initial/preseed provenance saved
in cache-initial.json. The content store deduplicates the shared hash into one13MiB
file. Headroom column excludes temporary-file reservations; sampled partial bytes
are shown separately. This is not a4GiB eviction/capacity stress test.

**RAM cache occupancy is not directly exported by the immutable production server.**
Actual server private/working-set memory and GC/heap samples are measured above.
The source path in frozen PhantomStorage.fs:180–199 loads the single13MiB entry into
RAM on its first actual download read, since it fits the64MiB budget. Therefore
13MiB occupied/51MiB nominal remaining is an inference from executed chunk reads
and this one-hash source path, not a reported RAM gauge or a measured multi-entry
capacity. All RAM cache starts cold on process restart. No production instrumentation
was added merely to manufacture an occupancy number.

## Default budgets and headroom

All phantom rows retain: compressed model64MiB/raw128MiB, pose256KiB/raw512KiB,
Disk4GiB/RAM64MiB/1024 entries; global model4MiB/s, per-player model512KiB/s;
per-player pose2MiB/s, total outbound poses32MiB/s;64 total transfers,2/player;
chunk16KiB/window4;4 visible/client,64 subscribers/source;2048 inspected pose
subscriptions per replication tick;50ms sample/replication intervals.

Per-publisher requested pose body traffic is120KiB/s (6KiB*20),5.86% of2MiB/s;
protobuf/enet overhead is additional. Four publishers with32 clients can have124
remote subscriptions:14.53MiB/s pose bodies at20Hz, leaving17.47MiB/s nominal global
body headroom. Four publishers at128 clients are capped by64 subscribers/source:
at most256 subscriptions,30MiB/s bodies, leaving only2MiB/s (6.25%) before envelopes.
Actual selected/received pairs and rates are recorded, rather than assuming N*4.
The128-publisher overload can select512 subscriptions:60MiB/s bodies requested
against32MiB/s, before envelopes;10.67Hz is the theoretical payload-only upper bound
if all512 steady pairs receive equally. It is not a capacity measurement and does
not account for startup/churn, latest-sample replacement or actual send cadence.

All clients requesting one13MiB copy require416MiB atN32 or1664MiB atN128:
at4MiB/s the zero-overhead global minimum is104/416 seconds, plus cold uploads
(4*13MiB) sharing that budget, application windows and contention. The26-second
per-publisher cold lower bound at512KiB/s also assumes no startup burst credit.
Initial token credits permit short-window rates above configured sustained budgets.
The60/30-second runs intentionally cannot prove full cold/warm distribution to all
clients under defaults. Increasing bandwidth/budgets to force readiness was avoided.
Process CPU headroom on this desktop does not establish WAN or large-asset capacity.

## Exact commands, hashes and failed cases

Frozen build and verification commands (cwd `build/phantom-after/source`):

```powershell
dotnet build tests/Dreamsleeve.Server.NetworkBenchmarks -c Release -p:NuGetAudit=false -p:RestorePackagesPath=C:/Users/newri/.nuget/packages --source C:/Users/newri/.nuget/packages
dotnet build tests/Dreamsleeve.Server.NetworkBenchmarks -c Release --no-restore -p:BuildProjectReferences=false
dotnet tests/Dreamsleeve.Server.NetworkBenchmarks/bin/Release/net10.0/Dreamsleeve.Server.NetworkBenchmarks.dll --verify-movement-oracle
```

The first command built all server dependencies successfully but failed generator
compilation with7 F# errors from an unparenthesized comparison in a named initializer.
The benchmark-only repair build passed with0 warnings/errors; oracle passed.
Both build logs are retained. No rejected bootstrap is represented as a load row.

Executed cases below use the new adapter, which invokes the unchanged old runners
with the original parameters stated above. All output paths are ignored. Every case
has a240-second total runner deadline, bounded registration/drain/disconnect and
finally-block child shutdown (graceful server quit, then bounded kill if needed).
Matrix orchestrator: `python -u build/phantom-after/run_matrix.py` from repo root.
Exact inner commands/cwd/UTC/exit statuses are in matrix-commands.json; the initial
sparse32 and warm32 were executed separately before that orchestrator.

```powershell
# cwd S:/Programming/Dreamsleeve/build/phantom-after/source
python -u Scripts/benchmark_phantom.py --mode off --clients 32 --scenario sparse --seconds 15 --timeout 240 --output S:/Programming/Dreamsleeve/build/phantom-after/measured-off-sparse-32
python -u Scripts/benchmark_phantom.py --mode warm --clients 32 --publishers 4 --seconds 30 --timeout 240 --output S:/Programming/Dreamsleeve/build/phantom-after/measured-warm-32
python -u Scripts/benchmark_phantom.py --mode off --scenario dense --clients 32 --publishers 4 --seconds 15 --timeout 240 --output S:/Programming/Dreamsleeve/build/phantom-after/measured-off-dense-32
python -u Scripts/benchmark_phantom.py --mode off --scenario sparse --clients 128 --publishers 4 --seconds 15 --timeout 240 --output S:/Programming/Dreamsleeve/build/phantom-after/measured-off-sparse-128
python -u Scripts/benchmark_phantom.py --mode off --scenario dense --clients 128 --publishers 4 --seconds 15 --timeout 240 --output S:/Programming/Dreamsleeve/build/phantom-after/measured-off-dense-128
python -u Scripts/benchmark_phantom.py --mode chat --scenario dense --clients 32 --publishers 4 --seconds 15 --timeout 240 --output S:/Programming/Dreamsleeve/build/phantom-after/measured-chat-32
python -u Scripts/benchmark_phantom.py --mode chat --scenario dense --clients 128 --publishers 4 --seconds 15 --timeout 240 --output S:/Programming/Dreamsleeve/build/phantom-after/measured-chat-128
python -u Scripts/benchmark_phantom.py --mode cold --scenario dense --clients 32 --publishers 4 --seconds 60 --timeout 240 --output S:/Programming/Dreamsleeve/build/phantom-after/measured-cold-32
python -u Scripts/benchmark_phantom.py --mode cold --scenario dense --clients 128 --publishers 4 --seconds 60 --timeout 240 --output S:/Programming/Dreamsleeve/build/phantom-after/measured-cold-128
python -u Scripts/benchmark_phantom.py --mode warm --scenario dense --clients 128 --publishers 4 --seconds 30 --timeout 240 --output S:/Programming/Dreamsleeve/build/phantom-after/measured-warm-128
python -u Scripts/benchmark_phantom.py --mode overload --scenario dense --clients 128 --publishers 128 --seconds 20 --timeout 240 --output S:/Programming/Dreamsleeve/build/phantom-after/measured-overload-128
python -u Scripts/benchmark_phantom.py --mode overload --clients 128 --scenario dense --publishers 128 --seconds 20 --timeout 240 --output S:/Programming/Dreamsleeve/build/phantom-after/measured-overload-128-corrected
python -u Scripts/benchmark_phantom.py --mode off --clients 128 --scenario dense --seconds 15 --timeout 240 --output S:/Programming/Dreamsleeve/build/phantom-after/measured-off-dense-128-repeat
```

The first overload failed its generator oracle after2.316s: two duplicate/out-of-order errors compared a source sequence across different view revisions. This is a failed benchmark attempt, not a completed overload/capacity measurement. Its logs/counts stay in measured-overload-128. The corrected generator resets its sequence oracle on a fresh Offer/Remove and adds active-subscription-seconds; no server behavior changed. Exact repair: [generator-view-epoch-repair.patch](../../build/phantom-after/generator-view-epoch-repair.patch), rebuilt with BuildProjectReferences=false,0warnings/0errors. The corrected20s overload and offdense128 repeat use manifest-corrected-before.json; only benchmark DLL/PDB changed, all production binaries were verified identical in generator-repair-verification.json.
Final complete benchmark diff: [benchmark-after-corrected.patch](../../build/phantom-after/benchmark-after-corrected.patch); corrected owned input hashes: [owned-inputs-corrected.json](../../build/phantom-after/owned-inputs-corrected.json).
The adapter sets `DREAMSLEEVE_BENCH_PHANTOM` to a saved JSON containing Publishers,Rate20,PoseBytes6144,ModelPath. Optional phantom cases require one worker. The parameter is removed for all old off/chat rows. Two early runs used adapterv1; subsequent cases usedv2, whose sole change tolerates disappearance of a temporary cache file during size sampling. No generator/server binary or workload changed between these adapter versions. Both exact patches/input hashes are saved.

| Artifact | SHA256 |
|---|---|
| Scripts/benchmark_enet.py | cb4199ccc1d544401a91bdf9e46bebb9b5af6f6e2ab0804e2ff3a2cf87b21201 |
| Scripts/benchmark_enet_workers.py | fa46f2730618fa537efd8e608916ecfed99ae7d2b7bc0442b5fd9933c1315870 |
| Scripts/benchmark_phantom.py | 4f3c330ed8c175b6333642c9dd99bb521266036b1b6fae3f5b20e26249338006 |
| tests/Dreamsleeve.Server.NetworkBenchmarks/Movement.fs | 0be51f0ca6fb280d482c2fc7045f0355741e6b589b0d02b51cb92ea77e0e3c83 |
| tests/Dreamsleeve.Server.NetworkBenchmarks/PhantomProbe.fs | 2b88c40f6dce9e0806959cd88fbc6a3dd4a8e254e42fc734b6f24b0927be1800 |
| tests/Dreamsleeve.Server.NetworkBenchmarks/Program.fs | d9c0a276ec24c1218d12c8cf0c3f79512f08e949ee5eacec3fd8c63f4c4dd223 |
| src/Dreamsleeve.Server.Core/PhantomAgent.fs | 9cb03a113a988097088ab00643371e335d6f1a54a495795b929c1a6eee8fbdb9 |
| src/Dreamsleeve.Server.Infrastructure/PhantomStorage.fs | 7741ef17d653a706de622a8749cff3f8978128d6697facd88e00b7b63c8becd8 |
| Protocol/phantom.proto | 1bf8a42d5575ecbda9ba486c0d09f1569dd636ec141f2c82e36c97c2b38b5d0a |
| src/Dreamsleeve.Server/bin/Release/net10.0/Dreamsleeve.Server.dll | 7610c9bb883cf11109899e361dfe83d10ef5d951f8891fb99cff88c604b6cf63 |
| src/Dreamsleeve.Server/bin/Release/net10.0/Dreamsleeve.Server.Core.dll | a7b9b816ea7f2a0c9967e4b126dfb2ad40c035eaa36511ea60e0f650a6c56b4b |
| src/Dreamsleeve.Server/bin/Release/net10.0/Dreamsleeve.Server.Infrastructure.dll | c91627f3794f2b9a6703da43ecdbd08958c2c1115bab4b3f07e51baa56c88c87 |
| src/Dreamsleeve.Server/bin/Release/net10.0/Dreamsleeve.Server.Infrastructure.Interop.dll | dbe9a8f022cfdd7464eed69d8f2a44df9e9a697aa4b92f0d7e0818f1b5e27299 |
| tests/Dreamsleeve.Server.NetworkBenchmarks/bin/Release/net10.0/Dreamsleeve.Server.NetworkBenchmarks.dll | 67a3963b8318c1652f709f4009cd6cdc42bc6a026aa05ffd05e2d95fd5c52d56 |
| src/Dreamsleeve.Server/bin/Release/net10.0/Dreamsleeve.Server.runtimeconfig.json | b4aad84277c120c025a443649c4e09ca20b08725acc12ef60940786bfb8c024f |
| benchmark-after.patch | 80a2fd49fd8ddbc7c118bd44c40770774ce1af3d83c00ebe440b38fcbdbfb1fe |
| source-base.zip | dcd27d1b000ba27691aa179c0757ab8ca5a080126c95f12c372a58a536c4d537 |

Full source/binary manifests contain480 source files and400 binaries/build outputs. `manifest-before.json` identifies the early adapterv1 runs, `manifest-matrix-before.json` the remaining matrix. Ready source mismatches are empty. Final manifest-after.json matches manifest-corrected-before.json exactly; final-verification.json records13 saved cases,12 correctness passes and1 preserved failed oracle attempt. All37 captured movement/server PIDs were absent at the final Get-Process audit; chat child shutdown returned successfully through the inherited runner. No benchmark process is intentionally left running. Corrected generator hashes are in manifest-corrected-before.json; the main selected table identifies the original matrix generator before the later benchmark-only oracle repair. Summary/raw configs/logs/phase files/process samples/metrics/DBs remain under [build/phantom-after](../../build/phantom-after). Credentials/tickets are not printed here.


| Case | Correctness success | Incomplete phantom bounds | Load span ms | Errors/rejections/disconnects | Raw result |
|---|---|---|---|---|---|
| off-sparse-32 | True | False | 15000.03–15000.04 | 0/0/0 | [measured-off-sparse-32/results.json](../../build/phantom-after/measured-off-sparse-32/results.json) |
| off-dense-32 | True | False | 15000.99–15001.99 | 0/0/0 | [measured-off-dense-32/results.json](../../build/phantom-after/measured-off-dense-32/results.json) |
| off-sparse-128 | True | False | 15000.23–15001.24 | 0/0/0 | [measured-off-sparse-128/results.json](../../build/phantom-after/measured-off-sparse-128/results.json) |
| off-dense-128 | True | False | 15000.73–15001.74 | 0/0/0 | [measured-off-dense-128/results.json](../../build/phantom-after/measured-off-dense-128/results.json) |
| chat-32 | True | False | 15000.78–15000.78 | 0/0/0 | [measured-chat-32/results.json](../../build/phantom-after/measured-chat-32/results.json) |
| chat-128 | True | False | 15001.26–15001.26 | 0/0/0 | [measured-chat-128/results.json](../../build/phantom-after/measured-chat-128/results.json) |
| cold-32 | True | True | 60003.79–60003.79 | 0/0/0 | [measured-cold-32/results.json](../../build/phantom-after/measured-cold-32/results.json) |
| cold-128 | True | True | 60004.18–60004.18 | 0/0/0 | [measured-cold-128/results.json](../../build/phantom-after/measured-cold-128/results.json) |
| warm-32 | True | True | 30002.51–30002.51 | 0/0/0 | [measured-warm-32/results.json](../../build/phantom-after/measured-warm-32/results.json) |
| warm-128 | True | True | 30004.48–30004.48 | 0/0/0 | [measured-warm-128/results.json](../../build/phantom-after/measured-warm-128/results.json) |
| overload-128 | False | True | 2315.92–2315.92 | 2/0/0 | [measured-overload-128/results.json](../../build/phantom-after/measured-overload-128/results.json) |
| overload-128-corrected | True | True | 20008.02–20008.02 | 0/0/0 | [measured-overload-128-corrected/results.json](../../build/phantom-after/measured-overload-128-corrected/results.json) |
| off-dense-128-repeat | True | False | 15000.11–15001.73 | 0/0/0 | [measured-off-dense-128-repeat/results.json](../../build/phantom-after/measured-off-dense-128-repeat/results.json) |


This is a preserved intermediate AFTER of the protocol21 server implementation, BEFORE any optimization in
response to these measurements. No server performance fix was applied between rows. Parent subsequently requested
an Observe/allocation audit; any resulting server patch needs separate snapshot/hash
and new affected rows, not replacement of these measurements.
Inherited BEFORE movement optimizations remain identified in the BEFORE report;
historical512-client/protocol15–16 results are not substituted for this comparison.
These short instrumented loopback runs cannot establish WAN stability, real rendering,
many distinct model cache behavior, long-session leaks, or sustainable production capacity.

The previously hanging UI browser runner has now returned **exit0,41/41 tests passed**
after stopping only four verified owned Vite shutdown processes. Those PIDs are absent.
UI Vitest94/94, build and tsc exit0 refer to the previous fourteen-setting UI freeze.
Parent is subsequently adding local upload/download budget fields and owns those
new UI checks; the old results do not certify the new sixteen-field revision. Logs and browser failure
attempts remain in build/phantom-baseline; browser verification is separate from
protocol21 throughput and no browser process was used as a benchmark generator.

Separate read-only fixed-Core review: [core-fixed-review/findings.md](../../build/phantom-after/core-fixed-review/findings.md), three reproduced issues with frozen source/probe hashes. This client review is separate from the managed server matrix.

## One targeted server postfix rerun — disabled dense128

All initial rows and their failed attempt remain above. ONE additional targeted case
uses `build/phantom-after/optimized-source`, independently frozen from saved final
readiness SHA256 `1c267e6a414f77f3fa1773297f1dee1847e40309cce7c262361d4153c3895616`.
Git base3885afa plus157 verified production input hashes and10 unchanged corrected
benchmark inputs are in [optimized-freeze.json](../../build/phantom-after/optimized-freeze.json).
The preserved readiness is [optimization-ready-frozen.json](../../build/phantom-after/optimization-ready-frozen.json),
so a subsequently refreshed owner readiness file does not change this provenance.
Owner readiness reports599/599 server tests and fresh native realUDP1/1; those are
owner verification, separate from the benchmark. Source HEAD metadata is69e8487
plus the exact uncommitted server overlays. This is a source-hash freeze, not a
claim that Git69e8487 already contains all server optimizations.

This snapshot includes Membership gating before location comparisons, bounded
atomic Full batches, idle view reuse, closure/source lookup and packet buffer fixes.
The later geometry default/hard-cap512 and601-test readiness are outside this run;
its prior geometry256 affects no enabled phantom traffic because Phantoms.Enabled=false.
Client/Core memory/cache/factory/ACK fixes also do not alter this managed workload.
The parent was permitted to build/package concurrently; this run is not evidence
of an otherwise idle host or a sustainable capacity guarantee.

The workload is identical:128 sockets,4 workers, dense16384 pairs including self,
requested20Hz movement,3 actor resources at4Hz,15s load, warmed AOI,50ms replication,
unchanged minimal capacities/buffers and no chat workload. No phantom-on row was rerun.

| Case | Achieved source Hz | Fresh Hz/pair | Alloc MiB/s | GC pause ms | Server private peak MiB | CPU cores |
|---|---|---|---|---|---|---|
| BEFORE20 dense128 | 19.669–19.681 | 19.603 | 148.97 | 73.52 | 87.15 | 0.386 |
| Initial AFTER21 dense128 | 19.652–19.673 | 19.493 | 182.84 | 740.23 | 115.06 | 0.483 |
| Initial AFTER21 dense128 repeat | 19.656–19.682 | 19.471 | 182.94 | 772.31 | 115.83 | 0.458 |
| Optimized AFTER21 dense128 | 19.624–19.662 | 19.586 | 150.27 | 78.28 | 91.46 | 0.340 |

Targeted correctness PASS:128/128 ready, final convergence true, zero client errors, rejections, unexpected disconnects, missed source intervals, and UDP receive errors. Sent movement samples=37710; fresh movements=4813780; actor updates/ACKs=7652/7652; actor achieved=3.985–3.985Hz. Delivery-age p95 worker range=51.99–56.39ms; actor ACK p95=4.16–4.16ms. GC counts=49/11/1, measured runtime span=14.900s. No chat latency is invented for a movement-only case.

GC/private-memory regressions observed in the original disabled row are much smaller
in this one postfix run. Cold/warm/on overload rows above still describe the initial
freeze; this disabled result does not certify their postfix performance.

Exact bounded commands (cwd `build/phantom-after/optimized-source`):

```powershell
dotnet build tests/Dreamsleeve.Server.NetworkBenchmarks -c Release -p:NuGetAudit=false -p:RestorePackagesPath=C:/Users/newri/.nuget/packages --source C:/Users/newri/.nuget/packages --disable-build-servers -p:UseSharedCompilation=false
dotnet tests/Dreamsleeve.Server.NetworkBenchmarks/bin/Release/net10.0/Dreamsleeve.Server.NetworkBenchmarks.dll --verify-movement-oracle
python -u Scripts/benchmark_phantom.py --mode off --scenario dense --clients 128 --publishers 4 --seconds 15 --timeout 240 --output S:/Programming/Dreamsleeve/build/phantom-after/measured-off-dense-128-optimized
```

Build passed0 warnings/errors in32.93s; movement oracle and case exited0. Exact commands/UTC/deadlines: [optimized-commands.json](../../build/phantom-after/optimized-commands.json). Source and binary manifests before/after are equal, recorded in [optimized-verification.json](../../build/phantom-after/optimized-verification.json).

| Artifact | SHA256 |
|---|---|
| src/Dreamsleeve.Server/bin/Release/net10.0/Dreamsleeve.Server.dll | 2e8cdffefaed9ab5430e3379a42dfed5a23212823377faf1904150281986ffde |
| src/Dreamsleeve.Server/bin/Release/net10.0/Dreamsleeve.Server.Core.dll | 8117b2bddf12f543bac0c511ffc85675046978155294ae5e78bff437fbc58b02 |
| src/Dreamsleeve.Server/bin/Release/net10.0/Dreamsleeve.Server.Infrastructure.dll | 13c09abeab8f777de076cc18d2355452ba1656953497720410e472d5e5dcbe21 |
| tests/Dreamsleeve.Server.NetworkBenchmarks/bin/Release/net10.0/Dreamsleeve.Server.NetworkBenchmarks.dll | 33f4581e252faefb8b7853860f1eb995abe2e994c54eca3f0767e4eff610fbb6 |
| src/Dreamsleeve.Server/bin/Release/net10.0/Dreamsleeve.Server.runtimeconfig.json | b4aad84277c120c025a443649c4e09ca20b08725acc12ef60940786bfb8c024f |
| src/Dreamsleeve.Server.Domain/Phantoms.fs | 72a864054c44898307015589b12fcb83ca02e728612c00ab670773cef35c7f48 |
| src/Dreamsleeve.Server.Core/PresenceAgent.fs | 9daf8e0b595a278627e7e9fcd41576ea81afb9444a3654257202fad2dc0d8803 |
| src/Dreamsleeve.Server.Core/ServerRuntime.fs | d1dcdc68dfe2643e23572760ded33414022cb25f62a34c007a3c5f406a2096b4 |
| src/Dreamsleeve.Server.Core/PhantomAgent.fs | a82f10d2578a21b9299e3d3f07d70c9e07184782815f6f8fc9a17ffb4de3975f |
| src/Dreamsleeve.Server.Core/Protocol/PhantomCodec.fs | ba3cf7f8fcb41a137cfb55b04889e5766e9dd4d9243016e70982f40b29610576 |
| tests/Dreamsleeve.Server.Tests/PhantomTests.fs | 60e25fabf18f9125766ab791a6f7b84c421e60c1fc220b2e5d8398537cb87154 |
| tests/Dreamsleeve.Server.Tests/PresenceAgentTests.fs | 2be2c82822e2ab407d35863d54864af6111016effb6cc03be3de06d9574eb794 |
| tests/Dreamsleeve.Server.Tests/ServerRuntimeTests.fs | f2f0e90911c1bced7bb7d4b38d239ab532f2059eeeda3eaa0fda986127b40318 |

Actual samples/configs/metrics/results/logs: [measured-off-dense-128-optimized](../../build/phantom-after/measured-off-dense-128-optimized), summarized in [optimized-summary.json](../../build/phantom-after/optimized-summary.json). The inherited child cleanup completed; exact PID exit audit is saved separately.
