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

let run () = task {
    let options =
        { MutableStatefulAgentOptions.create "presence" with
            AgentOptions =
                { AgentOptions.create "presence" with
                    Mailbox = AgentMailbox.boundedWait 128
                    DefaultAskTimeout = Some (TimeSpan.FromSeconds 2.) } }

    // The dictionary belongs exclusively to the agent from this point onward.
    use presence =
        MutableStatefulAgent<Dictionary<uint64, PlayerPresence>, PresenceCommand>.Start(
            options, Dictionary<uint64, PlayerPresence>(),
            fun _ players command -> task {
                match command with
                | Upsert player -> players[player.PlayerId] <- player
                | Remove (playerId, reply) -> reply.Reply(players.Remove playerId)
                return MutableStatefulTransition.Stay
            })

    let! posted =
        presence.PostAsync(Upsert
            { PlayerId = 7UL; Location = "Balmora"
              X = 10.f; Y = 2.f; Z = 0.f; Health = 100 })
    match posted with
    | AgentPostResult.Posted -> ()
    | other -> failwithf "Unexpected post result: %A" other

    // Materialize INSIDE the agent: no live Dictionary/Values/seq escapes.
    // Each element is immutable, and the returned array is an independent copy.
    let! snapshot =
        presence.ReadAsync(fun players ->
            players.Values
            |> Seq.sortBy (fun player -> player.PlayerId)
            |> Seq.toArray)

    let! removed = presence.AskAsync(fun reply -> Remove (7UL, reply))
    let! count = presence.ReadAsync(fun players -> players.Count)
    printfn "presence: snapshot=%d; removed=%b; current=%d"
        snapshot.Length removed count

    presence.Complete() |> ignore
    do! presence.Completion
}
