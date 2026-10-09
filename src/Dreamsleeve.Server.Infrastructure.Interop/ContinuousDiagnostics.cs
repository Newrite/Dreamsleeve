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
    private readonly Func<string, DiagnosticOperationResult> write;
    private readonly Func<DiagnosticOperationResult>? close;
    private readonly object lifetimeGate = new();
    private readonly List<Exception> faults = new();
    private readonly TaskCompletionSource<DiagnosticCollectorOutcome> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task cancellationCallbacks = Task.CompletedTask;
    private bool finished, reporterFailed, terminalReported;
    private IReadOnlyList<Exception>? reportingIo;
    private int admissionClosed;
    private readonly Action<Exception>? failed;
    private long dropped, errors;
    private long previousSample = Stopwatch.GetTimestamp();
    private int disposed;

    private ContinuousDiagnostics(Func<string, DiagnosticOperationResult> write, Func<DiagnosticOperationResult>? close, Action<Exception>? failed)
    {
        this.write = write;
        this.close = close;
        this.failed = failed;
        listener.InstrumentPublished = (instrument, owner) => {
            if (Volatile.Read(ref admissionClosed) != 0 || !instrument.Meter.Name.StartsWith("Dreamsleeve.", StringComparison.Ordinal)) return;
            lock (gate) {
                if (metrics.Count >= 256) return;
                metrics[instrument] = new Aggregate();
            }
            owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((i, value, tags, _) => Observe(i, value, tags));
        listener.SetMeasurementEventCallback<long>((i, value, tags, _) => Observe(i, value, tags));
        listener.SetMeasurementEventCallback<int>((i, value, tags, _) => Observe(i, value, tags));
        Exception? startupFault = null;
        // Listener startup is owned too; a partial startup is closed by Run.
        try { listener.Start(); }
        catch (Exception error) { startupFault = error; }
        _ = Task.Run(() => Run(startupFault));
    }

    public static DiagnosticCollectorStart TryStart(Func<string, DiagnosticOperationResult> write,
        Func<DiagnosticOperationResult>? close = null, Action<Exception>? failed = null) =>
        write is null ? DiagnosticCollectorStart.Reject : DiagnosticCollectorStart.Create(new(write, close, failed));

    // Dispose's bounded wait can leave this pending; it describes actual owner
    // completion, including original/secondary faults even if reporting failed.
    public Task<DiagnosticCollectorOutcome> Completion => completion.Task;
    private bool HasFault { get { lock (lifetimeGate) return faults.Count != 0; } }
    private void AddFault(Exception error) { lock (lifetimeGate) faults.Add(error); }
    private void AddFaults(IEnumerable<Exception> errors) { lock (lifetimeGate) faults.AddRange(errors); }

    private void RecordFault(Exception error)
    {
        if (reportingIo != null) {
            AddFaults(reportingIo);
            reportingIo = null;
            reporterFailed = true;
        }
        AddFault(error);
    }

    private void ReportIo(DiagnosticIoFailure failure)
    {
        var count = Interlocked.Increment(ref errors);
        if ((count & (count - 1)) != 0 || failed == null || reporterFailed) return;
        reportingIo = failure.Causes;
        failed(failure.Diagnostic);
        reportingIo = null;
    }

    private void ReportTerminal()
    {
        if (!HasFault || terminalReported || reporterFailed || failed == null) return;
        terminalReported = true;
        Exception[] causes;
        lock (lifetimeGate) causes = faults.ToArray();
        try { failed(causes.Length == 1 ? causes[0] : new AggregateException(causes)); }
        catch (Exception error) { reporterFailed = true; AddFault(error); }
    }

    private void StopAdmission()
    {
        if (Interlocked.Exchange(ref admissionClosed, 1) != 0) return;
        events.Writer.TryComplete();
    }

    private void Observe(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (Volatile.Read(ref admissionClosed) != 0 || !double.IsFinite(value)) return;
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
        // Serialization and the sink callback are outside expected-I/O adapters.
        // Their exceptions belong to Run's lifetime and stop this collector.
        var json = JsonSerializer.Serialize(value);
        write(json).Match(
            () => true,
            failure => { ReportIo(failure); return true; },
            causes => { AddFaults(causes); return false; });
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
        for (int n = 0; n < pending && !HasFault && events.Reader.TryRead(out var item); ++n) Emit(item);
        if (!HasFault) Emit(sample);
    }
    private async Task<bool> WaitTick(PeriodicTimer timer)
    {
        try { return await timer.WaitForNextTickAsync(cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return false; }
    }

    private async Task Run(Exception? startupFault)
    {
        bool stopped = false;
        try {
            if (startupFault != null) AddFault(startupFault);
            else {
                Emit(new { kind = "start", format = 1, utc_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    process = Environment.ProcessId, monotonic_frequency = Stopwatch.Frequency, histogram_upper = Limits });
                if (!HasFault) {
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                    while (!HasFault && await WaitTick(timer).ConfigureAwait(false)) Flush();
                    stopped = !HasFault;
                }
            }
        } catch (Exception error) { RecordFault(error); }

        StopAdmission();
        // Only this owner releases the listener; Dispose merely closes admission.
        try { listener.Dispose(); }
        catch (Exception error) { RecordFault(error); }
        // Never re-run the failed serialization/callback in a final flush.
        if (stopped && !HasFault) {
            try { Flush(); }
            catch (Exception error) { RecordFault(error); }
        }
        ReportTerminal();
        // Independently release the sink even when write or reporting failed.
        try {
            close?.Invoke().Match(
                () => true,
                failure => {
                    if (HasFault) AddFaults(failure.Causes);
                    ReportIo(failure);
                    return true;
                },
                causes => { AddFaults(causes); return false; });
        } catch (Exception error) { RecordFault(error); }
        ReportTerminal();

        Task callbacks;
        lock (lifetimeGate) {
            // Atomically close cancellation admission before taking its final
            // callback task; concurrent Dispose cannot append unjoined work.
            finished = true;
            callbacks = cancellationCallbacks;
        }
        try { await callbacks.ConfigureAwait(false); }
        catch (Exception error) {
            if (callbacks.Exception != null) AddFaults(callbacks.Exception.InnerExceptions);
            else AddFault(error);
        }
        lock (lifetimeGate) {
            // Cancellation was closed before awaiting its callback task; source
            // release and the actual completion result remain owned here.
            try { cancellation.Dispose(); }
            catch (Exception error) { faults.Add(error); }
            completion.TrySetResult(faults.Count == 0 ? DiagnosticCollectorOutcome.Success : DiagnosticCollectorOutcome.Fault(faults));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        StopAdmission();
        lock (lifetimeGate) {
            if (!finished) {
                try { cancellationCallbacks = cancellation.CancelAsync(); }
                catch (Exception error) { faults.Add(error); }
            }
        }
        // An unavailable disk cannot hold server shutdown indefinitely. Pending
        // completion retains the sole sink owner until the callback/IO returns.
        completion.Task.Wait(TimeSpan.FromSeconds(2));
    }
}
