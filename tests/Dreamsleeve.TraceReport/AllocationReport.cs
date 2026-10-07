using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

// Offline sampled allocation attribution. AllocationTick is not exact per-type accounting.
internal static class AllocationReport
{
    public static void Write(string input, string output)
    {
        var etlx = TraceLog.CreateFromEventPipeDataFile(input);
        using var log = new TraceLog(etlx);
        var totals = new Dictionary<string, (long Bytes, long Ticks)>();
        long missing = 0, count = 0;
        foreach (var data in log.Events)
        {
            if (data is not GCAllocationTickTraceData allocation) continue;
            count++;
            var frames = new List<string>();
            var stack = data.CallStack();
            if (stack is null) missing++;
            for (int depth = 0; stack is not null && depth < 48; depth++, stack = stack.Caller)
                frames.Add(stack.CodeAddress.FullMethodName ?? "<unknown>");
            var key = (allocation.TypeName ?? "<unknown>") + "\n" + string.Join("\n", frames);
            totals.TryGetValue(key, out var prior);
            totals[key] = (prior.Bytes + allocation.AllocationAmount64, prior.Ticks + 1);
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new {
            trace = Path.GetFileName(input), eventsLost = log.EventsLost,
            allocationTicks = count, missingStacks = missing,
            stacks = totals.OrderByDescending(x => x.Value.Bytes).Take(100).Select(x => new {
                estimatedBytes = x.Value.Bytes, ticks = x.Value.Ticks, frames = x.Key.Split('\n')
            })
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
