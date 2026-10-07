namespace Dreamsleeve.Network.Testing;

// Deterministic FIFO serializer. Time comes from the caller: virtual in unit
// tests, Stopwatch in the socket runner. Includes IPv4 + UDP, not Ethernet.
public sealed class LinkSchedule(double bytesPerSecond, double queueBytes)
{
    private double departure;
    public double? Admit(double now, int udpBytes)
    {
        if (bytesPerSecond == 0) return now;
        var start = Math.Max(now, departure);
        var wireBytes = udpBytes + 28;
        if ((start - now) * bytesPerSecond + wireBytes > queueBytes) return null;
        departure = start + wireBytes / bytesPerSecond;
        return departure;
    }
}

public sealed record RelayOptions(
    int ServerPort, double RttMs, double JitterMs, int LossEvery,
    double LinkMiBps, double QueueKiB, long PendingBytes = 64 * 1024 * 1024, int MaxPeers = 8, int PendingDatagrams = 65536)
{
    public bool Valid => ServerPort is > 0 and <= 65535 &&
        double.IsFinite(RttMs) && RttMs >= 0 && double.IsFinite(JitterMs) && JitterMs >= 0 &&
        LossEvery >= 0 && double.IsFinite(LinkMiBps) && LinkMiBps >= 0 &&
        double.IsFinite(QueueKiB) && QueueKiB > 0 && PendingBytes > 0 && MaxPeers > 0 && PendingDatagrams > 0;
}
