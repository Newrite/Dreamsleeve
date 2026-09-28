using System.Net.Sockets;
using Enet;
using enet;
using NativeSockets;

namespace Dreamsleeve.Server.Infrastructure.Interop;

/// <summary>Run protocol work even while application events remain queued.</summary>
public static unsafe class EnetPump
{
    public static int Service(EnetHost host, out SocketError socketError)
    {
        var result = ENET_API.enet_host_service(host.GetInner(), null, 0);
        // Read on the owner thread immediately, before diagnostics or another socket call.
        // ENet can also fail without a socket operation, so this is the LAST socket error,
        // not a claim that every negative result has an OS cause.
        socketError = result < 0 ? NativeSocketPal.GetLastSocketError() : SocketError.Success;
        return result;
    }
}
