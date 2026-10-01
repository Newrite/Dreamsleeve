module Dreamsleeve.Server.Tests.ServerRuntimeTests

open System
open System.Collections.Concurrent
open System.Threading.Channels
open System.Threading.Tasks
open Google.Protobuf
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Microsoft.Extensions.Logging.Abstractions
open Dreamsleeve.Protocol.Chat
open Expecto
open AgentTests

let private tick () =
    let now = System.Diagnostics.Stopwatch.GetTimestamp()
    ServerRuntimeMessage.Tick { DueTimestamp = now; QueuedTimestamp = now }

let private ok = function Ok value -> value | Error error -> failwithf "%A" error
let private receive (channel: Channel<'T>) = channel.Reader.ReadAsync().AsTask().WaitAsync guard

let private packet requestId payload =
    let packet = ClientPacket(ProtocolVersion = ProtocolCodec.Version, RequestId = requestId)
    payload packet
    packet.ToByteArray()

let private ticket (name: string) = name.PadRight(43, '_')
let private opening name = packet 1UL (fun packet -> packet.OpenSession <- OpenSession(SessionTicket = ticket name))
let private joinAsGuest requestId = packet requestId (fun packet -> packet.JoinAsGuest <- JoinAsGuest())

// Authentication is a controlled dependency in runtime tests; account/password/ticket
// consumption semantics are verified by authentication service tests.
let private createAuthentication () =
    let identities =
        [ "alice"; "bob"; "same"; "abandoned"; "healthy"; "shutdown"; "dependency";
          "race"; "silent"; "once"; "noack"; "survivor"; "stopnoack" ]
        |> List.mapi (fun index name ->
            ticket name,
            Dreamsleeve.Server.Domain.PlayerData.create
                (Dreamsleeve.Server.Domain.PlayerId.create (uint64 index + 1UL) |> ok)
                (Dreamsleeve.Server.Domain.Username.create 32 name |> ok)
                (Dreamsleeve.Server.Domain.DisplayName.create 64 name |> ok))
        |> Map.ofList
    let execute (request: SessionAuthenticationRequest) : SessionAuthenticationReply = {
        OperationId = request.OperationId
        Result = (match Map.tryFind request.Ticket identities with Some profile -> Ok { Profile = profile; Role = Dreamsleeve.Server.Domain.PlayerRole.Player; Mute = ValueNone } | None -> Error SessionAuthenticationError.InvalidTicket)
    }
    Agent.Start(AgentOptions.create "fixture-authentication",
        AgentReplyDispatcher.createHandler 64 (fun (request: SessionAuthenticationRequest) -> request.ReplyTo) execute)

// Display name changes are stored by the account service; here they succeed at once.
let private names =
    lazy (Agent.Start(AgentOptions.create "fixture-names", fun _ (request: DisplayNameChangeRequest) -> task {
        let profile = Dreamsleeve.Server.Domain.PlayerData.create request.PlayerId
                          (Dreamsleeve.Server.Domain.Username.create 32 $"p{Dreamsleeve.Server.Domain.PlayerId.value request.PlayerId}" |> ok) request.DisplayName
        request.ReplyTo.TryPost { OperationId = request.OperationId; Result = Ok profile } |> ignore
    }))

// Moderation is the account service's; runtime tests route it, not decide it.
let private moderation =
    lazy (Agent.Start(AgentOptions.create "fixture-moderation", fun _ (request: ModerationRequest) -> task {
        request.ReplyTo.TryPost { OperationId = request.OperationId; Result = Error ModerationError.Unavailable } |> ignore
    }))

let private authentication (agent: Agent<SessionAuthenticationRequest>) : SessionAuthenticator = {
    Requests = agent.Ref.TryReliable().Value
    DisplayNames = names.Value.Ref.TryReliable().Value
    Moderation = moderation.Value.Ref.TryReliable().Value
    Completion = agent.Completion
}

// Storage is a controlled dependency here: writes are collected, nothing is loaded.
let private persistence (writer: Agent<GroundMarkWrite>) : GroundMarkPersistence =
    { Loaded = []; NextId = 1UL; Writer = writer.Ref.TryReliable().Value }
let private discard (_: AgentContext<GroundMarkWrite>) (_: GroundMarkWrite) = task { () }
let private chat requestId text = packet requestId (fun packet -> packet.SendChat <- SendChat(ChannelId = 1UL, Text = text))

let private beginCharacter requestId name =
    packet requestId (fun packet -> packet.UpdatePlayer <- UpdatePlayer(BeginCharacter = BeginCharacter(Name = name)))

let private playerLocation x =
    PlayerLocation(Location = Location(LocationId = FormKey(PluginName = "Skyrim.esm", LocalFormId = 60u), LocationName = "Whiterun"),
                   Position = Position(X = x, Y = 2.0f, Z = 3.0f), Rotation = Rotation())

let private telemetry requestId x =
    packet requestId (fun packet ->
        packet.UpdatePlayer <- UpdatePlayer(SetLocation = SetPlayerLocation(ContextRevision = requestId, Location = playerLocation x)))

let private healthReading requestId current maximum =
    packet requestId (fun packet ->
        let values = ActorValues()
        values.Values.Add(ActorValueEntry(Key = "skyrim:health", DisplayName = "Health", Resource = ResourceActorValue(Current = current, Maximum = maximum)))
        packet.UpdatePlayer <- UpdatePlayer(SetActorValues = values))

/// The player of a presence change that republishes this player's identity.
let private updatedPlayer (value: ServerPacket) playerId =
    if value.PayloadCase <> ServerPacket.PayloadOneofCase.PresenceChanged then None
    else value.PresenceChanged.Updated |> Seq.tryFind (fun player -> player.Profile.PlayerId = playerId)


type private Fixture = {
    Runtime: Agent<ServerRuntimeMessage>
    Notify: unit -> bool
    Input: ConcurrentQueue<ServerTransportEvent>
    Output: Channel<Guid * ServerPacket>
    Movement: Channel<Guid * ServerMovementPacket>
    Sent: ConcurrentQueue<DeliveryLane>
    SendFailures: ConcurrentDictionary<DeliveryLane, string>
    Closed: Channel<Guid>
    Authentication: Agent<SessionAuthenticationRequest>
    IgnoreClose: ConcurrentDictionary<Guid, unit>
    Reset: Channel<Guid>
}

let private post (agent: Agent<_>) command = task {
    let! posted = agent.PostAsync command
    equal AgentPostResult.Posted posted
}

let private withRuntimeNamed options identity pseudonyms createAuthentication run = task {
    let mutable ready = fun () -> false
    let input = ConcurrentQueue<ServerTransportEvent>()
    let output = Channel.CreateUnbounded<Guid * ServerPacket>()
    let movement = Channel.CreateUnbounded<Guid * ServerMovementPacket>()
    let sent = ConcurrentQueue<DeliveryLane>()
    let failures = ConcurrentDictionary<DeliveryLane, string>()
    let closed = Channel.CreateUnbounded<Guid>()
    let reset = Channel.CreateUnbounded<Guid>()
    let ignoreClose = ConcurrentDictionary<Guid, unit>()
    let poll () =
        let events = ResizeArray()
        let mutable value = Unchecked.defaultof<ServerTransportEvent>
        while events.Count < 64 && input.TryDequeue(&value) do
            match value with
            | ServerTransportEvent.Disconnected id -> closed.Writer.TryWrite id |> ignore
            | ServerTransportEvent.Connected _ | ServerTransportEvent.Received _ | ServerTransportEvent.Failed _ -> ()
            events.Add value
        Ok (List.ofSeq events)
    let transport = {
        MaxUnfragmentedPayloadBytes = fun _ -> Int32.MaxValue
        SetReadyHandler = fun handler -> ready <- handler
        Poll = poll
        Send = fun (id, packet) ->
            sent.Enqueue packet.Lane
            match failures.TryGetValue packet.Lane with
            | true, reason -> Error reason
            | false, _ ->
                if packet.Lane = DeliveryLane.Realtime then
                    movement.Writer.TryWrite(id, ServerMovementPacket.Parser.ParseFrom packet.Bytes) |> ignore
                else output.Writer.TryWrite(id, ServerPacket.Parser.ParseFrom packet.Bytes) |> ignore
                Ok ()
        Close = fun id -> if not (ignoreClose.ContainsKey id) then input.Enqueue(ServerTransportEvent.Disconnected id)
        Reset = fun id -> reset.Writer.TryWrite id |> ignore
        Dispose = ignore
    }
    use authenticator = createAuthentication ()
    use writer = Agent.Start(AgentOptions.create "writer", discard)
    let game = Settings.game ServerConfig.defaults options identity AnnouncementOptions.defaults GroundMarkOptions.defaults
    use runtime = ServerRuntime.start game Dreamsleeve.Server.Domain.Moderation.empty pseudonyms (persistence writer) (authentication authenticator) transport NullLogger.Instance
    let fixture = { Runtime = runtime; Notify = (fun () -> ready ()); Input = input; Output = output; Movement = movement; Sent = sent; SendFailures = failures; Closed = closed; Authentication = authenticator; IgnoreClose = ignoreClose; Reset = reset }
    try
        do! run fixture
        if not runtime.Completion.IsCompleted then
            do! post runtime ServerRuntimeMessage.Stop
            do! awaitUnit runtime.Completion
    finally
        runtime.Abort()
}

let private withRuntimeUsing options createAuthentication run =
    withRuntimeNamed options IdentityOptions.defaults Dreamsleeve.Server.Domain.PseudonymDictionary.builtIn createAuthentication run

let private withRuntime options run =
    withRuntimeUsing options createAuthentication run

let private incoming (id, bytes) =
    let lane =
        try
            if (ClientPacket.Parser.ParseFrom(bytes: byte array)).PayloadCase = ClientPacket.PayloadOneofCase.SendChat then DeliveryLane.Chat
            else DeliveryLane.Control
        with :? InvalidProtocolBufferException -> DeliveryLane.Control
    ServerTransportEvent.Received(id, lane, bytes)

let private connect fixture name =
    let id = Guid.NewGuid()
    fixture.Input.Enqueue(ServerTransportEvent.Connected(id, Net.IPAddress.Loopback))
    fixture.Input.Enqueue(incoming(id, opening name))
    id

let private nextWhere fixture predicate = task {
    let mutable found = None
    while found.IsNone do
        let! id, value = receive fixture.Output
        if predicate id value then found <- Some (id, value)
    return found.Value
}

let private welcome fixture id = task {
    let! _, value = nextWhere fixture (fun target packet -> target = id)
    equal ServerPacket.PayloadOneofCase.SessionOpened value.PayloadCase
    return value.SessionOpened
}

let private stats fixture = task {
    let! result = fixture.Runtime.TryAskAsync ServerRuntimeMessage.Read
    match result with
    | AgentAskResult.Replied stats -> return stats
    | other -> return failwithf "Runtime unavailable: %A" other
}

let private empty fixture = task {
    let mutable doneWaiting = false
    let deadline = Environment.TickCount64 + 5000L
    while not doneWaiting do
        let! value = stats fixture
        doneWaiting <- value.Connections = 0 && value.Reservations = 0
        check (Environment.TickCount64 < deadline) "Routes or identities leaked after disconnect."
        if not doneWaiting then do! Task.Yield()
}

[<Tests>]
let tests = testList "ServerRuntime" [
    testTask "account revocation closes its ready session without closing another ready player" {
        do! withRuntime ServerRuntimeOptions.defaults (fun fixture -> task {
            let alice = connect fixture "alice"
            do! post fixture.Runtime (tick ())
            let! _ = welcome fixture alice
            let bob = connect fixture "bob"
            do! post fixture.Runtime (tick ())
            let! _ = welcome fixture bob
            let playerId = Dreamsleeve.Server.Domain.PlayerId.create 1UL |> ok
            do! post fixture.Runtime (ServerRuntimeMessage.AccountChanged(AccountChange.AccessRevoked playerId))
            let! closed = receive fixture.Closed
            equal alice closed
            let! state = stats fixture
            equal 1 state.Ready
            do! post fixture.Runtime ServerRuntimeMessage.Stop
            do! awaitUnit fixture.Runtime.Completion
        })
    }

    testTask "an IP range ban ends the sessions it covers saying why, refuses new connections from it and spares others" {
        do! withRuntime ServerRuntimeOptions.defaults (fun fixture -> task {
            let alice = connect fixture "alice"
            let! _ = welcome fixture alice
            let bob = Guid.NewGuid()
            fixture.Input.Enqueue(ServerTransportEvent.Connected(bob, Net.IPAddress.Parse "203.0.113.9"))
            fixture.Input.Enqueue(incoming(bob, opening "bob"))
            let! _ = welcome fixture bob
            let! rows = fixture.Runtime.AskAsync(fun reply -> ServerRuntimeMessage.ListSessions reply) |> awaitResult
            equal (set [ "127.0.0.1"; "203.0.113.9" ]) (rows |> List.map (fun row -> Dreamsleeve.Server.Domain.ClientAddress.text row.Address) |> set)
            let now = DateTimeOffset.UtcNow
            let ban : Dreamsleeve.Server.Domain.AddressBan = {
                Id = 1L; Range = Dreamsleeve.Server.Domain.AddressRange.parse "203.0.113.0/24" |> ok
                Reason = Dreamsleeve.Server.Domain.SanctionReason.create "Рейд" |> ok; IssuedBy = ValueNone
                IssuedAt = now; Expires = ValueSome (DateTimeOffset.FromUnixTimeMilliseconds((now.AddHours 1.).ToUnixTimeMilliseconds()))
            }
            do! post fixture.Runtime (ServerRuntimeMessage.AccountChanged(AccountChange.AddressBans [ ban ]))
            let! _, ended = nextWhere fixture (fun id packet -> id = bob && packet.PayloadCase = ServerPacket.PayloadOneofCase.SessionEnded)
            equal SessionEndReason.AddressBanned ended.SessionEnded.Reason
            equal "Рейд" ended.SessionEnded.Text
            equal (ban.Expires.Value.ToUnixTimeMilliseconds()) ended.SessionEnded.UntilUnixMs
            let! closed = receive fixture.Closed
            equal bob closed
            let guest = Guid.NewGuid()
            fixture.Input.Enqueue(ServerTransportEvent.Connected(guest, Net.IPAddress.Parse "::ffff:203.0.113.50"))
            fixture.Notify() |> ignore
            let! refused = receive fixture.Closed
            equal guest refused
            let! state = stats fixture
            equal 1 state.Ready
            equal 1 state.Connections
        })
    }

    testTask "a ban and a kick end only their player's session and say why; a mute reaches the live session and the next one" {
        do! withRuntime ServerRuntimeOptions.defaults (fun fixture -> task {
            let pid value = Dreamsleeve.Server.Domain.PlayerId.create value |> ok
            let reason text = Dreamsleeve.Server.Domain.SanctionReason.create text |> ok
            let issue target kind term text =
                let order : Dreamsleeve.Server.Domain.SanctionOrder =
                    { Target = pid target; Kind = kind; Term = term; Reason = reason text
                      IssuedBy = Dreamsleeve.Server.Domain.SanctionIssuer.Moderator(pid 9UL) }
                Dreamsleeve.Server.Domain.Sanction.issue (Dreamsleeve.Server.Domain.SanctionId.create 1L |> ok) DateTimeOffset.UtcNow order
            let alice = connect fixture "alice"
            let! _ = welcome fixture alice
            let bob = connect fixture "bob"
            let! _ = welcome fixture bob
            let mute = issue 2UL Dreamsleeve.Server.Domain.SanctionKind.Mute Dreamsleeve.Server.Domain.SanctionTerm.UntilLifted "Флуд"
            do! post fixture.Runtime (ServerRuntimeMessage.AccountChanged(AccountChange.MuteChanged(pid 2UL, ValueSome mute)))
            let! _, muted = nextWhere fixture (fun id packet -> id = bob && packet.PayloadCase = ServerPacket.PayloadOneofCase.MuteChanged)
            equal "Флуд" muted.MuteChanged.Mute.Reason
            check (not muted.MuteChanged.Mute.HasUntilUnixMs) "A mute until lifted has no end."
            let ban = issue 1UL Dreamsleeve.Server.Domain.SanctionKind.Ban (Dreamsleeve.Server.Domain.SanctionTerm.For(TimeSpan.FromDays 1.)) "Читы"
            do! post fixture.Runtime (ServerRuntimeMessage.AccountChanged(AccountChange.Banned ban))
            let! _, banned = nextWhere fixture (fun id packet -> id = alice && packet.PayloadCase = ServerPacket.PayloadOneofCase.SessionEnded)
            equal SessionEndReason.Banned banned.SessionEnded.Reason
            equal "Читы" banned.SessionEnded.Text
            equal (ban.Expires.Value.ToUnixTimeMilliseconds()) banned.SessionEnded.UntilUnixMs
            let! closed = receive fixture.Closed
            equal alice closed
            let! state = stats fixture
            equal 1 state.Ready
            do! post fixture.Runtime (ServerRuntimeMessage.AccountChanged(AccountChange.Kicked(pid 2UL, reason "Остынь")))
            let! _, kicked = nextWhere fixture (fun id packet -> id = bob && packet.PayloadCase = ServerPacket.PayloadOneofCase.SessionEnded)
            equal SessionEndReason.Kicked kicked.SessionEnded.Reason
            equal "Остынь" kicked.SessionEnded.Text
            let! closedBob = receive fixture.Closed
            equal bob closedBob
            do! empty fixture
            // The mute stays with the account: the next session is muted, in its welcome
            // or right after it when the change reaches the session once it has opened.
            let again = connect fixture "bob"
            let! _ = welcome fixture again
            fixture.Input.Enqueue(incoming(again, chat 5UL "hi"))
            fixture.Notify() |> ignore
            let! _, refused = nextWhere fixture (fun id packet -> id = again && packet.PayloadCase = ServerPacket.PayloadOneofCase.RequestRejected)
            equal RequestRejectionCode.Muted refused.RequestRejected.Code
        })
    }

    testTask "transport notification admits input independently of deadline timer" {
        let settings = { ServerRuntimeOptions.defaults with PollIntervalMs = 1000000; OpenTimeoutMs = 2000000; ShutdownTimeoutMs = 2000000 }
        do! withRuntime settings (fun fixture -> task {
            let! _ = stats fixture // Start registered the wakeup before this read.
            let alice = connect fixture "alice"
            check (fixture.Notify()) "Ready notification must be admitted."
            let! _ = welcome fixture alice
            fixture.Input.Enqueue(ServerTransportEvent.Failed(alice, "native send rejected"))
            check (fixture.Notify()) "Failure must wake runtime without a tick."
            do! empty fixture
            do! post fixture.Runtime ServerRuntimeMessage.Stop
            do! awaitUnit fixture.Runtime.Completion
        })
    }
    testTask "ready movement is sent on realtime without waiting for runtime tick or settlement" {
        let settings = { ServerRuntimeOptions.defaults with PollIntervalMs = 1000000; OpenTimeoutMs = 2000000; ShutdownTimeoutMs = 2000000 }
        do! withRuntime settings (fun fixture -> task {
            let alice = connect fixture "alice"
            do! post fixture.Runtime (tick ())
            let! _ = welcome fixture alice
            // Protocol v9: the (empty) own mark list follows the welcome on the control lane.
            let! _, own = nextWhere fixture (fun target packet -> target = alice)
            equal ServerPacket.PayloadOneofCase.OwnGroundMarks own.PayloadCase
            equal 0 own.OwnGroundMarks.Marks.Count
            let pid = Dreamsleeve.Server.Domain.PlayerId.create 1UL |> ok
            let point = Dreamsleeve.Server.Domain.Position.create 2.f 0.f 0.f |> ok
            let change: Dreamsleeve.Server.Domain.MovementChange = {
                PlayerId = pid; ViewRevision = 1UL; Sequence = 1UL
                Pose = { Position = point; Rotation = Dreamsleeve.Server.Domain.Rotation.zero; SampledAtUs = 1UL }
            }
            do! post fixture.Runtime (ServerRuntimeMessage.Host(SessionHostCommand.Send(alice, ServerResponse.PlayersMoved [|change|])))
            let! target, value = receive fixture.Movement
            equal alice target
            equal 2.f value.Movements.Players[0].Pose.Position.X
            equal 0 fixture.Output.Reader.Count
            check (fixture.Sent |> Seq.contains DeliveryLane.Realtime) "Movement uses its own channel."
            do! post fixture.Runtime ServerRuntimeMessage.Stop
            let deadline = Environment.TickCount64 + 5000L
            while not fixture.Runtime.Completion.IsCompleted && Environment.TickCount64 < deadline do
                fixture.Runtime.TryPost (tick ()) |> ignore
                do! Task.Delay 1
            do! awaitUnit fixture.Runtime.Completion
        })
    }
    testTask "two clients receive one authoritative publication each and reconnect retains identity" {
        do! withRuntime ServerRuntimeOptions.defaults (fun fixture -> task {
            let alice = connect fixture "alice"
            let! a = welcome fixture alice
            let bob = connect fixture "bob"
            let! b = welcome fixture bob
            equal 2 b.Players.Count
            fixture.Input.Enqueue(incoming(alice, chat 2UL "hello"))

            let messages = ResizeArray<Guid * ServerPacket>()
            while messages.Count < 2 do
                let! output = nextWhere fixture (fun _ packet -> packet.PayloadCase = ServerPacket.PayloadOneofCase.ChatPublished)
                messages.Add output
            let sender = messages |> Seq.find (fun (id, _) -> id = alice) |> snd
            let recipient = messages |> Seq.find (fun (id, _) -> id = bob) |> snd
            equal 2UL sender.RequestId
            equal 0UL recipient.RequestId
            equal sender.ChatPublished.Message recipient.ChatPublished.Message
            equal a.SelfPlayerId sender.ChatPublished.Message.Author.PlayerId

            fixture.Input.Enqueue(ServerTransportEvent.Disconnected alice)
            fixture.Input.Enqueue(ServerTransportEvent.Disconnected bob)
            do! empty fixture
            let reconnected = connect fixture "alice"
            let! again = welcome fixture reconnected
            equal a.SelfPlayerId again.SelfPlayerId
            equal 1 again.Players.Count
            equal 1 again.Channels[0].RecentMessages.Count
            // A stale close from the old connection cannot remove the new route.
            do! post fixture.Runtime (ServerRuntimeMessage.Host(SessionHostCommand.Close(alice, "late close")))
            let! status = stats fixture
            equal 1 status.Ready
        })
    }
    testTask "two simultaneous connections cannot reserve the same player" {
        do! withRuntime ServerRuntimeOptions.defaults (fun fixture -> task {
            let first = connect fixture "same"
            let second = connect fixture "same"
            let! loser = receive fixture.Closed
            check (loser = first || loser = second) "Unexpected rejected connection."
            let winner = if loser = first then second else first
            let! _, output = nextWhere fixture (fun id packet -> id = winner && packet.PayloadCase = ServerPacket.PayloadOneofCase.SessionOpened)
            equal 1 output.SessionOpened.Players.Count
            let! status = stats fixture
            equal 1 status.Ready
            equal 1 status.Reservations
        })
    }
    testTask "disconnect during opening leaves no membership or reservation" {
        do! withRuntime ServerRuntimeOptions.defaults (fun fixture -> task {
            let id = connect fixture "abandoned"
            fixture.Input.Enqueue(ServerTransportEvent.Disconnected id)
            let! closed = receive fixture.Closed
            equal id closed
            do! empty fixture
            let replacement = connect fixture "abandoned"
            let! snapshot = welcome fixture replacement
            equal 1 snapshot.Players.Count
        })
    }
    testTask "application deadline closes a connection that never opens" {
        let options = { ServerRuntimeOptions.defaults with OpenTimeoutMs = 30 }
        do! withRuntime options (fun fixture -> task {
            let id = Guid.NewGuid()
            fixture.Input.Enqueue(ServerTransportEvent.Connected(id, Net.IPAddress.Loopback))
            let! closed = receive fixture.Closed
            equal id closed
            do! empty fixture
        })
    }
    testTask "a guest outlives the open deadline, opens its session on the same connection and gets the full time to authenticate" {
        let options = { ServerRuntimeOptions.defaults with OpenTimeoutMs = 50 }
        do! withRuntime options (fun fixture -> task {
            let guest = Guid.NewGuid()
            fixture.Input.Enqueue(ServerTransportEvent.Connected(guest, Net.IPAddress.Loopback))
            fixture.Input.Enqueue(incoming(guest, joinAsGuest 1UL))
            fixture.Notify() |> ignore
            // Several deadlines of a connection that never announces itself.
            do! Task.Delay 250
            let! counted = stats fixture
            equal 1 counted.Connections
            equal 1 counted.Guests
            let! rows = fixture.Runtime.AskAsync ServerRuntimeMessage.ListSessions |> awaitResult
            check (rows |> List.exactlyOne |> fun row -> row.Phase = RuntimeSessionPhase.Guest && row.PlayerId.IsNone) "a guest row has no player"

            fixture.Input.Enqueue(incoming(guest, chat 2UL "hello"))
            let! _, early = nextWhere fixture (fun id _ -> id = guest)
            equal RequestRejectionCode.SessionNotReady early.RequestRejected.Code
            fixture.Input.Enqueue(incoming(guest, joinAsGuest 3UL))
            let! _, again = nextWhere fixture (fun id _ -> id = guest)
            equal RequestRejectionCode.InvalidRequest again.RequestRejected.Code

            fixture.Input.Enqueue(incoming(guest, packet 4UL (fun packet -> packet.OpenSession <- OpenSession(SessionTicket = ticket "alice"))))
            let! opened = welcome fixture guest
            equal 1 opened.Players.Count
            let! after = stats fixture
            equal 0 after.Guests
            equal 1 after.Ready
        })
    }

    testTask "a guest that leaves frees its connection" {
        do! withRuntime ServerRuntimeOptions.defaults (fun fixture -> task {
            let guest = Guid.NewGuid()
            fixture.Input.Enqueue(ServerTransportEvent.Connected(guest, Net.IPAddress.Loopback))
            fixture.Input.Enqueue(incoming(guest, joinAsGuest 1UL))
            fixture.Input.Enqueue(ServerTransportEvent.Disconnected guest)
            fixture.Notify() |> ignore
            let! closed = receive fixture.Closed
            equal guest closed
            do! empty fixture
        })
    }

    testTask "malformed envelope closes only its peer" {
        do! withRuntime ServerRuntimeOptions.defaults (fun fixture -> task {
            let first = connect fixture "healthy"
            let! _ = welcome fixture first
            let malformed = Guid.NewGuid()
            fixture.Input.Enqueue(ServerTransportEvent.Connected(malformed, Net.IPAddress.Loopback))
            fixture.Input.Enqueue(incoming(malformed, [|255uy|]))
            let! closed = receive fixture.Closed
            equal malformed closed
            fixture.Input.Enqueue(incoming(first, chat 2UL "still alive"))
            let! _, response = nextWhere fixture (fun id p -> id = first && p.PayloadCase = ServerPacket.PayloadOneofCase.ChatPublished)
            equal "still alive" response.ChatPublished.Message.Text
        })
    }
    testTask "shutdown drains children while preserving caller-owned authenticator" {
        do! withRuntime ServerRuntimeOptions.defaults (fun fixture -> task {
            let first = connect fixture "shutdown"
            let! _ = welcome fixture first
            do! post fixture.Runtime ServerRuntimeMessage.Stop
            do! awaitUnit fixture.Runtime.Completion
            check (not fixture.Authentication.Completion.IsCompleted) "Runtime stopped shared authentication."
        })
    }
    testTask "shared dependency termination is observed and terminates runtime" {
        do! withRuntime ServerRuntimeOptions.defaults (fun fixture -> task {
            let first = connect fixture "dependency"
            let! _ = welcome fixture first
            fixture.Authentication.Abort()
            let! failure = terminal fixture.Runtime.Completion
            check failure.IsSome "Dependency loss must be observable."
        })
    }
    testTask "late authentication reply after disconnect cannot revive or reserve the old route" {
        let requests = Channel.CreateUnbounded<SessionAuthenticationRequest>()
        let collect (_: AgentContext<SessionAuthenticationRequest>) request = task { requests.Writer.TryWrite request |> ignore }
        let createAuthentication () = Agent.Start(AgentOptions.create "controlled-authenticator", collect)
        do! withRuntimeUsing ServerRuntimeOptions.defaults createAuthentication (fun fixture -> task {
            let abandoned = connect fixture "race"
            let! oldRequest = receive requests
            fixture.Input.Enqueue(ServerTransportEvent.Disconnected abandoned)
            let! _ = receive fixture.Closed
            do! empty fixture

            let replacement = connect fixture "race"
            let! newRequest = receive requests
            let profile =
                Dreamsleeve.Server.Domain.PlayerData.create
                    (Dreamsleeve.Server.Domain.PlayerId.create 42UL |> ok)
                    (Dreamsleeve.Server.Domain.Username.create 32 "race" |> ok)
                    (Dreamsleeve.Server.Domain.DisplayName.create 64 "Race" |> ok)
            let respond (request: SessionAuthenticationRequest) =
                request.ReplyTo.TryPost { OperationId = request.OperationId; Result = Ok { Profile = profile; Role = Dreamsleeve.Server.Domain.PlayerRole.Player; Mute = ValueNone } }
            equal AgentTryDeliveryResult.Closed (respond oldRequest)
            equal AgentTryDeliveryResult.Posted (respond newRequest)
            let! snapshot = welcome fixture replacement
            equal 42UL snapshot.SelfPlayerId
            equal 1 snapshot.Players.Count
            let! status = stats fixture
            equal 1 status.Reservations
        })
    }
    testTask "one application deadline covers a live authenticator that never replies" {
        let requests = Channel.CreateUnbounded<SessionAuthenticationRequest>()
        let collect (_: AgentContext<SessionAuthenticationRequest>) request = task { requests.Writer.TryWrite request |> ignore }
        let options = { ServerRuntimeOptions.defaults with OpenTimeoutMs = 100 }
        do! withRuntimeUsing options (fun () -> Agent.Start(AgentOptions.create "silent-authenticator", collect)) (fun fixture -> task {
            let connection = connect fixture "silent"
            let! _ = receive requests
            let! closed = receive fixture.Closed
            equal connection closed
            do! empty fixture
            check (not fixture.Authentication.Completion.IsCompleted) "A live but silent authenticator was killed."
        })
    }

    testTask "duplicate initialization cannot replace live source owners" {
        do! withRuntime ServerRuntimeOptions.defaults (fun fixture -> task {
            let id = connect fixture "once"
            let! _ = welcome fixture id
            do! post fixture.Runtime ServerRuntimeMessage.Start
            fixture.Input.Enqueue(incoming(id, chat 2UL "one source"))
            let! _, response = nextWhere fixture (fun _ p -> p.PayloadCase = ServerPacket.PayloadOneofCase.ChatPublished)
            equal "one source" response.ChatPublished.Message.Text
        })
    }
    testCase "transport blocking interval must fit runtime deadlines" (fun () ->
        let config = { ServerConfig.defaults with ServiceTimeoutMs = UInt32.MaxValue }
        let errors = Settings.errors config ServerRuntimeOptions.defaults IdentityOptions.defaults AnnouncementOptions.defaults GroundMarkOptions.defaults
        check (errors |> List.exists (fun error -> error.Contains "deadlines")) "Deadline validation missing.")

    testTask "a peer that never acknowledges close is reset without stopping healthy sessions" {
        let options = { ServerRuntimeOptions.defaults with ShutdownTimeoutMs = 100 }
        do! withRuntime options (fun fixture -> task {
            let slow = connect fixture "noack"
            let! _ = welcome fixture slow
            let healthy = connect fixture "survivor"
            let! _ = welcome fixture healthy
            fixture.IgnoreClose.TryAdd(slow, ()) |> ignore
            do! post fixture.Runtime (ServerRuntimeMessage.Host(SessionHostCommand.Close(slow, "close without ack")))
            let! reset = receive fixture.Reset
            equal slow reset
            let! status = stats fixture
            equal 1 status.Ready
            equal 1 status.Reservations
            fixture.Input.Enqueue(incoming(healthy, chat 2UL "survived"))
            let! _, response = nextWhere fixture (fun id p -> id = healthy && p.PayloadCase = ServerPacket.PayloadOneofCase.ChatPublished)
            equal "survived" response.ChatPublished.Message.Text
        })
    }
    testTask "shutdown completes after resetting an unacknowledged transport with clean domain state" {
        let options = { ServerRuntimeOptions.defaults with ShutdownTimeoutMs = 100 }
        do! withRuntime options (fun fixture -> task {
            let id = connect fixture "stopnoack"
            let! _ = welcome fixture id
            fixture.IgnoreClose.TryAdd(id, ()) |> ignore
            do! post fixture.Runtime ServerRuntimeMessage.Stop
            let! reset = receive fixture.Reset
            equal id reset
            do! awaitUnit fixture.Runtime.Completion
        })
    }

    testTask "realtime transport saturation does not close the session or block control" {
        do! withRuntime ServerRuntimeOptions.defaults (fun fixture -> task {
            let id = connect fixture "alice"
            let! _ = welcome fixture id
            let pid = Dreamsleeve.Server.Domain.PlayerId.create 1UL |> ok
            let change: Dreamsleeve.Server.Domain.MovementChange = {
                PlayerId = pid; ViewRevision = 1UL; Sequence = 1UL
                Pose = { Position = Dreamsleeve.Server.Domain.Position.create 0.f 0.f 0.f |> ok
                         Rotation = Dreamsleeve.Server.Domain.Rotation.zero; SampledAtUs = 0UL }
            }
            fixture.SendFailures[DeliveryLane.Realtime] <- "Outgoing budget full"
            do! post fixture.Runtime (ServerRuntimeMessage.Host(SessionHostCommand.Send(id, ServerResponse.PlayersMoved [|change|])))
            let left = ServerResponse.PresenceChanged({ PresenceChange.empty with Left = [ pid ] }, ActorValueKinds.none)
            do! post fixture.Runtime (ServerRuntimeMessage.Host(SessionHostCommand.Send(id, left)))
            let! _, response = nextWhere fixture (fun _ p -> p.PayloadCase = ServerPacket.PayloadOneofCase.PresenceChanged && p.PresenceChanged.Left.Count > 0)
            equal [ 1UL ] (List.ofSeq response.PresenceChanged.Left)
            let! state = stats fixture
            equal 1 state.Ready
            equal 0 state.Closing
        })
    }
    testTask "movement has no acceptance and repeats to both author and visible observer" {
        do! withRuntime ServerRuntimeOptions.defaults (fun fixture -> task {
            let alice = connect fixture "alice"
            let! a = welcome fixture alice
            let bob = connect fixture "bob"
            let! _ = welcome fixture bob
            for id in [alice; bob] do
                fixture.Input.Enqueue(incoming(id, beginCharacter 2UL "Character"))
                fixture.Input.Enqueue(incoming(id, telemetry 3UL 0.f))
            let! _ = nextWhere fixture (fun id p -> id = alice && p.HasRequestId && p.RequestId = 3UL)
            let sample = ClientMovementPacket(ProtocolVersion = ProtocolCodec.Version,
                Sample = MovementSample(ContextRevision = 3UL, Sequence = 1UL,
                    Pose = MovementPose(Position = Position(X = 20.f, Y = 2.f, Z = 3.f), Rotation = Rotation(), SampledAtUs = 123UL)))
            fixture.Input.Enqueue(ServerTransportEvent.Received(alice, DeliveryLane.Realtime, sample.ToByteArray()))
            let observed = System.Collections.Generic.HashSet<Guid>()
            while observed.Count < 2 do
                let! id, packet = receive fixture.Movement
                if packet.Movements.Players |> Seq.exists (fun value -> value.PlayerId = a.SelfPlayerId && value.Sequence = 1UL && value.Pose.Position.X = 20.f) then
                    observed.Add id |> ignore
            equal (set [alice; bob]) (Set.ofSeq observed)
            // No new upstream sample: a subsequent period still repairs a lost final position.
            let mutable repeated = false
            while not repeated do
                let! _, packet = receive fixture.Movement
                repeated <- packet.Movements.Players |> Seq.exists (fun value -> value.PlayerId = a.SelfPlayerId && value.Sequence = 1UL)
            let mutable output = Unchecked.defaultof<Guid * ServerPacket>
            while fixture.Output.Reader.TryRead(&output) do
                let _, response = output
                check (not response.HasRequestId || response.RequestId <= 3UL) "No movement request or ACK generated."
            let late = connect fixture "healthy"
            let! initial = welcome fixture late
            let current = initial.Players |> Seq.find (fun value -> value.Profile.PlayerId = a.SelfPlayerId)
            check (isNull current.Location) "Unlocated late join must not receive remote coordinates."
        })
    }

    testTask "actor values reach other players as numbered readings and a later welcome defines their kinds" {
        do! withRuntime ServerRuntimeOptions.defaults (fun fixture -> task {
            let alice = connect fixture "alice"
            let! a = welcome fixture alice
            let bob = connect fixture "bob"
            let! _ = welcome fixture bob
            fixture.Input.Enqueue(incoming(alice, beginCharacter 2UL "Character"))
            let! _ = nextWhere fixture (fun target value ->
                target = bob && updatedPlayer value a.SelfPlayerId |> Option.exists (fun player -> player.CharacterGeneration > 0UL))
            fixture.Input.Enqueue(incoming(alice, healthReading 3UL -12 100))
            let readings (value: ServerPacket) =
                if value.PayloadCase <> ServerPacket.PayloadOneofCase.PresenceChanged then Seq.empty
                else value.PresenceChanged.Metadata |> Seq.filter (fun patch -> patch.PlayerId = a.SelfPlayerId) |> Seq.collect _.ActorValues
            let! _, changed = nextWhere fixture (fun target value -> target = bob && not (Seq.isEmpty (readings value)))
            let kind = changed.PresenceChanged.ActorValueKinds |> Seq.exactlyOne
            let reading = readings changed |> Seq.exactlyOne
            equal ("skyrim:health", "Health") (kind.Key, kind.DisplayName)
            equal kind.Id reading.Kind
            equal (-12, 100) (reading.Resource.Current, reading.Resource.Maximum)
            let late = connect fixture "healthy"
            let! initial = welcome fixture late
            equal [ kind ] (List.ofSeq initial.ActorValueKinds)
            let current = initial.Players |> Seq.find (fun value -> value.Profile.PlayerId = a.SelfPlayerId)
            equal [ reading ] (List.ofSeq current.ActorValues)
        })
    }
]

// Accounts whose real names are easy to find in raw bytes.
let private namedAuthentication (accounts: (string * string * string) list) () =
    let identities =
        accounts
        |> List.mapi (fun index (name, username, display) ->
            ticket name,
            Dreamsleeve.Server.Domain.PlayerData.create
                (Dreamsleeve.Server.Domain.PlayerId.create (uint64 index + 1UL) |> ok)
                (Dreamsleeve.Server.Domain.Username.create 32 username |> ok)
                (Dreamsleeve.Server.Domain.DisplayName.create 64 display |> ok))
        |> Map.ofList
    let execute (request: SessionAuthenticationRequest) : SessionAuthenticationReply = {
        OperationId = request.OperationId
        Result = (match Map.tryFind request.Ticket identities with Some profile -> Ok { Profile = profile; Role = Dreamsleeve.Server.Domain.PlayerRole.Player; Mute = ValueNone } | None -> Error SessionAuthenticationError.InvalidTicket)
    }
    Agent.Start(AgentOptions.create "named-authentication",
        AgentReplyDispatcher.createHandler 64 (fun (request: SessionAuthenticationRequest) -> request.ReplyTo) execute)

let private connectHidden fixture name =
    let id = Guid.NewGuid()
    fixture.Input.Enqueue(ServerTransportEvent.Connected(id, Net.IPAddress.Loopback))
    fixture.Input.Enqueue(incoming(id, packet 1UL (fun packet -> packet.OpenSession <- OpenSession(SessionTicket = ticket name, HiddenIdentity = HiddenIdentity.Everywhere))))
    id

let private dictionaryOf (names: string list) =
    names |> List.map (fun name -> Dreamsleeve.Server.Domain.Pseudonym.create name |> ok)
    |> Dreamsleeve.Server.Domain.PseudonymDictionary.create |> ValueOption.get

let private placeNote requestId text x =
    packet requestId (fun packet ->
        packet.PlaceGroundNote <-
            PlaceGroundNote(Text = text,
                            Placement = GroundMarkPlacement(LocationId = FormKey(PluginName = "Skyrim.esm", LocalFormId = 60u),
                                                            Position = Position(X = x, Y = 2.0f, Z = 3.0f)),
                            GameDate = GameDate(Era = 4u, Year = 201u, Month = 8u, Day = 17u, DayOfWeek = 2u, Hour = 14u, Minute = 5u)))

let private switchIdentity requestId (hiding: HiddenIdentity) =
    packet requestId (fun packet -> packet.SetIdentityVisibility <- SetIdentityVisibility(Hidden = hiding))

let private contains (needle: string) (bytes: byte array) =
    let pattern = Text.Encoding.UTF8.GetBytes needle
    Seq.windowed pattern.Length bytes |> Seq.exists (fun window -> window = pattern)

[<Tests>]
let hiddenIdentityTests = testList "ServerRuntime hidden identity" [
    testTask "no packet to another player carries a real name while the names are hidden" {
        let accounts = [ "alice", "alice.real", "Алиса Настоящая"; "bob", "bob", "Bob"; "carol", "carol", "Carol" ]
        let secrets = [ "alice.real"; "Алиса Настоящая"; "Секретная Героиня" ]
        do! withRuntimeNamed ServerRuntimeOptions.defaults IdentityOptions.defaults (dictionaryOf ["Страж"]) (namedAuthentication accounts) (fun fixture -> task {
            let bob = connect fixture "bob"
            let! _ = welcome fixture bob
            fixture.Input.Enqueue(incoming(bob, beginCharacter 2UL "Bob's Hero"))
            fixture.Input.Enqueue(incoming(bob, telemetry 3UL 0.f))
            let alice = connectHidden fixture "alice"
            let! opened = welcome fixture alice
            equal "Страж" opened.OwnPseudonym
            let self = opened.Players |> Seq.find (fun player -> player.Profile.PlayerId = opened.SelfPlayerId)
            equal "Алиса Настоящая" self.Profile.DisplayName
            check (not self.Profile.Pseudonymous) "The owner sees the real profile."
            fixture.Input.Enqueue(incoming(alice, beginCharacter 2UL "Секретная Героиня"))
            fixture.Input.Enqueue(incoming(alice, telemetry 3UL 1.f))
            fixture.Input.Enqueue(incoming(alice, chat 4UL "hello from alice"))
            fixture.Input.Enqueue(incoming(alice, placeNote 5UL "alice was here" 1.f))

            // Everything bob receives until he has seen the message, the mark and
            // the character of the hidden player is checked byte by byte.
            let mutable message, mark, character = false, false, false
            while not (message && mark && character) do
                let! target, value = receive fixture.Output
                if target = bob then
                    let bytes = value.ToByteArray()
                    for secret in secrets do
                        check (not (contains secret bytes)) $"A packet to another player leaks {secret}: {value}"
                    match value.PayloadCase with
                    | ServerPacket.PayloadOneofCase.ChatPublished when value.ChatPublished.Message.Text = "hello from alice" ->
                        let author = value.ChatPublished.Message.Author
                        check author.Pseudonymous "The author is pseudonymous."
                        equal "" author.Username
                        equal "Страж" author.DisplayName
                        check (not value.ChatPublished.Message.HasCharacterName) "No character snapshot."
                        message <- true
                    | ServerPacket.PayloadOneofCase.GroundMarksChanged ->
                        for added in value.GroundMarksChanged.Added do
                            if added.Text = "alice was here" then
                                check added.Author.Pseudonymous "The mark author is pseudonymous."
                                equal "Страж" added.Author.DisplayName
                                check (not added.HasCharacterName) "No character snapshot on the mark."
                                mark <- true
                    | ServerPacket.PayloadOneofCase.PresenceChanged ->
                        match updatedPlayer value opened.SelfPlayerId with
                        | Some player when player.CharacterGeneration > 0UL ->
                            check player.Profile.Pseudonymous "Presence shows the pseudonym."
                            check (not player.HasCharacterName) "No character name."
                            character <- true
                        | Some _ | None -> ()
                    | _ -> ()

            // Showing the names again: later copies carry them, retained history does not change.
            fixture.Input.Enqueue(incoming(alice, switchIdentity 6UL HiddenIdentity.None))
            let! _, shown = nextWhere fixture (fun target value -> target = alice && value.PayloadCase = ServerPacket.PayloadOneofCase.IdentityVisibilityChanged)
            equal 6UL shown.RequestId
            check (not shown.IdentityVisibilityChanged.HasPseudonym) "No pseudonym while shown."
            equal HiddenIdentity.None shown.IdentityVisibilityChanged.Hidden
            let! _, updated = nextWhere fixture (fun target value -> target = bob && (updatedPlayer value opened.SelfPlayerId).IsSome)
            let player = (updatedPlayer updated opened.SelfPlayerId).Value
            equal "Алиса Настоящая" player.Profile.DisplayName
            equal "Секретная Героиня" player.CharacterName
            let carol = connect fixture "carol"
            let! late = welcome fixture carol
            let history = late.Channels |> Seq.collect _.RecentMessages |> Seq.find (fun value -> value.Text = "hello from alice")
            check history.Author.Pseudonymous "A message sent while hidden keeps its pseudonym."
            equal "Страж" history.Author.DisplayName

            // A switch sooner than the interval is refused.
            fixture.Input.Enqueue(incoming(alice, switchIdentity 7UL HiddenIdentity.Everywhere))
            let! _, limited = nextWhere fixture (fun target value -> target = alice && value.HasRequestId && value.RequestId = 7UL)
            equal RequestRejectionCode.RateLimited limited.RequestRejected.Code
        })
    }

    testTask "each hidden session gets a pseudonym against the names online at that moment" {
        let accounts = [ "alice", "alice", "Alice"; "bob", "bob", "страж" ]
        do! withRuntimeNamed ServerRuntimeOptions.defaults IdentityOptions.defaults (dictionaryOf ["Страж"]) (namedAuthentication accounts) (fun fixture -> task {
            let bob = connect fixture "bob"
            let! _ = welcome fixture bob
            let first = connectHidden fixture "alice"
            let! opened = welcome fixture first
            equal "Страж 2" opened.OwnPseudonym
            fixture.Input.Enqueue(ServerTransportEvent.Disconnected first)
            fixture.Input.Enqueue(ServerTransportEvent.Disconnected bob)
            do! empty fixture
            let again = connectHidden fixture "alice"
            let! reopened = welcome fixture again
            equal "Страж" reopened.OwnPseudonym
        })
    }

    testTask "a server that does not allow hidden names refuses such an opening" {
        let refused = { IdentityOptions.defaults with AllowHiddenIdentity = false }
        do! withRuntimeNamed ServerRuntimeOptions.defaults refused Dreamsleeve.Server.Domain.PseudonymDictionary.builtIn createAuthentication (fun fixture -> task {
            let alice = connectHidden fixture "alice"
            let! _, value = nextWhere fixture (fun target _ -> target = alice)
            equal ServerPacket.PayloadOneofCase.RequestRejected value.PayloadCase
            equal RequestRejectionCode.HiddenIdentityNotAllowed value.RequestRejected.Code
            let! closed = receive fixture.Closed
            equal alice closed
        })
    }
]

// Accounts with a stored role, as a consumed ticket carries it.
let private roleAuthentication (accounts: (string * string * string * Dreamsleeve.Server.Domain.PlayerRole) list) () =
    let identities =
        accounts
        |> List.mapi (fun index (name, username, display, role) ->
            ticket name,
            ({ Profile =
                 Dreamsleeve.Server.Domain.PlayerData.create
                     (Dreamsleeve.Server.Domain.PlayerId.create (uint64 index + 1UL) |> ok)
                     (Dreamsleeve.Server.Domain.Username.create 32 username |> ok)
                     (Dreamsleeve.Server.Domain.DisplayName.create 64 display |> ok)
               Role = role; Mute = ValueNone } : AuthenticatedPlayer))
        |> Map.ofList
    let execute (request: SessionAuthenticationRequest) : SessionAuthenticationReply = {
        OperationId = request.OperationId
        Result = (match Map.tryFind request.Ticket identities with Some player -> Ok player | None -> Error SessionAuthenticationError.InvalidTicket)
    }
    Agent.Start(AgentOptions.create "role-authentication",
        AgentReplyDispatcher.createHandler 64 (fun (request: SessionAuthenticationRequest) -> request.ReplyTo) execute)

let private playerId value = Dreamsleeve.Server.Domain.PlayerId.create value |> ok

// The panel's path: list the sessions, then ask each one through the describer.
let private describePlayer fixture (describer: Agent<DescribeRequest>) id = task {
    let! rows = fixture.Runtime.AskAsync ServerRuntimeMessage.ListSessions |> awaitResult
    let row = rows |> List.find (fun row -> row.PlayerId = Some id)
    return! SessionDescriber.describe describer guard row.Session.Value
}

let private describeUntil fixture describer id (accept: Dreamsleeve.Server.Domain.AdminPlayerView -> bool) = task {
    let mutable found = None
    let mutable attempts = 0
    while found.IsNone do
        attempts <- attempts + 1
        check (attempts < 200) "The session never showed the expected state."
        match! describePlayer fixture describer id with
        | Some view when accept view -> found <- Some view
        | Some _ | None -> do! Task.Delay 10
    return found.Value
}

let adminTests = testList "ServerRuntime admin panel" [
    testTask "sessions list with phases and describe the real identity of a hidden player next to the pseudonym" {
        let accounts = [ "alice", "alice.real", "Алиса Настоящая", Dreamsleeve.Server.Domain.PlayerRole.Moderator
                         "bob", "bob", "Bob", Dreamsleeve.Server.Domain.PlayerRole.Player ]
        do! withRuntimeNamed ServerRuntimeOptions.defaults IdentityOptions.defaults (dictionaryOf ["Страж"]) (roleAuthentication accounts) (fun fixture -> task {
            use describer = SessionDescriber.start 8
            let bob = connect fixture "bob"
            let! _ = welcome fixture bob
            let alice = connectHidden fixture "alice"
            let! opened = welcome fixture alice
            fixture.Input.Enqueue(incoming(alice, beginCharacter 2UL "Секретная Героиня"))
            let! rows = fixture.Runtime.AskAsync ServerRuntimeMessage.ListSessions |> awaitResult
            equal 2 rows.Length
            check (rows |> List.forall (fun row -> row.Phase = RuntimeSessionPhase.Ready && row.Session.IsSome)) "both sessions are ready"
            let! view = describeUntil fixture describer (playerId opened.SelfPlayerId) (fun view -> view.CharacterName.IsSome)
            equal "alice.real" (Dreamsleeve.Server.Domain.Username.value view.Username)
            equal "Алиса Настоящая" (Dreamsleeve.Server.Domain.DisplayName.value view.DisplayName)
            equal (ValueSome "Секретная Героиня") (view.CharacterName |> ValueOption.map Dreamsleeve.Server.Domain.CharacterName.value)
            equal (ValueSome "Страж") (view.Pseudonym |> ValueOption.map Dreamsleeve.Server.Domain.Pseudonym.value)
            equal Dreamsleeve.Server.Domain.HiddenIdentity.Everywhere view.Hiding
            // The role comes with the consumed ticket.
            equal Dreamsleeve.Server.Domain.PlayerRole.Moderator view.Role
            equal Dreamsleeve.Server.Domain.AdminSessionPhase.Active view.Phase
        })
    }

    testTask "a role change reaches the live session and a session that opens later" {
        let accounts = [ "alice", "alice", "Alice", Dreamsleeve.Server.Domain.PlayerRole.Player
                         "bob", "bob", "Bob", Dreamsleeve.Server.Domain.PlayerRole.Player ]
        do! withRuntimeNamed ServerRuntimeOptions.defaults IdentityOptions.defaults (dictionaryOf ["Страж"]) (roleAuthentication accounts) (fun fixture -> task {
            use describer = SessionDescriber.start 8
            let alice = connect fixture "alice"
            let! opened = welcome fixture alice
            let aliceId = playerId opened.SelfPlayerId
            do! post fixture.Runtime (ServerRuntimeMessage.SetPlayerRole(aliceId, Dreamsleeve.Server.Domain.PlayerRole.Moderator))
            let! live = describeUntil fixture describer aliceId (fun view -> view.Role = Dreamsleeve.Server.Domain.PlayerRole.Moderator)
            equal Dreamsleeve.Server.Domain.PlayerRole.Moderator live.Role
            // Bob's ticket still says Player: the change stored before his session wins.
            let bobId = playerId 2UL
            do! post fixture.Runtime (ServerRuntimeMessage.SetPlayerRole(bobId, Dreamsleeve.Server.Domain.PlayerRole.Moderator))
            let bob = connect fixture "bob"
            let! _ = welcome fixture bob
            let! later = describeUntil fixture describer bobId (fun view -> view.Role = Dreamsleeve.Server.Domain.PlayerRole.Moderator)
            equal Dreamsleeve.Server.Domain.PlayerRole.Moderator later.Role
        })
    }

    testTask "a rename reaches the other players and never reveals the new name of a hidden player" {
        let accounts = [ "alice", "alice", "Alice", Dreamsleeve.Server.Domain.PlayerRole.Player
                         "bob", "bob", "Bob", Dreamsleeve.Server.Domain.PlayerRole.Player
                         "carol", "carol", "Carol", Dreamsleeve.Server.Domain.PlayerRole.Player ]
        do! withRuntimeNamed ServerRuntimeOptions.defaults IdentityOptions.defaults (dictionaryOf ["Страж"]) (roleAuthentication accounts) (fun fixture -> task {
            use describer = SessionDescriber.start 8
            let carol = connect fixture "carol"
            let! _ = welcome fixture carol
            let bob = connect fixture "bob"
            let! bobOpened = welcome fixture bob
            let alice = connectHidden fixture "alice"
            let! aliceOpened = welcome fixture alice
            let renamed id username display =
                Dreamsleeve.Server.Domain.PlayerData.create (playerId id) (Dreamsleeve.Server.Domain.Username.create 32 username |> ok)
                    (Dreamsleeve.Server.Domain.DisplayName.create 64 display |> ok)
            // A shown player: the others get the player again with the new name.
            do! post fixture.Runtime (ServerRuntimeMessage.RenamePlayer(renamed bobOpened.SelfPlayerId "bob" "Боб Новый"))
            let! _, updated = nextWhere fixture (fun target value ->
                target = carol
                && updatedPlayer value bobOpened.SelfPlayerId |> Option.exists (fun player -> player.Profile.DisplayName = "Боб Новый"))
            equal "Боб Новый" (updatedPlayer updated bobOpened.SelfPlayerId).Value.Profile.DisplayName
            let! view = describeUntil fixture describer (playerId bobOpened.SelfPlayerId) (fun view -> Dreamsleeve.Server.Domain.DisplayName.value view.DisplayName = "Боб Новый")
            equal "bob" (Dreamsleeve.Server.Domain.Username.value view.Username)
            // A hidden player: the panel sees the new name, other players only the pseudonym.
            do! post fixture.Runtime (ServerRuntimeMessage.RenamePlayer(renamed aliceOpened.SelfPlayerId "alice" "Тайное Новое Имя"))
            let! hidden = describeUntil fixture describer (playerId aliceOpened.SelfPlayerId) (fun view -> Dreamsleeve.Server.Domain.DisplayName.value view.DisplayName = "Тайное Новое Имя")
            equal (ValueSome "Страж") (hidden.Pseudonym |> ValueOption.map Dreamsleeve.Server.Domain.Pseudonym.value)
            fixture.Input.Enqueue(incoming(alice, chat 3UL "after the rename"))
            let mutable seen = false
            while not seen do
                let! target, value = receive fixture.Output
                if target = carol then
                    check (not (contains "Тайное Новое Имя" (value.ToByteArray()))) $"A packet to another player leaks the new name: {value}"
                    match value.PayloadCase with
                    | ServerPacket.PayloadOneofCase.ChatPublished when value.ChatPublished.Message.Text = "after the rename" ->
                        equal "Страж" value.ChatPublished.Message.Author.DisplayName
                        seen <- true
                    | _ -> ()
        })
    }
]

let private changeName requestId (name: string) =
    packet requestId (fun packet -> packet.ChangeDisplayName <- ChangeDisplayName(DisplayName = name))

let displayNameTests = testList "ServerRuntime display names" [
    testTask "a player's own new name reaches the others, and a hidden player's never does" {
        let accounts = [ "alice", "alice", "Alice"; "bob", "bob", "Bob"; "carol", "carol", "Carol" ]
        do! withRuntimeNamed ServerRuntimeOptions.defaults IdentityOptions.defaults (dictionaryOf ["Страж"]) (namedAuthentication accounts) (fun fixture -> task {
            let carol = connect fixture "carol"
            let! _ = welcome fixture carol
            let bob = connect fixture "bob"
            let! bobOpened = welcome fixture bob
            let alice = connectHidden fixture "alice"
            let! _ = welcome fixture alice

            fixture.Input.Enqueue(incoming(bob, changeName 2UL "  Боб Новый "))
            let! _, settled = nextWhere fixture (fun target value -> target = bob && value.PayloadCase = ServerPacket.PayloadOneofCase.DisplayNameChanged)
            equal 2UL settled.RequestId
            equal "Боб Новый" settled.DisplayNameChanged.DisplayName
            let! _, updated = nextWhere fixture (fun target value ->
                target = carol
                && updatedPlayer value bobOpened.SelfPlayerId |> Option.exists (fun player -> player.Profile.DisplayName = "Боб Новый"))
            check (not (updatedPlayer updated bobOpened.SelfPlayerId).Value.Profile.Pseudonymous) "A shown player keeps a real profile."

            // Too long: refused by the codec with the field, the session stays open.
            fixture.Input.Enqueue(incoming(bob, changeName 3UL (String('x', ServerConfig.defaults.ChatInput.DisplayName + 1))))
            let! _, refused = nextWhere fixture (fun target value -> target = bob && value.HasRequestId && value.RequestId = 3UL)
            equal RequestRejectionCode.InvalidRequest refused.RequestRejected.Code
            equal "display_name" refused.RequestRejected.Field

            // A hidden player: the change is confirmed to them, others keep seeing the pseudonym.
            fixture.Input.Enqueue(incoming(alice, changeName 2UL "Тайное Имя"))
            let! _, own = nextWhere fixture (fun target value -> target = alice && value.PayloadCase = ServerPacket.PayloadOneofCase.DisplayNameChanged)
            equal "Тайное Имя" own.DisplayNameChanged.DisplayName
            fixture.Input.Enqueue(incoming(alice, chat 3UL "after my rename"))
            let mutable seen = false
            while not seen do
                let! target, value = receive fixture.Output
                if target = carol then
                    check (not (contains "Тайное Имя" (value.ToByteArray()))) $"A packet to another player leaks the new name: {value}"
                    match value.PayloadCase with
                    | ServerPacket.PayloadOneofCase.ChatPublished when value.ChatPublished.Message.Text = "after my rename" ->
                        equal "Страж" value.ChatPublished.Message.Author.DisplayName
                        seen <- true
                    | _ -> ()
        })
    }

    testTask "a server that refuses own display name changes answers with its code" {
        let refused = { IdentityOptions.defaults with AllowDisplayNameChange = false }
        do! withRuntimeNamed ServerRuntimeOptions.defaults refused Dreamsleeve.Server.Domain.PseudonymDictionary.builtIn createAuthentication (fun fixture -> task {
            let alice = connect fixture "alice"
            let! _ = welcome fixture alice
            fixture.Input.Enqueue(incoming(alice, changeName 2UL "Other"))
            let! _, answer = nextWhere fixture (fun target value -> target = alice && value.HasRequestId && value.RequestId = 2UL)
            equal RequestRejectionCode.DisplayNameChangeNotAllowed answer.RequestRejected.Code
        })
    }
]
