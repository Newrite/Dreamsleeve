namespace Dreamsleeve.Server.Core

open System.Collections.Generic
open Dreamsleeve.Server.Domain

/// Admission per stable account: a token bucket plus a short memory of
/// recent normalized texts. Reconnecting does not reset it; refused attempts
/// consume nothing. Owned by one agent, like the state it protects.
[<RequireQualifiedAccess>]
module internal RateLimit =
    [<Literal>]
    let MaxRecentPerSender = 8

    [<RequireQualifiedAccess>]
    type Refusal =
        | Repeated
        | Exhausted

    type Sender = {
        mutable Tokens: float
        mutable Updated: int64
        Recent: Queue<struct (string * int64)>
    }

    type State = {
        Options: RateLimitOptions
        Senders: Dictionary<PlayerId, Sender>
        mutable NextPrune: int64
    }

    let create options = {
        Options = options
        Senders = Dictionary()
        NextPrune = 0L
    }

    let private refill options now (sender: Sender) =
        let elapsed = float (now - sender.Updated) / float options.RefillMs
        sender.Tokens <- min (float options.Burst) (sender.Tokens + elapsed)
        sender.Updated <- now

        while sender.Recent.Count > 0
              && (let struct (_, sentAt) = sender.Recent.Peek() in now - sentAt >= int64 options.DuplicateWindowMs) do
            sender.Recent.Dequeue() |> ignore

    // Entries of accounts that became idle are dropped; state stays bounded by
    // recent senders, not by everyone who ever passed through.
    let private prune state now =
        if now >= state.NextPrune then
            let idle = max (int64 state.Options.Burst * int64 state.Options.RefillMs) (int64 state.Options.DuplicateWindowMs)
            let expired =
                state.Senders
                |> Seq.filter (fun entry -> now - entry.Value.Updated >= idle)
                |> Seq.map _.Key
                |> Seq.toArray

            for playerId in expired do
                state.Senders.Remove playerId |> ignore

            state.NextPrune <- now + max 1000L idle

    /// One attempt of this account at now (Environment.TickCount64 milliseconds).
    let admit state now playerId (fingerprint: string) =
        prune state now

        let sender =
            match state.Senders.TryGetValue playerId with
            | true, sender -> sender
            | false, _ ->
                let sender = {
                    Tokens = float state.Options.Burst
                    Updated = now
                    Recent = Queue()
                }
                state.Senders[playerId] <- sender
                sender

        refill state.Options now sender

        let repeated =
            state.Options.DuplicateWindowMs > 0
            && sender.Recent |> Seq.exists (fun struct (recent, _) -> recent = fingerprint)

        if repeated then
            Error Refusal.Repeated
        elif sender.Tokens < 1.0 then
            Error Refusal.Exhausted
        else
            sender.Tokens <- sender.Tokens - 1.0
            if state.Options.DuplicateWindowMs > 0 then
                if sender.Recent.Count >= MaxRecentPerSender then
                    sender.Recent.Dequeue() |> ignore
                sender.Recent.Enqueue(struct (fingerprint, now))
            Ok ()
