namespace Dreamsleeve.Server.Infrastructure

open System.Collections.Generic
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain

/// The running agent owns both profiles and the ID allocator.
/// Keep this agent alive across network restarts to retain process-local profiles.
[<RequireQualifiedAccess>]
module MemoryProfileStore =
    type private State = {
        Profiles: Dictionary<Username, PlayerData>
        mutable NextId: uint64
    }

    let private find username state =
        match state.Profiles.TryGetValue username with
        | true, profile -> ProfileOutcome.Found (Some profile)
        | false, _ -> ProfileOutcome.Found None

    let private create username displayName state =
        if state.Profiles.ContainsKey username then
            Error ProfileStoreError.UsernameTaken
        else
            match PlayerId.create state.NextId with
            | Error _ -> Error ProfileStoreError.IdExhausted
            | Ok playerId ->
                let profile = PlayerData.create playerId username displayName
                state.Profiles.Add(username, profile)
                state.NextId <- if state.NextId = System.UInt64.MaxValue then 0UL else state.NextId + 1UL

                Ok (ProfileOutcome.Created profile)

    let private execute state command =
        match command with
        | ProfileCommand.FindByUsername username -> Ok (find username state)
        | ProfileCommand.Create(username, displayName) -> create username displayName state

    let private handle state (context: AgentContext<ProfileRequest>) (request: ProfileRequest) = task {
        let result =
            if context.CancellationToken.IsCancellationRequested then
                Error ProfileStoreError.Canceled
            else
                execute state request.Command

        let reply = { OperationId = request.OperationId; Result = result }
        let! delivered = request.ReplyTo.PostAsync(reply, cancellationToken = context.CancellationToken)

        match delivered with
        | AgentDeliveryResult.Posted -> ()
        // The caller can leave before receiving a result. Already applied writes remain.
        | AgentDeliveryResult.Closed
        | AgentDeliveryResult.Canceled -> ()
    }

    /// Capacity bounds queued requests; one further request can be in its handler.
    /// Reply backpressure is awaited asynchronously before taking another request.
    let start capacity =
        if capacity < 1 then
            Error "Profile mailbox capacity must be positive."
        else
            let state = { Profiles = Dictionary<Username, PlayerData>(); NextId = 1UL }
            let options = {
                AgentOptions.create "profiles" with
                    Mailbox = AgentMailbox.boundedWait capacity
            }

            Ok (Agent.Start(options, handle state))
