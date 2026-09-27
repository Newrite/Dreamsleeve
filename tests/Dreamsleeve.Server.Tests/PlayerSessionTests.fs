module Dreamsleeve.Server.Tests.PlayerSessionTests

open System
open System.Threading.Channels
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Expecto
open AgentTests
open BackgroundTests

let private ok = function Ok value -> value | Error error -> failwithf "%A" error
let private playerSnapshot profile = Player.snapshot (Player.create profile)
let private globalId = ChatChannelId.create 1UL |> ok
let private options = { ServerRuntimeOptions.defaults.Player with MaxPendingChat = 1; MaxBootstrapEvents = 4; MaxPendingOutput = 16 }
let private collect (output: Channel<'T>) (_: AgentContext<'T>) value = task {
    check (output.Writer.TryWrite value) "Test output closed."
}
let private receive (output: Channel<'T>) = output.Reader.ReadAsync().AsTask().WaitAsync guard
let private deliver (address: ReliableAgentRef<'T>) value = task {
    let! result = address.PostAsync value
    equal AgentDeliveryResult.Posted result
}
let private post (player: Agent<PlayerSessionMessage>) message = deliver (player.Ref.TryReliable().Value) message
let private read (player: Agent<PlayerSessionMessage>) = task {
    let! result = player.TryAskAsync PlayerSessionMessage.Read |> awaitResult
    match result with
    | AgentAskResult.Replied value -> return value
    | other -> return failwithf "%A" other
}

type private Fixture = {
    Request: SessionOpenRequest
    Player: Agent<PlayerSessionMessage>
    Authentication: Channel<SessionAuthenticationRequest>
    Chat: Channel<ChatRoomCommand>
    Presence: Channel<PresenceCommand>
    Host: Channel<SessionHostCommand>
}

let private withPlayerUsingPresence settings (createPresence: Channel<PresenceCommand> -> Agent<PresenceCommand>) run = task {
    let queries = Channel.CreateUnbounded<SessionAuthenticationRequest>()
    let chatCommands = Channel.CreateUnbounded<ChatRoomCommand>()
    let presenceCommands = Channel.CreateUnbounded<PresenceCommand>()
    let hostCommands = Channel.CreateUnbounded<SessionHostCommand>()
    use authentication = Agent.Start(AgentOptions.create "authentication", collect queries)
    use chat = Agent.Start(AgentOptions.create "chat", collect chatCommands)
    use presence = createPresence presenceCommands
    use host = Agent.Start(AgentOptions.create "host", collect hostCommands)
    let request = {
        ConnectionId = Guid.NewGuid()
        RequestId = 1UL
        SessionTicket = String('a', 43)
    }
    use player = PlayerSession.start settings 64 globalId
                     (authentication.Ref.TryReliable().Value) (chat.Ref.TryReliable().Value)
                     (presence.Ref.TryReliable().Value) (host.Ref.TryReliable().Value) request |> ok
    let fixture = { Request = request; Player = player; Authentication = queries;
                    Chat = chatCommands; Presence = presenceCommands; Host = hostCommands }
    do! run fixture
    if not player.Completion.IsCompleted then player.Abort()
    let! _ = terminal player.Completion
    authentication.Complete() |> ignore
    chat.Complete() |> ignore
    presence.Complete() |> ignore
    host.Complete() |> ignore
    do! awaitUnit authentication.Completion
    do! awaitUnit chat.Completion
    do! awaitUnit presence.Completion
    do! awaitUnit host.Completion
}

let private withPlayer settings run =
    withPlayerUsingPresence settings (fun commands -> Agent.Start(AgentOptions.create "presence", collect commands)) run

let private resolve fixture = task {
    let! query = receive fixture.Authentication
    equal fixture.Request.SessionTicket query.Ticket
    let profile = PlayerData.create (PlayerId.create 42UL |> ok)
                      (Username.create 32 "player" |> ok) (DisplayName.create 64 "Player" |> ok)
    do! deliver query.ReplyTo { OperationId = query.OperationId; Result = Ok profile }
    let! command = receive fixture.Host
    match command with
    | SessionHostCommand.Reserve(connectionId, playerId, reply) ->
        equal fixture.Request.ConnectionId connectionId
        equal profile.PlayerId playerId
        return profile, reply
    | other -> return failwithf "Expected Reserve: %A" other
}

let private joins fixture = task {
    let! profile, reply = resolve fixture
    do! deliver reply IdentityAdmission.Reserved
    let! chatCommand = receive fixture.Chat
    let! presenceCommand = receive fixture.Presence
    match chatCommand, presenceCommand with
    | ChatRoomCommand.Join chat, PresenceCommand.Join presence -> return profile, chat, presence
    | other -> return failwithf "Expected subscriptions: %A" other
}

let private snapshot (profile: PlayerData) = {
    ChannelId = globalId
    Players = Set.singleton profile.PlayerId
    Messages = []
    HistoryCapacity = 4
}

let private ready fixture = task {
    let! profile, chat, presence = joins fixture
    do! deliver chat.Events (ChatRoomEvent.Joined(snapshot profile))
    do! deliver presence.Events (PresenceEvent.Snapshot [playerSnapshot profile])
    let! command = receive fixture.Host
    match command with
    | SessionHostCommand.Activate(connectionId, requestId, welcome) ->
        equal fixture.Request.ConnectionId connectionId
        equal fixture.Request.RequestId requestId
        equal profile.PlayerId welcome.SelfPlayerId
    | other -> failwithf "Expected Activate: %A" other
    return profile, chat, presence
}

let private applyUpdate fixture requestId command = task {
    do! post fixture.Player (PlayerSessionMessage.Update(requestId, command))
    let! accepted = receive fixture.Host
    equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ChatResponse.PlayerUpdateAccepted requestId)) accepted
    let! change = receive fixture.Presence
    match change with
    | PresenceCommand.Update(connectionId, current) ->
        equal fixture.Request.ConnectionId connectionId
        return current
    | other -> return failwithf "Expected presence update: %A" other
}

