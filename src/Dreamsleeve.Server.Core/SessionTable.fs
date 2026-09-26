namespace Dreamsleeve.Server.Core

open System
open System.Collections.Generic
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

/// These records are touched only by the runtime handler, never by session agents.
[<RequireQualifiedAccess>]
module internal SessionTable =
    type Phase = Waiting | Opening | Ready | Closing

    type Entry = {
        ConnectionId: Guid
        mutable Phase: Phase
        mutable Deadline: int64
        mutable PlayerId: PlayerId option
        mutable Child: Agent<PlayerSessionMessage> option
        mutable ChildStopped: bool
        mutable ChatDetached: bool
        mutable PresenceDetached: bool
    }

    type State = {
        Connections: Dictionary<Guid, Entry>
        Players: Dictionary<PlayerId, Guid>
    }

    let create () = { Connections = Dictionary(); Players = Dictionary() }

    let add connectionId deadline state =
        let entry = {
            ConnectionId = connectionId; Phase = Waiting; Deadline = deadline
            PlayerId = None; Child = None; ChildStopped = false
            ChatDetached = false; PresenceDetached = false
        }
        state.Connections.Add(connectionId, entry)
        entry

    let find connectionId state =
        match state.Connections.TryGetValue connectionId with
        | true, entry -> Some entry
        | false, _ -> None

    let reserve playerId entry state =
        match entry.Phase, entry.PlayerId with
        | Opening, None when not (state.Players.ContainsKey playerId) ->
            state.Players.Add(playerId, entry.ConnectionId)
            entry.PlayerId <- Some playerId
            IdentityAdmission.Reserved
        | Opening, None | Opening, Some _ -> IdentityAdmission.AlreadyInUse
        | Waiting, _ | Ready, _ | Closing, _ -> IdentityAdmission.Closed

    /// A reservation survives transport removal and is freed only after cleanup.
    let remove entry state =
        state.Connections.Remove entry.ConnectionId |> ignore
        match entry.PlayerId with
        | Some playerId ->
            match state.Players.TryGetValue playerId with
            | true, owner when owner = entry.ConnectionId -> state.Players.Remove playerId |> ignore
            | true, _ | false, _ -> ()
        | None -> ()
