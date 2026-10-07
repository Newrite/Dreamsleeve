Reusable protocol24 native Streaming / production server UDP smoke

Run from S:/Programming/Dreamsleeve after the parent has serialized the native build:

```powershell
# Parent-owned native build; this fixture never invokes xmake itself.
xmake build Dreamsleeve.Client.Tests
# Build the isolated controlled-auth host (production Server is unchanged).
dotnet build tests/Dreamsleeve.Phantom.Smoke.Server/Dreamsleeve.Phantom.Smoke.Server.fsproj -v minimal
pwsh -NoProfile -File tests/phantom-smoke/run.ps1 -NativeTests build/windows/x64/releasedbg/Dreamsleeve.Client.Tests.exe -NoBuild
```

run.ps1 chooses an ephemeral loopback UDP port, starts the fixture with hidden redirected processes, passes bounded per-child environment values, selects only the new opt-in test, requires PHANTOM_NATIVE_UDP_PASS, then requests graceful server shutdown. Existing native test gates and normal pure runs are unchanged. Use -Artifacts to choose an output directory. Logs, result.json, readiness and both cache files are retained under build/phantom-native-smoke/run-* by default. There are no recursive deletes or inherited environment changes.

Without the two smoke environment variables, the new test is skipped. The script never builds native code. -NoBuild skips the F# build as well. An old binary with no matching test cannot silently pass: the success sentinel is mandatory.

The controlled-auth host uses production ServerRuntime, Presence, PhantomAgent, PhantomStorage, TransportOwner and EnetTransport. Only the account authenticator is replaced with loopback-only alice/bob fixture identities and fixed valid tickets; ancillary persistence writers are inert. No production Server source, configuration, schema or benchmark source is changed.

Native publication uses admitted Exchange.Submit(Generation, ValidatedAsset), then production Streaming's own Worker invokes Prepared(epoch, localRevision, Publication). Remote readiness uses Asset()/State(). The fixture supplies a synthetic native NIF with 1024 linked nodes; its compressed 742923-byte asset spans more than the 32 × 16384-byte application ACK window. Production native asset/pose codecs compress, hash, decode and validate everything.

Assertions cover protocol24 auth/Policy bootstrap, authenticated source assignment and positive request-ID correlation, bounded cold upload windows and actual model decode, chat requested after download begins with a separate two-second response deadline, atomic server/client cache files, real fragmented pose decode/playback, client FFFF rollover through production DreamNetPeer.RotateUnreliableSequence, a deliberately lost server-to-client unreliable fragment (no partial pose delivered), the next independent pose, receive=false server revocation and fresh view reentry, native disk-cache reuse, warm server same-hash generation publication without chunks, and the next generation's pose.

This bridge drives production Streaming over real ENet sockets; it does not emulate Skyrim capture/render or use ClientRuntime's login/HTTP path. The separate raw ENet transport tests remain the detailed low-level rollover/loss oracle. A native build/run is required to confirm this newly added cross-language path.

Manual host/self-check:

```powershell
dotnet run --project tests/Dreamsleeve.Phantom.Smoke.Server/Dreamsleeve.Phantom.Smoke.Server.fsproj --no-build --no-restore -- --port 18778 --state-dir build/phantom-native-smoke/manual --ready-file build/phantom-native-smoke/manual/ready.json --self-check
```

The normal host accepts a line containing stop on stdin. --self-check starts and drains the production owners without connecting clients.

## WAN delay, loss and real local assets

Build the Release smoke server, then run the relay (Python standard library only):

```powershell
dotnet build tests/Dreamsleeve.Phantom.Smoke.Server -c Release
python tests/phantom-smoke/latency.py --client build/diagnostics/windows/x64/releasedbg/Dreamsleeve.Client.Tests.exe --output build/phantom-native-smoke/wan --rtt-ms 100 --jitter-ms 20 --loss-every 200
```

The relay maintains separate UDP endpoints for both clients, delays both directions,
reorders datagrams through deterministic jitter and drops every Nth datagram.
`network.json` records the actual drop count. A fresh output directory is required;
no prior cache is removed. This is a transport regression scenario, not an Internet
congestion model or a Skyrim visual test.

Add `--model <local-cache.zst>` to test a real native asset through production
ReadAsset/Prepare/Streaming/Worker/server storage/download/decode. This mode only
checks bulk transfer, integrity and chat during download; the synthetic run separately
checks guaranteed multi-fragment poses, loss, sequence rollover, view reentry and
warm generation replacement. User assets remain local and are not part of the repo.
`PHANTOM_PHASE` and `PHANTOM_TRANSFER` report wall-clock stage and chat timings.

No protocol/schema changes are involved in the bounded 512 KiB sender flight
window. Native bandwidth limits, when explicitly configured, retain ENet's window.
