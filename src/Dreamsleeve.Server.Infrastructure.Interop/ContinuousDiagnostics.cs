using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Dreamsleeve.Server.Infrastructure;

// Listener callbacks only aggregate or enqueue. The background owner performs all serialization/IO.
public sealed class ContinuousDiagnostics : IDisposable
{
    private sealed class Aggregate
    {
        public long Count;
        public double Sum, Max = double.NegativeInfinity, Min = double.PositiveInfinity;
        public long[] Buckets = new long[11];
    }
    private static readonly double[] Limits = [0.1, 0.25, 0.5, 1, 2, 4, 8, 16, 33, 100];
    private readonly object gate = new();
    private readonly Dictionary<Instrument, Aggregate> metrics = new();
    private readonly Channel<object> events = Channel.CreateBounded<object>(new BoundedChannelOptions(2048) {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly MeterListener listener = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly Action<string> write;
    private readonly Task writer;
    private readonly Action? close;
    private readonly Action<Exception>? failed;
    private long dropped, errors;
    private long previousSample = Stopwatch.GetTimestamp();
    private int disposed;

    public ContinuousDiagnostics(Action<string> write, Action? close = null, Action<Exception>? failed = null)
    {
        this.write = write;
        this.close = close;
        this.failed = failed;
        listener.InstrumentPublished = (instrument, owner) => {
            if (!instrument.Meter.Name.StartsWith("Dreamsleeve.", StringComparison.Ordinal)) return;
            lock (gate) {
                if (metrics.Count >= 256) return;
                metrics[instrument] = new Aggregate();
            }
            owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((i, value, tags, _) => Observe(i, value, tags));
        listener.SetMeasurementEventCallback<long>((i, value, tags, _) => Observe(i, value, tags));
        listener.SetMeasurementEventCallback<int>((i, value, tags, _) => Observe(i, value, tags));
        listener.Start();
        writer = Task.Run(Run);
    }

    private void Observe(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (!double.IsFinite(value)) return;
        if (instrument.Name is "phantom.lifecycle" or "phantom.http") {
            var fields = new Dictionary<string, object?>();
            foreach (var tag in tags) fields[tag.Key] = tag.Value;
            if (!events.Writer.TryWrite(new { kind = "phantom", utc_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    monotonic = Stopwatch.GetTimestamp(), fields })) Interlocked.Increment(ref dropped);
            return;
        }
        lock (gate) {
            if (!metrics.TryGetValue(instrument, out var a)) return;
            a.Count++; a.Sum += value; a.Min = Math.Min(a.Min, value); a.Max = Math.Max(a.Max, value);
            int b = 0; while (b < Limits.Length && value > Limits[b]) b++;
            a.Buckets[b]++;
        }
    }

    private void Emit(object value)
    {
        try { write(JsonSerializer.Serialize(value)); }
        catch (Exception error) {
            var count = Interlocked.Increment(ref errors);
            if ((count & (count - 1)) == 0) { try { failed?.Invoke(error); } catch { } }
        }
    }

    private void Flush()
    {
        var utc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var monotonic = Stopwatch.GetTimestamp();
        var interval = Stopwatch.GetElapsedTime(previousSample, monotonic).TotalMilliseconds;
        previousSample = monotonic;
        List<object> batch = new();
        lock (gate) {
            foreach (var (instrument, a) in metrics) {
                if (a.Count == 0) continue;
                batch.Add(new { meter = instrument.Meter.Name, name = instrument.Name, unit = instrument.Unit,
                    count = a.Count, sum = a.Sum, min = a.Min, max = a.Max, buckets = a.Buckets });
            }
            foreach (var instrument in metrics.Keys.ToArray()) metrics[instrument] = new Aggregate();
        }
        using var process = Process.GetCurrentProcess();
        var sample = new { kind = "sample", utc_ms = utc, monotonic, interval_ms = interval,
            cpu_ms_total = process.TotalProcessorTime.TotalMilliseconds, working_bytes = process.WorkingSet64,
            private_bytes = process.PrivateMemorySize64, allocated_bytes_total = GC.GetTotalAllocatedBytes(),
            gc_collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
            gc_pause_ms_total = GC.GetTotalPauseDuration().TotalMilliseconds,
            events_dropped_total = Interlocked.Read(ref dropped), write_errors_total = Interlocked.Read(ref errors), metrics = batch };
        // A producer cannot starve samples or shutdown by refilling the bounded queue.
        var pending = Math.Min(2048, events.Reader.Count);
        for (int n = 0; n < pending && events.Reader.TryRead(out var item); ++n) Emit(item);
        Emit(sample);
    }
    private async Task Run()
    {
        Emit(new { kind = "start", format = 1, utc_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            process = Environment.ProcessId, monotonic_frequency = Stopwatch.Frequency, histogram_upper = Limits });
        try {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellation.Token).ConfigureAwait(false)) Flush();
        } catch (OperationCanceledException) { }
        finally {
            try { Flush(); } finally { try { close?.Invoke(); } catch { Interlocked.Increment(ref errors); } cancellation.Dispose(); }
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        listener.Dispose();
        cancellation.Cancel();
        // An unavailable disk must not hold the server shutdown indefinitely.
        // The background task retains sole ownership of the sink until IO returns.
        writer.Wait(TimeSpan.FromSeconds(2));
    }
}
