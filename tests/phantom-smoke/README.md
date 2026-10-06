Reusable protocol22 native Streaming / production server UDP smoke

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

The controlled-auth host uses production ServerRuntime, Presence, PhantomAgent, PhantomStorage, TransportOwner and EnetTransport. Only the account authenticator is replaced with loopback-only alice/bob fixture identities and fixed valid tickets; ancillary persistence writers are inert. No production Server source, configuration, schema or benchmark source is changed. Singer confirmed these paths do not overlap its AFTER benchmark and freezes/builds from a separate ignored copy.

Native publication uses admitted Exchange.Submit(Generation, ValidatedAsset), then production Streaming's own Worker invokes Prepared(epoch, localRevision, Publication). Remote readiness uses Asset()/State(). The fixture supplies a valid neutral model with a deterministic 256KiB alpha mask and 1024 linked nodes, so cold upload/download spans multiple 4x16384 ACK windows. Production native asset/pose codecs compress, hash, decode and validate everything.

Assertions cover protocol22 auth/Policy bootstrap, authenticated source assignment and positive request-ID correlation, bounded cold upload windows and actual model decode, chat during model transfer, atomic server/client cache files, real fragmented pose decode/playback, client FFFF rollover through production DreamNetPeer.RotateUnreliableSequence, a deliberately lost server-to-client unreliable fragment (no partial pose delivered), the next independent pose, receive=false server revocation and fresh view reentry, native disk-cache reuse, warm server same-hash generation publication without chunks, and the next generation's pose.

This bridge drives production Streaming over real ENet sockets; it does not emulate Skyrim capture/render or use ClientRuntime's login/HTTP path. The separate raw ENet transport tests remain the detailed low-level rollover/loss oracle. A native build/run is required to confirm this newly added cross-language path.

Manual host/self-check:

```powershell
dotnet run --project tests/Dreamsleeve.Phantom.Smoke.Server/Dreamsleeve.Phantom.Smoke.Server.fsproj --no-build --no-restore -- --port 18778 --state-dir build/phantom-native-smoke/manual --ready-file build/phantom-native-smoke/manual/ready.json --self-check
```

The normal host accepts a line containing stop on stdin. --self-check starts and drains the production owners without connecting clients.
