module Dreamsleeve.Agent.Examples.Party

open System
open Dreamsleeve.Agent

type PartyState =
    { Leader: string option
      Members: Set<string>
      JoinedOrder: string list }

type JoinResult = Joined | AlreadyMember | PartyFull

type PartyCommand =
    | Join of player: string * ReplyChannel<JoinResult>
    | Leave of player: string

let private handle (_: StatefulAgentContext) (state: PartyState) command = task {
    match command with
    | Join (player, reply) when Set.contains player state.Members ->
        reply.Reply AlreadyMember
        return StatefulTransition.Stay
    | Join (_, reply) when state.Members.Count >= 4 ->
        reply.Reply PartyFull
        return StatefulTransition.Stay
    | Join (player, reply) ->
        let next =
            { Leader = state.Leader |> Option.orElse (Some player)
              Members = Set.add player state.Members
              JoinedOrder = state.JoinedOrder @ [ player ] }

        reply.Reply Joined
        return StatefulTransition.SetState next
    | Leave player ->
        let members = Set.remove player state.Members
        let joinedOrder = state.JoinedOrder |> List.filter ((<>) player)
        let leader =
            match state.Leader with
            | Some current when Set.contains current members -> Some current
            | _ -> List.tryHead joinedOrder
        return StatefulTransition.SetState
            { Leader = leader; Members = members; JoinedOrder = joinedOrder }
}

let run () = task {
    let options =
        { StatefulAgentOptions.create "party-42" with
            AgentOptions =
                { AgentOptions.create "party-42" with
                    Mailbox = AgentMailbox.boundedWait 32
                    DefaultAskTimeout = Some (TimeSpan.FromSeconds 2.) } }

    let initial = { Leader = None; Members = Set.empty; JoinedOrder = [] }

    use party = StatefulAgent.Start(options, initial, handle)

    let! joined = party.TryAskAsync(fun reply -> Join ("Nerevar", reply))
    match joined with
    | AgentAskResult.Replied result ->
        // The queued query executes after the command and its state transition.
        let! current = party.TryReadAsync id
        match current with
        | AgentAskResult.Replied snapshot ->
            printfn "party: %A; members=%d; leader=%s"
                result snapshot.Members.Count (defaultArg snapshot.Leader "none")
        | (AgentAskResult.Full | AgentAskResult.Dropped | AgentAskResult.Closed | AgentAskResult.TimedOut | AgentAskResult.Canceled | AgentAskResult.Faulted _) as failure -> eprintfn "party: read not confirmed: %A" failure
    | (AgentAskResult.Full | AgentAskResult.Dropped | AgentAskResult.Closed | AgentAskResult.TimedOut | AgentAskResult.Canceled | AgentAskResult.Faulted _) as failure ->
        eprintfn "party: join not confirmed: %A; do not retry automatically" failure

    party.Complete() |> ignore
    do! party.Completion
}
