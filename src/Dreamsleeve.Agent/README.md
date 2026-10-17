# Dreamsleeve.Agent

Sequential, in-process F# agents built on `System.Threading.Channels` and `Task`. Targets **.NET 10+**; no external runtime packages.

**[Interactive guide — English / Русский](docs/index.html)** · **[Русский README](README.ru.md)** · [Source](Agent.fs) · [Runnable examples](examples)

Open `docs/index.html` directly in a browser. It is a self-contained, responsive page with a language switch, section search, light/dark themes and copy buttons. It requires no server, build step, CDN or network connection.

## Choose the owner of your work

| Type | Model | Good starting points |
|---|---|---|
| `Agent<'Message>` | Sequential asynchronous effects; your handler defines the protocol. | Outbound work, persistence requests, coordinating a bounded stream of jobs. |
| `StatefulAgent<'State,'Command>` | A command returns `Stay`, `SetState`, or a graceful stop transition. | Party/guild rules, session lifecycle, small immutable domain state. |
| `MutableStatefulAgent<'State,'Command>` | A command mutates one privately owned state instance. | Presence dictionaries, indexes, counters with frequent updates. |

All three serialize handlers, including asynchronous work inside each handler. They do not own a dedicated thread. Mutable state remains safe only while every alias is confined to the agent; return snapshots from queries.

## Build and run

From the repository root, with a .NET 10 SDK:

```sh
dotnet build src/Dreamsleeve.Agent/Dreamsleeve.Agent.fsproj -c Release
dotnet run --project src/Dreamsleeve.Agent/examples/Dreamsleeve.Agent.Examples.fsproj -c Release
dotnet run --project tests/Dreamsleeve.Server.Tests -c Release -- --filter-test-list Dreamsleeve.Agent
```

The remaining library checks are the `Background`, `Outbox`, `AsyncDispatcher`, `Admission`, `Lifetimes` and `AgentTicker` lists of the same project.

The example program runs a base outbound worker, immutable party state, and mutable presence snapshots. It produces deterministic output and shuts down all agents. Source files: [Outbound.fs](examples/Outbound.fs), [Party.fs](examples/Party.fs), [Presence.fs](examples/Presence.fs).

## Minimal request/reply

```fsharp
open System
open Dreamsleeve.Agent

type Command = Echo of string * ReplyChannel<string>

let example () = task {
    let options =
        { AgentOptions.create "echo" with
            Mailbox = AgentMailbox.boundedWait 64
            DefaultAskTimeout = Some (TimeSpan.FromSeconds 2.) }
    match Agent<Command>.TryStart(options, fun _ (Echo (text, reply)) -> task { reply.Reply text }) with
    | Error error -> eprintfn "Startup rejected: %A" error
    | Ok owner ->
        use agent = owner
        let! result = agent.TryAskAsync(fun reply -> Echo ("hello", reply))
        match result with
        | AgentAskResult.Replied text -> printfn "%s" text
        | (AgentAskResult.Full | AgentAskResult.Dropped | AgentAskResult.Closed | AgentAskResult.TimedOut | AgentAskResult.Canceled | AgentAskResult.Faulted _ | AgentAskResult.InvalidRequest _) as failure ->
            eprintfn "Request not confirmed: %A; do not retry automatically" failure
        agent.Complete() |> ignore
        do! agent.Completion
}
```

`TryPost` attempts immediate admission. `PostAsync` waits for space on a bounded `Wait` mailbox and returns `AgentPostResult`. `Posted` means admitted, not processed. `TryAskAsync` returns `AgentAskResult<'Reply>`. Both stateful variants also support queued state projections through `TryReadAsync`. The throwing `AskAsync` / `ReadAsync` methods and `askAsync` / `readAsync` pipeline helpers have been removed; migrate callers to `TryAskAsync` / `TryReadAsync` (or `tryAskAsync` / `tryReadAsync`) and handle every outcome explicitly. Handler faults remain `Faulted` with their original exception; `Completion` still reports lifecycle faults.

## Checked construction and reliable owners

Raw construction uses `Agent.TryStart`, `StatefulAgent.TryStart`, or `MutableStatefulAgent.TryStart`, returning `Result<_, AgentStartError>`. Invalid capacities, unsupported timeout values, missing control classifiers, null required delegates and unknown mailbox modes are rejected before resources or callbacks start. The options are copied at this boundary. There is no throwing `Start` compatibility factory.

