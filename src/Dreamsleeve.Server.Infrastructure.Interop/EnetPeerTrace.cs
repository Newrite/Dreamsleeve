using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Enet;
using enet;

namespace Dreamsleeve.Server.Infrastructure.Interop;

// Opt-in diagnostic probe. Called only on the host owner, before native pointers
// can be reset/reused by another Service call. No packet payloads are recorded.
internal sealed class EnetPeerTrace
{
    private readonly string path;
    private int records;
    private const int RecordLimit = 20000;

    private EnetPeerTrace(string directory, EnetHost host)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, $"enet-{Environment.ProcessId}-{host.Socket.Handle}.jsonl");
        Write(new { kind = "host", pid = Environment.ProcessId, socket = host.Socket.Handle.ToInt64(),
            local = host.Address.ToString(), stopwatchFrequency = Stopwatch.Frequency });
    }

    internal static EnetPeerTrace? Create(EnetHost host)
    {
        var directory = Environment.GetEnvironmentVariable("DREAMSLEEVE_ENET_TRACE_DIRECTORY");
        return string.IsNullOrEmpty(directory) ? null : new EnetPeerTrace(directory, host);
    }

    private void Write(object value)
    {
        if (records >= RecordLimit) return;
        File.AppendAllText(path, JsonSerializer.Serialize(value) + Environment.NewLine);
        records++;
        if (records == RecordLimit)
            File.AppendAllText(path, "{\"kind\":\"truncated\"}" + Environment.NewLine);
    }

    private static unsafe object Queue(ENetList* list, uint time)
    {
        var end = &list->sentinel;
        var node = end->next;
        var count = 0;
        var retries = 0;
        var attempts = 0;
        ENetOutgoingCommand* oldest = null;

        while (node != end && count < 4096)
        {
            var command = (ENetOutgoingCommand*)node;
            count++;
            attempts = Math.Max(attempts, command->sendAttempts);
            if (command->sendAttempts > 1) retries++;
            if (command->sendAttempts > 0 && (oldest == null || unchecked(time - command->sentTime) > unchecked(time - oldest->sentTime)))
                oldest = command;
            node = node->next;
        }

        object? head = oldest == null ? null : new {
            sequence = oldest->reliableSequenceNumber, command = oldest->command.header.command & 15,
            channel = oldest->command.header.channelID, attempts = oldest->sendAttempts,
            sinceSendMs = unchecked(time - oldest->sentTime), timeoutMs = oldest->roundTripTimeout,
            queueOrdinal = oldest->queueTime, fragmentOffset = oldest->fragmentOffset,
            fragmentBytes = oldest->fragmentLength
        };
        return new { count, truncated = node != end, retryCommands = retries, maxAttempts = attempts, oldest = head };
    }

    internal unsafe void Record(EnetHost host, EnetPeer peer)
    {
        if (records >= RecordLimit) return;

        var time = host.ServiceTime;
        if (peer.EarliestTimeout == 0 || unchecked(time - peer.EarliestTimeout) < 250) return;

        var p = peer.GetInner();
        Write(new {
            kind = "suspect", ticks = Stopwatch.GetTimestamp(), utc = DateTime.UtcNow,
            hostTimeMs = time, slot = p->incomingPeerID, remoteSlot = p->outgoingPeerID,
            connection = p->connectID, remote = peer.Address.ToString(), state = p->state.ToString(),
            rttMs = p->roundTripTime, rttVarianceMs = p->roundTripTimeVariance,
            receiveAgeMs = unchecked(time - p->lastReceiveTime), sendAgeMs = unchecked(time - p->lastSendTime),
            timeoutAgeMs = unchecked(time - p->earliestTimeout), nextTimeoutMs = p->nextTimeout,
            timeoutLimit = p->timeoutLimit, timeoutMinimumMs = p->timeoutMinimum, timeoutMaximumMs = p->timeoutMaximum,
            mtu = p->mtu, windowBytes = p->windowSize, inflightBytes = p->reliableDataInTransit,
            waitingBytes = (ulong)p->totalWaitingData, packetsSentWindow = p->packetsSent, packetsLostWindow = p->packetsLost,
            throttle = p->packetThrottle, throttleLimit = p->packetThrottleLimit,
            sentReliable = Queue(&p->sentReliableCommands, time),
            outgoingReliable = Queue(&p->outgoingSendReliableCommands, time),
            outgoingControl = Queue(&p->outgoingCommands, time)
        });
    }
}
