using Enet;
using enet;

namespace Dreamsleeve.Server.Infrastructure.Interop;

/// <summary>Run protocol work even while application events remain queued.</summary>
public static unsafe class EnetPump
{
    public static int Service(EnetHost host) => ENET_API.enet_host_service(host.GetInner(), null, 0);
}
