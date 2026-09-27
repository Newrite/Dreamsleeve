namespace Dreamsleeve.Server.Core

open System.Diagnostics.Metrics

/// Standard instruments; without a listener no samples are retained.
[<RequireQualifiedAccess>]
module internal RuntimeMetrics =
    let private meter = new Meter("Dreamsleeve.Server")
    let presenceFlush = meter.CreateHistogram<double>("presence.flush.duration", "ms")
    let runtimeTick = meter.CreateHistogram<double>("runtime.tick.duration", "ms")
    let presenceInterval = meter.CreateHistogram<double>("presence.flush.interval", "ms")
    let presenceLateness = meter.CreateHistogram<double>("presence.flush.lateness", "ms")
    let runtimeInterval = meter.CreateHistogram<double>("runtime.tick.interval", "ms")
    let presenceTimerLateness = meter.CreateHistogram<double>("presence.timer.lateness", "ms")
    let presenceQueueDelay = meter.CreateHistogram<double>("presence.tick.queue", "ms")
    let runtimeTimerLateness = meter.CreateHistogram<double>("runtime.timer.lateness", "ms")
    let runtimeQueueDelay = meter.CreateHistogram<double>("runtime.tick.queue", "ms")
    // Encoded indivisible entries above the effective target, before transport admission.
    let movementTargetExceeded = meter.CreateHistogram<double>("movement.packet.target_exceeded.bytes", "By")

    // Each sample is one nonempty flush; sum is entries flushed for that reason.
    let movementTick = meter.CreateHistogram<double>("movement.flush.tick.entries")
    let movementBoundary = meter.CreateHistogram<double>("movement.flush.boundary.entries")
    let movementSettlement = meter.CreateHistogram<double>("movement.flush.settlement.entries")
    let movementChat = meter.CreateHistogram<double>("movement.flush.chat.entries")
    let movementMetadata = meter.CreateHistogram<double>("movement.flush.metadata.entries")
    let movementLifecycle = meter.CreateHistogram<double>("movement.flush.lifecycle.entries")
