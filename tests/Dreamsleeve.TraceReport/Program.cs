using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;

// Offline only; neither the server nor the load generator references this tool.
if (args.Length == 3 && args[0] == "--alloc-stacks")
{
    AllocationReport.Write(args[1], args[2]);
    return 0;
}
if (args.Length == 3 && args[0] == "--udp")
{
    UdpReport.Write(args[1], args[2]);
    return 0;
}
if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Dreamsleeve.TraceReport input.nettrace output.json");
    return 2;
}

using var source = new EventPipeEventSource(args[0]);
var allocations = new Dictionary<string, (long Bytes, int Ticks)>();
var pauses = new List<Pause>();
var generations = new int[3];
var suspensions = new Dictionary<int, (double Start, string Reason)>();

source.Clr.GCAllocationTick += data =>
{
    var name = data.TypeName ?? "<unknown>";
    allocations.TryGetValue(name, out var previous);
    allocations[name] = (previous.Bytes + data.AllocationAmount64, previous.Ticks + 1);
};
source.Clr.GCStart += data =>
{
    if (data.Depth >= 0 && data.Depth < generations.Length) generations[data.Depth]++;
};
source.Clr.GCSuspendEEStart += data =>
{
    suspensions[data.ThreadID] = (data.TimeStampRelativeMSec, data.Reason.ToString());
};
source.Clr.GCRestartEEStop += data =>
{
    if (suspensions.Remove(data.ThreadID, out var start))
        pauses.Add(new Pause(start.Start, data.TimeStampRelativeMSec - start.Start, start.Reason));
};
source.Process();

var result = new
{
    trace = Path.GetFileName(args[0]),
    eventsLost = source.EventsLost,
    collectionsByGeneration = generations,
    runtimeSuspensions = pauses,
    suspensionTotalMs = pauses.Sum(p => p.DurationMs),
    suspensionMaxMs = pauses.Count == 0 ? 0 : pauses.Max(p => p.DurationMs),
    incompleteSuspensions = suspensions.Count,
    gcSuspensionTotalMs = pauses.Where(p => p.Reason is "SuspendForGC" or "SuspendForGCPrep").Sum(p => p.DurationMs),
    gcSuspensionMaxMs = pauses.Where(p => p.Reason is "SuspendForGC" or "SuspendForGCPrep").Select(p => p.DurationMs).DefaultIfEmpty().Max(),
    // AllocationTick attributes an interval to the triggering type, not an exact type total.
    sampledAllocations = allocations.OrderByDescending(p => p.Value.Bytes).Take(40)
        .Select(p => new { type = p.Key, estimatedBytes = p.Value.Bytes, ticks = p.Value.Ticks })
};
File.WriteAllText(args[1], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
return 0;

record Pause(double StartMs, double DurationMs, string Reason);
