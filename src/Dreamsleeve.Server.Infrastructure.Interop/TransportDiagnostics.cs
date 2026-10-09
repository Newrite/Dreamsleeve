using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Sockets;
using Enet;

namespace Dreamsleeve.Server.Infrastructure.Interop;

// Access on the ENet owner only. No peer pointers or socket ownership escape.
public sealed class TransportDiagnostics
{
    private static readonly Meter Meter = new("Dreamsleeve.Transport");
    private static readonly Histogram<double> PollDuration = Meter.CreateHistogram<double>("transport.poll.duration", "ms");
    private static readonly Histogram<double> PollInterval = Meter.CreateHistogram<double>("transport.poll.interval", "ms");
    private static readonly Histogram<double> Events = Meter.CreateHistogram<double>("transport.poll.events");
    private static readonly Histogram<double> SendDuration = Meter.CreateHistogram<double>("transport.enqueue.duration", "ms");
    private static readonly Histogram<double> PacketSize = Meter.CreateHistogram<double>("transport.enqueue.bytes", "By");
    private static readonly Histogram<double> PendingBytes = Meter.CreateHistogram<double>("transport.pending.bytes", "By");
    private static readonly Histogram<double> PendingPackets = Meter.CreateHistogram<double>("transport.pending.packets");
    private static readonly Histogram<double> SentPackets = Meter.CreateHistogram<double>("transport.udp.sent.total");
    private static readonly Histogram<double> ReceivedPackets = Meter.CreateHistogram<double>("transport.udp.received.total");
    private static readonly Histogram<double> Rtt = Meter.CreateHistogram<double>("transport.peer.rtt.max", "ms");
    private static readonly Histogram<double> InTransit = Meter.CreateHistogram<double>("transport.reliable.inflight.bytes", "By");
    private static readonly Histogram<double> Loss = Meter.CreateHistogram<double>("transport.peer.loss.window");
    private static readonly Histogram<double> ReceiveBuffer = Meter.CreateHistogram<double>("transport.socket.receive.bytes", "By");
    private static readonly Histogram<double> SendBuffer = Meter.CreateHistogram<double>("transport.socket.send.bytes", "By");
    // Accepted logical packets needing fragmentation, not measured UDP sends/retries.
    private static readonly Histogram<double> FragmentedPacketSize = Meter.CreateHistogram<double>("transport.fragmentation.accepted.bytes", "By");

    public static void RecordAcceptedPacket(EnetPeer peer, int bytes)
    {
        if (FragmentedPacketSize.Enabled && bytes > OutgoingPackets.GetUnfragmentedPayloadBytes(peer))
            FragmentedPacketSize.Record(bytes);
    }

    private static readonly Histogram<double> TimeoutAge = Meter.CreateHistogram<double>("transport.peer.timeout.age.max", "ms");
    private static readonly Histogram<double> ReceiveAge = Meter.CreateHistogram<double>("transport.peer.receive.age.max", "ms");
    private EnetPeerTrace? peerTrace;
    private readonly Action<Exception> reportFailure;
    private long lastPoll;
    private long lastSample;

    public TransportDiagnostics(Action<Exception> reportFailure) => this.reportFailure = reportFailure;

    public static bool ConfigureBuffers(EnetHost host, int receive, int send) =>
        enet.ENET_API.enet_socket_set_option(host.Socket, enet.ENetSocketOption.ENET_SOCKOPT_RCVBUF, receive) == 0 &&
        enet.ENET_API.enet_socket_set_option(host.Socket, enet.ENetSocketOption.ENET_SOCKOPT_SNDBUF, send) == 0;

    public static bool TryReadBuffers(EnetHost host, out (int Receive, int Send) buffers, out SocketException? error)
    {
        // The wrapper borrows the descriptor; disposal cannot close the ENet socket.
        try
        {
            using var handle = new SafeSocketHandle(host.Socket.Handle, ownsHandle: false);
            using var socket = new Socket(handle);
            buffers = (socket.ReceiveBufferSize, socket.SendBufferSize);
            error = null;
            return true;
        }
        catch (SocketException failure)
        {
            buffers = default;
            error = failure;
            return false;
        }
    }

    public long BeginPoll()
    {
        if (!PollDuration.Enabled) return 0;
        var now = Stopwatch.GetTimestamp();
        if (lastPoll != 0) PollInterval.Record(Stopwatch.GetElapsedTime(lastPoll, now).TotalMilliseconds);
        lastPoll = now;
        return now;
    }

    public void EndPoll(long start, int events, EnetHost host, PacketBudget budget)
    {
        if (start == 0) return;
        PollDuration.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        Events.Record(events);
        if (Environment.TickCount64 - lastSample < 100) return;
        if (lastSample == 0)
        {
            if (!EnetPeerTrace.TryCreate(host, out peerTrace, out var traceError)) reportFailure(traceError!);
            if (TryReadBuffers(host, out var buffers, out var bufferError))
            {
                ReceiveBuffer.Record(buffers.Receive);
                SendBuffer.Record(buffers.Send);
            }
            else reportFailure(bufferError!);
        }
        lastSample = Environment.TickCount64;
        PendingBytes.Record(budget.Bytes);
        PendingPackets.Record(budget.Packets);
        SentPackets.Record(host.TotalSentPackets);
        ReceivedPackets.Record(host.TotalReceivedPackets);
        double rtt = 0, inflight = 0, loss = 0, timeoutAge = 0, receiveAge = 0;
        for (var i = 0; i < (int)host.PeerCount; i++)
        {
            if (!host.TryGetPeer((ushort)i, out var peer)) continue;
            if (peer.State != EnetPeerState.Connected) continue;
            if (peerTrace is not null && !peerTrace.TryRecord(host, peer, out var traceError))
            {
                peerTrace = null;
                reportFailure(traceError!);
            }
            rtt = Math.Max(rtt, peer.RoundTripTime);
            if (peer.EarliestTimeout != 0)
                timeoutAge = Math.Max(timeoutAge, unchecked(host.ServiceTime - peer.EarliestTimeout));
            if (peer.LastReceiveTime != 0)
                receiveAge = Math.Max(receiveAge, unchecked(host.ServiceTime - peer.LastReceiveTime));
            inflight += peer.ReliableDataInTransit;
            loss += peer.PacketsLost; // ENet resets these windows; NOT a cumulative loss counter.
        }
        Rtt.Record(rtt);
        InTransit.Record(inflight);
        Loss.Record(loss);
        TimeoutAge.Record(timeoutAge);
        ReceiveAge.Record(receiveAge);
    }

    public static long BeginSend() => SendDuration.Enabled ? Stopwatch.GetTimestamp() : 0;
    public static void EndSend(long start, int bytes)
    {
        if (start == 0) return;
        SendDuration.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        PacketSize.Record(bytes);
    }
}
