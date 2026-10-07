namespace Dreamsleeve.Server.Core

open System.Collections.Generic

/// Sender-owned flight control, independent of configured bandwidth budgets.
/// Application ACK delay includes network, storage and receiver credit.
[<RequireQualifiedAccess>]
module ModelFlow =
    type State = private {
        Chunk: int; Maximum: int; mutable Window: double
        mutable Probing: bool; mutable Baseline: int64; mutable Smoothed: double; mutable AdjustAt: int64
        Sent: Queue<struct (int * int64)>
    }
    let create chunk chunks =
        { Chunk = chunk; Maximum = chunk * chunks; Window = double (chunk * min 2 chunks)
          Probing = true; Baseline = System.Int64.MaxValue; Smoothed = 0.0; AdjustAt = 0L; Sent = Queue() }
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
            state.Smoothed <- if state.Smoothed = 0.0 then double delay else state.Smoothed * 0.875 + double delay * 0.125
            if at >= state.AdjustAt then
                // Estimate queued bytes, rather than mistaking jitter for a
                // standing queue even when only one chunk remains in flight.
                let queued = state.Window * (state.Smoothed - double state.Baseline) / state.Smoothed
                if queued >= double state.Chunk then state.Probing <- false
                if queued > 2.0 * double state.Chunk then
                    state.Window <- max (double state.Chunk) (state.Window - double state.Chunk)
                elif queued < double state.Chunk then
                    state.Window <- min (double state.Maximum) (if state.Probing then state.Window * 2.0 else state.Window + double state.Chunk)
                state.AdjustAt <- at + state.Baseline
