module Dreamsleeve.Agent.Examples.Outbound

open System
open System.Threading.Tasks
open Dreamsleeve.Agent

type OutboundCommand =
    | Send of recipient: string * text: string
    | SendAndConfirm of recipient: string * text: string * ReplyChannel<unit>

let run () = task {
    let options =
        { AgentOptions.create "outbound" with
            Mailbox = AgentMailbox.boundedWait 32
            DefaultAskTimeout = Some (TimeSpan.FromSeconds 2.) }

    // Replace with an asynchronous dependency that accepts ctx.CancellationToken.
    // This example confirms local processing, not delivery to a remote player.
    let send recipient text =
        printfn "outbound: %s <- %s" recipient text
        Task.CompletedTask

    use agent =
        Agent<OutboundCommand>.Start(options, fun ctx command -> task {
            ctx.CancellationToken.ThrowIfCancellationRequested()

            match command with
            | Send (recipient, text) ->
                do! send recipient text
            | SendAndConfirm (recipient, text, reply) ->
                do! send recipient text
                reply.Reply ()
        })

    let! posted = agent.PostAsync(Send ("global", "hello"))

    match posted with
    | AgentPostResult.Posted -> ()
    | other -> failwithf "Unexpected post result: %A" other

    do! agent.AskAsync(fun reply ->
        SendAndConfirm ("global", "ready", reply))

    agent.Complete() |> ignore
    do! agent.Completion
}
