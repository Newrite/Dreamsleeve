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
        mutable TransportClosed: bool
        mutable ChatDetached: bool
        mutable SystemDetached: bool
        mutable PresenceDetached: bool
        mutable GroundMarksDetached: bool
    }

    type State = {
        Connections: Dictionary<Guid, Entry>
        Players: Dictionary<PlayerId, Guid>
        /// Names shown for each reserved player and the pseudonyms of hidden ones;
        /// freed together with the PlayerId reservation.
        Names: PseudonymBook
    }

    let create dictionary = { Connections = Dictionary(); Players = Dictionary(); Names = PseudonymBook.create dictionary }

    let add connectionId deadline state =
        let entry = {
            ConnectionId = connectionId; Phase = Waiting; Deadline = deadline
            PlayerId = None; Child = None; ChildStopped = false; TransportClosed = false
            ChatDetached = false; SystemDetached = false; PresenceDetached = false; GroundMarksDetached = false
        }
        state.Connections.Add(connectionId, entry)
        entry

    let find connectionId state =
        match state.Connections.TryGetValue connectionId with
        | true, entry -> Some entry
        | false, _ -> None

    /// Reserves the account and the names it is shown under; hiding picks a
    /// pseudonym with pick (an index below the dictionary size).
    let reserve (profile: PlayerData) hiding pick entry state =
        let playerId = profile.PlayerId
        match entry.Phase, entry.PlayerId with
        | Opening, None when not (state.Players.ContainsKey playerId) ->
            state.Players.Add(playerId, entry.ConnectionId)
            entry.PlayerId <- Some playerId
            IdentityAdmission.Reserved(PseudonymBook.apply pick hiding profile state.Names)
        | Opening, None | Opening, Some _ -> IdentityAdmission.AlreadyInUse
        | Waiting, _ | Ready, _ | Closing, _ -> IdentityAdmission.Closed

    /// See PseudonymBook.apply. None when the connection is not a ready session
    /// holding its reservation.
    let changeIdentity hiding pick (entry: Entry) state =
        match entry.Phase, entry.PlayerId |> Option.map (fun playerId -> PseudonymBook.tryProfile playerId state.Names) with
        | Ready, Some (ValueSome profile) -> Some (PseudonymBook.apply pick hiding profile state.Names)
        | (Waiting | Opening | Ready | Closing), _ -> None

    let domainClean (entry: Entry) =
        entry.ChildStopped && entry.ChatDetached && entry.SystemDetached && entry.PresenceDetached && entry.GroundMarksDetached

    let clean (entry: Entry) = domainClean entry && entry.TransportClosed

    /// A reservation survives transport removal and is freed only after cleanup.
    let remove entry state =
        state.Connections.Remove entry.ConnectionId |> ignore
        match entry.PlayerId with
        | Some playerId ->
            match state.Players.TryGetValue playerId with
            | true, owner when owner = entry.ConnectionId ->
                state.Players.Remove playerId |> ignore
                PseudonymBook.release playerId state.Names
            | true, _ | false, _ -> ()
        | None -> ()
