namespace Dreamsleeve.Server.Core

open System
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

[<RequireQualifiedAccess>]
type PlayerFailure =
    | Profile of ProfileStoreError
    | DependencyUnavailable
    | InvalidReply
    | Unexpected of exn
    | Overloaded

[<RequireQualifiedAccess>]
type PlayerEvent =
    | Join of PlayerData
    | Leave of PlayerId
    | Failed of PlayerFailure

[<RequireQualifiedAccess>]
type PlayerMessage =
    | Begin
    | ProfileReplied of ProfileReply
    | ProfileDeliveryFailed of AgentSendFailure
    | Joined
    | Left
    | Update of PlayerUpdate
    | Read of ReplyChannel<Result<PlayerSnapshot, PlayerStateError>>
    | Stop

[<RequireQualifiedAccess>]
module PlayerAgent =
    type private Phase =
        | Starting
        | Resolving of Guid
        | Joining of Player
        | Active of Player
        | Leaving of Player
        | Finished

    type private State = {
        mutable Phase: Phase
        mutable Stopping: bool
        Profiles: AgentOutbox<ProfileRequest>
        Output: AgentOutbox<PlayerEvent>
    }

    let private emit state (context: AgentContext<PlayerMessage>) event =
        if not (state.Output.TrySend(context, event)) then
            context.Abort()

    let private fail state context error =
        state.Phase <- Finished
        emit state context (PlayerEvent.Failed error)

    let private leave state context player =
        state.Phase <- Leaving player
        emit state context (PlayerEvent.Leave player.Data.PlayerId)

    let private beginResolve request state (context: AgentContext<PlayerMessage>) =
        match state.Phase, context.Ref.TryReliable() with
        | Starting, Some address ->
            let operationId = Guid.NewGuid()
            let query = {
                OperationId = operationId
                Command = ProfileCommand.GetOrCreate(request.Username, request.DisplayName)
                ReplyTo = address.Map PlayerMessage.ProfileReplied
            }
            state.Phase <- Resolving operationId
            if not (state.Profiles.TrySend(context, query, PlayerMessage.ProfileDeliveryFailed)) then
                fail state context PlayerFailure.Overloaded
        | Starting, None -> context.Abort()
        | Resolving _, _ | Joining _, _ | Active _, _ | Leaving _, _ | Finished, _ -> ()

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
        | Starting | Resolving _ | Joining _ | Active _ | Leaving _ | Finished -> ()

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
        | Starting | Resolving _ -> context.Abort()
        | Joining _ | Leaving _ | Finished -> ()
        | Active player -> leave state context player

    let private handle request state (context: AgentContext<PlayerMessage>) message = task {
        match message with
        | PlayerMessage.Begin -> beginResolve request state context
        | PlayerMessage.ProfileReplied reply -> profileReply request state context reply
        | PlayerMessage.ProfileDeliveryFailed failure ->
            match failure with
            | AgentSendFailure.Closed | AgentSendFailure.Canceled -> fail state context PlayerFailure.DependencyUnavailable
            | AgentSendFailure.Faulted error -> fail state context (PlayerFailure.Unexpected error)
        | PlayerMessage.Joined ->
            match state.Phase with
            | Joining player when state.Stopping -> leave state context player
            | Joining player -> state.Phase <- Active player
            | Starting | Resolving _ | Active _ | Leaving _ | Finished -> ()
        | PlayerMessage.Left ->
            match state.Phase with
            | Leaving _ -> state.Phase <- Finished
            | Starting | Resolving _ | Joining _ | Active _ | Finished -> ()
        | PlayerMessage.Update command ->
            match state.Phase with
            | Active player when not state.Stopping -> state.Phase <- Active (update command player)
            | Starting | Resolving _ | Joining _ | Active _ | Leaving _ | Finished -> ()
        | PlayerMessage.Read reply ->
            match state.Phase with
            | Active player when not state.Stopping -> reply.Reply(Ok (Player.snapshot player))
            | Starting | Resolving _ | Joining _ | Active _ -> reply.Reply(Error PlayerStateError.NotReady)
            | Leaving _ | Finished -> reply.Reply(Error PlayerStateError.Closed)
        | PlayerMessage.Stop -> stop state context

        match state.Phase with
        | Finished -> context.Complete() |> ignore
        | Starting | Resolving _ | Joining _ | Active _ | Leaving _ -> ()
    }

    let start mailboxCapacity maxPendingEvents (request: SessionOpenRequest) profiles output =
        if mailboxCapacity < 1 || maxPendingEvents < 1 then
            Error "Player mailbox and pending event capacities must be positive."
        else
            let state = {
                Phase = Starting
                Stopping = false
                Profiles = AgentOutbox(1, profiles)
                Output = AgentOutbox(maxPendingEvents, output)
            }
            let options = {
                AgentOptions.create $"player-{request.ConnectionId}" with
                    Mailbox = AgentMailbox.boundedWait mailboxCapacity
            }
            let agent = Agent.Start(options, handle request state)
            agent.TryPost PlayerMessage.Begin |> ignore
            Ok agent
