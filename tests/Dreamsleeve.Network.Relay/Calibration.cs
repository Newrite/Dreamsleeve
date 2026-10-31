using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Dreamsleeve.Network.Testing;

public static class Calibration
{
    // Independent paced UDP source + echo, without ENet or the application flow
    // controller. Warm the socket path before measuring the nominal 5 MiB/s.
    public static int Run(string output)
    {
        const int payload = 1372, wire = payload + 28, rate = 5 * 1048576;
        const double duration = 2;
        int count = (int)(duration * rate / wire);
        using var echo = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        echo.ReceiveBufferSize = 4 * 1048576;
        echo.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        client.ReceiveBufferSize = 4 * 1048576;
        client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var cancellation = new CancellationTokenSource();
        using var warm = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        using var relay = new DatagramRelay(new(((IPEndPoint)echo.LocalEndPoint!).Port, 40, 0, 0, 5, 64));
        relay.Start();
        var destination = new IPEndPoint(IPAddress.Loopback, relay.Port);
        var seen = new bool[count];
        int sent = 0, received = 0, duplicates = 0;
        long firstReceive = 0, lastReceive = 0;
        double totalRtt = 0, maxRtt = 0, maxSendLate = 0;
        string? echoError = null, receiveError = null;
        var echoThread = new Thread(() => {
            var buffer = new byte[65535];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            try {
                while (!cancellation.IsCancellationRequested) {
                    if (!echo.Poll(1000, SelectMode.SelectRead)) continue;
                    int length = echo.ReceiveFrom(buffer, ref remote);
                    echo.SendTo(buffer.AsSpan(0, length), SocketFlags.None, remote);
                }
            } catch (SocketException e) { echoError = e.SocketErrorCode.ToString(); }
        }) { IsBackground = true };
        var reader = new Thread(() => {
            var buffer = new byte[65535];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            try {
                while (!cancellation.IsCancellationRequested) {
                    if (!client.Poll(1000, SelectMode.SelectRead)) continue;
                    int length = client.ReceiveFrom(buffer, ref remote);
                    if (length != payload || !remote.Equals(destination)) continue;
                    int sequence = BinaryPrimitives.ReadInt32LittleEndian(buffer);
                    if (sequence == -1) { warm.Set(); continue; }
                    if (sequence < 0 || sequence >= count) continue;
                    if (seen[sequence]) { ++duplicates; continue; }
                    seen[sequence] = true;
                    long now = Stopwatch.GetTimestamp();
                    if (received == 0) firstReceive = now;
                    lastReceive = now;
                    double rtt = (now - BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(4))) * 1000.0 / Stopwatch.Frequency;
                    totalRtt += rtt; maxRtt = Math.Max(maxRtt, rtt);
                    if (++received == count) done.Set();
                }
            } catch (SocketException e) { receiveError = e.SocketErrorCode.ToString(); }
        }) { IsBackground = true };
        echoThread.Start(); reader.Start();
        bool warmed;
        try {
            var buffer = new byte[payload];
            BinaryPrimitives.WriteInt32LittleEndian(buffer, -1);
            client.SendTo(buffer, destination);
            warmed = warm.Wait(TimeSpan.FromSeconds(2));
            if (warmed) {
                long start = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 20;
                for (int i = 0; i < count; ++i) {
                    long due = start + (long)((double)i * wire * Stopwatch.Frequency / rate);
                    while (Stopwatch.GetTimestamp() < due) Thread.SpinWait(32);
                    long now = Stopwatch.GetTimestamp();
                    maxSendLate = Math.Max(maxSendLate, (now - due) * 1000.0 / Stopwatch.Frequency);
                    BinaryPrimitives.WriteInt32LittleEndian(buffer, i);
                    BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(4), now);
                    client.SendTo(buffer, destination);
                    ++sent;
                }
                done.Wait(TimeSpan.FromSeconds(5));
            }
        } finally {
            cancellation.Cancel(); echoThread.Join(); reader.Join(); relay.Dispose();
        }
        var receiveRate = received > 1 ? (received - 1.0) * wire * Stopwatch.Frequency / (lastReceive - firstReceive) / 1048576 : 0;
        // A complete but slow stream is not a successful rate calibration.
        const double tolerance = .02;
        var rateAccurate = receiveRate >= 5 * (1 - tolerance) && receiveRate <= 5 * (1 + tolerance);
        var sourceAccurate = maxSendLate < 64.0 * 1024 / rate * 1000;
        var result = new {
            warmed, expected = count, sent, received, duplicates, rateAccurate, sourceAccurate, rateToleranceFraction = tolerance, applicationIndependent = true, rateIpv4BytesPerSecond = rate,
            measuredReceiveMiBps = receiveRate,
            meanRttMs = received == 0 ? 0 : totalRtt / received, maxRttMs = maxRtt, maxSourceLateMs = maxSendLate,
            echoError, receiveError, relay = relay.Report
        };
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "calibration.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return warmed && rateAccurate && sourceAccurate && sent == count && received == count && duplicates == 0 && echoError is null && receiveError is null && relay.Healthy ? 0 : 1;
    }
}
