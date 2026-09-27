using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;

internal static class UdpReport
{
    internal static void Write(string input, string output)
    {
        using var source = new ETWTraceEventSource(input);
        var counts = new Dictionary<string, long>();
        var examples = new Dictionary<string, List<object>>();
        var dropGroups = new Dictionary<string, long>();
        var dropAddresses = new Dictionary<string, long>();
        var dropSizes = new Dictionary<string, long>();
        source.Dynamic.All += data =>
        {
            if (data.ProviderName is not ("Microsoft-Windows-TCPIP" or "Microsoft-Windows-Winsock-AFD")) return;
            var key = $"{data.ProviderName}/{data.EventName}/{(int)data.ID}";
            counts.TryGetValue(key, out var count);
            counts[key] = count + 1;
            if ((int)data.ID == 1033)
            {
                var group = $"{data.PayloadStringByName("Process")}|{data.PayloadStringByName("Endpoint")}|{data.PayloadStringByName("Reason")}|{data.ProcessID}";
                dropGroups.TryGetValue(group, out var previous);
                dropGroups[group] = previous + 1;
                var address = $"{data.PayloadStringByName("Endpoint")}|{data.PayloadStringByName("Address")}";
                dropAddresses.TryGetValue(address, out var addressCount);
                dropAddresses[address] = addressCount + 1;
                var size = data.PayloadByName("BufferLength")?.ToString() ?? "unknown";
                dropSizes.TryGetValue(size, out var sizeCount);
                dropSizes[size] = sizeCount + 1;
            }
            if (!examples.TryGetValue(key, out var list)) examples[key] = list = new();
            if (list.Count >= 200) return;
            var fields = new Dictionary<string, string?>();
            foreach (var name in data.PayloadNames)
            {
                fields[name] = data.PayloadByName(name) is byte[] bytes
                    ? Convert.ToHexString(bytes) : data.PayloadStringByName(name);
            }
            list.Add(new { ms = data.TimeStampRelativeMSec, utc = data.TimeStamp.ToUniversalTime(),
                pid = data.ProcessID, tid = data.ThreadID, message = data.FormattedMessage, fields });
        };
        source.Process();
        File.WriteAllText(output, JsonSerializer.Serialize(new { eventsLost = source.EventsLost,
            counts, dropGroups, dropAddresses, dropSizes, examples }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
