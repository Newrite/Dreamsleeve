namespace Dreamsleeve.Server.Infrastructure

open System.Collections.Generic
open Dreamsleeve.Agent
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Domain

type MemoryProfileStoreConfig = {
    MailboxCapacity: int
    MaxPendingReplies: int
}

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
                let profile = PlayerData.create playerId username displayName NameColor.unknown
                state.Profiles.Add(username, profile)
                state.NextId <- if state.NextId = System.UInt64.MaxValue then 0UL else state.NextId + 1UL

                Ok profile

    let private getOrCreate username displayName state =
        match state.Profiles.TryGetValue username with
        | true, profile -> Ok profile
        | false, _ -> create username displayName state

    let private execute state command =
        match command with
        | ProfileCommand.FindByUsername username -> Ok (find username state)
        | ProfileCommand.Create(username, displayName) ->
            create username displayName state |> Result.map ProfileOutcome.Created
        | ProfileCommand.GetOrCreate(username, displayName) ->
            getOrCreate username displayName state |> Result.map ProfileOutcome.Resolved

    let private reply state (request: ProfileRequest) = {
        OperationId = request.OperationId
        Result = execute state request.Command
    }

    /// Replies are independent; capacity is reserved before executing a command.
    let start config =
        if config.MailboxCapacity < 1 || config.MaxPendingReplies < 1 then
            Error "Profile mailbox and pending reply capacities must be positive."
        else
            let state = { Profiles = Dictionary<Username, PlayerData>(); NextId = 1UL }
            let options = {
                AgentOptions.create "profiles" with
                    Mailbox = AgentMailbox.boundedWait config.MailboxCapacity
            }

            let handle = AgentReplyDispatcher.createHandler config.MaxPendingReplies (fun request -> request.ReplyTo) (reply state)
            Ok (Agent.Start(options, handle))
