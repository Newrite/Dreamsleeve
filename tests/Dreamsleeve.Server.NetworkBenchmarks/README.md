# Real ENet network load generator

This executable is a separate process from `Dreamsleeve.Server`. It uses yENet and
protobuf from the existing projects. The movement host also subscribes to standard
server duration instruments; no benchmark branch is added to the server handlers.
It connects to IPv4 loopback on the selected ENet port and authenticates through the configured HTTP(S) endpoint. Protocol v5 keeps ticket authentication and uses full player snapshots; no username enters OpenSession.

```powershell
dotnet build tests/Dreamsleeve.Server.NetworkBenchmarks -c Release
dotnet tests/Dreamsleeve.Server.NetworkBenchmarks/bin/Release/net10.0/Dreamsleeve.Server.NetworkBenchmarks.dll --auth-url http://127.0.0.1:8779 --port 8778 --clients 100 --seconds 10 --rate 10 --output build/network-100.json
```

Start the server separately with sufficient finite capacities. The command does
not start, stop or change server configuration. `Scripts/benchmark_enet.py` starts
both processes and samples their resource usage separately.

Each run registers fresh benchmark accounts through `POST /auth/register`. It obtains
one ticket through `POST /auth/login` immediately before each peer's ENet connection,
so tickets do not sit unused throughout a large registration phase. Passwords remain
in the benchmark process and HTTP bodies; neither passwords nor tickets enter argv,
logs or result JSON. Plain HTTP is accepted only on loopback; other endpoints require
HTTPS. The fixed password belongs only to fresh disposable benchmark accounts.

Registration uses at most four concurrent HTTP operations and happens before ENet starts and is reported as `registrationMs`. Login
runs in batches of at most four during connection ramp and is separately accumulated in `loginMs`; `rampMs`
includes it, while transport/application opening timers start after that peer's login.
The ramp admits at most four peers, then waits for all admitted online lists to
converge before obtaining the next batch of tickets. HTTP waits can still add
service delay to existing peers. Steady-state SendChat latency
starts only after every peer is ready and the idle phase has completed. This tool is
not an authentication throughput benchmark. Historical protocol-v1 measurements
remain historical; current opening times are not directly comparable to them.

`--rate` is aggregate SendChat requests per second across all clients; zero means
idle sessions throughout the load interval. Senders rotate. Connections use one
ENet host, one UDP socket and one client service loop, with up to four application
opens in flight. These are independent protocol peers and profiles, not 1000 game
processes or 1000 independent IP addresses. No packet loss, latency or WAN jitter
is injected. This measures the server plus local network/protobuf/ENet path.

After registration, the generator services incoming events through connection, load and cleanup phases, with the login waits described above. Before `READY`, every
client must receive its SessionOpened. Self player IDs must be unique, and every
client's online set must equal the exact set of all N self player IDs. Then it idles for three seconds before starting the requested workload.
The measured load lasts the requested wall-clock duration; admission pressure may
reduce achieved send rate, reported separately. There are at most 128 messages
waiting for delivery to all recipients and one unacknowledged own send per peer.
The requested workload is capped at 100000 messages to bound retained samples.

Every publication is checked for benchmark sequence, canonical message ID, author,
channel and strictly increasing message ID per recipient. A sender's copy must
carry its exact request ID; other copies must not carry one. Success requires all
actually sent messages to reach every client exactly once, no remaining author
requests, no server rejection or unexpected disconnect, complete presence
convergence, and graceful cleanup. Latencies use the client's monotonic clock:
`authorAckMs` is send to authoritative author publication; `allRecipientsMs` is
send to the last recipient. Reported percentiles use nearest ranks. Empty samples
have count zero and percentile values zero, not a measured latency of zero.

The JSON records aggregate counts, per-recipient counts and final message IDs,
opening latency, load/drain durations, actual send rate, time spent at the client
admission limits, peak in-flight messages, bounded diagnostic examples (first 64)
and the full error count. Exit status is 0 for a healthy run, 1 for a failed run
with a report, and 2 for invalid arguments or failure before a report can be made.
HTTP calls are bounded at 30s each. Connection/open waits are bounded (30s per peer,
max(120s, 2s × clients) whole ramp, including login); delivery drain
is bounded at 30s, graceful disconnect at 10s. A failed run must not be interpreted
as a successful throughput result even if it produced partial samples.