let private rejectUpdate fixture requestId command = task {
    do! post fixture.Player (PlayerSessionMessage.Update(requestId, command))
    let! rejected = receive fixture.Host
    match rejected with
    | SessionHostCommand.Send(_, ChatResponse.RequestRejected(id, rejection)) ->
        equal requestId id
        equal RequestRejectionCode.InvalidRequest rejection.Code
    | other -> failwithf "Expected update refusal: %A" other
    equal 0 fixture.Presence.Reader.Count
}

let private publication profile id =
    ChatMessage.create (ChatMessageId.create id |> ok) globalId profile
        (ChatMessageText.create 2000 $"message {id}" |> ok) DateTimeOffset.UnixEpoch

let private finish fixture = task {
    let! chatCommand = receive fixture.Chat
    let! presenceCommand = receive fixture.Presence
    match chatCommand, presenceCommand with
    | ChatRoomCommand.Detach chat, PresenceCommand.Detach presence ->
        do! deliver chat.ReplyTo fixture.Request.ConnectionId
        check (not fixture.Player.Completion.IsCompleted) "Session skipped presence cleanup."
        do! deliver presence.ReplyTo fixture.Request.ConnectionId
        do! awaitUnit fixture.Player.Completion
    | other -> failwithf "Expected detach: %A" other
}

