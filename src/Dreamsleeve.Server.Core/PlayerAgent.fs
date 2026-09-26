namespace Dreamsleeve.Server.Core

open System
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

/// A new ID per transport connection, independent of reusable ENet peer slots.
type SessionOpenRequest = {
    ConnectionId: Guid
    RequestId: uint64
    Username: Username
    DisplayName: DisplayName
}

[<RequireQualifiedAccess>]
type PlayerFailure =
    | Profile of ProfileStoreError
    | DependencyUnavailable
    | InvalidReply
    | Unexpected of exn
    | Overloaded

[<RequireQualifiedAccess>]
type PlayerStateError =
    | NotReady
    | Closed
    | Busy

/// Validated domain input from an adapter; no wire telemetry contract is implied.
[<RequireQualifiedAccess>]
type PlayerUpdate =
    | BeginCharacter of CharacterName
    | RenameCharacter of CharacterName
    | SetLocation of PlayerLocation
    | ClearLocation
    | SetActorValues of (ActorValueKey * ActorValueInfo) list
    | LeaveGame

[<RequireQualifiedAccess>]
type PlayerEvent =
    | Join of PlayerData
    | Leave of PlayerId
    | Failed of PlayerFailure

[<RequireQualifiedAccess>]
type PlayerMessage =
    | Begin
    | ProfileReplied of ProfileReply
    | ProfileAdmissionFinished of Result<AgentDeliveryResult, exn>
    | OutputAdmissionFinished of Result<AgentDeliveryResult, exn>
    | Joined
    | Left
    | Update of PlayerUpdate
    | Read of ReplyChannel<Result<PlayerSnapshot, PlayerStateError>>
    | Stop

