# Dreamsleeve: engineering instructions

Apply these rules to new and changed code. Existing violations are not precedents.
Follow explicit task requirements; raise material conflicts instead of silently changing policy.
Read any more specific `AGENTS.md` for the area being changed.

## Start with the responsibility

- Read `README.md`, `docs/README.md`, and the relevant component/feature documentation.
  Check the current implementation: historical specifications and stale examples are not current contracts.
- Before a substantial change, identify the owner of the rule and state, input boundary, failure outcomes,
  affected contracts, and smallest coherent implementation. A short plan is enough; routine fixes need no ceremony.

| Area | Responsibility |
| --- | --- |
| `src/Dreamsleeve.Server.Domain` | Domain values, invariants, rules, and owner-confined domain storage |
| `src/Dreamsleeve.Agent` | Reusable sequential agents and their lifecycle; no Dreamsleeve business rules |
| `src/Dreamsleeve.Server.Core` | Application orchestration, sessions, state owners, ports, and protocol codecs |
| `src/Dreamsleeve.Server.Infrastructure`, `src/Dreamsleeve.Server.Infrastructure.Interop` | Persistence, service adapters, and native transport ownership |
| `src/Dreamsleeve.Server.Web`, `src/Dreamsleeve.Server` | HTTP adapters and server composition/startup |
| `src/Dreamsleeve.Client.Core` | C++23 domain, state, networking, and application runtime independent of Skyrim |
| `src/Dreamsleeve.Client`, `src/Dreamsleeve.Client.Dev` | Skyrim/SKSE integration and standalone development host |
| `src/Dreamsleeve.Client.UI` | React/TypeScript presentation and host bridge |
| `Protocol`, `db` | Wire schemas and database migrations; sources for generated representations |

Dreamsleeve is a social layer that relays Skyrim observations, not an authoritative game simulation.
Preserve this scope: server authority over social operations does not justify inventing simulation rules.
Keep domain decisions independent of protobuf, SQLite, HTTP, ENet, UI, and game-engine details.

## Model valid domain states

- Express domain distinctions and valid state combinations in types: discriminated unions, meaningful IDs,
  units, and controlled construction. Prefer a small precise type over flags and optional fields that permit
  contradictory combinations. Do not build a type-level framework for a simple invariant.
- `option`, `voption`, and `std::optional` mean legitimate absence. Missing required input, malformed data,
  and external failure must produce a typed error, not an invented absence or a successful default.
  Keep `Some null`, `ValueSome null`, foreign sentinels, and unchecked DTOs out of trusted domain values.
- Keep distinct meanings distinct: unknown, unchanged, explicitly cleared, rejected, and unavailable are
  not interchangeable. Nested options are acceptable when their states have clear, documented semantics;
  use a named union when it makes the contract easier to understand.
- Construct invariant-bearing values through their owning API. UMX tags distinguish primitive kinds but
  do not enforce validation: never turn untrusted input into a trusted value using `UMX.tag`, casts,
  `Unchecked.defaultof`, deserialization, or zero-initialized storage that bypasses the checked path.
- A function that cannot fail for valid arguments returns its value directly. Do not wrap infallible
  internal operations in `Result` or `option` to repeat a boundary check.
- Handle closed domain unions and variants exhaustively. Do not add wildcard/default cases that hide
  newly added alternatives. Reject unknown wire values separately while preserving compatibility rules.

## Validate at the owning boundary

- Parse external representations into trusted values once, at the boundary responsible for each invariant.
  Reuse the owning constructor/validator; do not copy its checks into handlers, stores, codecs, and UI.
  Internal code relies on established invariants instead of reparsing or renormalizing the same values.
- Distinguish stable value invariants from current-state preconditions. The owner must still check current
  permissions, membership, phase, existence, quota, and operation/generation identity when applying a command.
  Perform the relevant checks in the same serialized operation as the mutation; an earlier check may be stale.
- Respect the existing client/server validation split in `docs/CurrentStateRu.md` and component READMEs.
  Wire shape checks, domain construction, and state transitions have different responsibilities.
  Do not add server business-rule validation to the client as a second authority.
  Preserve server-to-client wire/session checks and trust the server-validated domain content;
  do not repeat its value validation or normalization in the client model.