let tests = testList "PlayerSession" [
    case "bootstrap orders history before later chat and buffers independent presence changes" (fun () ->
        withPlayer options (fun fixture -> task {
            let! profile, chat, presence = joins fixture
            let message = publication profile 1UL
            do! deliver chat.Events (ChatRoomEvent.Joined(snapshot profile))
            do! deliver chat.Events (ChatRoomEvent.Published message)
            let! state = read fixture.Player
            equal (Error PlayerStateError.NotReady) state
            equal 0 fixture.Host.Reader.Count

            do! deliver presence.Events (PresenceEvent.Snapshot [playerSnapshot profile])
            let! first = receive fixture.Host
            match first with
            | SessionHostCommand.Activate(_, _, welcome) -> equal [] welcome.RecentMessages
            | other -> failwithf "Expected welcome first: %A" other
            let! second = receive fixture.Host
            equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ChatResponse.ChatPublished message)) second
            do! deliver presence.Events (PresenceEvent.Left profile.PlayerId)
            let! third = receive fixture.Host
            equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ChatResponse.PlayerLeft profile.PlayerId)) third
        }))

    case "presence snapshot and later delta remain ordered while chat snapshot is delayed" (fun () ->
        withPlayer options (fun fixture -> task {
            let! profile, chat, presence = joins fixture
            do! deliver presence.Events (PresenceEvent.Snapshot [playerSnapshot profile])
            do! deliver presence.Events (PresenceEvent.Left profile.PlayerId)
            do! deliver chat.Events (ChatRoomEvent.Joined(snapshot profile))
            let! welcome = receive fixture.Host
            match welcome with
            | SessionHostCommand.Activate(_, _, value) -> equal [playerSnapshot profile] value.Players
            | other -> failwithf "%A" other
            let! delta = receive fixture.Host
            equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ChatResponse.PlayerLeft profile.PlayerId)) delta
        }))

    case "personal quota and rejection settle one request without closing the player" (fun () ->
        withPlayer options (fun fixture -> task {
            let! profile, chat, _ = ready fixture
            let text = ChatMessageText.create 2000 "hello" |> ok
            do! post fixture.Player (PlayerSessionMessage.SendChat(2UL, globalId, text))
            let! first = receive fixture.Chat
            match first with
            | ChatRoomCommand.Publish value -> equal 2UL value.RequestId
            | other -> failwithf "%A" other
            do! post fixture.Player (PlayerSessionMessage.SendChat(3UL, globalId, text))
            let! overloaded = receive fixture.Host
            match overloaded with
            | SessionHostCommand.Send(_, ChatResponse.RequestRejected(id, reason)) ->
                equal 3UL id
                equal RequestRejectionCode.Overloaded reason.Code
            | other -> failwithf "%A" other
            equal 0 fixture.Chat.Reader.Count

            let rejection = { Code = RequestRejectionCode.NotChannelMember; Message = "refused"; Field = "" }
            do! deliver chat.Events (ChatRoomEvent.Rejected(2UL, rejection))
            let! rejected = receive fixture.Host
            equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ChatResponse.RequestRejected(2UL, rejection))) rejected
            do! post fixture.Player (PlayerSessionMessage.SendChat(4UL, globalId, text))
            let! next = receive fixture.Chat
            match next with
            | ChatRoomCommand.Publish value -> equal 4UL value.RequestId
            | other -> failwithf "%A" other
            let message = publication profile 1UL
            do! deliver chat.Events (ChatRoomEvent.Accepted(4UL, message))
            let! accepted = receive fixture.Host
            equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ChatResponse.ChatAccepted(4UL, message))) accepted
            let! state = read fixture.Player
            equal profile (ok state).Data
        }))

    case "stop before snapshots detaches in the source command order and waits for both acknowledgements" (fun () ->
        withPlayer options (fun fixture -> task {
            let! profile, chat, presence = joins fixture
            do! post fixture.Player PlayerSessionMessage.Stop
            do! deliver chat.Events (ChatRoomEvent.Joined(snapshot profile))
            do! deliver presence.Events (PresenceEvent.Snapshot [playerSnapshot profile])
            let! state = read fixture.Player
            equal (Error PlayerStateError.Closed) state
            do! finish fixture
            equal 0 fixture.Host.Reader.Count
        }))

    case "bootstrap overflow closes only this session and completes its subscriptions" (fun () ->
        withPlayer { options with MaxBootstrapEvents = 1 } (fun fixture -> task {
            let! profile, chat, _ = joins fixture
            do! deliver chat.Events (ChatRoomEvent.Joined(snapshot profile))
            do! deliver chat.Events (ChatRoomEvent.Published(publication profile 1UL))
            do! deliver chat.Events (ChatRoomEvent.Published(publication profile 2UL))
            let! command = receive fixture.Host
            match command with
            | SessionHostCommand.Close(id, _) -> equal fixture.Request.ConnectionId id
            | other -> failwithf "%A" other
            do! finish fixture
            equal 0 fixture.Host.Reader.Count
        }))

    case "denied identity sends correlated refusal before close and never joins services" (fun () ->
        withPlayer options (fun fixture -> task {
            let! _, reply = resolve fixture
            do! deliver reply IdentityAdmission.AlreadyInUse
            let! rejected = receive fixture.Host
            match rejected with
            | SessionHostCommand.Send(_, ChatResponse.RequestRejected(id, reason)) ->
                equal fixture.Request.RequestId id
                equal RequestRejectionCode.SessionAlreadyOpen reason.Code
            | other -> failwithf "%A" other
            let! closed = receive fixture.Host
            match closed with
            | SessionHostCommand.Close(id, _) -> equal fixture.Request.ConnectionId id
            | other -> failwithf "%A" other
            let! _ = terminal fixture.Player.Completion
            equal 0 fixture.Chat.Reader.Count
            equal 0 fixture.Presence.Reader.Count
        }))

    case "profile resolution can stop without waiting for its reply" (fun () ->
        withPlayer options (fun fixture -> task {
            let! query = receive fixture.Authentication
            do! post fixture.Player PlayerSessionMessage.Stop
            let! _ = terminal fixture.Player.Completion
            let! late = query.ReplyTo.PostAsync { OperationId = query.OperationId; Result = Error SessionAuthenticationError.Unavailable }
            equal AgentDeliveryResult.Closed late
            equal 0 fixture.Host.Reader.Count
        }))

    case "player owns detached telemetry across character changes" (fun () ->
        withPlayer options (fun fixture -> task {
            let! profile, _, _ = ready fixture
            let name = CharacterName.create 128 "Nerevar" |> ok
            let key = ActorValueKey.create 128 "skyrim:health" |> ok
            let health value =
                ActorValueInfo.create (ActorValueName.create 64 "Health" |> ok)
                    (ActorValueState.resource value 100.0f |> ok)
            let form = FormKey.create (PluginName.create 255 "Skyrim.esm" |> ok) (LocalFormId.create 0x3Cu |> ok)
            let location = PlayerLocation.create
                               (Location.create form (LocationName.create 128 "Whiterun" |> ok))
                               (Position.create 1.0f 2.0f 3.0f |> ok) Rotation.zero
            let mutable nextRequestId = 2UL
            let update value = task {
                let requestId = nextRequestId
                nextRequestId <- nextRequestId + 1UL
                do! post fixture.Player (PlayerSessionMessage.Update(requestId, value))
                let! accepted = receive fixture.Host
                equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ChatResponse.PlayerUpdateAccepted requestId)) accepted
                let! change = receive fixture.Presence
                match change with
                | PresenceCommand.Update(connectionId, current) ->
                    equal fixture.Request.ConnectionId connectionId
                    equal profile current.Data
                | other -> failwithf "Expected presence update: %A" other
            }

            do! update (PlayerUpdate.BeginCharacter name)
            do! update (PlayerUpdate.Sample(ValueSome location, Map.ofList [(key, health 80.0f)]))
            let! first = read fixture.Player
            let first = ok first
            equal profile first.Data
            equal (ValueSome name) first.CharacterName
            equal (ValueSome location) first.Location

            do! update (PlayerUpdate.Sample(ValueSome location, Map.ofList [(key, health 20.0f)]))
            let! second = read fixture.Player
            equal (health 20.0f) (ok second).ActorValues[key]
            equal (health 80.0f) first.ActorValues[key]

            // Loading another save with the same character name clears its old telemetry.
            do! update (PlayerUpdate.BeginCharacter name)
            let! fresh = read fixture.Player
            let fresh = ok fresh
            equal (first.CharacterGeneration + 1UL) fresh.CharacterGeneration
            equal (ValueSome name) fresh.CharacterName
            equal ValueNone fresh.Location
            equal Map.empty fresh.ActorValues
            do! update PlayerUpdate.LeaveGame
            let! cleared = read fixture.Player
            equal { fresh with CharacterName = ValueNone; CharacterGeneration = fresh.CharacterGeneration + 1UL } (ok cleared)
            equal 0 fixture.Host.Reader.Count
            do! post fixture.Player PlayerSessionMessage.Stop
            do! finish fixture
        }))

    case "duplicate pending ID on another channel closes without settling the original as a refusal" (fun () ->
        withPlayer options (fun fixture -> task {
            let! _, _, _ = ready fixture
            let text = ChatMessageText.create 2000 "hello" |> ok
            do! post fixture.Player (PlayerSessionMessage.SendChat(2UL, globalId, text))
            let! published = receive fixture.Chat
            match published with
            | ChatRoomCommand.Publish value -> equal 2UL value.RequestId
            | other -> failwithf "%A" other
            let otherChannel = ChatChannelId.create 99UL |> ok
            do! post fixture.Player (PlayerSessionMessage.SendChat(2UL, otherChannel, text))
            let! command = receive fixture.Host
            match command with
            | SessionHostCommand.Close(id, _) -> equal fixture.Request.ConnectionId id
            | other -> failwithf "Duplicate must not create another correlated response: %A" other
            do! finish fixture
            equal 0 fixture.Host.Reader.Count
        }))

    case "duplicate Begin cannot issue a second authentication request" (fun () ->
        withPlayer options (fun fixture -> task {
            let! _ = receive fixture.Authentication
            do! post fixture.Player PlayerSessionMessage.Begin
            let! state = read fixture.Player
            equal (Error PlayerStateError.NotReady) state
            equal 0 fixture.Authentication.Reader.Count
        }))

    case "invalid or unavailable authentication rejects opening before any membership" (fun () -> task {
        for failure, expectedCode in [
            SessionAuthenticationError.InvalidTicket, RequestRejectionCode.AuthenticationFailed
            SessionAuthenticationError.Unavailable, RequestRejectionCode.Overloaded
        ] do
            do! withPlayer options (fun fixture -> task {
                let! query = receive fixture.Authentication
                do! deliver query.ReplyTo { OperationId = query.OperationId; Result = Error failure }
                let! rejected = receive fixture.Host
                match rejected with
                | SessionHostCommand.Send(connectionId, ChatResponse.RequestRejected(requestId, rejection)) ->
                    equal fixture.Request.ConnectionId connectionId
                    equal fixture.Request.RequestId requestId
                    equal expectedCode rejection.Code
                    check (not (rejection.Message.Contains fixture.Request.SessionTicket)) "Credential leaked in rejection."
                | other -> failwithf "Expected authentication rejection: %A" other
                let! command = receive fixture.Host
                match command with
                | SessionHostCommand.Close(connectionId, _) -> equal fixture.Request.ConnectionId connectionId
                | other -> failwithf "Expected closing after rejection: %A" other
                let! _ = terminal fixture.Player.Completion
                equal 0 fixture.Chat.Reader.Count
                equal 0 fixture.Presence.Reader.Count
            })
    })

    case "a closed authentication destination cannot leave opening waiting forever" (fun () -> task {
        let queries, chatCommands, presenceCommands, hostCommands =
            Channel.CreateUnbounded<SessionAuthenticationRequest>(), Channel.CreateUnbounded<ChatRoomCommand>(),
            Channel.CreateUnbounded<PresenceCommand>(), Channel.CreateUnbounded<SessionHostCommand>()
        use authentication = Agent.Start(AgentOptions.create "closed-authentication", collect queries)
        authentication.Complete() |> ignore
        do! awaitUnit authentication.Completion
        use chat = Agent.Start(AgentOptions.create "chat", collect chatCommands)
        use presence = Agent.Start(AgentOptions.create "presence", collect presenceCommands)
        use host = Agent.Start(AgentOptions.create "host", collect hostCommands)
        let request = {
            ConnectionId = Guid.NewGuid(); RequestId = 1UL
            SessionTicket = String('b', 43)
        }
        use player = PlayerSession.start options 64 globalId
                         (authentication.Ref.TryReliable().Value) (chat.Ref.TryReliable().Value)
                         (presence.Ref.TryReliable().Value) (host.Ref.TryReliable().Value) request |> ok
        let! failure = terminal player.Completion
        check failure.IsSome "Closed dependency should terminate this session observably."
        equal 0 chatCommands.Reader.Count
        equal 0 presenceCommands.Reader.Count
    })

    case "telemetry validates character state and bounded full replacement without partial mutation" (fun () ->
        withPlayer options (fun fixture -> task {
            let! profile, _, presence = ready fixture
            let name = CharacterName.create 128 "Nerevar" |> ok
            let key = ActorValueKey.create 128 "skyrim:health" |> ok
            let health = ActorValueInfo.create (ActorValueName.create 64 "Health" |> ok) (ActorValueState.resource 20.0f 100.0f |> ok)
            do! rejectUpdate fixture 2UL (PlayerUpdate.Sample(ValueNone, Map.ofList [(key, health)]))
            do! rejectUpdate fixture 3UL (PlayerUpdate.RenameCharacter name)
            let! beginning = applyUpdate fixture 4UL (PlayerUpdate.BeginCharacter name)
            let oversized = [for index in 0 .. 64 -> (ActorValueKey.create 128 $"test:value{index}" |> ok), health] |> Map.ofList
            do! rejectUpdate fixture 6UL (PlayerUpdate.Sample(ValueNone, oversized))
            let! afterRejected = read fixture.Player
            equal beginning (ok afterRejected)

            let! populated = applyUpdate fixture 7UL (PlayerUpdate.Sample(ValueNone, Map.ofList [(key, health)]))
            equal 1 populated.ActorValues.Count
            let! cleared = applyUpdate fixture 8UL (PlayerUpdate.Sample(ValueNone, Map.empty))
            equal Map.empty cleared.ActorValues
            equal profile cleared.Data
            equal 0 fixture.Host.Reader.Count
            // Only the source's publication updates the author's outbound state.
            do! deliver presence.Events (PresenceEvent.Updated cleared)
            let! replicated = receive fixture.Host
            equal (SessionHostCommand.Send(fixture.Request.ConnectionId, ChatResponse.PlayerUpdated cleared)) replicated
        }))

    case "telemetry request ID cannot settle another pending chat command" (fun () ->
        withPlayer options (fun fixture -> task {
            let! _, _, _ = ready fixture
            let text = ChatMessageText.create 2000 "pending" |> ok
            do! post fixture.Player (PlayerSessionMessage.SendChat(2UL, globalId, text))
            let! _ = receive fixture.Chat
            do! post fixture.Player (PlayerSessionMessage.Update(2UL, PlayerUpdate.LeaveGame))
            let! closed = receive fixture.Host
            match closed with
            | SessionHostCommand.Close(id, _) -> equal fixture.Request.ConnectionId id
            | other -> failwithf "Expected collision close, no correlated refusal: %A" other
            do! finish fixture
        }))

    case "menu details are valid before character while race and level require one and reset with it" (fun () ->
        withPlayer options (fun fixture -> task {
            let! _, _, _ = ready fixture
            let activity = PlayerActivity.create 256 64 ActivityKind.Menu ValueNone LockDifficulty.Unknown (ValueSome "main_menu") |> ok
            let menu = PlayerDetails.create ValueNone ValueNone activity ValueNone (ValueSome DateTimeOffset.UnixEpoch) |> ok
            let leveled = PlayerDetails.create ValueNone (ValueSome 10u) activity ValueNone ValueNone |> ok
            let! initial = applyUpdate fixture 2UL (PlayerUpdate.SetDetails menu)
            equal ValueNone initial.CharacterName
            equal menu initial.Details
            do! rejectUpdate fixture 3UL (PlayerUpdate.SetDetails leveled)
            let name = CharacterName.create 128 "Nerevar" |> ok
            let! beginning = applyUpdate fixture 4UL (PlayerUpdate.BeginCharacter name)
            equal PlayerDetails.empty beginning.Details
            let! playing = applyUpdate fixture 5UL (PlayerUpdate.SetDetails leveled)
            equal leveled playing.Details
            let! leaving = applyUpdate fixture 6UL PlayerUpdate.LeaveGame
            equal PlayerDetails.empty leaving.Details
            equal ValueNone leaving.CharacterName
        }))

    case "a full presence outbox refuses new updates before changing session state" (fun () -> task {
        let entered, release = gate<unit>(), gate<unit>()
        let createPresence commands =
            let handle (context: AgentContext<PresenceCommand>) command = task {
                do! collect commands context command
                match command with
                | PresenceCommand.Update _ ->
                    entered.TrySetResult() |> ignore
                    do! release.Task.WaitAsync context.CancellationToken
                | PresenceCommand.Join _ | PresenceCommand.Detach _ | PresenceCommand.Flush -> ()
            }
            Agent.Start({ AgentOptions.create "blocked-presence" with Mailbox = AgentMailbox.boundedWait 1 }, handle)
        do! withPlayerUsingPresence { options with MaxPendingUpdates = 1 } createPresence (fun fixture -> task {
            let! _, _, _ = ready fixture
            let initial = CharacterName.create 128 "Original" |> ok
            let! _ = applyUpdate fixture 2UL (PlayerUpdate.BeginCharacter initial)
            do! awaitResult entered.Task
            let mutable expected = initial
            let mutable refused = 0
            for requestId in 3UL .. 12UL do
                let name = CharacterName.create 128 $"Character {requestId}" |> ok
                do! post fixture.Player (PlayerSessionMessage.Update(requestId, PlayerUpdate.RenameCharacter name))
                let! answer = receive fixture.Host
                match answer with
                | SessionHostCommand.Send(_, ChatResponse.PlayerUpdateAccepted id) ->
                    equal requestId id
                    expected <- name
                | SessionHostCommand.Send(_, ChatResponse.RequestRejected(id, rejection)) ->
                    equal requestId id
                    equal RequestRejectionCode.Overloaded rejection.Code
                    refused <- refused + 1
                | other -> failwithf "Unexpected admission result: %A" other
            check (refused > 0) "Blocked source accepted unlimited updates."
            let! current = read fixture.Player
            equal (ValueSome expected) (ok current).CharacterName
            release.SetResult()
        })
    })

]
