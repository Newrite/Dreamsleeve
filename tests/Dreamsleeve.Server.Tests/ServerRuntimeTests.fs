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

let private ok = function Ok value -> value | Error error -> failwithf "%A" error
let private receive (channel: Channel<'T>) = channel.Reader.ReadAsync().AsTask().WaitAsync guard

let private packet requestId payload =
    let packet = ClientPacket(ProtocolVersion = ChatCodec.Version, RequestId = requestId)
    payload packet
    packet.ToByteArray()

let private ticket (name: string) = name.PadRight(43, '_')
let private opening name = packet 1UL (fun packet -> packet.OpenSession <- OpenSession(SessionTicket = ticket name))

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
        Result = (match Map.tryFind request.Ticket identities with Some profile -> Ok profile | None -> Error SessionAuthenticationError.InvalidTicket)
    }
    Agent.Start(AgentOptions.create "fixture-authentication",
        AgentReplyDispatcher.createHandler 64 (fun (request: SessionAuthenticationRequest) -> request.ReplyTo) execute)

let private authentication (agent: Agent<SessionAuthenticationRequest>) : SessionAuthenticator = {
    Requests = agent.Ref.TryReliable().Value
    Completion = agent.Completion
}
let private chat requestId text = packet requestId (fun packet -> packet.SendChat <- SendChat(ChannelId = 1UL, Text = text))

let private beginCharacter requestId name =
    packet requestId (fun packet -> packet.UpdatePlayer <- UpdatePlayer(BeginCharacter = BeginCharacter(Name = name)))

let private playerLocation x =
    PlayerLocation(Location = Location(LocationId = FormKey(PluginName = "Skyrim.esm", LocalFormId = 60u), LocationName = "Whiterun"),
                   Position = Position(X = x, Y = 2.0f, Z = 3.0f), Rotation = Rotation())

let private telemetry requestId x =
    packet requestId (fun packet ->
        packet.UpdatePlayer <- UpdatePlayer(SampleMovement = SampleMovement(Location = playerLocation x)))


type private Fixture = {
    Runtime: Agent<ServerRuntimeMessage>
    Input: ConcurrentQueue<ServerTransportEvent>
    Output: Channel<Guid * ServerPacket>
    Closed: Channel<Guid>
    Authentication: Agent<SessionAuthenticationRequest>
    IgnoreClose: ConcurrentDictionary<Guid, unit>
    Reset: Channel<Guid>
}

let private post (agent: Agent<_>) command = task {
    let! posted = agent.PostAsync command
    equal AgentPostResult.Posted posted
}

let private withRuntimeUsing options createAuthentication run = task {
    let input = ConcurrentQueue<ServerTransportEvent>()
    let output = Channel.CreateUnbounded<Guid * ServerPacket>()
    let closed = Channel.CreateUnbounded<Guid>()
    let reset = Channel.CreateUnbounded<Guid>()
    let ignoreClose = ConcurrentDictionary<Guid, unit>()
    let poll () =
        let events = ResizeArray()
        let mutable value = Unchecked.defaultof<ServerTransportEvent>
        while events.Count < 64 && input.TryDequeue(&value) do
            match value with
            | ServerTransportEvent.Disconnected id -> closed.Writer.TryWrite id |> ignore
            | ServerTransportEvent.Connected _ | ServerTransportEvent.Received _ -> ()
            events.Add value
        Ok (List.ofSeq events)
    let transport = {
        Poll = poll
        Send = fun (id, bytes) -> output.Writer.TryWrite(id, ServerPacket.Parser.ParseFrom bytes) |> ignore; Ok ()
        Close = fun id -> if not (ignoreClose.ContainsKey id) then input.Enqueue(ServerTransportEvent.Disconnected id)
        Reset = fun id -> reset.Writer.TryWrite id |> ignore
        Dispose = ignore
    }
    use authenticator = createAuthentication ()
    use runtime = ServerRuntime.start options ServerConfig.defaults (authentication authenticator) transport NullLogger.Instance |> ok
    let fixture = { Runtime = runtime; Input = input; Output = output; Closed = closed; Authentication = authenticator; IgnoreClose = ignoreClose; Reset = reset }
    try
        do! run fixture
        if not runtime.Completion.IsCompleted then
            do! post runtime ServerRuntimeMessage.Stop
            do! awaitUnit runtime.Completion
    finally
        runtime.Abort()
}

let private withRuntime options run =
    withRuntimeUsing options createAuthentication run

