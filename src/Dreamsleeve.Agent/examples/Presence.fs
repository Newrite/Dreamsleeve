module Dreamsleeve.Agent.Examples.Presence

open System
open System.Collections.Generic
open Dreamsleeve.Agent

type PlayerPresence =
    { PlayerId: uint64
      Location: string
      X: float32
      Y: float32
      Z: float32
      Health: int }

type PresenceCommand =
    | Upsert of PlayerPresence
    | Remove of playerId: uint64 * ReplyChannel<bool>

let private handle (_: StatefulAgentContext) (players: Dictionary<uint64, PlayerPresence>) command = task {
    match command with
    | Upsert player -> players[player.PlayerId] <- player
    | Remove (playerId, reply) -> reply.Reply(players.Remove playerId)

    return MutableStatefulTransition.Stay
}

let private snapshot (players: Dictionary<uint64, PlayerPresence>) =
    players.Values
    |> Seq.sortBy (fun player -> player.PlayerId)
    |> Seq.toArray

let run () = task {
    let options =
        { MutableStatefulAgentOptions.create "presence" with
            AgentOptions =
                { AgentOptions.create "presence" with
                    Mailbox = AgentMailbox.boundedWait 128
                    DefaultAskTimeout = Some (TimeSpan.FromSeconds 2.) } }

    // The dictionary belongs exclusively to the agent from this point onward.
    use presence =
        MutableStatefulAgent.Start(options, Dictionary<uint64, PlayerPresence>(), handle)

    let! posted =
        presence.PostAsync(Upsert
            { PlayerId = 7UL; Location = "Balmora"
              X = 10.f; Y = 2.f; Z = 0.f; Health = 100 })

    match posted with
    | AgentPostResult.Posted -> ()
    | AgentPostResult.Full | AgentPostResult.Dropped | AgentPostResult.Closed | AgentPostResult.Canceled ->
        eprintfn "presence: update admission failed: %A" posted

    // Materialize INSIDE the agent: no live Dictionary/Values/seq escapes.
    // Each element is immutable, and the returned array is an independent copy.
    let! captured = presence.TryReadAsync snapshot
    match captured with
    | AgentAskResult.Replied snapshot ->
        let! removal = presence.TryAskAsync(fun reply -> Remove (7UL, reply))
        match removal with
        | AgentAskResult.Replied removed ->
            let! current = presence.TryReadAsync(fun players -> players.Count)
            match current with
            | AgentAskResult.Replied count ->
                printfn "presence: snapshot=%d; removed=%b; current=%d"
                    snapshot.Length removed count
            | (AgentAskResult.Full | AgentAskResult.Dropped | AgentAskResult.Closed | AgentAskResult.TimedOut | AgentAskResult.Canceled | AgentAskResult.Faulted _) as failure -> eprintfn "presence: count not confirmed: %A" failure
        | (AgentAskResult.Full | AgentAskResult.Dropped | AgentAskResult.Closed | AgentAskResult.TimedOut | AgentAskResult.Canceled | AgentAskResult.Faulted _) as failure ->
            eprintfn "presence: removal not confirmed: %A; do not retry automatically" failure
    | (AgentAskResult.Full | AgentAskResult.Dropped | AgentAskResult.Closed | AgentAskResult.TimedOut | AgentAskResult.Canceled | AgentAskResult.Faulted _) as failure -> eprintfn "presence: snapshot not confirmed: %A" failure

    presence.Complete() |> ignore
    do! presence.Completion
}
