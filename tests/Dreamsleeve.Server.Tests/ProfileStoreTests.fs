module Dreamsleeve.Server.Tests.ProfileStoreTests

open System
open System.Threading.Channels
open System.Threading.Tasks
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Infrastructure
open Expecto
open AgentTests
open BackgroundTests

let ok = function Ok value -> value | Error error -> failwithf "%A" error
let username value = Username.create 32 value |> ok
let display = DisplayName.create 64 "Player" |> ok
let start capacity = MemoryProfileStore.start { MailboxCapacity = capacity; MaxPendingReplies = 4 } |> ok
let request command (reply: AgentRef<ProfileReply>) : ProfileRequest =
    let reliable =
        reply.TryReliable()
        |> Option.defaultWith (fun () -> failwith "Test reply mailbox must be non-dropping.")

    { OperationId = Guid.NewGuid(); Command = command; ReplyTo = reliable }

let collect (output: Channel<ProfileReply>) (_: AgentContext<ProfileReply>) reply = task {
    check (output.Writer.TryWrite reply) "Test reply channel closed."
}

let receive (output: Channel<ProfileReply>) =
    output.Reader.ReadAsync().AsTask().WaitAsync guard

let send (agent: Agent<ProfileRequest>) request = task {
    let! admission = agent.Ref.PostAsync request
    equal AgentPostResult.Posted admission
}

let stop (agent: Agent<'Message>) = task {
    agent.Complete() |> ignore
    do! awaitUnit agent.Completion
}

type ReceiverMessage =
    | Hold of TaskCompletionSource<unit> * TaskCompletionSource<unit>
    | Reply of ProfileReply
    | Filler

let slowReceiver (received: TaskCompletionSource<ProfileReply>) (_: AgentContext<ReceiverMessage>) message = task {
    match message with
    | Hold(entered, release) ->
        entered.SetResult()
        do! release.Task
    | Reply reply -> received.TrySetResult reply |> ignore
    | Filler -> ()
}