let private connect fixture name =
    let id = Guid.NewGuid()
    fixture.Input.Enqueue(ServerTransportEvent.Connected id)
    fixture.Input.Enqueue(ServerTransportEvent.Received(id, opening name))
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
    testTask "two clients receive one authoritative publication each and reconnect retains identity" {
        do! withRuntime ServerRuntimeOptions.defaults (fun fixture -> task {
            let alice = connect fixture "alice"
            let! a = welcome fixture alice
            let bob = connect fixture "bob"
            let! b = welcome fixture bob
            equal 2 b.Players.Count
            fixture.Input.Enqueue(ServerTransportEvent.Received(alice, chat 2UL "hello"))

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
            equal 1 again.RecentMessages.Count
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
            fixture.Input.Enqueue(ServerTransportEvent.Connected id)
            let! closed = receive fixture.Closed
            equal id closed
            do! empty fixture
        })
    }
    testTask "malformed envelope closes only its peer" {
        do! withRuntime ServerRuntimeOptions.defaults (fun fixture -> task {
            let first = connect fixture "healthy"
            let! _ = welcome fixture first
            let malformed = Guid.NewGuid()
            fixture.Input.Enqueue(ServerTransportEvent.Connected malformed)
            fixture.Input.Enqueue(ServerTransportEvent.Received(malformed, [|255uy|]))
            let! closed = receive fixture.Closed
            equal malformed closed
            fixture.Input.Enqueue(ServerTransportEvent.Received(first, chat 2UL "still alive"))
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
                request.ReplyTo.TryPost { OperationId = request.OperationId; Result = Ok profile }
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
            fixture.Input.Enqueue(ServerTransportEvent.Received(id, chat 2UL "one source"))
            let! _, response = nextWhere fixture (fun _ p -> p.PayloadCase = ServerPacket.PayloadOneofCase.ChatPublished)
            equal "one source" response.ChatPublished.Message.Text
        })
    }
    testCase "transport blocking interval must fit runtime deadlines" (fun () ->
        use authenticator = createAuthentication ()
        let transport = {
            Poll = fun () -> failwith "Invalid runtime must not poll."
            Send = fun _ -> failwith "Invalid runtime must not send."
            Close = ignore; Reset = ignore; Dispose = ignore
        }
        let config = { ServerConfig.defaults with ServiceTimeoutMs = UInt32.MaxValue }
        match ServerRuntime.start ServerRuntimeOptions.defaults config (authentication authenticator) transport NullLogger.Instance with
        | Error errors -> check (errors |> List.exists (fun error -> error.Contains "deadlines")) "Deadline validation missing."
        | Ok runtime -> runtime.Abort(); failwith "Invalid runtime started.")

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
            fixture.Input.Enqueue(ServerTransportEvent.Received(healthy, chat 2UL "survived"))
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

    testTask "authenticated source owns telemetry and both author and observer receive replication" {
        let options = { ServerRuntimeOptions.defaults with Presence = { ServerRuntimeOptions.defaults.Presence with ReplicationIntervalMs = 10 } }
        do! withRuntime options (fun fixture -> task {
            let alice = connect fixture "alice"
            let! a = welcome fixture alice
            let bob = connect fixture "bob"
            let! _ = welcome fixture bob
            fixture.Input.Enqueue(ServerTransportEvent.Received(bob, beginCharacter 2UL "Observer"))
            fixture.Input.Enqueue(ServerTransportEvent.Received(bob, telemetry 3UL 0.0f))
            let mutable observerReady = false
            while not observerReady do
                let! id, response = receive fixture.Output
                observerReady <- id = bob && response.RequestId = 3UL
            fixture.Input.Enqueue(ServerTransportEvent.Received(alice, beginCharacter 2UL "Nerevar"))
            fixture.Input.Enqueue(ServerTransportEvent.Received(alice, telemetry 3UL 10.0f))
            let replicated = System.Collections.Generic.Dictionary<Guid, PlayerInfo>()
            let located = System.Collections.Generic.HashSet<Guid>()
            let mutable accepted = false
            while located.Count < 2 || not accepted do
                let! id, response = receive fixture.Output
                if id = alice && response.RequestId = 3UL then
                    equal ServerPacket.PayloadOneofCase.PlayerUpdateAccepted response.PayloadCase
                    accepted <- true
                elif response.PayloadCase = ServerPacket.PayloadOneofCase.PlayerUpdated
                     && response.PlayerUpdated.Player.Profile.PlayerId = a.SelfPlayerId then
                    let current = response.PlayerUpdated.Player
                    replicated[id] <- current
                    if not (isNull current.Location) && current.Location.Position.X = 10.0f then located.Add id |> ignore
                elif response.PayloadCase = ServerPacket.PayloadOneofCase.PlayerMoved
                     && response.PlayerMoved.PlayerId = a.SelfPlayerId then
                    replicated[id].Location <- response.PlayerMoved.Location
                    if response.PlayerMoved.Location.Position.X = 10.0f then located.Add id |> ignore
            equal (set [alice; bob]) (replicated.Keys |> Set.ofSeq)
            for KeyValue(_, current) in replicated do
                equal a.SelfPlayerId current.Profile.PlayerId
                equal "Nerevar" current.CharacterName
                equal 1UL current.CharacterGeneration
                equal 10.0f current.Location.Position.X
                equal 0 current.ActorValues.Count

            fixture.Input.Enqueue(ServerTransportEvent.Received(alice, telemetry 4UL 20.0f))
            let moved = ResizeArray<Guid>()
            let mutable movedAccepted = false
            while moved.Count < 2 || not movedAccepted do
                let! id, response = receive fixture.Output
                if id = alice && response.RequestId = 4UL then
                    equal ServerPacket.PayloadOneofCase.PlayerUpdateAccepted response.PayloadCase
                    movedAccepted <- true
                elif response.PayloadCase = ServerPacket.PayloadOneofCase.PlayerMoved then
                    equal a.SelfPlayerId response.PlayerMoved.PlayerId
                    equal 20.0f response.PlayerMoved.Location.Position.X
                    moved.Add id
            equal (set [alice; bob]) (Set.ofSeq moved)

            let late = connect fixture "healthy"
            let! initial = welcome fixture late
            let current = initial.Players |> Seq.find (fun value -> value.Profile.PlayerId = a.SelfPlayerId)
            check (isNull current.Location) "Unlocated late join must not receive remote coordinates."
            equal 0 current.ActorValues.Count
            equal "Nerevar" current.CharacterName
        })
    }

]
