namespace Dreamsleeve.Server.Core

open System
open System.Threading.Tasks
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

type DescribeRequest = {
    Session: AgentRef<PlayerSessionMessage>
    Reply: ReplyChannel<AdminPlayerView option>
}

/// Lets the panel ask many sessions at once with a short deadline. A session
/// address has no Ask of its own: this relay receives the caller's Ask, hands
/// its reply channel to the session and returns at once, so the session answers
/// the caller directly and a slow session never holds up the others.
[<RequireQualifiedAccess>]
module SessionDescriber =
    let private relay _ (request: DescribeRequest) = task {
        match request.Session.TryPost(PlayerSessionMessage.Describe request.Reply) with
        | AgentPostResult.Posted -> ()
        | AgentPostResult.Full | AgentPostResult.Closed | AgentPostResult.Canceled | AgentPostResult.Dropped ->
            request.Reply.Reply None
    }

    let start capacity =
        Agent.Start({ AgentOptions.create "session-describer" with Mailbox = AgentMailbox.boundedWait capacity }, relay)

    /// None when the session did not answer in time or is not open yet.
    let describe (describer: Agent<DescribeRequest>) (timeout: TimeSpan) (session: AgentRef<PlayerSessionMessage>) : Task<AdminPlayerView option> = task {
        match! describer.TryAskAsync((fun reply -> { Session = session; Reply = reply }), timeout) with
        | AgentAskResult.Replied view -> return view
        | AgentAskResult.Faulted _ | AgentAskResult.Dropped | AgentAskResult.Full | AgentAskResult.Closed
        | AgentAskResult.TimedOut | AgentAskResult.Canceled -> return None
    }
