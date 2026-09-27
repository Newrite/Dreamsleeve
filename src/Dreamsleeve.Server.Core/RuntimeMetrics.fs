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
