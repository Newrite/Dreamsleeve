module Dreamsleeve.Server.Tests.GroundMarkTests

open System
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Expecto
open Google.Protobuf
open Microsoft.Extensions.Logging.Abstractions
open Dreamsleeve.Agent
open Dreamsleeve.Server
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure
open AgentTests
open BackgroundTests

let private ok = function Ok value -> value | Error error -> failtestf "Expected success, got %A" error
let private pid raw = PlayerId.create raw |> ok
let private profile number =
    PlayerData.create (pid number) (Username.create 32 $"player{number}" |> ok) (DisplayName.create 64 $"Player {number}" |> ok)
let private formKey plugin id = FormKey.create (PluginName.create 255 plugin |> ok) (LocalFormId.create id |> ok)
let private whiterun = formKey "Skyrim.esm" 0x1A26Fu
let private riften = formKey "Skyrim.esm" 0x16BB4u
let private position x = Position.create x 0.0f 0.0f |> ok
let private placement space x = GroundMarkPlacement.create space (position x) (Radian.create 1.5f |> ok)
let private located space x =
    ValueSome (PlayerLocation.create (Location.create space (LocationName.create 128 "" |> ok)) (position x) Rotation.zero)
let private note text = GroundMarkBody.Note (GroundNoteText.create 200 text |> ok)
let private death label = GroundMarkBody.Death (DeathMarkText.create 64 label |> ok)
let private markId value = GroundMarkId.create value |> ok
// Tirdas, 17 Last Seed 4E 201, 14:05.
let private gameDate = GameDate.create 4 201 8 17 2 14 5 |> ok
let private wireDate () =
    Dreamsleeve.Protocol.Chat.GameDate(Era = 4u, Year = 201u, Month = 8u, Day = 17u, DayOfWeek = 2u, Hour = 14u, Minute = 5u)