`TryPrepare(options, handler)` validates once and returns a plan whose `Start()` begins processing. For owners that must check derived budgets before creating dependent workers, `Agent<'Message>.TryCheckReliable` returns a reusable checked configuration. Its `Start(localHandler)` is a trusted path for an authored handler already known nonnull; external/raw handlers use `TryStartReliable` or `TryPrepareReliable`.

`TryStartReliable` rejects dropping mailboxes and supplies `ReliableAgentContext`. Its `ReliableAgent` facade delegates to one underlying owner and exposes a direct `ReliableAgentRef` through `Ref`. Background work, ownership, watching, outboxes, dispatchers, reply scopes and tickers require this capability. General dropping owners keep `AgentContext`; `TryReliable()` can optionally convert a compatible general address/context when wiring a boundary.

Raw helper factories use `tryCreate`/`TryCreate`, `tryCreateHandler`, `tryStart`, or `tryStartWithTimeProvider` and preserve `AgentStartError`. Trusted helper paths consume `AgentDeliveryCapacity` or `AgentTickerInterval` checked tokens. Delivery capacity must be positive, without an arbitrary library cap. Timer intervals must truncate to 1..4294967294 milliseconds, matching `PeriodicTimer`.

An invalid Ask/Read timeout, null builder or null projection returns `InvalidRequest of AgentRequestError` before a command is built or admitted. Builder/projection exceptions remain `Faulted` with the original cause. `TryReplyError` returns `Result<bool, AgentRequestError>`; a null error is rejected without settling the reply, while `Ok false` means another settlement already won. `ReplyError` returns `Result<unit, AgentRequestError>`.

## Contracts to know

- **Backpressure:** prefer `boundedWait` for chat, guild commands and request/reply. Await or limit pending posts; a capacity limit does not bound an unlimited number of waiting producers.
- **Loss:** drop modes may discard incoming or previously queued items. `Dropped` and `DroppedCount` make this visible; a previously successful post can still be evicted later. Drop policies operate on the whole queue, not separately per player.
- **Deadlines:** Ask/Read timeout includes admission and waiting for the reply. Zero times out without building a command. Timeout or caller cancellation does not roll back an accepted command or cancel its handler.
- **Ownership:** a projection executes inside the agent, but returning a live dictionary, lazy sequence, mutable array or mutable nested object can leak shared state. Materialize independent snapshots inside the projection.
- **Shutdown:** `Complete()` closes admission and drains queued work. `Abort()` requests cooperative cancellation and discards remaining queued work. Await `Completion` to observe actual termination.
- **Errors:** the default handler error policy stops with `Faulted`. A failed current Ask receives `Faulted` even if the chosen policy continues. State-wrapper `OnUnhandled` overrides the base `OnError`; otherwise the base policy is used.
- **Completion:** joins owned work and all cleanup/stop notification attempts. With no lifecycle faults it succeeds for `Completed` and is canceled for `Aborted`; otherwise it retains the original and secondary exceptions. `StopReason` is sealed before stop notifications, so a failing stop callback faults Completion without changing the reason already delivered to observers. `IDisposable.Dispose` requests abort; `IAsyncDisposable.DisposeAsync` completes gracefully and awaits termination.
- **Deadlocks:** do not await your own Ask/Read, your own full bounded `PostAsync`, your own completion from a lifecycle callback, or a cycle of agents that await each other.
- **Start:** successful `TryStart` begins immediately; preparing a plan does not. Register `OnStarted` in options when startup must be observed; `Started` is not replayed to late subscribers.

The [full guide](docs/index.html) includes result tables, lifecycle and error-policy details, migration notes, and guidance for a Dreamsleeve server. This module has no automatic restarts, supervisor tree, durable delivery, keyed coalescing or distributed actor protocol.

## Updating the web guide

`docs/build.py` is the page source. It contains the RU/EN text and embeds the current `.fs` examples, keeping the guide synchronized with runnable code. Regenerate it with standard Python 3:

```sh
python src/Dreamsleeve.Agent/docs/build.py
```

The generated `docs/index.html` needs neither Python nor a server to open. Keep the bundled directory layout, including `examples`, when regenerating it.

Run all suites: `python Scripts/run_tests.py`. [Test organization](../../tests/README.md).

## Message addresses and background operations

`agent.Ref` / `context.Ref` expose send-only AgentRef addresses. `Ref.Map(Constructor)`
narrows an address to part of the receiver's protocol. Stateful wrappers expose
command addresses too. IsNonDropping describes mailbox policy, not processing.

