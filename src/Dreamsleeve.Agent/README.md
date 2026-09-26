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