let tests = testList "Profiles" [
    case "concurrent get-or-create requests resolve to one profile" (fun () -> task {
        let output = Channel.CreateUnbounded<ProfileReply>()
        use receiver = Agent.Start(AgentOptions.create "replies", collect output)
        use profiles = start 4
        let requests = [|
            for index in 0..31 ->
                let name = if index % 2 = 0 then "USER" else "user"
                let displayName = DisplayName.create 64 (sprintf "Player %d" index) |> ok
                request (ProfileCommand.GetOrCreate(username name, displayName)) receiver.Ref
        |]

        let! admissions = requests |> Array.map profiles.Ref.PostAsync |> Task.WhenAll
        check (admissions |> Array.forall ((=) AgentPostResult.Posted)) "Requests were not admitted."

        let replies = ResizeArray<ProfileReply>()
        for _ in requests do
            let! reply = receive output
            replies.Add reply

        equal (requests |> Array.map _.OperationId |> Set.ofArray) (replies |> Seq.map _.OperationId |> Set.ofSeq)
        let resolved = replies |> Seq.map (fun reply ->
            match reply.Result with
            | Ok (ProfileOutcome.Resolved profile) -> profile
            | other -> failwithf "Get-or-create failed: %A" other) |> Seq.toArray

        check (resolved |> Array.forall ((=) resolved[0])) "A canonical username resolved to different profiles."
        equal (username "user") resolved[0].Username
        equal 1UL (PlayerId.value resolved[0].PlayerId)

        do! send profiles (request (ProfileCommand.GetOrCreate(username "other", display)) receiver.Ref)
        let! other = receive output
        match other.Result with
        | Ok (ProfileOutcome.Resolved profile) -> equal 2UL (PlayerId.value profile.PlayerId)
        | result -> failwithf "Could not resolve another profile: %A" result

        do! stop profiles
        do! stop receiver
    })

    case "get-or-create preserves an existing profile and its identity" (fun () -> task {
        let output = Channel.CreateUnbounded<ProfileReply>()
        use receiver = Agent.Start(AgentOptions.create "replies", collect output)
        use profiles = start 2
        let name = username "player"
        do! send profiles (request (ProfileCommand.Create(name, display)) receiver.Ref)
        let! created = receive output
        let original =
            match created.Result with
            | Ok (ProfileOutcome.Created profile) -> profile
            | other -> failwithf "Could not create profile: %A" other

        let replacement = DisplayName.create 64 "Different name" |> ok
        do! send profiles (request (ProfileCommand.GetOrCreate(username "PLAYER", replacement)) receiver.Ref)
        let! resolved = receive output
        match resolved.Result with
        | Ok (ProfileOutcome.Resolved profile) -> equal original profile
        | other -> failwithf "Could not resolve existing profile: %A" other

        do! send profiles (request (ProfileCommand.FindByUsername name) receiver.Ref)
        let! found = receive output
        match found.Result with
        | Ok (ProfileOutcome.Found (Some profile)) -> equal original profile
        | other -> failwithf "Existing profile changed: %A" other

        do! stop profiles
        do! stop receiver
    })

    case "concurrent senders reserve a canonical username exactly once" (fun () -> task {
        let output = Channel.CreateUnbounded<ProfileReply>()
        use receiver = Agent.Start(AgentOptions.create "replies", collect output)
        use profiles = start 4
        let requests = [|
            for index in 0..31 ->
                let name = if index % 2 = 0 then "USER" else "user"
                request (ProfileCommand.Create(username name, display)) receiver.Ref
        |]

        let! admissions = requests |> Array.map profiles.Ref.PostAsync |> Task.WhenAll
        check (admissions |> Array.forall ((=) AgentPostResult.Posted)) "Requests were not admitted."
        do! stop profiles

        let replies = ResizeArray<ProfileReply>()
        for _ in requests do
            let! reply = receive output
            replies.Add reply

        equal (requests |> Array.map _.OperationId |> Set.ofArray) (replies |> Seq.map _.OperationId |> Set.ofSeq)
        let created = replies |> Seq.choose (fun reply ->
            match reply.Result with Ok (ProfileOutcome.Created player) -> Some player | _ -> None) |> Seq.toList
        equal 1 created.Length
        equal (username "user") created.Head.Username
        equal 31 (replies |> Seq.filter (fun reply ->
            match reply.Result with Error ProfileStoreError.UsernameTaken -> true | _ -> false) |> Seq.length)

        do! stop receiver
    })

    case "different profiles receive distinct stable IDs" (fun () -> task {
        let output = Channel.CreateUnbounded<ProfileReply>()
        use receiver = Agent.Start(AgentOptions.create "replies", collect output)
        use profiles = start 2
        do! send profiles (request (ProfileCommand.Create(username "one", display)) receiver.Ref)
        do! send profiles (request (ProfileCommand.Create(username "two", display)) receiver.Ref)
        let! first = receive output
        let! second = receive output

        match first.Result, second.Result with
        | Ok (ProfileOutcome.Created a), Ok (ProfileOutcome.Created b) ->
            check (a.PlayerId <> b.PlayerId) "Different profiles received the same player ID."
            check (PlayerId.value a.PlayerId <> 0UL && PlayerId.value b.PlayerId <> 0UL) "Zero ID allocated."
            do! send profiles (request (ProfileCommand.FindByUsername a.Username) receiver.Ref)
            let! found = receive output
            match found.Result with
            | Ok (ProfileOutcome.Found(Some profile)) -> equal a profile
            | other -> failwithf "Profile lost: %A" other
        | other -> failwithf "Creation failed: %A" other

        do! stop profiles
        do! stop receiver
    })

    case "unknown username returns absence without creating a profile" (fun () -> task {
        let output = Channel.CreateUnbounded<ProfileReply>()
        use receiver = Agent.Start(AgentOptions.create "replies", collect output)
        use profiles = start 2
        do! send profiles (request (ProfileCommand.FindByUsername(username "missing")) receiver.Ref)
        let! absent = receive output
        match absent.Result with
        | Ok (ProfileOutcome.Found None) -> ()
        | other -> failwithf "Unexpected lookup: %A" other

        do! send profiles (request (ProfileCommand.Create(username "missing", display)) receiver.Ref)
        let! created = receive output
        match created.Result with
        | Ok (ProfileOutcome.Created _) -> ()
        | other -> failwithf "Lookup reserved a name: %A" other

        do! stop profiles
        do! stop receiver
    })

    case "profiles outlive individual consumers" (fun () -> task {
        let firstOutput, secondOutput = Channel.CreateUnbounded<ProfileReply>(), Channel.CreateUnbounded<ProfileReply>()
        use first = Agent.Start(AgentOptions.create "first-session", collect firstOutput)
        use second = Agent.Start(AgentOptions.create "next-session", collect secondOutput)
        use profiles = start 2
        do! send profiles (request (ProfileCommand.Create(username "player", display)) first.Ref)
        let! created = receive firstOutput
        do! stop first

        do! send profiles (request (ProfileCommand.FindByUsername(username "player")) second.Ref)
        let! found = receive secondOutput
        match created.Result, found.Result with
        | Ok (ProfileOutcome.Created a), Ok (ProfileOutcome.Found(Some b)) -> equal a b
        | other -> failwithf "Profile did not survive consumer shutdown: %A" other

        do! stop profiles
        do! stop second
    })

    case "closed reply target does not roll back an accepted write" (fun () -> task {
        let output = Channel.CreateUnbounded<ProfileReply>()
        use closed = Agent.Start(AgentOptions.create "closed", collect output)
        do! stop closed
        use receiver = Agent.Start(AgentOptions.create "live", collect output)
        use profiles = start 2
        do! send profiles (request (ProfileCommand.Create(username "stored", display)) closed.Ref)
        do! send profiles (request (ProfileCommand.FindByUsername(username "stored")) receiver.Ref)
        let! found = receive output
        match found.Result with
        | Ok (ProfileOutcome.Found(Some profile)) -> equal (username "stored") profile.Username
        | other -> failwithf "Accepted write lost: %A" other

        do! stop profiles
        do! stop receiver
    })

    case "a slow reply target does not delay another caller" (fun () -> task {
        let entered, release, received = gate<unit>(), gate<unit>(), gate<ProfileReply>()
        use slow = Agent.Start(options "slow" (AgentMailbox.boundedWait 1), slowReceiver received)
        slow.TryPost(Hold(entered, release)) |> ignore
        do! awaitResult entered.Task
        slow.TryPost Filler |> ignore
        let replies = Channel.CreateUnbounded<ProfileReply>()
        use fast = Agent.Start(AgentOptions.create "fast", collect replies)
        use profiles = MemoryProfileStore.start { MailboxCapacity = 2; MaxPendingReplies = 2 } |> ok
        let create = request (ProfileCommand.Create(username "stored", display)) (slow.Ref.Map Reply)
        let lookup = request (ProfileCommand.FindByUsername(username "stored")) fast.Ref
        do! send profiles create
        do! send profiles lookup
        let! found = receive replies
        equal lookup.OperationId found.OperationId
        match found.Result with
        | Ok (ProfileOutcome.Found(Some profile)) -> equal (username "stored") profile.Username
        | other -> failwithf "The later query did not observe the write: %A" other
        profiles.Complete() |> ignore
        check (not profiles.Completion.IsCompleted) "Completion skipped the slow reply."
        release.SetResult()
        do! awaitUnit profiles.Completion
        let! saved = awaitResult received.Task
        equal create.OperationId saved.OperationId
        do! stop slow
        do! stop fast
    })

    case "mailbox bounds queued requests and completion drains them" (fun () -> task {
        let entered, release, received = gate<unit>(), gate<unit>(), gate<ProfileReply>()
        use receiver = Agent.Start(options "slow" (AgentMailbox.boundedWait 1), slowReceiver received)
        receiver.TryPost(Hold(entered, release)) |> ignore
        do! awaitResult entered.Task
        equal AgentPostResult.Posted (receiver.TryPost Filler)
        use profiles = MemoryProfileStore.start { MailboxCapacity = 1; MaxPendingReplies = 1 } |> ok
        let query = request (ProfileCommand.FindByUsername(username "missing")) (receiver.Ref.Map Reply)
        do! send profiles query
        do! eventually (fun () -> profiles.QueueLength = 0)

        // One reserved reply, one handler waiting BEFORE execution, one mailbox entry.
        do! send profiles query
        do! eventually (fun () -> profiles.QueueLength = 0)
        let second = profiles.TryPost query
        let third = profiles.TryPost query
        profiles.Complete() |> ignore
        let stoppedEarly = profiles.Completion.IsCompleted
        let afterStop = profiles.TryPost query
        release.SetResult()
        do! awaitUnit profiles.Completion

        equal AgentPostResult.Posted second
        equal AgentPostResult.Full third
        equal AgentPostResult.Closed afterStop
        check (not stoppedEarly) "Completion must drain accepted requests and replies."
        let! reply = awaitResult received.Task
        equal query.OperationId reply.OperationId
        do! stop receiver
    })

    case "abort interrupts waiting for a full reply mailbox" (fun () -> task {
        let entered, release, received = gate<unit>(), gate<unit>(), gate<ProfileReply>()
        use receiver = Agent.Start(options "slow" (AgentMailbox.boundedWait 1), slowReceiver received)
        receiver.TryPost(Hold(entered, release)) |> ignore
        do! awaitResult entered.Task
        receiver.TryPost Filler |> ignore
        use profiles = MemoryProfileStore.start { MailboxCapacity = 1; MaxPendingReplies = 1 } |> ok
        let query = request (ProfileCommand.FindByUsername(username "missing")) (receiver.Ref.Map Reply)
        do! send profiles query
        do! eventually (fun () -> profiles.QueueLength = 0)
        profiles.Abort()
        let! _ = terminal profiles.Completion
        release.SetResult()

        check profiles.Completion.IsCanceled "Abort should retain its cancellation outcome."
        equal AgentPostResult.Closed (profiles.TryPost query)
        do! stop receiver
    })

    case "dropping mailboxes cannot supply a profile reply address" (fun () -> task {
        let output = Channel.CreateUnbounded<ProfileReply>()
        for mode in [BoundedChannelFullMode.DropWrite; BoundedChannelFullMode.DropOldest; BoundedChannelFullMode.DropNewest] do
            use receiver = Agent.Start(options "drop" (AgentMailbox.bounded 1 mode), collect output)
            check (receiver.Ref.TryReliable().IsNone) "Dropping reply address was accepted."
            check ((receiver.Ref.Map id).TryReliable().IsNone) "Mapping lost the mailbox policy."
            check receiver.IsAcceptingMessages "Checking reply policy must not crash the recipient."
            do! stop receiver
    })

    case "invalid mailbox capacity does not start an agent" (fun () -> task {
        check (MemoryProfileStore.start { MailboxCapacity = 0; MaxPendingReplies = 4 } |> Result.isError) "Zero capacity accepted."
        check (MemoryProfileStore.start { MailboxCapacity = -1; MaxPendingReplies = 4 } |> Result.isError) "Negative capacity accepted."
        for limit in [0; -1] do
            check (MemoryProfileStore.start { MailboxCapacity = 1; MaxPendingReplies = limit } |> Result.isError) "Invalid reply limit accepted."
    })
]