From a reliable Agent handler, call `context.PipeToSelf(operation, toMessage)` to start
tracked work and post its Result<_, exn> as a new message. Both functions run outside
the mailbox: capture immutable inputs, never mutate actor-owned state. Use a
non-dropping mailbox; bound outstanding work in the owning component. Abort/fault
cancels work; Completion joins it before disposing lifetime resources. Operation
errors become messages; mapping failures fault the agent, including during shutdown.

Complete closes admission immediately, including background replies. To drain
results, use an application Stop message and call context.Complete after accepted
work and replies finish.
Cancellation is cooperative; an operation ignoring it can delay Completion.

For protocols requiring admission without dropping, call `Ref.TryReliable()`
when wiring addresses. Some contains a `ReliableAgentRef`; None means an incompatible
mailbox policy. `ReliableAgentRef.Map` preserves the guarantee. PostAsync returns
only Posted/Closed/Canceled, asynchronously waiting when the mailbox is full.
Shutdown can still discard unprocessed messages: Posted only acknowledges admission.

The owner observes `child.Completion` through `context.Own(child, stopped)` or
`context.Watch(target, stopped)` and receives a termination message. Faults preserve the original exception; observation also works
after the child has stopped. Restart means creating a new agent after Completion
and updating consumer addresses; `AgentSupervisor` (below) does it by policy. The library
does not restore state or replay accepted commands. Stopped events are not replayed for late
subscribers; OnStopped in options is installed before startup but runs before
Completion settles.

## Bounded ordered sends

Construct one `AgentOutbox.TryCreate(capacity, destination)` per independently progressing
route. From the owner handler, call `TrySend(context, message, onFailure)`.
False means the local limit is full, the owner stopped, or scheduling failed: the message was not scheduled. True means
accepted by the outbox, not delivered or processed by the destination. The limit
includes queued messages, the current admission, and any pending failure notification.
Only one actual admission runs at a time; ordering is FIFO.

The library advances the queue and releases capacity without successful admission
messages, Pump, Acknowledge, or public receipts. Only AgentSendFailure
(Closed, Canceled, Faulted) enters the owner's mailbox through onFailure. The mapper
runs outside the handler and must only construct a message from immutable inputs.
The overload without onFailure aborts the owner for an unavailable destination and
faults it for an exception. Abort cancellation does not produce failure messages.

Complete drains accepted sends automatically, even after mailbox admission closes.
Abort cancels queued/waiting sends and joins workers. If a failure can no longer enter
its owner mailbox during Complete, shutdown escalates to cancellation or the original
exception instead of silently succeeding. Completed termination is sealed only after
tracked work has finished. For a terminal failure that must first flush notifications,
call `AbortAfterDrain(context)` once and do not schedule further sends on that route.

Outboxes use the same internal delivery window as the request/reply handlers below.
SemaphoreSlim protects only library delivery reservations; domain state remains in
the sequential owner handler. Use non-dropping owner mailboxes. Outboxes do not retry
commands and do not guarantee processing, deduplication or exactly-once effects.
PipeToSelf remains available for background operations whose result the owner needs,
while child observation uses Own/Watch; ordinary sends no longer require it.

A ready outbox send uses synchronous admission when earlier sends have finished.
While waiting for space it takes the bounded tracked path; the mapper runs once.
A finished send means admission, not processing by the destination.

## Request/reply handlers

`AgentOutbox.createHandler checkedCapacity output execute` creates an ordered request/reply
handler. `AgentReplyDispatcher.createHandler checkedCapacity replyTo execute` uses independent
per-request destinations. Independent replies may arrive out of order: correlate them
by operation ID. Construct each handler once per owner.

Both reserve capacity BEFORE calling execute in the owner handler. At saturation,
further execution waits; a bounded mailbox supplies upstream backpressure. Delivery
workers release capacity without requiring an owner mailbox round trip. A slow caller
uses part of the independent delivery limit; if all slots are occupied, later commands
wait too. This is a global limit, not a per-caller quota.

Complete joins deliveries; Abort cancels capacity waits and delivery workers. A closed
sole ordered output aborts its owner; a closed independent recipient does not stop
other callers or roll back a write. Delivery exceptions fault the owner and remain
observable through Completion. A live recipient that never drains can delay graceful
completion; the lifecycle owner can Abort.

### Background request execution

