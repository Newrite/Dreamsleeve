# Dreamsleeve.Agent

`Dreamsleeve.Agent` is a small task-first agent library for F# built on top of `System.Threading.Channels`.

It provides three main building blocks:

- `Agent<'Message>` for sequential message processing with no built-in state model.
- `StatefulAgent<'State, 'Command>` for agents with explicitly modeled state transitions.
- `MutableStatefulAgent<'State, 'Command>` for agents that encapsulate mutable state internally and mutate it in-place.

The library is designed for server-side and infrastructure code where agents own their mailbox, process messages with a single reader, and expose a clear API to callers.

---

## Design goals

- `Task`-first API, no dependency on `MailboxProcessor`.
- Single-reader processing model.
- Explicit bounded or unbounded mailbox choice.
- Request/reply support through `ReplyChannel<'T>`.
- Graceful shutdown through `Complete()`.
- Immediate shutdown through `Abort()`.
- Clear operational result types for low-level APIs.
- Support for both immutable and mutable internal state models.

---

## Main building blocks

### `Agent<'Message>`

A low-level sequential message processor.

Use it when:

- you want a mailbox and a task-based processing loop;
- state is simple or infrastructure-oriented;
- you want full control over how state is stored and updated.

This is the most flexible primitive in the library.

---

### `StatefulAgent<'State, 'Command>`

A stateful agent that owns its state and models transitions explicitly.

The command handler receives:

- the agent context;
- the current state;
- the command.

It returns a `StatefulTransition<'State>`:

- `Stay`
- `SetState of 'State`
- `Stop`
- `StopWithState of 'State`

Use it when:

- the agent models a domain FSM;
- you want explicit state transitions;
- you want reads and writes to go through the same mailbox;
- reasoning and testing of state changes matter more than minimizing allocations.

Typical fit:

- `PartyAgent`
- `GuildAgent`
- `ModerationAgent`
- `SocialGraphAgent`

---

### `MutableStatefulAgent<'State, 'Command>`

A stateful agent that owns a mutable state object and allows in-place mutation inside the single-reader loop.

The command handler receives:

- the agent context;
- the mutable state instance;
- the command.

It returns a `MutableStatefulTransition`:

- `Stay`
- `Stop`

Use it when:

- state is large;
- updates are frequent;
- you want to avoid allocating a new state value on every transition;
- the state is purely internal and never exposed directly.

Typical fit:

- transport/session statistics
- rate limiter buckets
- hot-path counters
- mutable lookup caches
- large technical state maps

Important: the mutable state must remain fully encapsulated inside the agent. Do not expose internal mutable references directly.

---

## Main semantics

### Posting

- `TryPost` is synchronous and never waits.
- `PostAsync` may wait for capacity when the mailbox is bounded with `BoundedChannelFullMode.Wait`.
- Once `Complete()` or `Abort()` is called, the agent is closed for new messages.

Low-level post operations return `AgentPostResult`:

- `Posted`
- `Full`
- `Closed`
- `Canceled`

---

### Completion

- `Complete()` stops accepting new messages and drains the mailbox.
- `Abort()` stops the agent immediately and cancels the internal lifetime token.
- `Completion` completes when the agent actually stops.

Stop reasons are represented by `AgentStopReason`:

- `Completed`
- `Aborted`
- `Faulted of exn`

---

### Request/reply

- `ReplyChannel<'T>` is the one-shot reply primitive.
- `TryAskAsync` returns `AgentAskResult<'T>` and is the preferred low-level API.
- `AskAsync` throws for non-successful outcomes.

`AgentAskResult<'T>` can be:

- `Replied of 'T`
- `Faulted of exn`
- `Full`
- `Closed`
- `TimedOut`
- `Canceled`

A request message must eventually reply, or the caller should provide a timeout or cancellation token.

---

### Error handling

#### Base `Agent<'Message>`
`Agent<'Message>` uses `AgentOptions.OnError` to decide what happens after an unhandled handler exception:

- `Continue`
- `Stop`

If no error callback is configured, the default is to stop the agent.

#### `StatefulAgent<'State, 'Command>`
`StatefulAgent<'State, 'Command>` uses `StatefulAgentOptions.OnUnhandled`.

It can decide to:

- `KeepStateAndContinue`
- `ReplaceStateAndContinue of 'State`
- `Stop`
- `StopWithState of 'State`

#### `MutableStatefulAgent<'State, 'Command>`
`MutableStatefulAgent<'State, 'Command>` uses `MutableStatefulAgentOptions.OnUnhandled`.

