namespace Dreamsleeve.Server.Core

open System
open System.Threading.Tasks
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

type DescribeRequest = {
    Session: AgentRef<PlayerSessionMessage>
    Reply: ReplyChannel<Result<AdminPlayerView option, SessionDescribeError>>
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
        | AgentPostResult.Full -> request.Reply.Reply(Error SessionDescribeError.Full)
        | AgentPostResult.Closed -> request.Reply.Reply(Error SessionDescribeError.Closed)
        | AgentPostResult.Canceled -> request.Reply.Reply(Error SessionDescribeError.Canceled)
        | AgentPostResult.Dropped -> request.Reply.Reply(Error SessionDescribeError.Dropped)
    }

    let start capacity =
        Agent.TryStart({ AgentOptions.create "session-describer" with Mailbox = AgentMailbox.boundedWait capacity }, relay)

    /// The relay never waits for a session; every caller owns its bounded ask.
    let describe (describer: Agent<DescribeRequest>) (timeout: TimeSpan) (session: AgentRef<PlayerSessionMessage>) : Task<Result<AdminPlayerView option, SessionDescribeError>> = task {
        match! describer.TryAskAsync((fun reply -> { Session = session; Reply = reply }), timeout) with
        | AgentAskResult.Replied view -> return view
        | AgentAskResult.InvalidRequest error -> return Error (SessionDescribeError.InvalidRequest error)
        | AgentAskResult.Faulted error -> return Error (SessionDescribeError.Faulted error)
        | AgentAskResult.Full -> return Error SessionDescribeError.Full
        | AgentAskResult.Closed -> return Error SessionDescribeError.Closed
        | AgentAskResult.Canceled -> return Error SessionDescribeError.Canceled
        | AgentAskResult.Dropped -> return Error SessionDescribeError.Dropped
        | AgentAskResult.TimedOut -> return Error SessionDescribeError.TimedOut
    }
