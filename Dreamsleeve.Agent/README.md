# Dreamsleeve.Agent

`Dreamsleeve.Agent` is a small task-first agent library for F# built on top of `System.Threading.Channels`.

It provides two main building blocks:

- `Agent<'Message>` for message processing with no built-in state model.
- `StatefulAgent<'State, 'Command>` for agents that fully encapsulate mutable state and model a finite-state machine.

## Design goals

- `Task`-first API, no dependency on `MailboxProcessor`.
- Single-reader processing.
- Explicit bounded or unbounded mailbox choice.
- Request/reply support through `ReplyChannel<'T>`.
- Graceful shutdown through `Complete()`.
- Immediate shutdown through `Abort()`.
- Clear operational result types instead of exceptions for low-level API.

## Main semantics

### Posting

- `TryPost` is synchronous and never waits.
- `PostAsync` may wait for capacity when the mailbox is bounded with `BoundedChannelFullMode.Wait`.
- Once `Complete()` or `Abort()` is called, the agent is considered closed for new messages.

### Completion

- `Complete()` stops accepting new messages and drains the mailbox.
- `Abort()` stops the agent immediately and cancels the internal lifetime token.
- `Completion` completes when the agent actually stops.

### Request/reply

- `TryAskAsync` returns a discriminated union and is the preferred low-level API.
- `AskAsync` throws for non-successful outcomes.
- A request message **must eventually reply**, or the caller should always provide a timeout or cancellation token.

### Error handling

- `Agent<'Message>` uses `AgentOptions.OnError` to decide whether to continue or stop after a handler exception.
- `StatefulAgent<'State, 'Command>` uses `StatefulAgentOptions.OnUnhandled`.
- If no handler is configured, the default is to stop the agent.

## Base agent example

```fsharp
open System
open System.Threading.Tasks
open Dreamsleeve.Agent

type CounterMessage =
    | Increment
    | Add of int
    | Get of ReplyChannel<int>
    | Stop

let options =
    AgentOptions.create "counter"
    |> fun x ->
        { x with
            Mailbox = AgentMailbox.boundedWait 1024
        }

let counter =
    let mutable value = 0

    Agent.start options (fun ctx message ->
        task {
            match message with
            | Increment ->
                value <- value + 1
            | Add n ->
                value <- value + n
            | Get reply ->
                reply.Reply value
            | Stop ->
                ctx.Complete() |> ignore
        })
```

The example above is intentionally short, but there is one important caveat:
for long-lived state you usually want `StatefulAgent<'State, 'Command>` instead of storing mutable state in a closure manually.

## Stateful agent example

```fsharp
open System.Threading.Tasks
open Dreamsleeve.Agent

type CounterState =
    { Value: int }

type CounterCommand =
    | Increment
    | Add of int
    | Get of ReplyChannel<int>
    | Stop

let options =
    StatefulAgentOptions.create "counter"

let counter =
    StatefulAgent.start options { Value = 0 } (fun _ state command ->
        task {
            match command with
            | Increment ->
                return StatefulTransition.SetState { state with Value = state.Value + 1 }

            | Add n ->
                return StatefulTransition.SetState { state with Value = state.Value + n }

            | Get reply ->
                reply.Reply state.Value
                return StatefulTransition.Stay

            | Stop ->
                return StatefulTransition.Stop
        })
```

Reading a snapshot:

```fsharp
let! currentValue = counter.ReadAsync(fun state -> state.Value)
```

## Recommended usage patterns

### Use `Agent<'Message>` when

- the agent is infrastructure-oriented;
- the handler does not need an explicit state machine abstraction;
- you want a small task-first message loop.

### Use `StatefulAgent<'State, 'Command>` when

- the agent owns domain state;
- the agent models a finite-state machine;
- you want all reads and writes to go through the same mailbox.

## Important caveats

### 1. Keep handlers short

Agents are single-reader. A long handler delays every message behind it.

### 2. Avoid blocking waits

Do not call `.Result` or `.Wait()` inside handlers.
Use `task {}` and `let!` instead.

### 3. Prefer bounded mailboxes for domain agents

A bounded mailbox gives you backpressure instead of unbounded memory growth.

### 4. Use timeouts on `AskAsync` in operational code

A missing reply is a protocol bug. Timeouts make it visible.

### 5. Keep `ReadAsync` projections pure and fast

`ReadAsync` executes inside the agent and blocks the mailbox while the projection runs.

### 6. Do not expose state directly

`StatefulAgent` is designed around serialized mailbox access.
If state is exposed directly, the main safety property is lost.

## Suggested project structure

- `Dreamsleeve.Agent` — this library.
- `Dreamsleeve.Server` — concrete domain agents like `PartyAgent`, `GuildAgent`, `SessionAgent`.
- `Dreamsleeve.Server.Tests` — runtime and domain tests.

