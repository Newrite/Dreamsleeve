# Real ENet network load generator

This executable is a separate process from `Dreamsleeve.Server`. It uses yENet and
protobuf from the existing Infrastructure references; no production code changes.
It connects to IPv4 loopback on the selected port.

```powershell
dotnet build tests/Dreamsleeve.Server.NetworkBenchmarks -c Release
dotnet tests/Dreamsleeve.Server.NetworkBenchmarks/bin/Release/net10.0/Dreamsleeve.Server.NetworkBenchmarks.dll --port 8778 --clients 100 --seconds 10 --rate 10 --output build/network-100.json
```

Start the server separately with sufficient finite capacities. The command does
not start, stop or change server configuration. `Scripts/benchmark_enet.py` starts
both processes and samples their resource usage separately.

`--rate` is aggregate SendChat requests per second across all clients; zero means
idle sessions throughout the load interval. Senders rotate. Connections use one
ENet host, one UDP socket and one client service loop, with up to eight application
opens in flight. These are independent protocol peers and profiles, not 1000 game
processes or 1000 independent IP addresses. No packet loss, latency or WAN jitter
is injected. This measures the server plus local network/protobuf/ENet path.

The generator services incoming events during every phase. Before `READY`, every
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
Connection/open waits are bounded (30s per peer, 120s whole ramp), delivery drain
is bounded at 30s, graceful disconnect at 10s. A failed run must not be interpreted
as a successful throughput result even if it produced partial samples.

Flushed stdout markers, consumed by the external measurement runner:

```text
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
server process and a fresh generator process. The runner keeps full server and
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
measured. No forced GC is performed. Heap retention and the process-local
profile store mean memory after disconnect need not return to the startup value.

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