`AgentReplyDispatcher.createAsyncHandler checkedCapacity replyTo execute` accepts
`execute: CancellationToken -> Request -> Task<Reply>`. It reserves one slot before
starting work through the existing tracked background worker. Even execute's
synchronous prefix runs outside the mailbox handler; no additional Task.Run is needed
around a synchronous dependency call. The slot covers both operation execution and
pending reply admission. Capacity wait is asynchronous and released by workers, so it
does not require a completion message to pass through the saturated owner mailbox.

Pass immutable requests and dependencies safe for concurrent use. Execute may run
concurrently up to the shared capacity; it must not capture mutable owner state or
wait for work that only this saturated owner can process. `replyTo` still runs in the
owner and should only select an address. Use the synchronous createHandler when
execute must update state confined to the owner; its behavior is unchanged.

Complete drains accepted mailbox requests, then joins their work and reply admissions.
Abort requests cooperative cancellation and waits for tracked cleanup; it cannot
interrupt a synchronous call that ignores its token. Unhandled operation or delivery
exceptions fault Completion. Closing a requester releases its reply slot after work
finishes, without rolling back effects or stopping other callers. A slow requester
continues to occupy its slot; this limit is global, not a per-caller quota, timeout,
or dedicated thread pool.

Both dispatcher constructors allow Request to differ from the owner's message type:
a handler for a larger DU can call `dispatch context request` in one branch and
handle other commands normally. The asynchronous constructor does not receive the
owner's state or context inside execute. For results that must change owner state,
use an explicit response message or PipeToSelf instead.

## Child ownership and shared dependencies

Call `context.Own(child, stopped)` once from the owning handler. The mapper creates
an owner message from `Result<unit, exn>` after the child's actual Completion,
including for an already stopped child. Parent Abort/fault aborts the child and joins
its cleanup; cooperative cleanup may delay the parent. Graceful child Stop remains
application-specific: stop children first, then Complete the parent. Own does not
restart children or recover their state; `AgentSupervisor` restarts. A delivered child failure preserves all causes (singleton original, ordered AggregateException for multiple); when shutdown or failed notification prevents delivery, Own retains every original cause in the parent Completion. Only actual child cancellation caused by parent cancellation is consumed.

`context.Watch(target, stopped)` observes a shared dependency without owning it.
The `Watch(completion: Task, stopped)` overload provides the same observation when
a boundary exposes only its lifetime task; the Agent overload delegates to it.
Complete and Abort detach the observation without stopping the target or waiting
for it to terminate. Mappers run outside the handler and must only construct messages.
Both APIs require a non-dropping owner mailbox.

## Forwarded reply ownership

Create `AgentReplyScope.create context target checkedCapacity closedReply busyReply` once per
route. `scope.Forward(reply, send)` bounds unfinished reply channels and schedules a
forward through the supplied synchronous send function. The function runs outside
the library lock, must return false when refused, and can use an outbox to preserve
ordering with other target commands. False settles closedReply; a thrown exception
settles the request with that original error, releases the scope reservation, and faults the owner's lifetime.

Scope closure is automatic on target termination or owner shutdown, including when
the owner mailbox is busy. `scope.Close()` also closes a domain route immediately,
for example on disconnect. Closed scopes reject later forwarding with closedReply;
a full scope returns busyReply. Settled/canceled channels are reclaimed on the next
Forward, and closure clears all retained channels. Duplicate forwarding of the same
pending channel does not schedule a second command. ReplyChannel's first-result rule
protects a successful reply from concurrent closure and rejects late replies.

A short library lock coordinates reservation and settlement with termination; send
never runs under it. Application data stays in its handler. Closing waits does not
retract admitted commands and the scope never stops the target. Result mapping,
route identity, request semantics and graceful domain cleanup remain application code.

## Admission with reserved control capacity

`AgentMailbox.boundedWithControl ordinaryCapacity controlReserve` creates one FIFO
with total capacity `ordinaryCapacity + controlReserve`. Pass a named pure classifier
as `Agent.TryStartReliable(options, handler, isControl = isControlMessage)`. Classification runs
on the posting caller, including Map and Ask, and must not access receiver-owned state.
Existing mailbox variants and Start calls are unchanged.

Ordinary messages occupy at most ordinaryCapacity queued slots. Control messages can
use the total capacity but never overtake already accepted messages. The reserve
protects admission from ordinary traffic; it is not priority processing. Both limits
exclude the current handler. Dequeue releases capacity before the handler finishes,
including when that handler subsequently faults.

