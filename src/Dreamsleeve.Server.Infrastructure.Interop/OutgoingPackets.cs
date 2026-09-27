using System;
using System.Runtime.InteropServices;
using Enet;

namespace Dreamsleeve.Server.Infrastructure.Interop;

// All access, including ENet's final packet-release callback, belongs to the
// transport owner. Count payloads retained by ENet, including unacknowledged sends.
public sealed class PacketBudget(int maximumPackets, long maximumBytes, int reliablePacketReserve = 1)
{
    public int Packets { get; private set; }
    public long Bytes { get; private set; }

    internal bool TryReserve(int length, bool reliable)
    {
        // Realtime cannot occupy the reliable headroom. These are admission
        // limits within the existing total budget, not additional memory.
        var packetLimit = reliable ? maximumPackets
            : maximumPackets - Math.Max(1, Math.Min(maximumPackets, reliablePacketReserve));
        var byteLimit = reliable ? maximumBytes : maximumBytes - Math.Max(1L, maximumBytes / 4);
        if (Packets >= packetLimit || length > byteLimit - Bytes)
            return false;

        ++Packets;
        Bytes += length;
        return true;
    }

    internal void Release(int length)
    {
        --Packets;
        Bytes -= length;
    }
}

public enum PacketSendResult
{
    Sent,
    BudgetExceeded,
    PeerRejected
}

// C# expresses the managed function pointer required by yENet. Routing, polling,
// lifecycle and configuration stay in the F# adapter; this owns packet leases and payload limits.
public static unsafe class OutgoingPackets
{
    // Match enet_peer_send: its fragmentation threshold reserves the fragment
    // command even for an ordinary reliable send. Read negotiated peer MTU.
    public static int GetUnfragmentedPayloadBytes(EnetPeer peer) =>
        checked((int)peer.Mtu) - sizeof(enet.ENetProtocolHeader)
        - sizeof(enet.ENetProtocolSendFragment)
        - (peer.Host.ChecksumCallback == null ? 0 : sizeof(uint));

    private sealed class Lease(PacketBudget host, PacketBudget peer, int length)
    {
        public void Release()
        {
            host.Release(length);
            peer.Release(length);
        }
    }

    private static void Released(enet.ENetPacket* packet)
    {
        var handle = GCHandle.FromIntPtr((nint)packet->userData);
        var lease = (Lease)handle.Target!;
        packet->userData = null;
        handle.Free();
        lease.Release();
    }

    public static PacketSendResult TrySend(EnetPeer peer, ReadOnlySpan<byte> bytes,
        PacketBudget hostBudget, PacketBudget peerBudget) => TrySend(peer, bytes, hostBudget, peerBudget, 0, true);

    public static PacketSendResult TrySend(EnetPeer peer, ReadOnlySpan<byte> bytes,
        PacketBudget hostBudget, PacketBudget peerBudget, byte channel, bool reliable)
    {
        if (!hostBudget.TryReserve(bytes.Length, reliable))
            return PacketSendResult.BudgetExceeded;

        if (!peerBudget.TryReserve(bytes.Length, reliable))
        {
            hostBudget.Release(bytes.Length);
            return PacketSendResult.BudgetExceeded;
        }

        var handle = default(GCHandle);
        var packet = default(EnetPacket);
        var packetOwnsLease = false;

        try
        {
            var lease = new Lease(hostBudget, peerBudget, bytes.Length);
            handle = GCHandle.Alloc(lease);
            packet = EnetPacket.Create(bytes, reliable ? EnetPacketFlag.Reliable : default,
                &Released, (void*)GCHandle.ToIntPtr(handle));
            if (!packet.IsCreated)
                return PacketSendResult.PeerRejected;

            packetOwnsLease = true;

            return peer.TrySend(channel, ref packet)
                ? PacketSendResult.Sent
                : PacketSendResult.PeerRejected;
        }
        finally
        {
            // Successful TrySend clears the wrapper. On failure we still own it;
            // Dispose invokes Released once, just as ACK/reset/host destruction do.
            if (packet.IsCreated)
                packet.Dispose();

            if (!packetOwnsLease)
            {
                if (handle.IsAllocated)
                    handle.Free();
                hostBudget.Release(bytes.Length);
                peerBudget.Release(bytes.Length);
            }
        }
    }
}
