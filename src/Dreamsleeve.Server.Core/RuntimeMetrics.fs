namespace Dreamsleeve.Server.Core

open System.Diagnostics.Metrics

/// Standard instruments; without a listener no samples are retained.
[<RequireQualifiedAccess>]
module internal RuntimeMetrics =
    let private meter = new Meter("Dreamsleeve.Server")
    let presenceFlush = meter.CreateHistogram<double>("presence.flush.duration", "ms")
    let runtimeTick = meter.CreateHistogram<double>("runtime.tick.duration", "ms")