[<RequireQualifiedAccess>]
module PlayerAgent =
    type private Phase =
        | Resolving of Guid
        | Joining of Player
        | Active of Player
        | Leaving of Player
        | Finished

    type private State = {
        mutable Phase: Phase
        mutable Stopping: bool
        mutable ProfileSending: bool
        Output: AgentOutbox<PlayerEvent>
    }

    let private emit state (context: AgentContext<PlayerMessage>) event =
        if state.Output.TryEnqueue event then
            state.Output.Pump(context, PlayerMessage.OutputAdmissionFinished)
        else
            context.Abort()

    let private fail state context error =
        state.Phase <- Finished
        emit state context (PlayerEvent.Failed error)

    let private leave state context player =
        state.Phase <- Leaving player
        emit state context (PlayerEvent.Leave player.Data.PlayerId)

    let private resolve (profiles: ReliableAgentRef<ProfileRequest>) reply token =
        profiles.PostAsync(reply, cancellationToken = token)

    let private beginResolve request profiles state (context: AgentContext<PlayerMessage>) =
        match state.Phase, context.Ref.TryReliable() with
        | Resolving operationId, Some address when not state.ProfileSending ->
            let query = {
                OperationId = operationId
                Command = ProfileCommand.GetOrCreate(request.Username, request.DisplayName)
                ReplyTo = address.Map PlayerMessage.ProfileReplied
            }
            state.ProfileSending <- true
            context.PipeToSelf(resolve profiles query, PlayerMessage.ProfileAdmissionFinished)
        | Resolving _, None -> context.Abort()
        | Resolving _, Some _ | Joining _, _ | Active _, _ | Leaving _, _ | Finished, _ -> ()

    let private profileReply request state context (reply: ProfileReply) =
        match state.Phase with
        | Resolving operationId when operationId = reply.OperationId ->
            match reply.Result with
            | Ok (ProfileOutcome.Resolved profile) ->
                if profile.Username <> request.Username then
                    fail state context PlayerFailure.InvalidReply
                else
                    let player = Player.create profile
                    state.Phase <- Joining player
                    emit state context (PlayerEvent.Join profile)
            | Ok (ProfileOutcome.Found _ | ProfileOutcome.Created _) ->
                fail state context PlayerFailure.InvalidReply
            | Error error -> fail state context (PlayerFailure.Profile error)
        | Resolving _ | Joining _ | Active _ | Leaving _ | Finished -> ()

    let private update command player =
        match command with
        | PlayerUpdate.BeginCharacter name -> Player.beginCharacter name player
        | PlayerUpdate.RenameCharacter name -> Player.withCharacterName name player
        | PlayerUpdate.SetLocation location -> Player.withLocation location player
        | PlayerUpdate.ClearLocation -> Player.clearLocation player
        | PlayerUpdate.SetActorValues updates ->
            Player.setActorValues (List.toArray updates) player
            player
        | PlayerUpdate.LeaveGame -> Player.clearGameState player

    let private stop state (context: AgentContext<PlayerMessage>) =
        state.Stopping <- true
        match state.Phase with
        // GetOrCreate may finish after cancellation; it cannot create channel membership.
        | Resolving _ -> context.Abort()
        | Joining _ | Leaving _ | Finished -> ()
        | Active player -> leave state context player

    let private handle request profiles state (context: AgentContext<PlayerMessage>) message = task {
        match message with
        | PlayerMessage.Begin -> beginResolve request profiles state context
        | PlayerMessage.ProfileReplied reply -> profileReply request state context reply
        | PlayerMessage.ProfileAdmissionFinished result ->
            state.ProfileSending <- false
            match result with
            | Ok AgentDeliveryResult.Posted -> ()
            | Ok (AgentDeliveryResult.Closed | AgentDeliveryResult.Canceled) -> fail state context PlayerFailure.DependencyUnavailable
            | Error error -> fail state context (PlayerFailure.Unexpected error)
        | PlayerMessage.OutputAdmissionFinished result ->
            state.Output.Acknowledge()
            match result with
            | Ok AgentDeliveryResult.Posted -> state.Output.Pump(context, PlayerMessage.OutputAdmissionFinished)
            | Ok (AgentDeliveryResult.Closed | AgentDeliveryResult.Canceled) | Error _ -> context.Abort()
        | PlayerMessage.Joined ->
            match state.Phase with
            | Joining player when state.Stopping -> leave state context player
            | Joining player -> state.Phase <- Active player
            | Resolving _ | Active _ | Leaving _ | Finished -> ()
        | PlayerMessage.Left ->
            match state.Phase with
            | Leaving _ -> state.Phase <- Finished
            | Resolving _ | Joining _ | Active _ | Finished -> ()
        | PlayerMessage.Update command ->
            match state.Phase with
            | Active player when not state.Stopping -> state.Phase <- Active (update command player)
            | Resolving _ | Joining _ | Active _ | Leaving _ | Finished -> ()
        | PlayerMessage.Read reply ->
            match state.Phase with
            | Active player when not state.Stopping -> reply.Reply(Ok (Player.snapshot player))
            | Resolving _ | Joining _ | Active _ -> reply.Reply(Error PlayerStateError.NotReady)
            | Leaving _ | Finished -> reply.Reply(Error PlayerStateError.Closed)
        | PlayerMessage.Stop -> stop state context

        match state.Phase with
        | Finished when not state.ProfileSending && state.Output.IsEmpty -> context.Complete() |> ignore
        | Resolving _ | Joining _ | Active _ | Leaving _ | Finished -> ()
    }

    let start mailboxCapacity maxPendingEvents request profiles output =
        if mailboxCapacity < 1 || maxPendingEvents < 1 then
            Error "Player mailbox and pending event capacities must be positive."
        else
            let state = {
                Phase = Resolving(Guid.NewGuid())
                Stopping = false
                ProfileSending = false
                Output = AgentOutbox(maxPendingEvents, output)
            }
            let options = {
                AgentOptions.create $"player-{request.ConnectionId}" with
                    Mailbox = AgentMailbox.boundedWait mailboxCapacity
            }
            let agent = Agent.Start(options, handle request profiles state)
            agent.TryPost PlayerMessage.Begin |> ignore
            Ok agent