- Validate configuration and relationships between limits when constructing configuration. Limits must come
  from a documented contract, actual resource budget, or measured workload. Explain their units and purpose;
  do not invent arbitrary caps, silent clamping, or truncation merely to appear defensive.
- Preserve modded-game semantics. Finite-value and payload checks do not authorize limits on Skyrim speed,
  coordinate ranges, angles, HP, or current/maximum relationships that the product does not require.
- Reject invalid input before committing business state or publishing success. When persistence and memory
  must change together, preserve the owner's defined commit/failure ordering; do not report success early.

## Own mutation and asynchronous work

- Actor-owned state MAY be mutable. Only its owner's serialized processing context may read or mutate it,
  including snapshot construction. Prefer efficient private collections to repeatedly rebuilding large
  immutable state graphs. Pure functions remain useful for domain decisions.
- Publish values and snapshots that remain immutable after publication, with no mutable aliases into
  owned state. An immutable record containing a mutable collection, a read-only interface, or a lazy
  sequence over live storage is not a safe snapshot. Keep indexes and cached snapshots consistent
  under the same owner.
- Background work receives detached immutable inputs and returns through messages. Callbacks and task
  continuations outside the owner's serialized processing context must not access actor state.
  Observe failures and cancellation; validate late replies against the current operation and lifecycle.
- A sequential actor is not a dedicated OS thread. Preserve `TransportOwner` as the native ENet owner.
  Keep the client's game/network thread boundary in `ClientExchange`; do not pass game pointers or invoke
  game/UI operations from the networking thread. Follow existing explicit packet/buffer ownership transfers.
- Give native and managed resources a clear owner and deterministic cleanup on success, error, cancellation,
  and shutdown. Do not send borrowed pointers, `string_view`, spans, or pooled buffers through deferred work
  without a guaranteed lifetime. After a mutable buffer transfer, the sender must not access it until
  ownership explicitly returns.
- Use `TryAskAsync` / `TryReadAsync` and handle their typed outcomes in production callers. Consult
  `src/Dreamsleeve.Agent/README.md` for admission, completion, cancellation, and supervision semantics.
  A timeout does not prove an admitted operation did not execute; do not automatically retry it.
- Bound queues AND outstanding work where either can grow independently. Define saturation behavior and
  preserve lifecycle/control capacity. Do not silently lose reliable commands; realtime dropping/coalescing
  must follow its contract. Keep admission, state commit, and delivery outcomes distinct.
- Do not block async handlers or callbacks with synchronous waits. Explicit startup/join boundaries of
  dedicated threads are different; preserve their documented lifecycle and cleanup behavior.

## Control allocation without weakening the model

- Treat per-packet, per-player, replication, and frequent update paths as allocation-sensitive.
  Look for closures, boxing, temporary collections, repeated conversions, unnecessary serialization,
  and work proportional to all players where the operation has a smaller natural scope.
- Choose `voption`, struct records/unions, and struct tuples deliberately. Consider payload size, copying,
  boxing, and lifetimes; do not mechanically turn every type into a struct. F# `Result` is already a struct
  union. Preserve meaningful domain types instead of replacing them with primitives or `obj`.
- Project-owned synchronous higher-order helpers on hot paths MUST use `inline` and MUST mark function
  parameters intended to inline caller lambdas with `[<InlineIfLambda>]`. The attribute belongs on the
  parameter, not on a `fun` expression. For example:

  ```fsharp
  let inline visitAll ([<InlineIfLambda>] visit: 'T -> unit) (items: 'T array) =
      for item in items do
          visit item
  ```

- Stored callbacks, asynchronous handlers, and non-inlineable third-party APIs need an appropriate design;
  an attribute cannot remove their lifetime requirements. Use a direct loop or suitable helper where useful.
  Neither F# annotations nor C++ `inline` prove zero allocations or better generated code.
- F# SRTP, C++ templates, and .NET generics are encouraged when they express a real shared contract and
  reduce current duplication or overhead. Keep constraints and call sites readable. Avoid unnecessary
  type erasure and boxing on hot paths; do not generalize unrelated lookalike code.
