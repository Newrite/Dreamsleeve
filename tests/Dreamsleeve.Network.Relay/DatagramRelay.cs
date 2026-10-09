using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Dreamsleeve.Network.Testing;

// A test-only UDP relay. It never interprets ENet or model payloads. One owner
// services sockets and scheduled datagrams; no managed callback per packet.
public sealed class DatagramRelay : IDisposable
{
    private sealed record Peer(Socket Socket, IPEndPoint Client, LinkSchedule Upload, LinkSchedule Download);
    private readonly record struct Datagram(Socket Socket, EndPoint Target, byte[] Buffer, int Length);
    private readonly Socket front = NewSocket();
    private readonly Dictionary<IPEndPoint, Peer> clients = new();
    private readonly Dictionary<Socket, Peer> upstreams = new();
    private readonly PriorityQueue<Datagram, (double Due, long Serial)> pending = new();
    private readonly List<Socket> readable = new();
    private readonly byte[] receive = new byte[65535];
    private readonly Random random = new(73);
    private readonly RelayOptions options;
    private readonly IPEndPoint server;
    private readonly Thread owner;
    private volatile bool stop;
    private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long serial, dropped, overflow, resets, resourceDrops, payloadBytes, acceptedWireBytes, pendingBytes, peakPendingBytes, forwarded;
    private double maximumLate, totalLate, maximumServiceGap, totalServiceGap;
    private long serviceCount, peakPendingCount;
    private readonly long[] serviceBins = new long[6];
    private readonly long[] lateBins = new long[6];
    private readonly List<string> errors = new();
    public int Port => ((IPEndPoint)front.LocalEndPoint!).Port;
    public Task Completion => completed.Task;
    private static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    public DatagramRelay(RelayOptions validatedOptions)
    {
        options = validatedOptions;
        server = new(IPAddress.Loopback, options.ServerPort);
        owner = new Thread(Run) { IsBackground = true, Name = "UDP impairment fixture" };
    }
    private static Socket NewSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveBufferSize = 4 * 1024 * 1024;
        socket.SendBufferSize = 4 * 1024 * 1024;
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }
    public void Start() => owner.Start();
    private Peer AddPeer(IPEndPoint endpoint)
    {
        var address = new IPEndPoint(endpoint.Address, endpoint.Port);
        var peer = new Peer(NewSocket(), address,
            new(options.LinkMiBps * 1048576, options.QueueKiB * 1024),
            new(options.LinkMiBps * 1048576, options.QueueKiB * 1024));
        clients.Add(address, peer);
        upstreams.Add(peer.Socket, peer);
        return peer;
    }
    private void Receive(Socket socket)
    {
        EndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
        int count;
        try { count = socket.ReceiveFrom(receive, ref endpoint); }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
        { ++resets; return; } // Late ICMP after the native client has exited.
        var address = (IPEndPoint)endpoint;
        Peer peer;
        if (socket == front)
        {
            if (!clients.TryGetValue(address, out peer!))
            {
                if (clients.Count >= options.MaxPeers) { ++resourceDrops; return; }
                peer = AddPeer(address);
            }
        }
        else
        {
            if (!address.Equals(server)) { errors.Add("Unexpected upstream endpoint"); return; }
            peer = upstreams[socket];
        }
        ++serial;
        payloadBytes += count;
        if (options.LossEvery != 0 && serial % options.LossEvery == 0) { ++dropped; return; }
        var now = Now;
        var finish = (socket == front ? peer.Upload : peer.Download).Admit(now, count);
        if (!finish.HasValue) { ++overflow; ++dropped; return; }
        // This is fixture capacity, not simulated congestion. Any hit invalidates
        // the measurement and makes the relay process fail.
        if (pending.Count >= options.PendingDatagrams || pendingBytes + count > options.PendingBytes) { ++resourceDrops; return; }
        var delay = Math.Max(0, options.RttMs / 2 + (random.NextDouble() * 2 - 1) * options.JitterMs) / 1000;
        var buffer = ArrayPool<byte>.Shared.Rent(count);
        if (pendingBytes + buffer.Length > options.PendingBytes)
        { ArrayPool<byte>.Shared.Return(buffer); ++resourceDrops; return; }
        receive.AsSpan(0, count).CopyTo(buffer);
        pending.Enqueue(new(socket == front ? peer.Socket : front, socket == front ? server : peer.Client, buffer, count), (finish.Value + delay, serial));
        pendingBytes += buffer.Length;
        peakPendingCount = Math.Max(peakPendingCount, pending.Count);
        peakPendingBytes = Math.Max(peakPendingBytes, pendingBytes);
        acceptedWireBytes += count + 28;
    }
    private void Dispatch()
    {
        while (pending.TryPeek(out _, out var due) && due.Due <= Now)
        {
            var packet = pending.Dequeue();
            var late = Math.Max(0, Now - due.Due) * 1000;
            maximumLate = Math.Max(maximumLate, late);
            totalLate += late;
            ++lateBins[late <= .25 ? 0 : late <= 1 ? 1 : late <= 2 ? 2 : late <= 5 ? 3 : late <= 10 ? 4 : 5];
            try { packet.Socket.SendTo(packet.Buffer.AsSpan(0, packet.Length), SocketFlags.None, packet.Target); ++forwarded; }
            finally { ArrayPool<byte>.Shared.Return(packet.Buffer); pendingBytes -= packet.Buffer.Length; }
        }
    }
    private void Run()
    {
        try
        {
            var previousService = Now;
            while (!stop)
            {
                var serviceAt = Now;
                var gap = (serviceAt - previousService) * 1000;
                previousService = serviceAt;
                ++serviceCount; totalServiceGap += gap;
                maximumServiceGap = Math.Max(maximumServiceGap, gap);
                ++serviceBins[gap <= .25 ? 0 : gap <= 1 ? 1 : gap <= 2 ? 2 : gap <= 5 ? 3 : gap <= 10 ? 4 : 5];
                Dispatch();
                readable.Clear(); readable.Add(front); readable.AddRange(upstreams.Keys);
                var wait = pending.TryPeek(out _, out var next) ? (int)Math.Clamp(Math.Ceiling((next.Due - Now) * 1000000), 0, 1000) : 1000;
                Socket.Select(readable, null, null, wait);
                foreach (var socket in readable)
                {
                    Receive(socket);
                    // Return to deadlines regularly, even under a receive flood.
                    for (int i = 1; i < 16 && socket.Poll(0, SelectMode.SelectRead); ++i) Receive(socket);
                    Dispatch();
                }
            }
        }
        catch (SocketException e) { errors.Add(e.SocketErrorCode.ToString()); }
        finally { completed.TrySetResult(); }
    }
    public object Report => new {
        binarySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(DatagramRelay).Assembly.Location))).ToLowerInvariant(),
        timingValid = TimingValid,
        datagrams = serial, dropped, icmpResets = resets, overflowDrops = overflow,
        rttMs = options.RttMs, jitterMs = options.JitterMs, linkMiBps = options.LinkMiBps, queueKiB = options.QueueKiB,
        udpPayloadBytes = payloadBytes, acceptedIpv4Bytes = acceptedWireBytes,
        clock = "System.Diagnostics.Stopwatch", clockFrequency = Stopwatch.Frequency, highResolutionClock = Stopwatch.IsHighResolution,
        forwardedDatagrams = forwarded, meanDispatchLateMs = forwarded == 0 ? 0 : totalLate / forwarded, maxDispatchLateMs = maximumLate,
        dispatchLateBins = lateBins, dispatchLateUpperBoundsMs = new[] { .25, 1, 2, 5, 10 },
        meanServiceGapMs = serviceCount == 0 ? 0 : totalServiceGap / serviceCount, maxServiceGapMs = maximumServiceGap, serviceGapBins = serviceBins,
        pendingBufferBytesLimit = options.PendingBytes, peakPendingBufferBytes = peakPendingBytes,
        pendingDatagramsLimit = options.PendingDatagrams, peakPendingDatagrams = peakPendingCount, peerLimit = options.MaxPeers, peers = clients.Count, resourceDrops, relayErrors = errors.ToArray()
    };
    // A service stall longer than the entire simulated queue's drain time can
    // turn kernel-buffered arrivals into false tail drops. Reject that run.
    public bool TimingValid => options.LinkMiBps == 0 || maximumServiceGap / 1000 <= options.QueueKiB * 1024 / (options.LinkMiBps * 1048576);
    public bool Healthy => errors.Count == 0 && resourceDrops == 0 && TimingValid;
    public void Dispose()
    {
        stop = true;
        if (owner.IsAlive) owner.Join();
        while (pending.TryDequeue(out var packet, out _)) ArrayPool<byte>.Shared.Return(packet.Buffer);
        foreach (var peer in clients.Values) peer.Socket.Dispose();
        front.Dispose();
    }
}
