module Dreamsleeve.Server.Tests.ServerRuntimeTests

open System
open System.Collections.Concurrent
open System.Threading.Channels
open System.Threading.Tasks
open Google.Protobuf
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Infrastructure
open Dreamsleeve.Protocol.Chat
open Expecto
open AgentTests

let private ok = function Ok value -> value | Error error -> failwithf "%A" error
let private receive (channel: Channel<'T>) = channel.Reader.ReadAsync().AsTask().WaitAsync guard

let private packet requestId payload =
    let packet = ClientPacket(ProtocolVersion = ChatCodec.Version, RequestId = requestId)
    payload packet
    packet.ToByteArray()

let private opening name = packet 1UL (fun packet -> packet.OpenSession <- OpenSession(Username = name, DisplayName = name))
let private chat requestId text = packet requestId (fun packet -> packet.SendChat <- SendChat(ChannelId = 1UL, Text = text))

type private Fixture = {
    Runtime: Agent<ServerRuntimeMessage>
    Input: ConcurrentQueue<ServerTransportEvent>
    Output: Channel<Guid * ServerPacket>
    Closed: Channel<Guid>
    Profiles: Agent<ProfileRequest>
}

let private post (agent: Agent<_>) command = task {
    let! posted = agent.PostAsync command
    equal AgentPostResult.Posted posted
}

let private withRuntimeUsing options createProfiles run = task {
    let input = ConcurrentQueue<ServerTransportEvent>()
    let output = Channel.CreateUnbounded<Guid * ServerPacket>()
    let closed = Channel.CreateUnbounded<Guid>()
    let poll () =
        let events = ResizeArray()
        let mutable value = Unchecked.defaultof<ServerTransportEvent>
        while events.Count < 64 && input.TryDequeue(&value) do events.Add value
        Ok (List.ofSeq events)
    let transport = {
        Poll = poll
        Send = fun (id, bytes) -> output.Writer.TryWrite(id, ServerPacket.Parser.ParseFrom bytes) |> ignore; Ok ()
        Close = fun id -> closed.Writer.TryWrite id |> ignore
        Dispose = ignore
    }
    use profiles = createProfiles ()
    let diagnostics = ConcurrentQueue<string>()
    use runtime = ServerRuntime.start options ServerConfig.defaults profiles transport diagnostics.Enqueue |> ok
    let fixture = { Runtime = runtime; Input = input; Output = output; Closed = closed; Profiles = profiles }
    try
        do! run fixture
        if not runtime.Completion.IsCompleted then
            do! post runtime ServerRuntimeMessage.Stop
            do! awaitUnit runtime.Completion
    finally
        runtime.Abort()
}

let private withRuntime options run =
    withRuntimeUsing options (fun () -> MemoryProfileStore.start { MailboxCapacity = 64; MaxPendingReplies = 64 } |> ok) run

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
    testTask "shutdown drains children while preserving caller-owned profiles" {
        do! withRuntime ServerRuntimeOptions.defaults (fun fixture -> task {
            let first = connect fixture "shutdown"
            let! _ = welcome fixture first
            do! post fixture.Runtime ServerRuntimeMessage.Stop
            do! awaitUnit fixture.Runtime.Completion
            check (not fixture.Profiles.Completion.IsCompleted) "Runtime stopped shared profile storage."
        })
    }
    testTask "shared dependency termination is observed and terminates runtime" {
        do! withRuntime ServerRuntimeOptions.defaults (fun fixture -> task {
            let first = connect fixture "dependency"
            let! _ = welcome fixture first
            fixture.Profiles.Abort()
            let! failure = terminal fixture.Runtime.Completion
            check failure.IsSome "Dependency loss must be observable."
        })
    }
    testTask "late profile reply after disconnect cannot revive or reserve the old route" {
        let requests = Channel.CreateUnbounded<ProfileRequest>()
        let collect (_: AgentContext<ProfileRequest>) request = task { requests.Writer.TryWrite request |> ignore }
        let createProfiles () = Agent.Start(AgentOptions.create "controlled-profiles", collect)
        do! withRuntimeUsing ServerRuntimeOptions.defaults createProfiles (fun fixture -> task {
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
            let respond (request: ProfileRequest) =
                request.ReplyTo.TryPost { OperationId = request.OperationId; Result = Ok (ProfileOutcome.Resolved profile) }
            equal AgentTryDeliveryResult.Closed (respond oldRequest)
            equal AgentTryDeliveryResult.Posted (respond newRequest)
            let! snapshot = welcome fixture replacement
            equal 42UL snapshot.SelfPlayerId
            equal 1 snapshot.Players.Count
            let! status = stats fixture
            equal 1 status.Reservations
        })
    }
    testTask "one application deadline covers a live store that never replies" {
        let requests = Channel.CreateUnbounded<ProfileRequest>()
        let collect (_: AgentContext<ProfileRequest>) request = task { requests.Writer.TryWrite request |> ignore }
        let options = { ServerRuntimeOptions.defaults with OpenTimeoutMs = 100 }
        do! withRuntimeUsing options (fun () -> Agent.Start(AgentOptions.create "silent-profiles", collect)) (fun fixture -> task {
            let connection = connect fixture "silent"
            let! _ = receive requests
            let! closed = receive fixture.Closed
            equal connection closed
            do! empty fixture
            check (not fixture.Profiles.Completion.IsCompleted) "A live but silent shared store was killed."
        })
    }

]