- Support material performance claims with a focused measurement or generated-code inspection.
  Reuse the repository's benchmark tools when relevant; compare equivalent workloads and report the limits
  of the evidence. Do not require a benchmark for every ordinary edit.

## Errors are values; exceptions stay at designated boundaries

- Project-authored throwing is forbidden in production server AND client code, including UI application
  logic. Do not introduce `throw`, `raise`, `failwith`, `failwithf`, `invalidArg`, `nullArg`, or equivalent
  exception-based control flow. Existing throwing convenience APIs are not examples to propagate.
- Represent expected failure with existing typed results: F# `Result`/specific outcome unions and C++
  `std::expected`/existing error types. Preserve meaningful error categories; do not branch on message text.
  Use total or Try-style APIs when absence or rejection is possible, not unchecked extraction that throws.
- Catch exceptions only in narrow adapters for dependency APIs that throw on ordinary failures and lack
  a suitable nonthrowing alternative, or at server top-level lifecycle/supervision boundaries.
  Adapters translate relevant failures once and preserve cancellation and useful diagnostic context.
- Do not add catch/rethrow chains, catch-and-continue handlers, swallowed errors, or fabricated success.
  Do not wrap ordinary operations just because allocation might fail, and do not disguise an unexpected
  runtime failure as `None`, an empty collection, or a normal command rejection.
- Server supervision must have a defined recovery action: isolate, stop, release ownership, and restart
  or reconstruct the affected subsystem as its contract requires. Do not continue using partially mutated
  state as if the failed operation succeeded. Supervision is not a guarantee against every process failure.
- Do not implement this policy by indiscriminately adding C++ `noexcept` or disabling compiler/runtime
  exception support. `noexcept` must match the actual operation; escaping exceptions terminate the process.

## Keep changes cohesive and code readable

- Put each rule and its state transitions under one clear owner. Organize files around cohesive
  responsibilities and hidden implementation decisions. Keep adapters thin and feature logic discoverable.
  Avoid generic `Manager`, `Utils`, or service layers that accumulate unrelated responsibilities.
- Aim to keep a small feature local to its responsibility. If it requires edits across many unrelated
  owners, examine the design first. A real cross-boundary contract change may legitimately touch several
  projects; do not hide that work to minimize the file count.
- Use descriptive names, consistent formatting, and blank lines between functions/types and logical
  sections. Flatten deep nesting with explicit branching, early exits where idiomatic, or named helpers.
  Split functions/types/files with multiple responsibilities; size alone is not a reason to fragment a
  single coherent unit. Do not replace readable code with compressed expressions.
- Name meaningful limits, units, intervals, flags, and policy values. Keep constants with the owning
  feature; use a dedicated module/resource for a coherent shared set. Avoid a global constants dumping
  ground and pointless names for self-evident local values such as a loop's zero index.
- Preserve the established C++ syntax style from `.clang-format` and subsequent manual edits.
  A completed formatting pass does not reduce the need for source review or structural improvement.
  Do not repeat repository-wide clang-format runs during a manual readability pass.
- Edit F# manually. Do not install or run Fantomas or another F# formatter, including through scripts,
  hooks, format-on-save, or automatic build/format stages. Read the actual source and choose each edit;
  do not substitute blanket whitespace scripts, regex spacing passes, or generic reflow operations.
  Normal editing and patch tools are appropriate. Follow the existing UI formatting configuration.
- Choose architecture and patterns to solve the present problem. Useful generic abstractions and local
  refactoring are welcome; speculative frameworks, extension points, and compatibility layers are not.
  Preserve user changes and unrelated behavior.
- Fix nearby violations when that directly supports the task and remains reviewable. Report significant
  findings outside that scope. If a broad redesign or product decision is necessary, present the concrete
  problem, proposed boundary changes, tradeoffs, and incremental plan before expanding the work.

### Acceptance for manual readability and structural refactoring

- Apply the same criteria to handwritten C++ and F#: semantic blank lines, declaration separation,
  comprehensible branches, cohesive helper boundaries, and function/type/file organization. Review
  source files, headers, module units, tests, benchmarks, and examples. Tool success alone cannot
  justify leaving a file unchanged; actual source review may show that no material improvement is needed.