let private collect (output: Channel<'T>) (_: AgentContext<'T>) value = task {
    check (output.Writer.TryWrite value) "Test output closed."
}
let private receive (output: Channel<'T>) = output.Reader.ReadAsync().AsTask().WaitAsync guard
let private post (agent: Agent<'T>) value = task {
    let! result = agent.PostAsync value
    equal AgentPostResult.Posted result
}
let private stop (agent: Agent<'T>) = task {
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
}

// Fast expiry checks and a small radius, so cells and distances are easy to reason about.
let private options = {
    GroundMarkOptions.defaults with
        VisibilityDistance = 100.0f; MaxPlacementDistance = 0.0f; ExpiryCheckIntervalMs = 3600000
        MaxNotesPerPlayer = 2; MaxDeathMarksPerPlayer = 2; MaxPerIndexCell = 3
        NoteRate = { Burst = 10; RefillMs = 1000; DuplicateWindowMs = 0 }; DeathMinIntervalMs = 0
}

type private Observer = {
    Subscription: Subscription<GroundMarkEvent>
    Events: Channel<GroundMarkEvent>
}

type private Fixture = {
    Marks: Agent<GroundMarkCommand>
    Writes: Channel<GroundMarkWrite>
    Host: Channel<SessionHostCommand>
    Alice: Observer
    Bob: Observer
    Cleanup: ReliableAgentRef<Guid>
    Acknowledgments: Channel<Guid>
}

let private withMarksUsing settings (loaded: StoredGroundMark list) nextId run = task {
    let writes, hostEvents, aliceEvents, bobEvents, acknowledgments =
        Channel.CreateUnbounded<GroundMarkWrite>(), Channel.CreateUnbounded<SessionHostCommand>(),
        Channel.CreateUnbounded<GroundMarkEvent>(), Channel.CreateUnbounded<GroundMarkEvent>(), Channel.CreateUnbounded<Guid>()
    use writer = Agent.Start(AgentOptions.create "writer", collect writes)
    use host = Agent.Start(AgentOptions.create "host", collect hostEvents)
    use alice = Agent.Start(AgentOptions.create "alice", collect aliceEvents)
    use bob = Agent.Start(AgentOptions.create "bob", collect bobEvents)
    use cleanup = Agent.Start(AgentOptions.create "cleanup", collect acknowledgments)
    let rules = GroundMarkOptions.rules settings |> ok
    use marks = GroundMarksAgent.start settings rules loaded nextId (writer.Ref.TryReliable().Value) (host.Ref.TryReliable().Value) NullLogger.Instance |> ok
    let observer number (agent: Agent<GroundMarkEvent>) events =
        { Subscription = { ConnectionId = Guid.NewGuid(); Profile = profile number; Events = agent.Ref.TryReliable().Value }; Events = events }
    let a, b = observer 1UL alice aliceEvents, observer 2UL bob bobEvents
    do! post marks (GroundMarkCommand.Join a.Subscription)
    do! post marks (GroundMarkCommand.Join b.Subscription)
    do! run { Marks = marks; Writes = writes; Host = hostEvents; Alice = a; Bob = b; Cleanup = cleanup.Ref.TryReliable().Value; Acknowledgments = acknowledgments }
    do! stop marks
    do! stop writer
}

let private withMarks run = withMarksUsing options [] 1UL run

let private observe fixture (observer: Observer) generation location =
    post fixture.Marks (GroundMarkCommand.Observe(observer.Subscription.ConnectionId, generation, location))

let private submit fixture (observer: Observer) requestId body place =
    post fixture.Marks (GroundMarkCommand.Place {
        ConnectionId = observer.Subscription.ConnectionId; RequestId = requestId; Body = body; Placement = place
        GameDate = gameDate; CharacterName = ValueSome (CharacterName.create 128 "Nerevar" |> ok); Pseudonym = ValueNone
        Fingerprint = Moderation.normalize (GroundMarkBody.text body); Flagged = [] })

let private placed requestId = function
    | GroundMarkEvent.Placed(actual, record, evicted) ->
        equal requestId actual
        record, evicted
    | other -> failwithf "Expected placement: %A" other

let private changed = function
    | GroundMarkEvent.Changed view -> view
    | other -> failwithf "Expected visible delta: %A" other

let private rejected requestId code = function
    | GroundMarkEvent.Rejected(actual, rejection) ->
        equal requestId actual
        equal code rejection.Code
    | other -> failwithf "Expected refusal: %A" other

let private ids (records: GroundMarkRecord list) = records |> List.map (fun record -> GroundMarkId.value record.Mark.Id)
let private removedIds (view: GroundMarkView) = view.Removed |> List.map GroundMarkId.value

// The author's own list travels beside the visible deltas; most cases look past it.
let rec private next (observer: Observer) = task {
    let! event = receive observer.Events
    match event with
    | GroundMarkEvent.Own _ -> return! next observer
    | other -> return other
}

let private own = function
    | GroundMarkEvent.Own records -> records
    | other -> failwithf "Expected own list: %A" other

// A repeated Join is a FIFO barrier: whatever the owner sent before it is already
// queued. Counts everything but own lists (the Join itself sends one).
let private settled fixture (observer: Observer) = task {
    do! post fixture.Marks (GroundMarkCommand.Join observer.Subscription)
    do! post fixture.Marks (GroundMarkCommand.Detach { ConnectionId = Guid.NewGuid(); ReplyTo = fixture.Cleanup })
    let! _ = receive fixture.Acknowledgments
    let mutable count = 0
    let mutable event = Unchecked.defaultof<GroundMarkEvent>
    while observer.Events.Reader.TryRead(&event) do
        match event with
        | GroundMarkEvent.Own _ -> ()
        | _ -> count <- count + 1
    return count
}

let private stored id author kind x (createdAt: DateTimeOffset) : StoredGroundMark =
    let body =
        match kind with
        | GroundMarkKind.Note -> note $"stored {id}"
        | GroundMarkKind.Death -> death "wolf"
    { Mark = GroundMark.create (markId id) (pid author) body (placement whiterun x) createdAt; Author = profile author }

let private agentTests = testList "GroundMarksAgent" [
    case "the own list is sent on join and again after placing, evicting, removing and expiring" (fun () ->
        let settings = { options with MaxNotesPerPlayer = 1 }
        // The death mark outlives the first expiry pass by two seconds.
        let dying = DateTimeOffset.UtcNow.AddDays(-7.0).AddSeconds(2.0)
        let loaded = [ stored 1UL 1UL GroundMarkKind.Note 0.0f DateTimeOffset.UtcNow; stored 2UL 1UL GroundMarkKind.Death 9000.0f dying ]
        withMarksUsing settings loaded 3UL (fun fixture -> task {
            // Join sends everything of the author, including the far death mark, ascending by ID.
            let! joined = receive fixture.Alice.Events
            equal [1UL; 2UL] (ids (own joined))
            equal (PublicIdentity.Profile (profile 1UL)) (own joined).Head.Author
            let! others = receive fixture.Bob.Events
            equal [] (ids (own others))
            // A placement under the note quota evicts the older note: the list follows the confirmation.
            do! observe fixture fixture.Alice 1UL (located whiterun 0.0f)
            let! _ = next fixture.Alice
            do! submit fixture fixture.Alice 5UL (note "newer") (placement whiterun 10.0f)
            let! confirmation = next fixture.Alice
            let _, evicted = placed 5UL confirmation
            equal (ValueSome 1UL) (evicted |> ValueOption.map GroundMarkId.value)
            let! replaced = receive fixture.Alice.Events
            equal [2UL; 3UL] (ids (own replaced))
            let! _ = next fixture.Alice
            do! post fixture.Marks (GroundMarkCommand.Remove(fixture.Alice.Subscription.ConnectionId, 6UL, markId 3UL, false))
            let! _ = next fixture.Alice
            let! removed = receive fixture.Alice.Events
            equal [2UL] (ids (own removed))
            let! _ = next fixture.Alice
            // Expiry of a far mark reaches the author only through the own list.
            do! Task.Delay 2500
            let now = System.Diagnostics.Stopwatch.GetTimestamp()
            do! post fixture.Marks (GroundMarkCommand.Expire { DueTimestamp = now; QueuedTimestamp = now })
            let! expired = receive fixture.Alice.Events
            equal [] (ids (own expired))
            let! count = settled fixture fixture.Bob
            equal 0 count
        }))

    case "a moderator lists, removes and clears another player's marks; whoever saw them drops them" (fun () ->
        let loaded = [ stored 1UL 1UL GroundMarkKind.Note 0.0f DateTimeOffset.UtcNow
                       stored 2UL 1UL GroundMarkKind.Death 5.0f DateTimeOffset.UtcNow
                       stored 3UL 1UL GroundMarkKind.Note 9000.0f DateTimeOffset.UtcNow ]
        withMarksUsing options loaded 4UL (fun fixture -> task {
            let alice = fixture.Alice.Subscription.Profile.PlayerId
            let moderator = fixture.Bob.Subscription.ConnectionId
            do! observe fixture fixture.Alice 1UL (located whiterun 0.0f)
            let! _ = next fixture.Alice
            // Every mark of the author, far ones included, newest first.
            do! post fixture.Marks (GroundMarkCommand.ListOf(moderator, 5UL, alice))
            match! next fixture.Bob with
            | GroundMarkEvent.AuthorMarks(5UL, author, records) ->
                equal alice author
                equal [3UL; 2UL; 1UL] (ids records)
            | other -> failwithf "Expected the author's marks: %A" other
            // Another's mark goes only when the session asks for any author.
            do! post fixture.Marks (GroundMarkCommand.Remove(moderator, 6UL, markId 3UL, false))
            let! refused = next fixture.Bob
            rejected 6UL RequestRejectionCode.GroundMarkNotFound refused
            do! post fixture.Marks (GroundMarkCommand.Remove(moderator, 7UL, markId 3UL, true))
            let! removed = next fixture.Bob
            equal (GroundMarkEvent.Removed(7UL, markId 3UL, alice)) removed
            let! remaining = receive fixture.Alice.Events
            equal [1UL; 2UL] (ids (own remaining))
            // Clearing by kind: the author's list and view both lose the death mark.
            do! post fixture.Marks (GroundMarkCommand.ClearOf(moderator, 8UL, alice, [ GroundMarkKind.Death ]))
            let! cleared = next fixture.Bob
            equal (GroundMarkEvent.Cleared(8UL, alice, [ markId 2UL ])) cleared
            let! notes = receive fixture.Alice.Events
            equal [1UL] (ids (own notes))
            let! gone = next fixture.Alice
            equal [2UL] (removedIds (changed gone))
            let! firstWrite = receive fixture.Writes
            equal (GroundMarkWrite.Delete [ markId 3UL ]) firstWrite
            let! secondWrite = receive fixture.Writes
            equal (GroundMarkWrite.Delete [ markId 2UL ]) secondWrite
            // Nothing of that kind left: an empty clearing still answers.
            do! post fixture.Marks (GroundMarkCommand.ClearOf(moderator, 9UL, alice, [ GroundMarkKind.Death ]))
            let! nothing = next fixture.Bob
            equal (GroundMarkEvent.Cleared(9UL, alice, [])) nothing
        }))

    case "a placed mark is confirmed to the author and delivered to observers in range only" (fun () ->
        withMarks (fun fixture -> task {
            do! observe fixture fixture.Alice 1UL (located whiterun 0.0f)
            do! observe fixture fixture.Bob 1UL (located whiterun 50.0f)
            do! submit fixture fixture.Alice 7UL (note "praise the sun") (placement whiterun 10.0f)
            let! confirmation = next fixture.Alice
            let record, evicted = placed 7UL confirmation
            equal "praise the sun" record.Mark.Text
            equal (PublicIdentity.Profile (profile 1UL)) record.Author
            equal (ValueSome "Nerevar") (record.Mark.CharacterName |> ValueOption.map CharacterName.value)
            equal (ValueSome gameDate) record.Mark.GameDate
            equal ValueNone evicted
            let! own = next fixture.Alice
            let! seen = next fixture.Bob
            equal [1UL] (ids (changed own).Added)
            equal [1UL] (ids (changed seen).Added)
            check (not (changed own).Clear) "A single placement is a delta, not a baseline."
            let! write = receive fixture.Writes
            match write with
            | GroundMarkWrite.Insert mark -> equal record.Mark mark
            | other -> failwithf "Expected insert: %A" other

            // Out of range and in another space see nothing; an observer without a position sees nothing.
            do! observe fixture fixture.Bob 1UL (located whiterun 500.0f)
            let! gone = next fixture.Bob
            equal [1UL] (removedIds (changed gone))
            do! submit fixture fixture.Alice 8UL (note "far") (placement riften 10.0f)
            let! _ = next fixture.Alice
            let! count = settled fixture fixture.Bob
            equal 0 count
        }))

    case "moving between cells sends added and removed deltas; another space or generation starts a new baseline" (fun () ->
        withMarks (fun fixture -> task {
            do! observe fixture fixture.Alice 1UL (located whiterun 0.0f)
            do! submit fixture fixture.Alice 1UL (note "west") (placement whiterun 0.0f)
            let! _ = next fixture.Alice
            let! _ = next fixture.Alice
            do! submit fixture fixture.Alice 2UL (note "east") (placement whiterun 300.0f)
            let! _ = next fixture.Alice
            do! observe fixture fixture.Bob 1UL (located whiterun 0.0f)
            let! first = next fixture.Bob
            equal [1UL] (ids (changed first).Added)
            check (changed first).Clear "The first position establishes a baseline."
            do! observe fixture fixture.Bob 1UL (located whiterun 250.0f)
            let! moved = next fixture.Bob
            equal [2UL] (ids (changed moved).Added)
            equal [1UL] (removedIds (changed moved))
            check (not (changed moved).Clear) "A cell change is a delta."
            check ((changed moved).ViewRevision > (changed first).ViewRevision) "Revisions increase."
            // Same cell again: nothing new.
            do! observe fixture fixture.Bob 1UL (located whiterun 260.0f)
            do! observe fixture fixture.Bob 1UL (located riften 260.0f)
            let! elsewhere = next fixture.Bob
            check (changed elsewhere).Clear "Another space clears the view."
            equal [] (ids (changed elsewhere).Added)
            do! observe fixture fixture.Bob 2UL (located whiterun 250.0f)
            let! reborn = next fixture.Bob
            check (changed reborn).Clear "A new character generation starts a new baseline."
            equal [2UL] (ids (changed reborn).Added)
            do! observe fixture fixture.Bob 2UL ValueNone
            let! lost = next fixture.Bob
            check (changed lost).Clear "Losing the position clears the view."
            let! count = settled fixture fixture.Bob
            equal 0 count
        }))

    case "only the author removes a mark; observers that saw it get a removal and storage a delete" (fun () ->
        withMarks (fun fixture -> task {
            do! observe fixture fixture.Alice 1UL (located whiterun 0.0f)
            do! observe fixture fixture.Bob 1UL (located whiterun 0.0f)
            do! submit fixture fixture.Alice 1UL (death "Alduin") (placement whiterun 0.0f)
            let! _ = next fixture.Alice
            let! _ = next fixture.Alice
            let! _ = next fixture.Bob
            let! _ = receive fixture.Writes
            do! post fixture.Marks (GroundMarkCommand.Remove(fixture.Bob.Subscription.ConnectionId, 5UL, markId 1UL, false))
            let! refused = next fixture.Bob
            rejected 5UL RequestRejectionCode.GroundMarkNotFound refused
            do! post fixture.Marks (GroundMarkCommand.Remove(fixture.Alice.Subscription.ConnectionId, 6UL, markId 9UL, false))
            let! unknown = next fixture.Alice
            rejected 6UL RequestRejectionCode.GroundMarkNotFound unknown
            do! post fixture.Marks (GroundMarkCommand.Remove(fixture.Alice.Subscription.ConnectionId, 7UL, markId 1UL, false))
            let! removed = next fixture.Alice
            equal (GroundMarkEvent.Removed(7UL, markId 1UL, fixture.Alice.Subscription.Profile.PlayerId)) removed
            let! own = next fixture.Alice
            let! seen = next fixture.Bob
            equal [1UL] (removedIds (changed own))
            equal [1UL] (removedIds (changed seen))
            let! write = receive fixture.Writes
            equal (GroundMarkWrite.Delete [markId 1UL]) write
        }))

    case "quota evicts the author's oldest mark of that kind and one delta carries both changes" (fun () ->
        withMarks (fun fixture -> task {
            do! observe fixture fixture.Bob 1UL (located whiterun 0.0f)
            for request in 1UL .. 2UL do
                do! submit fixture fixture.Alice request (note $"note {request}") (placement whiterun 0.0f)
                let! _ = next fixture.Alice
                let! _ = next fixture.Bob
                let! _ = receive fixture.Writes
                ()
            do! submit fixture fixture.Alice 3UL (death "fall") (placement whiterun 0.0f)
            let! _ = next fixture.Alice
            let! _ = next fixture.Bob
            let! _ = receive fixture.Writes
            do! submit fixture fixture.Alice 4UL (note "note 3") (placement whiterun 0.0f)
            let! confirmation = next fixture.Alice
            let record, evicted = placed 4UL confirmation
            equal 4UL (GroundMarkId.value record.Mark.Id)
            equal (ValueSome (markId 1UL)) evicted
            let! seen = next fixture.Bob
            equal [4UL] (ids (changed seen).Added)
            equal [1UL] (removedIds (changed seen))
            let! first = receive fixture.Writes
            let! second = receive fixture.Writes
            equal (GroundMarkWrite.Delete [markId 1UL]) first
            match second with
            | GroundMarkWrite.Insert mark -> equal (markId 4UL) mark.Id
            | other -> failwithf "Expected insert: %A" other
            // The author without a position of their own sees no delta.
            let! count = settled fixture fixture.Alice
            equal 0 count
        }))

    case "a full index cell refuses new marks without evicting other players' marks" (fun () ->
        withMarks (fun fixture -> task {
            for request in 1UL .. 2UL do
                do! submit fixture fixture.Alice request (note $"a{request}") (placement whiterun (float32 request))
                let! _ = next fixture.Alice
                let! _ = receive fixture.Writes
                ()
            do! submit fixture fixture.Bob 3UL (note "b1") (placement whiterun 3.0f)
            let! _ = next fixture.Bob
            let! _ = receive fixture.Writes
            do! submit fixture fixture.Bob 4UL (note "b2") (placement whiterun 4.0f)
            let! refused = next fixture.Bob
            rejected 4UL RequestRejectionCode.GroundMarkAreaFull refused
            // Alice is at her quota: her own eviction frees room in the same cell.
            do! submit fixture fixture.Alice 5UL (note "a3") (placement whiterun 5.0f)
            let! confirmation = next fixture.Alice
            let _, evicted = placed 5UL confirmation
            equal (ValueSome (markId 1UL)) evicted
            // The next cell is free.
            do! submit fixture fixture.Bob 6UL (note "b3") (placement whiterun 150.0f)
            let! accepted = next fixture.Bob
            placed 6UL accepted |> ignore
        }))

    case "notes are rate limited per account and deaths keep a minimum interval" (fun () ->
        withMarksUsing { options with NoteRate = { Burst = 1; RefillMs = 60000; DuplicateWindowMs = 60000 }; DeathMinIntervalMs = 60000 } [] 1UL (fun fixture -> task {
            do! submit fixture fixture.Alice 1UL (note "first") (placement whiterun 0.0f)
            let! _ = next fixture.Alice
            do! submit fixture fixture.Alice 2UL (note "second") (placement whiterun 0.0f)
            let! refused = next fixture.Alice
            rejected 2UL RequestRejectionCode.RateLimited refused
            do! submit fixture fixture.Bob 3UL (note "first") (placement whiterun 0.0f)
            let! other = next fixture.Bob
            placed 3UL other |> ignore
            do! submit fixture fixture.Alice 4UL (death "") (placement whiterun 0.0f)
            let! deathAccepted = next fixture.Alice
            let record, _ = placed 4UL deathAccepted
            equal "" record.Mark.Text
            do! submit fixture fixture.Alice 5UL (death "again") (placement whiterun 0.0f)
            let! tooSoon = next fixture.Alice
            rejected 5UL RequestRejectionCode.RateLimited tooSoon
        }))

    case "loaded marks are visible, expired ones are deleted and IDs continue from storage" (fun () ->
        let old = DateTimeOffset.UtcNow - TimeSpan.FromDays 8.0
        let loaded = [ stored 5UL 1UL GroundMarkKind.Death 0.0f old; stored 9UL 2UL GroundMarkKind.Note 10.0f DateTimeOffset.UtcNow ]
        withMarksUsing options loaded 12UL (fun fixture -> task {
            // The initial expiry pass removed the old death mark before anyone joined.
            let! write = receive fixture.Writes
            equal (GroundMarkWrite.Delete [markId 5UL]) write
            do! observe fixture fixture.Alice 1UL (located whiterun 0.0f)
            let! baseline = next fixture.Alice
            equal [9UL] (ids (changed baseline).Added)
            equal (PublicIdentity.Profile (profile 2UL)) (changed baseline).Added.Head.Author
            do! submit fixture fixture.Alice 1UL (note "new") (placement whiterun 0.0f)
            let! confirmation = next fixture.Alice
            let record, _ = placed 1UL confirmation
            equal 12UL (GroundMarkId.value record.Mark.Id)
        }))

    case "an expiry pass removes marks past their lifetime from observers and storage" (fun () ->
        let loaded = [ stored 3UL 1UL GroundMarkKind.Note 0.0f (DateTimeOffset.UtcNow - TimeSpan.FromDays 29.9) ]
        withMarksUsing { options with NoteTtlDays = 30 } loaded 4UL (fun fixture -> task {
            do! observe fixture fixture.Bob 1UL (located whiterun 0.0f)
            let! baseline = next fixture.Bob
            equal [3UL] (ids (changed baseline).Added)
            // Nothing expired yet.
            let now = System.Diagnostics.Stopwatch.GetTimestamp()
            do! post fixture.Marks (GroundMarkCommand.Expire { DueTimestamp = now; QueuedTimestamp = now })
            let! count = settled fixture fixture.Bob
            equal 0 count
            equal 0 fixture.Writes.Reader.Count
        }) |> fun first -> task {
            do! first
            let loaded = [ stored 3UL 1UL GroundMarkKind.Note 0.0f (DateTimeOffset.UtcNow - TimeSpan.FromDays 30.1) ]
            do! withMarksUsing { options with NoteTtlDays = 30 } loaded 4UL (fun fixture -> task {
                let! write = receive fixture.Writes
                equal (GroundMarkWrite.Delete [markId 3UL]) write
                do! observe fixture fixture.Bob 1UL (located whiterun 0.0f)
                let! count = settled fixture fixture.Bob
                equal 0 count
            })
        })

    case "a detached observer receives nothing; a second connection of a live account is an identity conflict" (fun () ->
        withMarks (fun fixture -> task {
            let intruder = { fixture.Alice.Subscription with ConnectionId = Guid.NewGuid() }
            do! post fixture.Marks (GroundMarkCommand.Join intruder)
            let! refused = receive fixture.Host
            equal (SessionHostCommand.Close(intruder.ConnectionId, "ground_marks_identity_conflict")) refused
            do! observe fixture fixture.Bob 1UL (located whiterun 0.0f)
            do! post fixture.Marks (GroundMarkCommand.Detach { ConnectionId = fixture.Bob.Subscription.ConnectionId; ReplyTo = fixture.Cleanup })
            let! ack = receive fixture.Acknowledgments
            equal fixture.Bob.Subscription.ConnectionId ack
            do! submit fixture fixture.Alice 1UL (note "quiet") (placement whiterun 0.0f)
            let! _ = next fixture.Alice
            let replacement = { fixture.Bob.Subscription with ConnectionId = Guid.NewGuid() }
            do! post fixture.Marks (GroundMarkCommand.Join replacement)
            do! post fixture.Marks (GroundMarkCommand.Observe(replacement.ConnectionId, 1UL, located whiterun 0.0f))
            let! baseline = next fixture.Bob
            equal [1UL] (ids (changed baseline).Added)
            equal 0 fixture.Host.Reader.Count
        }))
]

let private config = ServerConfig.defaults
let private codec = ProtocolCodec.create config
let private parse bytes = Dreamsleeve.Protocol.Chat.ServerPacket.Parser.ParseFrom(bytes: byte array)
let private wirePlacement x =
    Dreamsleeve.Protocol.Chat.GroundMarkPlacement(
        LocationId = Dreamsleeve.Protocol.Chat.FormKey(PluginName = "Skyrim.ESM", LocalFormId = 0x1A26Fu),
        Position = Dreamsleeve.Protocol.Chat.Position(X = x, Y = 2.0f, Z = 3.0f), Heading = 1.5f)
let private client requestId (fill: Dreamsleeve.Protocol.Chat.ClientPacket -> unit) =
    let packet = Dreamsleeve.Protocol.Chat.ClientPacket(ProtocolVersion = ProtocolCodec.Version, RequestId = requestId)
    fill packet
    ProtocolCodec.decodeClient codec (packet.ToByteArray())
let private record id x : GroundMarkRecord =
    { Mark = GroundMark.create (markId id) (pid 7UL) (note "hi\nthere") (placement whiterun x) (DateTimeOffset.FromUnixTimeMilliseconds 1700000000000L)
             |> GroundMark.withFlagged [ { Start = 0; Length = 2 } ]
      Author = PublicIdentity.Profile (profile 7UL) }

let private codecTests = testList "GroundMarkCodec" [
    testCase "placement commands decode through the domain factories with their limits" <| fun _ ->
        let decoded = client 3UL (fun packet -> packet.PlaceGroundNote <- Dreamsleeve.Protocol.Chat.PlaceGroundNote(Text = "  praise\n", Placement = wirePlacement 1.0f, GameDate = wireDate ())) |> ok
        match decoded.Command with
        | ClientCommand.PlaceGroundNote(text, place, date) ->
            equal "  praise\n" (GroundNoteText.value text)
            equal "skyrim.esm" (PluginName.value place.LocationId.PluginName)
            equal 1.5f (Radian.value place.Heading)
            equal gameDate date
        | other -> failwithf "Expected note: %A" other
        equal DeliveryLane.Control (ProtocolCodec.requestLane decoded)
        let death = client 4UL (fun packet -> packet.ReportDeath <- Dreamsleeve.Protocol.Chat.ReportDeath(Label = "", Placement = wirePlacement 1.0f, GameDate = wireDate ())) |> ok
        match death.Command with
        | ClientCommand.ReportDeath(label, _, date) ->
            equal "" (DeathMarkText.value label)
            equal gameDate date
        | other -> failwithf "Expected death: %A" other
        // The game date is required and checked for calendar ranges only.
        Expect.isError (client 11UL (fun packet -> packet.PlaceGroundNote <- Dreamsleeve.Protocol.Chat.PlaceGroundNote(Text = "x", Placement = wirePlacement 1.0f))) "missing game date"
        Expect.isError (client 12UL (fun packet -> packet.ReportDeath <- Dreamsleeve.Protocol.Chat.ReportDeath(Label = "", Placement = wirePlacement 1.0f))) "missing death date"
        let dated (change: Dreamsleeve.Protocol.Chat.GameDate -> unit) =
            let date = wireDate ()
            change date
            match client 13UL (fun packet -> packet.PlaceGroundNote <- Dreamsleeve.Protocol.Chat.PlaceGroundNote(Text = "x", Placement = wirePlacement 1.0f, GameDate = date)) with
            | Error error -> error.Failure
            | Ok _ -> failtest "Expected a refused date"
        equal (ProtocolCodecFailure.InvalidDomain(DomainError.InvalidGameDate "era")) (dated (fun date -> date.Era <- 0u))
        equal (ProtocolCodecFailure.InvalidDomain(DomainError.InvalidGameDate "year")) (dated (fun date -> date.Year <- 4000000000u))
        equal (ProtocolCodecFailure.InvalidDomain(DomainError.InvalidGameDate "month")) (dated (fun date -> date.Month <- 13u))
        equal (ProtocolCodecFailure.InvalidDomain(DomainError.InvalidGameDate "day")) (dated (fun date -> date.Month <- 2u; date.Day <- 29u))
        equal (ProtocolCodecFailure.InvalidDomain(DomainError.InvalidGameDate "day_of_week")) (dated (fun date -> date.DayOfWeek <- 7u))
        equal (ProtocolCodecFailure.InvalidDomain(DomainError.InvalidGameDate "hour")) (dated (fun date -> date.Hour <- 24u))
        equal (ProtocolCodecFailure.InvalidDomain(DomainError.InvalidGameDate "minute")) (dated (fun date -> date.Minute <- 60u))
        Expect.isOk (GameDate.create 1 1 12 31 6 23 59) "the last minute of a year"
        Expect.isOk (GameDate.create 99 99999 2 28 0 0 0) "the widest era and year"
        let removal = client 5UL (fun packet -> packet.RemoveGroundMark <- Dreamsleeve.Protocol.Chat.RemoveGroundMark(MarkId = 9UL)) |> ok
        equal (ClientCommand.RemoveGroundMark(markId 9UL)) removal.Command
        let failure result = match result with Error (error: ProtocolCodecError) -> error.Failure | Ok _ -> failtest "Expected failure"
        equal (ProtocolCodecFailure.InvalidDomain(DomainError.InvalidText("GroundNoteText", TextError.TooLong 200)))
            (failure (client 6UL (fun packet -> packet.PlaceGroundNote <- Dreamsleeve.Protocol.Chat.PlaceGroundNote(Text = String('a', 201), Placement = wirePlacement 1.0f, GameDate = wireDate ()))))
        Expect.isError (client 7UL (fun packet -> packet.PlaceGroundNote <- Dreamsleeve.Protocol.Chat.PlaceGroundNote(Text = "x", Placement = wirePlacement Single.NaN))) "non-finite position"
        Expect.isError (client 8UL (fun packet -> packet.PlaceGroundNote <- Dreamsleeve.Protocol.Chat.PlaceGroundNote(Text = "x"))) "missing placement"
        Expect.isError (client 9UL (fun packet -> packet.ReportDeath <- Dreamsleeve.Protocol.Chat.ReportDeath(Label = "two\nlines", Placement = wirePlacement 1.0f))) "multiline label"
        Expect.isError (client 10UL (fun packet -> packet.RemoveGroundMark <- Dreamsleeve.Protocol.Chat.RemoveGroundMark(MarkId = 0UL))) "zero id"

    testCase "visible deltas, placements and removals encode on the control lane with correlation where required" <| fun _ ->
        let view = { ViewRevision = 3UL; Added = [ record 1UL 1.0f; record 2UL 2.0f ]; Removed = [ markId 5UL ]; Clear = true }
        let packet = Packets.single codec (ServerResponse.GroundMarksChanged view) |> ok |> parse
        Expect.isFalse packet.HasRequestId "notification"
        equal 3UL packet.GroundMarksChanged.ViewRevision
        Expect.isTrue packet.GroundMarksChanged.Clear "baseline"
        equal [1UL; 2UL] (packet.GroundMarksChanged.Added |> Seq.map _.MarkId |> List.ofSeq)
        equal [5UL] (List.ofSeq packet.GroundMarksChanged.RemovedIds)
        let first = packet.GroundMarksChanged.Added[0]
        equal Dreamsleeve.Protocol.Chat.GroundMarkKind.Note first.Kind
        equal "hi\nthere" first.Text
        equal 7UL first.Author.PlayerId
        equal 1700000000000L first.CreatedAtUnixMs
        equal "skyrim.esm" first.Placement.LocationId.PluginName
        equal 1 first.Flagged.Count
        // A mark stored before dates were kept goes out without one.
        Expect.isNull first.GameDate "no game date"
        let datedRecord = { record 6UL 0.0f with Mark = (record 6UL 0.0f).Mark |> GroundMark.withGameDate (ValueSome gameDate) }
        let datedPacket = Packets.single codec (ServerResponse.OwnGroundMarks [ datedRecord ]) |> ok |> parse
        equal (wireDate ()) datedPacket.OwnGroundMarks.Marks[0].GameDate
        equal DeliveryLane.Control (ProtocolCodec.delivery (ServerResponse.GroundMarksChanged view)).Lane
        let placed = Packets.single codec (ServerResponse.GroundMarkPlaced(11UL, record 4UL 0.0f, ValueSome (markId 1UL))) |> ok |> parse
        equal 11UL placed.RequestId
        equal 4UL placed.GroundMarkPlaced.Mark.MarkId
        equal 1UL placed.GroundMarkPlaced.EvictedId
        let bare = Packets.single codec (ServerResponse.GroundMarkPlaced(12UL, record 4UL 0.0f, ValueNone)) |> ok |> parse
        equal 0UL bare.GroundMarkPlaced.EvictedId
        let removed = Packets.single codec (ServerResponse.GroundMarkRemoved(13UL, markId 4UL)) |> ok |> parse
        equal 13UL removed.RequestId
        equal 4UL removed.GroundMarkRemoved.MarkId
        Expect.isError (Packets.single codec (ServerResponse.GroundMarkRemoved(0UL, markId 4UL))) "correlation required"
        let ownList = Packets.single codec (ServerResponse.OwnGroundMarks [ record 4UL 0.0f; record 5UL 1.0f ]) |> ok |> parse
        equal 0UL ownList.RequestId
        equal [4UL; 5UL] (ownList.OwnGroundMarks.Marks |> Seq.map _.MarkId |> List.ofSeq)
        equal DeliveryLane.Control (ProtocolCodec.delivery (ServerResponse.OwnGroundMarks [])).Lane
        Expect.isOk (Packets.single codec (ServerResponse.OwnGroundMarks [])) "an empty own list encodes"
        Expect.isError (Packets.single codec (ServerResponse.OwnGroundMarks [ record 4UL 0.0f; record 4UL 1.0f ])) "duplicate id"
        Expect.isError (Packets.single codec (ServerResponse.OwnGroundMarks [ record 4UL 0.0f; { record 5UL 1.0f with Author = PublicIdentity.Profile (profile 8UL) } ])) "author mismatch"
        Expect.isError (Packets.single codec (ServerResponse.GroundMarksChanged { view with ViewRevision = 0UL })) "revision required"
        Expect.isError (Packets.single codec (ServerResponse.GroundMarksChanged { view with Added = []; Removed = []; Clear = false })) "empty delta"
        Expect.isError (Packets.single codec (ServerResponse.GroundMarksChanged { view with Added = [ record 1UL 1.0f; record 1UL 2.0f ]; Clear = false })) "duplicate id"
        Expect.isError (Packets.single codec (ServerResponse.GroundMarkPlaced(1UL, { record 1UL 1.0f with Author = PublicIdentity.Profile (profile 8UL) }, ValueNone))) "author mismatch"
]

let private withFile (text: string) action =
    let path = Path.Combine(Path.GetTempPath(), sprintf "dreamsleeve-marks-%O.toml" (Guid.NewGuid()))
    try
        File.WriteAllText(path, text)
        action path
    finally
        File.Delete path

let private load text =
    withFile text (fun path ->
        match Configuration.parse [|"--config"; path|] with
        | Ok (LaunchCommand.Run(settings, _)) -> Ok settings
        | Ok other -> failtestf "Unexpected launch %A" other
        | Error failure -> Error failure)

let private configurationTests = testList "GroundMarks configuration" [
    testCase "a missing section keeps the defaults and a partial section overrides only its keys" <| fun _ ->
        let settings = load "[Server]\nPort = 8778\n" |> ok
        equal GroundMarkOptions.defaults settings.GroundMarks
        equal settings.Runtime.Presence.VisibilityDistance settings.GroundMarks.VisibilityDistance
        equal 200 settings.Server.ChatInput.GroundNoteText
        equal 64 settings.Server.ChatInput.DeathMarkText
        let custom = load "[GroundMarks]\nMaxNotesPerPlayer = 1\nNoteTtlDays = 0\nVisibilityDistance = 512\n[Server.ChatInput]\nDeathMarkText = 32\n" |> ok
        equal 1 custom.GroundMarks.MaxNotesPerPlayer
        equal 0 custom.GroundMarks.NoteTtlDays
        equal 512.0f custom.GroundMarks.VisibilityDistance
        equal 10 custom.GroundMarks.MaxDeathMarksPerPlayer
        equal 32 custom.Server.ChatInput.DeathMarkText

    testCase "out-of-range keys are refused at startup" <| fun _ ->
        for invalid in [ "[GroundMarks]\nMaxNotesPerPlayer = 0\n"; "[GroundMarks]\nNoteTtlDays = -1\n"; "[GroundMarks]\nVisibilityDistance = -1\n"
                         "[GroundMarks]\nMaxPerIndexCell = 0\n"; "[GroundMarks.NoteRate]\nRefillMs = 0\n"; "[GroundMarks]\nExpiryCheckIntervalMs = 10\n"
                         "[GroundMarks]\nDeathMinIntervalMs = -5\n"; "[GroundMarks]\nUnknown = 1\n"
                         "[Server.ChatInput]\nGroundNoteText = 0\n"; "[Server.ChatInput]\nDeathMarkText = 129\n" ] do
            Expect.isError (load invalid) $"refused: {invalid}"

    testCase "the bundled example equals the defaults" <| fun _ ->
        let rec find (directory: DirectoryInfo) =
            let candidate = Path.Combine(directory.FullName, "src", "Dreamsleeve.Server", "server.example.toml")
            if File.Exists candidate then candidate
            elif isNull directory.Parent then failtest "server.example.toml not found"
            else find directory.Parent
        let settings = load (File.ReadAllText(find (DirectoryInfo AppContext.BaseDirectory))) |> ok
        equal GroundMarkOptions.defaults settings.GroundMarks
        equal ServerConfig.defaults.ChatInput settings.Server.ChatInput
]

let private token = CancellationToken.None

type private Database() =
    let directory = Path.Combine(Path.GetTempPath(), "Dreamsleeve.GroundMarkStoreTests", Guid.NewGuid().ToString("N"))
    let config = { DatabasePath = Path.Combine(directory, "marks.db"); BusyTimeoutSeconds = 1 }
    member _.Config = config
    member _.Connect() =
        let builder = Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(
            DataSource = config.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = Nullable true, Pooling = false, DefaultTimeout = config.BusyTimeoutSeconds)
        Directory.CreateDirectory directory |> ignore
        let connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString())
        connection.Open()
        connection
    member this.Execute sql =
        use connection = this.Connect()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteNonQuery() |> ignore
    member this.Scalar sql =
        use connection = this.Connect()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteScalar() :?> int64
    interface IDisposable with
        member _.Dispose() =
            let builder = Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(
                DataSource = config.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
                ForeignKeys = Nullable true, Pooling = true, DefaultTimeout = config.BusyTimeoutSeconds)
            use pooled = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString())
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool pooled
            builder.Mode <- Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate
            use bootstrap = new Microsoft.Data.Sqlite.SqliteConnection(builder.ToString())
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool bootstrap
            if Directory.Exists directory then Directory.Delete(directory, true)

