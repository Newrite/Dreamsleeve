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

    use agent =
        Agent<Command>.Start(options, fun _ (Echo (text, reply)) -> task {
            reply.Reply text
        })

    let! text = agent.AskAsync(fun reply -> Echo ("hello", reply))
    printfn "%s" text
    agent.Complete() |> ignore
    do! agent.Completion
}
```

`TryPost` attempts immediate admission. `PostAsync` waits for space on a bounded `Wait` mailbox and returns `AgentPostResult`. `Posted` means admitted, not processed. `TryAskAsync` returns `AgentAskResult<'Reply>`; `AskAsync` turns unsuccessful outcomes into exceptions. Both stateful variants also support command/reply and queued state projections through `TryReadAsync` / `ReadAsync`.

## Contracts to know

- **Backpressure:** prefer `boundedWait` for chat, guild commands and request/reply. Await or limit pending posts; a capacity limit does not bound an unlimited number of waiting producers.
- **Loss:** drop modes may discard incoming or previously queued items. `Dropped` and `DroppedCount` make this visible; a previously successful post can still be evicted later. Drop policies operate on the whole queue, not separately per player.
- **Deadlines:** Ask/Read timeout includes admission and waiting for the reply. Zero times out without building a command. Timeout or caller cancellation does not roll back an accepted command or cancel its handler.
- **Ownership:** a projection executes inside the agent, but returning a live dictionary, lazy sequence, mutable array or mutable nested object can leak shared state. Materialize independent snapshots inside the projection.
- **Shutdown:** `Complete()` closes admission and drains queued work. `Abort()` requests cooperative cancellation and discards remaining queued work. Await `Completion` to observe actual termination.
- **Errors:** the default handler error policy stops with `Faulted`. A failed current Ask receives `Faulted` even if the chosen policy continues. State-wrapper `OnUnhandled` overrides the base `OnError`; otherwise the base policy is used.
- **Completion:** successful for `Completed`, canceled for `Aborted`, faulted for `Faulted`. It is published after cleanup and stop callbacks. `IDisposable.Dispose` requests abort; `IAsyncDisposable.DisposeAsync` completes gracefully and awaits termination.
- **Deadlocks:** do not await your own Ask/Read, your own full bounded `PostAsync`, your own completion from a lifecycle callback, or a cycle of agents that await each other.
- **Start:** `Start` begins immediately. Register `OnStarted` in options when startup must be observed; `Started` is not replayed to late subscribers.

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

From a base Agent handler, call `context.PipeToSelf(operation, toMessage)` to start
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

The owner observes `child.Completion`, for example through PipeToSelf and a child
termination message. Faults preserve the original exception; observation also works
after the child has stopped. Restart means creating a new agent after Completion
and updating consumer addresses. The library does not automatically restart agents,
restore state or replay accepted commands. Stopped events are not replayed for late
subscribers; OnStopped in options is installed before startup but runs before
Completion settles.

## Bounded ordered sends

Construct one `AgentOutbox(capacity, destination)` per independently progressing
route. From the owner handler, call `TrySend(context, message, onFailure)`.
False means the local limit is full: the message was not scheduled. True means
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
including observing child Completion; ordinary sends no longer require it.

## Request/reply handlers

`AgentOutbox.createHandler capacity output execute` creates an ordered request/reply
handler. `AgentReplyDispatcher.createHandler capacity replyTo execute` uses independent
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