Flushed stdout markers, consumed by the external measurement runner:

```text
STAGE auth-register
STAGE connecting
READY clients=N ramp_ms=X
STAGE idle
STAGE load
LOAD_DONE sent=M expected=M*N
STAGE drain
DELIVERY_DONE received=M*N
STAGE disconnect
STAGE done
```

On failure, later successful stages may be skipped; cleanup and the JSON report
still run where possible. Server memory and CPU must be sampled from the server
PID, separately from the generator's O(N^2) online-set validation data.

Chat payload throughput is measured separately from presence and session opening.
`sentChatPayloadBytes` counts the complete serialized ClientPacket/SendChat protobuf
payload admitted by ENet; `receivedChatPayloadBytes` counts ServerPacket/ChatPublished
payload bytes actually received across all recipients. Their per-second rates use
the load duration for sends and load plus drain duration for receives. These totals
do not include UDP/IP/ENet headers, acknowledgements, retransmissions or socket
traffic. Chat text is a short unique benchmark marker (roughly 16–20 characters),
not a representative distribution of player message lengths.

## Automated server/client runs (Windows)

Build both executables before running; the runner deliberately does not change
or rebuild production code during measurement:

```powershell
dotnet build src/Dreamsleeve.Server -c Release
dotnet build tests/Dreamsleeve.Server.NetworkBenchmarks -c Release
python Scripts/benchmark_enet.py
```

The default matrix uses 100/500/1000 peers, aggregate rates 0/10/100 requests per
second, ten seconds per load phase, one repetition. For repeated measurements:

```powershell
python Scripts/benchmark_enet.py --clients 100 500 1000 --rates 0 10 100 --seconds 30 --repetitions 3
```

An explicit `--output` must name a new directory. Every case starts a fresh real
server process and a fresh generator process. Each case uses its own SQLite database
in the case directory and an unused loopback HTTP port, explicitly enables test
registration, and sets a finite 6000 requests/minute rate limit without changing
production defaults. The real configured password hashing cost is retained. The
runner keeps full server and
client logs, the complete server configuration, client verification JSON, 100ms
process samples, and summaries. Failed cases remain in the results and make the
runner return a nonzero exit code. Processes started by the runner are cleaned
up on normal completion or failure; server `quit` has a 15-second guard. The
whole-case guard defaults to180 seconds (`--timeout`), so increase it for longer
load intervals. A free loopback port is chosen per case; another process could
claim it before server bind, in which case the run fails visibly.

The `scaled` profile is **not the production default configuration**. It uses
the same capacities at every N to keep the allocated ENet host comparable:

| Setting | Benchmark value |
| --- | ---: |
| Server.PeerLimit / Runtime.MaxSessions | 1000 |
| Runtime.ControlReserve | 3004 |
| Runtime.MailboxCapacity | 8192 |
| Server.ServiceTimeoutMs / EventBudget | 0 / 512 |
| Server.MaxOutgoingPackets | 65536 |
| Server.MaxOutgoingBytes | 64 MiB |
| Runtime.OpenTimeoutMs / ShutdownTimeoutMs | 30000 / 10000 |

Other values come from the checked-in `server.example.json`, including each
player's mailbox/output limits128 and the per-peer outgoing limit256 packets.
Limits are finite and are not automatically raised when a run fails. `minimal`
changes only peer/session counts and the required lifecycle control reserve,
leaving all other example values intact. It is a separate configuration, not a
comparable repetition of `scaled`.

Metrics use Windows GetProcessTimes and GetProcessMemoryInfo. CPU is accumulated
kernel+user CPU time divided by sampled wall time, expressed in **equivalents of
one core**: 0.25 means25% of one logical processor, not25% of the whole machine.
Private bytes and working set cover only the named process, including managed
and native memory. They are not live managed-heap size. Per-stage medians and
sampled peaks can miss spikes shorter than100ms; GC and allocation rates are not
measured. No forced GC is performed. Heap retention, connection pools, and the ticket cache mean memory after
disconnect need not return to the startup value.

