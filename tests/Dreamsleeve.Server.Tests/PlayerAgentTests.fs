module Dreamsleeve.Server.Tests.PlayerAgentTests

open System
open System.Threading.Channels
open System.Threading.Tasks
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Expecto
open AgentTests
open BackgroundTests

let private ok = function Ok value -> value | Error error -> failwithf "%A" error

let private request () : SessionOpenRequest = {
    ConnectionId = Guid.NewGuid()
    RequestId = 1UL
    Username = Username.create 32 "player" |> ok
    DisplayName = DisplayName.create 64 "Player" |> ok
}

let private collect (output: Channel<'T>) (_: AgentContext<'T>) value = task {
    check (output.Writer.TryWrite value) "Test channel closed."
}

let private receive (output: Channel<'T>) = output.Reader.ReadAsync().AsTask().WaitAsync guard

let private post (agent: Agent<PlayerMessage>) message = task {
    let! result = agent.PostAsync message
    equal AgentPostResult.Posted result
}

let private read (agent: Agent<PlayerMessage>) = task {
    let! result = agent.TryAskAsync PlayerMessage.Read |> awaitResult
    match result with
    | AgentAskResult.Replied value -> return value
    | other -> return failwithf "Expected player state reply: %A" other
}

let private resolve (pending: ProfileRequest) result = task {
    let! posted = pending.ReplyTo.PostAsync { OperationId = pending.OperationId; Result = result }
    equal AgentDeliveryResult.Posted posted
}

let private profile (request: SessionOpenRequest) =
    PlayerData.create (PlayerId.create 42UL |> ok) request.Username request.DisplayName

let private withPlayer run = task {
    let queries = Channel.CreateUnbounded<ProfileRequest>()
    let events = Channel.CreateUnbounded<PlayerEvent>()
    use profiles = Agent.Start(AgentOptions.create "controlled-profiles", collect queries)
    use receiver = Agent.Start(AgentOptions.create "player-events", collect events)
    let opening = request ()
    use player = PlayerAgent.start 4 4 opening (profiles.Ref.TryReliable().Value) (receiver.Ref.TryReliable().Value) |> ok

    do! run opening player queries events

    if not player.Completion.IsCompleted then player.Abort()
    let! _ = terminal player.Completion
    profiles.Complete() |> ignore
    receiver.Complete() |> ignore
    do! awaitUnit profiles.Completion
    do! awaitUnit receiver.Completion
}

let tests = testList "PlayerAgent" [
    case "player owns telemetry and publishes detached snapshots across character changes" (fun () ->
        withPlayer (fun opening player queries events -> task {
            let! pending = receive queries
            equal (ProfileCommand.GetOrCreate(opening.Username, opening.DisplayName)) pending.Command
            let! before = read player
            equal (Error PlayerStateError.NotReady) before

            let data = profile opening
            do! resolve pending (Ok (ProfileOutcome.Resolved data))
            let! join = receive events
            equal (PlayerEvent.Join data) join
            do! post player PlayerMessage.Joined

            let name = CharacterName.create 128 "Nerevar" |> ok
            let key = ActorValueKey.create 128 "skyrim:health" |> ok
            let health value =
                ActorValueInfo.create (ActorValueName.create 64 "Health" |> ok)
                    (ActorValueState.resource value 100.0f |> ok)
            let form = FormKey.create (PluginName.create 255 "Skyrim.esm" |> ok) (LocalFormId.create 0x3Cu |> ok)
            let location = PlayerLocation.create
                               (Location.create form (LocationName.create 128 "Whiterun" |> ok))
                               (Position.create 1.0f 2.0f 3.0f |> ok) Rotation.zero

            do! post player (PlayerMessage.Update(PlayerUpdate.BeginCharacter name))
            do! post player (PlayerMessage.Update(PlayerUpdate.SetLocation location))
            do! post player (PlayerMessage.Update(PlayerUpdate.SetActorValues [key, health 80.0f]))
            let! first = read player
            let first = ok first
            equal data first.Data
            equal (ValueSome name) first.CharacterName
            equal (ValueSome location) first.Location

            do! post player (PlayerMessage.Update(PlayerUpdate.SetActorValues [key, health 20.0f]))
            let! second = read player
            equal (health 20.0f) (ok second).ActorValues[key]
            equal (health 80.0f) first.ActorValues[key]

            // Beginning a different save with the same name still clears old telemetry.
            do! post player (PlayerMessage.Update(PlayerUpdate.BeginCharacter name))
            let! fresh = read player
            let fresh = ok fresh
            equal (ValueSome name) fresh.CharacterName
            equal ValueNone fresh.Location
            equal Map.empty fresh.ActorValues

            do! post player (PlayerMessage.Update PlayerUpdate.LeaveGame)
            let! cleared = read player
            equal { fresh with CharacterName = ValueNone } (ok cleared)
            do! post player PlayerMessage.Stop
            let! leave = receive events
            equal (PlayerEvent.Leave data.PlayerId) leave
            do! post player PlayerMessage.Left
            do! awaitUnit player.Completion
        }))

    case "stopping profile resolution closes its reply address without waiting for storage" (fun () ->
        withPlayer (fun opening player queries events -> task {
            let! pending = receive queries
            do! post player PlayerMessage.Stop
            let! _ = terminal player.Completion
            let! late = pending.ReplyTo.PostAsync {
                OperationId = pending.OperationId
                Result = Ok (ProfileOutcome.Resolved(profile opening))
            }
            equal AgentDeliveryResult.Closed late
            equal 0 events.Reader.Count
        }))

    case "stopping during join waits for acknowledgement and compensating leave" (fun () ->
        withPlayer (fun opening player queries events -> task {
            let! pending = receive queries
            let data = profile opening
            do! resolve pending (Ok (ProfileOutcome.Resolved data))
            let! joined = receive events
            equal (PlayerEvent.Join data) joined

            do! post player PlayerMessage.Stop
            let! state = read player
            equal (Error PlayerStateError.NotReady) state
            check (not player.Completion.IsCompleted) "Join was abandoned before its cleanup."
            do! post player PlayerMessage.Joined
            let! leave = receive events
            equal (PlayerEvent.Leave data.PlayerId) leave
            let! state = read player
            equal (Error PlayerStateError.Closed) state

            do! post player PlayerMessage.Left
            do! awaitUnit player.Completion
            equal 0 events.Reader.Count
        }))

    case "a profile operation failure preserves its cause and finishes only this player" (fun () ->
        withPlayer (fun _ player queries events -> task {
            let! pending = receive queries
            let expected = InvalidOperationException("profile query failed")
            do! resolve pending (Error (ProfileStoreError.Failed expected))
            let! failure = receive events
            match failure with
            | PlayerEvent.Failed(PlayerFailure.Profile(ProfileStoreError.Failed error)) ->
                check (Object.ReferenceEquals(expected, error)) "Original operation failure was lost."
            | other -> failwithf "Expected profile failure: %A" other

            do! awaitUnit player.Completion
            equal 0 events.Reader.Count
        }))
]
