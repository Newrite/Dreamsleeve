module Dreamsleeve.Agent.Examples.Program

[<EntryPoint>]
let main _ =
    let work = task {
        do! Outbound.run ()
        do! Party.run ()
        do! Presence.run ()
    }
    work.GetAwaiter().GetResult()
    0