`ReliableAgentRef.TryPost` returns `AgentTryDeliveryResult.Posted/Full/Closed`.
`PostAsync` waits for the appropriate capacity; a canceled waiter reserves nothing.
Complete and Abort close admission and wake waiting senders. Cancellation after Posted
does not retract a message. Mapped addresses and Ask use the same classifier and FIFO.

Control traffic remains bounded. This mailbox reserve does not reserve capacity in
an outgoing AgentOutbox: its owner must separately bound ordinary pending operations,
leave slots for Stop/Detach in the same ordered outbox, and stop ordinary admission
before shutdown. Independent concurrent writers are ordered by actual admission,
not by when their PostAsync calls started. The shared admission notification TCS is
created only when writers actually wait.

## Periodic ticker

`AgentTicker.start checkedInterval context toMessage` creates one owned PeriodicTimer.
The owner calls `Acknowledge()` after handling a tick; until then further periods
are coalesced, and missed periods are skipped rather than caught up in a burst. The
callback only builds a message and never reads the agent's mutable state. It needs a
non-dropping mailbox. Complete/Abort stop the worker. `startWithTimeProvider` tests the
schedule without wall-clock sleeps. AgentTick timestamps come from the TimeProvider.

## Supervisor

`AgentSupervisor.tryStart name policy start observe` keeps one child and restarts it by a
`RestartPolicy`. The supervisor is an agent itself: the child's start, its termination and the
restart delays reach it as messages. `start` returns a `SupervisedChild`: the value consumers
use, `Completion` (the full stop including everything the child owns) and `Stop` (the graceful
stop). Every termination the supervisor did not ask for (fault, Abort, even a graceful one) is a
failure, and so is an exception from `start`. The restart delay is `InitialDelay`, doubled for
each further failure within `Window`, capped at `MaxDelay`. More than `MaxRestarts` failures
within the window and the supervisor gives up: `Completion` faults with
`SupervisorGaveUpException`, the last failure inside. `MaxRestarts = 0` never restarts.

`Current` is the running child, or nothing while it starts or restarts; consumers read it at
the moment of use instead of keeping a reference. `StopAsync()` stops the child gracefully and
ends supervision: a pending restart is canceled, and a child still starting is stopped as soon
as it starts. `observe` receives the events (`Started`, `StartFailed`, `Stopped`, `Restarting`,
`GaveUp`) on the supervisor's handler: quick work such as logging only; observer failures stop and fault supervision.
The supervisor neither restores the child's state nor replays its accepted commands.
`startWithTimeProvider` lets tests drive the failure window without waiting.


The child-start delegate returns `Task<Result<SupervisedChild<'Child>, 'StartError>>`. `SupervisorEvent<'Child,'StartError>.StartRejected` preserves an expected construction refusal; `StartFailed` preserves an unexpected thrown exception. Both use the same existing whole-child reconstruction policy. At exhaustion, `SupervisorGaveUpException<'StartError>.Failure` retains `SupervisorFailure.StartRejected reason`, `Faulted originalException`, or `CompletedUnexpectedly`; only an actual fault supplies `InnerException`. A rejected factory must release and join partially acquired resources before returning its typed error. This reconstruction policy never retries an individual admitted operation.

Lifecycle notifications and cancellation callbacks belong to the owner. Their unexpected faults close admission and stop dispatch; an error notification fault overrides a handler policy that returns Continue. State replacement commits before OnTransition; if that notification faults, the committed owner is stopped rather than passed to recovery or reused for another command. Separate event/configured notifications are attempted independently; standard event multicast stops at its first throwing subscriber. Completion retains each original failure once by reference, including cancellation/cleanup failures, after pending requests and queue reservations have been released.

Supervisor observer failures stop supervision and join any acquired child; they fault Completion without reconstructing another child. Concurrent StopAsync callers join the same child Stop and actual Completion. Both are attempted independently, retaining every original fault. StopAsync consumes only a sole prior exhaustion of this supervisor; it propagates additional cleanup or observer faults. Awaiting StopAsync exposes one exception, so Completion is the authoritative full lifetime result: inspect its Exception.InnerExceptions to retain all causes. Once the current child termination result is handled by the supervisor, its outcome belongs to the established reconstruction policy. A delayed ownership waiter does not stop that child again or register its handled fault as a separate cleanup cause. Unhandled child outcomes and actual Stop or observer faults remain part of Completion; no individual operation is retried.