Stages are inferred from flushed stdout markers, observed at the next100ms
sample. Startup is sampled for one second before client launch; after successful
client disconnect the server is sampled for another two seconds before quit.
The three-second idle stage after presence convergence separates connection work
from the load phase. There is no chat warmup; first-chat JIT is included. Tiered
compilation is not overridden. Source and executable hashes, Git status, SDK,
OS and logical-processor count are recorded in `results.json`.

This is a local controlled-load diagnostic, **not a saturation benchmark or a
capacity guarantee**. All peers share one generator loop and socket; client CPU,
client parsing, scheduler delays and local UDP buffering can limit achieved
throughput. Both processes compete on the same machine. There is no WAN loss,
latency injection, production-size gameplay payload, or prolonged soak.

For a short protocol/authentication integration check instead of the full matrix:

```powershell
python Scripts/benchmark_enet.py --clients 2 --rates 2 --seconds 1 --repetitions 1 --output build/network-auth-smoke
```

The runner's process deadline also bounds registration and login. If running large
account counts on a slower machine, increase `--timeout` explicitly; do not lower
the production password hashing policy to manufacture better chat benchmark results.

## Movement benchmark

```powershell
python Scripts/benchmark_enet.py --clients 100 500 1000 --rates 10 --scenarios dense spaces sparse boundaries --seconds 10 --profile movement
```

For movement, --rate is **per-client Hz**, not aggregate chat rate. Each case uses a
fresh server process and SQLite database; registration/login and complete online
convergence precede BeginCharacter. Characters start in batches of 16 with 150 ms
service windows; every one of N*N character projections must arrive before the
one-second idle window and timed movement. Thus setup fan-out cannot silently
contaminate the load interval.
Source timestamps use the generator clock, so deliveryAgeMs includes server
coalescing/replication, ENet and the generator's own receive processing. It is not
server-only latency or a WAN estimate. Quantiles use fixed logarithmic buckets:
upper bound within 1% + 0.01 ms; max is exact. State matrices and histograms are bounded.

- dense: every player shares one space and radius (worst-case all-to-all).
- spaces: ten WRLD/CELL identities, evenly populated.
- sparse: groups of 25, separated by 20000 units in the same space.
- boundaries: groups change space and jump across the radius every two seconds.

All players send fresh timestamps even while occupying similar coordinates. A
source sends at most one due update per pump; missed intervals and eight-pending
admission stalls are recorded rather than hidden behind catch-up bursts. The generator
transport budget is 16 packets per peer and max(4096, 16*N) packets globally
(16 MiB); the global limit accommodates all per-peer windows. Final
samples are sent after the timed load, then every pair is checked for the exact
latest timestamp/X or absent position, according to space and the 8192-unit radius.
Intermediate samples are allowed to coalesce. Cleanup waits for ENet disconnects.
Success means delivery/state correctness, **not** attainment of the requested rate;
actualSamplesPerClientSecond must be assessed separately. Per-stage CPU uses only
samples from that stage. Payload bytes omit UDP/IP/ENet overhead/retransmission.

The movement profile raises finite capacities for per-tick fan-out: per-player
mailbox/output = max(256, 2*N+128), bootstrap = max(128,N), presence mailbox 8192,
runtime mailbox 65536, ENet per-peer 4096 packets/16 MiB, global 262144 packets/256 MiB.
Other scaled settings remain (1000 sessions, event budget 512, service timeout 0).
These are experimental settings written to each result directory; production
server.example.json is unchanged. Large queues do not establish a sustainable rate.

For movement only, the server runs in a separate benchmark process which calls the
unmodified Dreamsleeve.Server.Program.main with its saved configuration. dotnet exec
uses the production server.runtimeconfig.json (in particular Server GC), not the
load generator runtime defaults. The selected runtime config is saved in metadata
and the host logs its actual GC mode. Diagnostic schema v2 stores `serverMeasurements`
and `clientMeasurements`, each containing `byPhase`, `runtimeSamples`, and bounded
`slowEvents`. Earlier reports retain their original whole-run schema.