- Maintain a finite source inventory and record each file's review, confirmed findings, disposition,
  accountable owner, review evidence, and relevant verification. Exclude generated/vendor files only
  with ownership evidence. Resolve confirmed in-scope findings; a small selected batch does not establish
  repository-wide completion. Pending work must remain explicit in an incomplete checkpoint.
- Before delegating edits, produce and internally review representative manual improvements in both
  languages. Each assignment states exact files and ownership, concrete problems, an approved example,
  allowed edits, forbidden semantic changes, verification, and required return evidence. Use one writer
  per file and one integration owner; serialize builds and other operations sharing mutable outputs.
- Review every function edited by a delegated low-autonomy worker in its complete final form, not just
  its diff. Substantial structural changes require a different qualified reviewer. Preserve F# scopes,
  evaluation and effect order, actor ownership, allocation constraints, and C++ resource lifetimes.
  Ambiguous control-flow, ownership, or decomposition decisions return to the accountable design owner.
- Final acceptance includes reviewed coverage, real before/after examples from both languages,
  structural decisions, actual checks, and remaining exceptions. Confirm that no F# formatter was used.
  Formatting-only edits need no artificial tests; structural changes need checks for their actual risks.

## Keep contracts and generated code synchronized

- `Protocol/*.proto` owns wire messages and enum numbers. Change schemas and regenerate with
  `python Scripts/generate_protocol.py`; do not hand-edit generated protocol files or duplicate enum tables.
  Keep protobuf headers out of C++ module interfaces; see `docs/MsvcProtobufModulesRu.md`.
- Client and server ship together. Follow the existing protocol-version policy when changing the wire
  contract: update both codec versions, affected endpoints, tests, and documentation. Do not add legacy
  protocol fallbacks unless requested. Preserve reserved fields and numbers.
- Generate UI bridge/settings representations from their native owners using the procedure in
  `src/Dreamsleeve.Client.UI/README.ru.md`; do not patch generated TypeScript to bypass its source.
- Never edit an already deployed database migration. Add a migration and follow `db/README.md` to update
  the supported schema version and regenerate `Generated/AccountSchema.fs` when needed.
- Preserve server-confirmed application state and separate pending UI state. Do not introduce optimistic
  acceptance, automatic resend, or retry of state-changing operations without an explicit contract.

## Verify and finish the whole change

Use the smallest relevant checks first. Native builds require Windows x64/MSVC with C++23 modules and
xmake; managed projects target .NET 10. Consult `tests/README.md` for prerequisites and targeted suites.

| Change | Existing verification entry point |
| --- | --- |
| Managed behavior | `python Scripts/run_tests.py --suite managed` |
| Native behavior | `python Scripts/run_tests.py --suite native` |
| Both runtimes | `python Scripts/run_tests.py` |
| Targeted managed tests | `dotnet run --project tests/Dreamsleeve.Server.Tests -c Release -- --filter-test-list <name>` |
| UI behavior | From `src/Dreamsleeve.Client.UI`: `npm test` and `npm run build` |
| Browser interaction/layout | From the UI directory: `npm run test:browser` |
| Native formatting | `python Scripts/format_code.py --check <changed-paths>` |

- Managed tests are an Expecto executable: `dotnet test` does not replace their runner. Do not run stale
  binaries after a failed build or use `--no-build` without a successful build of the current sources.
- Add focused regression coverage for changed behavior or a bug. Test observable results, invariants,
  ownership, and meaningful failure paths. Do not test private layout, mirror the implementation, or add
  tests for formatting-only edits. Async tests use controlled delivery/gates and bounded waits, not timing luck.
- For contract or lifecycle changes, check the affected producer and consumer, late/stale results, and
  cleanup/overload behavior as applicable. Run smoke/load scenarios only when they address a concrete risk.
- Update existing documentation in the same change when behavior, contracts, ownership, configuration,
  generation, or commands change. Include affected example configurations and documentation links.
  Preserve each document's language; most project documentation is Russian. Correct relevant stale claims,
  and keep stable guidance here with detailed facts in their owning documents.
- Finish with a concise account of what changed, why, what actually ran, its result, and any unverified
  behavior or separate architectural follow-up. Missing tools are a reported limitation, not a passing check.