let private register (database: Database) name =
    SqliteAccountStore.create database.Config (Username.create 32 name |> ok) (DisplayName.create 64 $"Display {name}" |> ok) "hash" token |> ok

let private storeTests = testList "SQLite ground marks" [
    testCase "a fresh database and a version two database both reach the current schema" (fun () ->
        use fresh = new Database()
        SqliteAccountStore.initialize fresh.Config |> ok
        equal 13L (fresh.Scalar "PRAGMA user_version")
        equal 0L (fresh.Scalar "SELECT count(*) FROM ground_marks")
        // Back to version two: the mark table, the admin tables and their migration markers are gone.
        fresh.Execute "DROP TABLE guild_invites; DROP TABLE guild_members; DROP TABLE guilds; DELETE FROM __migrondi_migrations WHERE name LIKE '%guilds%'; DROP TABLE device_bans; DROP TABLE player_devices; DELETE FROM __migrondi_migrations WHERE name LIKE '%devices%'; DROP TABLE address_bans; DROP TABLE sign_in_addresses; DELETE FROM __migrondi_migrations WHERE name LIKE '%addresses%'; DROP TABLE server_settings; DELETE FROM __migrondi_migrations WHERE name LIKE '%registration%'; DELETE FROM __migrondi_migrations WHERE name LIKE '%moderator_audit%'; DROP TABLE sanctions; DELETE FROM __migrondi_migrations WHERE name LIKE '%sanctions%'; DROP TABLE display_name_changes; DELETE FROM __migrondi_migrations WHERE name LIKE '%display_names%'; DROP TABLE admin_audit; DROP TABLE player_roles; DROP TABLE admin_api_tokens; DROP TABLE admin_sessions; DROP TABLE admin_accounts; DELETE FROM __migrondi_migrations WHERE name LIKE '%admin%'; DROP TABLE ground_marks; DELETE FROM __migrondi_migrations WHERE name LIKE '%ground_mark%'; PRAGMA user_version = 2"
        Expect.throws (fun () -> fresh.Scalar "SELECT count(*) FROM ground_marks" |> ignore) "table is gone"
        SqliteAccountStore.initialize fresh.Config |> ok
        equal 13L (fresh.Scalar "PRAGMA user_version")
        equal 13L (fresh.Scalar "SELECT count(*) FROM __migrondi_migrations")
        equal 0L (fresh.Scalar "SELECT count(*) FROM ground_marks"))

    testCase "a version three database keeps its marks and gains the pseudonym and game date columns" (fun () ->
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        let alice = register database "alice"
        let mark = GroundMark.create (markId 1UL) alice.PlayerId (note "before") (placement whiterun 1.0f) (DateTimeOffset.FromUnixTimeMilliseconds 1700000000000L)
        SqliteGroundMarkStore.insert database.Config mark token |> ok
        database.Execute "DROP TABLE guild_invites; DROP TABLE guild_members; DROP TABLE guilds; DELETE FROM __migrondi_migrations WHERE name LIKE '%guilds%'; DROP TABLE device_bans; DROP TABLE player_devices; DELETE FROM __migrondi_migrations WHERE name LIKE '%devices%'; DROP TABLE address_bans; DROP TABLE sign_in_addresses; DELETE FROM __migrondi_migrations WHERE name LIKE '%addresses%'; DROP TABLE server_settings; DELETE FROM __migrondi_migrations WHERE name LIKE '%registration%'; DELETE FROM __migrondi_migrations WHERE name LIKE '%moderator_audit%'; DROP TABLE sanctions; DELETE FROM __migrondi_migrations WHERE name LIKE '%sanctions%'; DROP TABLE display_name_changes; DELETE FROM __migrondi_migrations WHERE name LIKE '%display_names%'; DROP TABLE admin_audit; DROP TABLE player_roles; DROP TABLE admin_api_tokens; DROP TABLE admin_sessions; DROP TABLE admin_accounts; DELETE FROM __migrondi_migrations WHERE name LIKE '%admin%'; ALTER TABLE ground_marks DROP COLUMN author_pseudonym; DELETE FROM __migrondi_migrations WHERE name LIKE '%pseudonym%'; ALTER TABLE ground_marks DROP COLUMN game_era; ALTER TABLE ground_marks DROP COLUMN game_year; ALTER TABLE ground_marks DROP COLUMN game_month; ALTER TABLE ground_marks DROP COLUMN game_day; ALTER TABLE ground_marks DROP COLUMN game_day_of_week; ALTER TABLE ground_marks DROP COLUMN game_hour; ALTER TABLE ground_marks DROP COLUMN game_minute; DELETE FROM __migrondi_migrations WHERE name LIKE '%game_date%'; PRAGMA user_version = 3"
        SqliteAccountStore.initialize database.Config |> ok
        equal 13L (database.Scalar "PRAGMA user_version")
        let loaded = SqliteGroundMarkStore.loadAll database.Config token |> ok
        equal [ValueNone] (loaded.Marks |> List.map (fun entry -> entry.Mark.Pseudonym))
        equal [ValueNone] (loaded.Marks |> List.map (fun entry -> entry.Mark.GameDate))
        equal "Display alice" (DisplayName.value loaded.Marks.Head.Author.DisplayName))

    testCase "a mark placed under a pseudonym keeps it across a restart" (fun () ->
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        let alice = register database "alice"
        let pseudonym = Pseudonym.create "Страж" |> ok |> Pseudonym.numbered 12
        let mark =
            GroundMark.create (markId 1UL) alice.PlayerId (note "hidden") (placement whiterun 1.0f) (DateTimeOffset.FromUnixTimeMilliseconds 1700000000000L)
            |> GroundMark.withPseudonym (ValueSome pseudonym)
        SqliteGroundMarkStore.insert database.Config mark token |> ok
        SqliteAccountStore.initialize database.Config |> ok
        let loaded = (SqliteGroundMarkStore.loadAll database.Config token |> ok).Marks.Head
        equal (ValueSome "Страж 12") (loaded.Mark.Pseudonym |> ValueOption.map Pseudonym.value)
        // The wire shows the pseudonym, never the joined real profile.
        equal (PublicIdentity.Pseudonymous(alice.PlayerId, pseudonym)) (GroundMark.authorIdentity loaded.Author loaded.Mark))

    testCase "marks round-trip with the author's profile, survive restart and follow account deletion" (fun () ->
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        let alice = register database "alice"
        let bob = register database "bob"
        let noteMark =
            GroundMark.create (markId 1UL) alice.PlayerId (note "praise\nthe sun") (placement whiterun 10.0f) (DateTimeOffset.FromUnixTimeMilliseconds 1700000000000L)
            |> GroundMark.withCharacterName (ValueSome (CharacterName.create 128 " Ne\u0301revar " |> ok))
            |> GroundMark.withGameDate (ValueSome gameDate)
        let deathMark = GroundMark.create (markId 2UL) bob.PlayerId (death "") (placement riften -5.5f) (DateTimeOffset.FromUnixTimeMilliseconds 1700000001000L)
        SqliteGroundMarkStore.insert database.Config noteMark token |> ok
        SqliteGroundMarkStore.insert database.Config deathMark token |> ok
        SqliteAccountStore.initialize database.Config |> ok
        let loaded = SqliteGroundMarkStore.loadAll database.Config token |> ok
        equal 3UL loaded.NextId
        equal [ noteMark; deathMark ] (loaded.Marks |> List.map _.Mark)
        equal [ alice; bob ] (loaded.Marks |> List.map _.Author)
        equal (GroundMarkKind.Death) loaded.Marks[1].Mark.Kind
        equal (ValueSome " Ne\u0301revar ") (loaded.Marks[0].Mark.CharacterName |> ValueOption.map CharacterName.value)
        equal ValueNone loaded.Marks[1].Mark.CharacterName
        equal [ ValueSome gameDate; ValueNone ] (loaded.Marks |> List.map _.Mark.GameDate)
        // A partial or out-of-range stored date is damage, not a mark without a date.
        database.Execute "UPDATE ground_marks SET game_minute = NULL WHERE id = 1"
        Expect.isError (SqliteGroundMarkStore.loadAll database.Config token) "partial game date"
        database.Execute "UPDATE ground_marks SET game_minute = 5 WHERE id = 1"
        // A deleted top ID is not reused; deleting an account removes its marks.
        SqliteGroundMarkStore.delete database.Config [ markId 2UL ] token |> ok
        equal 3UL (SqliteGroundMarkStore.loadAll database.Config token |> ok).NextId
        database.Execute "DELETE FROM accounts WHERE username = 'alice'"
        let remaining = SqliteGroundMarkStore.loadAll database.Config token |> ok
        equal [] remaining.Marks
        Expect.isError (SqliteGroundMarkStore.insert database.Config noteMark token) "an unknown author is refused by the foreign key"
        Expect.isError (SqliteGroundMarkStore.insert database.Config (GroundMark.create (markId 3UL) bob.PlayerId (note "x") (placement whiterun 0.0f) DateTimeOffset.UnixEpoch) token |> Result.bind (fun () -> SqliteGroundMarkStore.insert database.Config (GroundMark.create (markId 3UL) bob.PlayerId (note "y") (placement whiterun 0.0f) DateTimeOffset.UnixEpoch) token)) "duplicate id")

    case "the writer applies inserts and deletes in order and finishes queued work before completing" (fun () -> task {
        use database = new Database()
        SqliteAccountStore.initialize database.Config |> ok
        let alice = register database "writer"
        use writer = SqliteGroundMarkStore.startWriter database.Config NullLogger.Instance 8
        let mark id = GroundMark.create (markId id) alice.PlayerId (note $"n{id}") (placement whiterun 0.0f) DateTimeOffset.UnixEpoch
        for id in 1UL .. 3UL do
            let! posted = writer.PostAsync(GroundMarkWrite.Insert (mark id))
            equal AgentPostResult.Posted posted
        let! posted = writer.PostAsync(GroundMarkWrite.Delete [ markId 2UL; markId 3UL ])
        equal AgentPostResult.Posted posted
        // A failing write is logged and does not stop the writer.
        let! failing = writer.PostAsync(GroundMarkWrite.Insert (mark 1UL))
        equal AgentPostResult.Posted failing
        let! last = writer.PostAsync(GroundMarkWrite.Insert (mark 4UL))
        equal AgentPostResult.Posted last
        do! stop writer
        let loaded = SqliteGroundMarkStore.loadAll database.Config token |> ok
        equal [1UL; 4UL] (loaded.Marks |> List.map (fun record -> GroundMarkId.value record.Mark.Id))
        equal 5UL loaded.NextId
    })
]

let tests = testList "GroundMarks" [ agentTests; codecTests; configurationTests; storeTests ]