The listener records presence flush duration/interval/lateness, runtime tick
interval/duration, transport poll duration/interval/event count, packet enqueue
cost/size, pending payload bytes/packets, RTT, reliable bytes in flight, and ENet
loss-window counters. Socket sizes are read back from the live descriptor. Peer
statistics are sampled by the owner every 100 ms; no background thread touches ENet.
Without a listener transport diagnostics do not sample peers or allocate histograms.

Runtime samples include cumulative allocated bytes, GC pause time and generation
counts, last-GC heap/fragmentation, ThreadPool backlog, and system-wide IPv4 UDP
statistics. Subtract the first/last **load** samples; phase transitions from the runner
have up to 100 ms uncertainty. UDP statistics cover the entire machine, not a socket;
zero receive errors does not prove loss-free delivery. ENet `PacketsLost` is a resetting
window, not a cumulative loss counter; `TotalSentPackets` counts send attempts even
when the installed ENet socket implementation reports WouldBlock. Pending bytes
exclude ENet command/fragment headers. Histograms cost time and use a benchmark-only
lock. Slow-event timestamps and runtime samples use the same process-local clock.
Presence interval/lateness use Environment.TickCount64 and have its clock granularity.
An interval spanning setup/idle must not be treated as steady-state cadence.

Diagnostic options (buffer sizes are per socket, in bytes):

```powershell
python Scripts/benchmark_enet.py --clients 1000 --rates 10 --scenarios sparse dense --seconds 20 --timeout 600 --profile movement --server-buffer 8388608 --client-buffer 8388608 --output build/benchmarks/buffers8m
python Scripts/benchmark_enet.py --clients 1000 --rates 30 --scenarios sparse --replication-ms 20 --seconds 20 --timeout 600 --profile movement --output build/benchmarks/cadence
```

`--replication-ms` changes the server interval and the reported client metadata;
the source rate is independent. A successful final-state check does not imply the
configured publication cadence was maintained.

For a separate profile run (never compare its throughput directly with an unprofiled run):

```powershell
dotnet tool install dotnet-trace --version 10.0.745401 --tool-path build/tools
python Scripts/benchmark_enet.py --clients 1000 --rates 10 --scenarios sparse --seconds 20 --profile movement --trace-server build/tools/dotnet-trace.exe --output build/benchmarks/profile
build/tools/dotnet-trace report build/benchmarks/profile/sparse-n1000-r10-run1/server.nettrace topN -n 30
dotnet run --project tests/Dreamsleeve.TraceReport -c Release -- build/benchmarks/profile/sparse-n1000-r10-run1/server.nettrace build/benchmarks/profile/gc-report.json
```

The runner starts `dotnet-sampled-thread-time,gc-verbose` at the load marker and saves
trace/log files. Attachment has latency; rundown/drain may also be present. Stack
percentages represent sampled **thread time including waits**, not CPU percentages.
The offline TraceEvent reader separates GC suspensions from SampleProfiler's
`SuspendOther`, pairing suspend/restart by thread, and reports lost events. Allocation
Ticks estimate allocation intervals attributed to their triggering type; they are not
exact object counts or precise per-type totals. The `.nettrace` files remain in ignored
build output; keep their compact summaries with benchmark reports.

C++ cost is measured independently:

```powershell
xmake run Dreamsleeve.Client.Dev --movement-benchmark
```

One consumer with 100/500/1000 visible tracks, real ClientModel/ClientExchange/MovementView,
10 seconds of virtual time, 10 Hz input, 50 Hz frames. It runs as fast as possible and
reports wall-clock costs of complete model publication and complete consumer frames
(including drain/apply and sampling all tracks). It excludes ENet/protobuf/SKSE/rendering.
The stalled case skips consumption for two virtual seconds, then verifies snapshot
recovery and the final pose. snapshotRecoveriesObserved counts delivered replacement
snapshots, not all queue overflows that occurred during the stall.
