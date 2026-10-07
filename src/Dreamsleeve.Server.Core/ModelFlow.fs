namespace Dreamsleeve.Server.Core

open System.Collections.Generic

/// Sender-owned flight control, independent of configured bandwidth budgets.
/// Application ACK delay includes network, storage and receiver credit.
[<RequireQualifiedAccess>]
module ModelFlow =
    type State = private {
        Chunk: int; Maximum: int; mutable Window: double
        mutable Baseline: int64; mutable Smoothed: double; mutable AdjustAt: int64
        Sent: Queue<struct (int * int64)>
    }
    let create chunk chunks =
        { Chunk = chunk; Maximum = chunk * chunks; Window = double (chunk * min 2 chunks)
          Baseline = System.Int64.MaxValue; Smoothed = 0.0; AdjustAt = 0L; Sent = Queue() }
    let allows flight bytes state = double flight + double bytes <= state.Window
    let sent offset at state = state.Sent.Enqueue(struct (offset, at))
    let acknowledge offset at state =
        let mutable sample = None
        while state.Sent.Count > 0 && (let struct (endOffset, _) = state.Sent.Peek() in endOffset <= offset) do
            let struct (_, sentAt) = state.Sent.Dequeue()
            sample <- Some sentAt
        match sample with
        | None -> () // Duplicate/partial ACK cannot grow the window.
        | Some sentAt ->
            let delay = max 1L (at - sentAt)
            state.Baseline <- min state.Baseline delay
            let target = max 25.0 (double state.Baseline / 4.0)
            let bounded = min (double delay) (double state.Baseline + 4.0 * target)
            state.Smoothed <- if state.Smoothed = 0.0 then bounded else state.Smoothed * 0.875 + bounded * 0.125
            if at >= state.AdjustAt then
                // Adjust once per baseline RTT, preserving headroom for control/poses.
                state.Window <-
                    if state.Smoothed > double state.Baseline + target then max (double state.Chunk) (state.Window * 0.75)
                    else min (double state.Maximum) (state.Window + double state.Chunk)
                state.AdjustAt <- at + state.Baseline
