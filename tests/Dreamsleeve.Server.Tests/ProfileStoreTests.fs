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
let start capacity = MemoryProfileStore.start capacity |> ok
let request command (reply: AgentRef<ProfileReply>) =
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
            do! send profiles (request (ProfileCommand.FindByUsername(username "one")) receiver.Ref)
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

    case "mailbox bounds queued requests and completion drains them" (fun () -> task {
        let entered, release, received = gate<unit>(), gate<unit>(), gate<ProfileReply>()
        use receiver = Agent.Start(options "slow" (AgentMailbox.boundedWait 1), slowReceiver received)
        receiver.TryPost(Hold(entered, release)) |> ignore
        do! awaitResult entered.Task
        equal AgentPostResult.Posted (receiver.TryPost Filler)
        use profiles = start 1
        let query = request (ProfileCommand.FindByUsername(username "missing")) (receiver.Ref.Map Reply)
        do! send profiles query
        do! eventually (fun () -> profiles.QueueLength = 0)

        // The first request is waiting to deliver its reply; only one further request fits.
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
        use profiles = start 1
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
        check (MemoryProfileStore.start 0 |> Result.isError) "Zero capacity accepted."
        check (MemoryProfileStore.start -1 |> Result.isError) "Negative capacity accepted."
    })
]
