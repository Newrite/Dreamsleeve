module Dreamsleeve.Agent.Examples.Outbound

open System
open System.Threading.Tasks
open Dreamsleeve.Agent

type OutboundCommand =
    | Send of recipient: string * text: string
    | SendAndConfirm of recipient: string * text: string * ReplyChannel<unit>

let private handle (send: string -> string -> Task) (context: AgentContext<OutboundCommand>) command = task {
    match command with
    | Send (recipient, text) ->
        if not context.CancellationToken.IsCancellationRequested then
            do! send recipient text
    | SendAndConfirm (recipient, text, reply) ->
        if context.CancellationToken.IsCancellationRequested then reply.Cancel()
        else
            do! send recipient text
            reply.Reply ()
}

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

    match Agent.TryStart(options, handle send) with
    | Error error -> eprintfn "agent: startup rejected: %A" error
    | Ok owner ->
        use agent = owner

        let! posted = agent.PostAsync(Send ("global", "hello"))

        match posted with
        | AgentPostResult.Posted -> ()
        | AgentPostResult.Full | AgentPostResult.Dropped | AgentPostResult.Closed | AgentPostResult.Canceled ->
            eprintfn "outbound: message admission failed: %A" posted

        let! confirmed = agent.TryAskAsync(fun reply ->
            SendAndConfirm ("global", "ready", reply))
        match confirmed with
        | AgentAskResult.Replied () -> ()
        | (AgentAskResult.Full | AgentAskResult.Dropped | AgentAskResult.Closed | AgentAskResult.TimedOut | AgentAskResult.Canceled | AgentAskResult.Faulted _ | AgentAskResult.InvalidRequest _) as failure ->
            eprintfn "outbound: command not confirmed: %A; do not retry automatically" failure

        agent.Complete() |> ignore
        do! agent.Completion
}