It can decide to:

- `Continue`
- `Stop`

Since state is mutated in-place, there is no “replace state” path in the mutable variant.

---

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
            Mailbox = AgentMailbox.boundedWait 1024 }

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
````

This is a valid pattern for small infrastructure agents, but for long-lived domain state prefer either `StatefulAgent` or `MutableStatefulAgent`.

---

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

Reading a safe snapshot:

```fsharp
let! currentValue = counter.ReadAsync(fun state -> state.Value)
```

This model is best when explicit state evolution matters.

---

## Mutable stateful agent example

```fsharp
open System.Threading.Tasks
open Dreamsleeve.Agent

type CounterState() =
    member val Value = 0 with get, set

type CounterCommand =
    | Increment
    | Add of int
    | Get of ReplyChannel<int>
    | Stop

let options =
    MutableStatefulAgentOptions.create "counter"

let counter =
    MutableStatefulAgent.start options (CounterState()) (fun _ state command ->
        task {
            match command with
            | Increment ->
                state.Value <- state.Value + 1
                return MutableStatefulTransition.Stay

            | Add n ->
                state.Value <- state.Value + n
                return MutableStatefulTransition.Stay

            | Get reply ->
                reply.Reply state.Value
                return MutableStatefulTransition.Stay

            | Stop ->
                return MutableStatefulTransition.Stop
        })
```

Reading a safe projection:

```fsharp
let! currentValue = counter.ReadAsync(fun state -> state.Value)
```

This model is best when in-place mutation is desirable and the state never escapes the agent.

---

## Recommended usage patterns

### Use `Agent<'Message>` when

* the agent is infrastructure-oriented;
* state is trivial or not modeled explicitly;
* you want a small task-first message loop;
* you want to build your own protocol over messages.

### Use `StatefulAgent<'State, 'Command>` when

* the agent owns domain state;
* the agent models a finite-state machine;
* you want explicit state transitions;
* you want easier reasoning and testing.

### Use `MutableStatefulAgent<'State, 'Command>` when

* the state is large or hot-path heavy;
* you want to mutate in-place;
* you do not want per-transition state allocations;
* the state can stay fully encapsulated.

---

## Important caveats

### 1. Keep handlers short

Agents are single-reader.
A long-running handler blocks every message behind it.

---

### 2. Avoid blocking waits

Do not call `.Result` or `.Wait()` inside handlers.

Use `task {}` and `let!` instead.

---

### 3. Prefer bounded mailboxes for domain agents

A bounded mailbox provides backpressure instead of unbounded memory growth.

---

### 4. Use timeouts on `AskAsync` in operational code

A missing reply is a protocol bug.
Timeouts make it visible.

---

### 5. Keep `ReadAsync` projections pure and fast

`ReadAsync` runs inside the agent loop and blocks the mailbox while the projection executes.

---

### 6. Do not expose internal state directly

This is especially important for `MutableStatefulAgent`.

Do not return live mutable collections or state objects that can be modified from outside the agent.

Prefer returning:

* primitive values;
* immutable snapshots;
* copied arrays/lists/maps;
* derived projections.

Good:

```fsharp
let! users = agent.ReadAsync(fun state -> state.Users |> Seq.toArray)
```

Bad:

```fsharp
let! users = agent.ReadAsync(fun state -> state.Users)
```

if `state.Users` is an internal mutable collection.

---

### 7. Do not assume `QueueLength` is exact

`QueueLength` is an operational approximation suitable for diagnostics and monitoring.
It should not be used as part of business logic.

---

### 8. `OnTransition` should be lightweight

For `StatefulAgent`, `OnTransition` runs inline with agent processing.
Do not do heavy I/O or long-running work there.

---

### 9. Error callbacks should be defensive

`OnError` and `OnUnhandled` are intended for policy decisions.
If they throw, the library falls back to stopping the agent.

---

## Suggested project structure

* `Dreamsleeve.Agent` — this library.
* `Dreamsleeve.Server` — concrete domain agents such as `PartyAgent`, `GuildAgent`, `SessionAgent`.
* `Dreamsleeve.Server.Tests` — runtime and domain tests.

---

## Summary

Use:

* `Agent<'Message>` for low-level or infrastructure-oriented agents.
* `StatefulAgent<'State, 'Command>` for explicit immutable-style state transitions.
* `MutableStatefulAgent<'State, 'Command>` for fully encapsulated in-place mutable state.

All three share the same task-first, single-reader, channel-based execution model.
