using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace Dreamsleeve.Server.Infrastructure;

// These alternatives are local to the diagnostic owner. Match makes every
// outcome explicit without throwing Value/Error accessors or mutable DTO flags.
public abstract record DiagnosticFileStartError
{
    private DiagnosticFileStartError() { }
    public sealed record InvalidLimit(long Value) : DiagnosticFileStartError;
    public sealed record InvalidPath : DiagnosticFileStartError;
    public sealed record PathResolution(Exception Cause) : DiagnosticFileStartError;
}

public abstract record DiagnosticFileCreation
{
    private DiagnosticFileCreation() { }
    public abstract T Match<T>(Func<DiagnosticFile, T> ready, Func<DiagnosticFileStartError, T> rejected);
    private sealed record Ready(DiagnosticFile File) : DiagnosticFileCreation
    {
        public override T Match<T>(Func<DiagnosticFile, T> ready, Func<DiagnosticFileStartError, T> rejected) => ready(File);
    }
    private sealed record Rejected(DiagnosticFileStartError Error) : DiagnosticFileCreation
    {
        public override T Match<T>(Func<DiagnosticFile, T> ready, Func<DiagnosticFileStartError, T> rejected) => rejected(Error);
    }
    internal static DiagnosticFileCreation Create(DiagnosticFile file) => new Ready(file);
    internal static DiagnosticFileCreation Reject(DiagnosticFileStartError error) => new Rejected(error);
}

public sealed record DiagnosticIoFailure
{
    public IReadOnlyList<Exception> Causes { get; }
    internal DiagnosticIoFailure(IEnumerable<Exception> causes) => Causes = Array.AsReadOnly(causes.ToArray());
    public Exception Diagnostic => Causes.Count == 1 ? Causes[0] : new AggregateException(Causes);
}

public abstract record DiagnosticOperationResult
{
    private DiagnosticOperationResult() { }
    public abstract T Match<T>(Func<T> written, Func<DiagnosticIoFailure, T> ioFailure, Func<IReadOnlyList<Exception>, T> ownerFault);
    private sealed record Written : DiagnosticOperationResult
    {
        public override T Match<T>(Func<T> written, Func<DiagnosticIoFailure, T> ioFailure, Func<IReadOnlyList<Exception>, T> ownerFault) => written();
    }
    private sealed record IoFailed(DiagnosticIoFailure Error) : DiagnosticOperationResult
    {
        public override T Match<T>(Func<T> written, Func<DiagnosticIoFailure, T> ioFailure, Func<IReadOnlyList<Exception>, T> ownerFault) => ioFailure(Error);
    }
    private sealed record Faulted(IReadOnlyList<Exception> Causes) : DiagnosticOperationResult
    {
        public override T Match<T>(Func<T> written, Func<DiagnosticIoFailure, T> ioFailure, Func<IReadOnlyList<Exception>, T> ownerFault) => ownerFault(Causes);
    }
    public static DiagnosticOperationResult Success { get; } = new Written();
    public static DiagnosticOperationResult IoFailure(IOException error) => Io(error);
    public static DiagnosticOperationResult IoFailure(UnauthorizedAccessException error) => Io(error);
    internal static DiagnosticOperationResult Io(Exception error) => new IoFailed(new([error]));
    internal static DiagnosticOperationResult Io(IEnumerable<Exception> causes) => new IoFailed(new(causes));
    internal static DiagnosticOperationResult Fault(IEnumerable<Exception> causes) => new Faulted(Array.AsReadOnly(causes.ToArray()));
}

public abstract record DiagnosticCollectorOutcome
{
    private DiagnosticCollectorOutcome() { }
    public abstract T Match<T>(Func<T> stopped, Func<IReadOnlyList<Exception>, T> faulted);
    private sealed record Stopped : DiagnosticCollectorOutcome
    {
        public override T Match<T>(Func<T> stopped, Func<IReadOnlyList<Exception>, T> faulted) => stopped();
    }
    private sealed record Faulted(IReadOnlyList<Exception> Causes) : DiagnosticCollectorOutcome
    {
        public override T Match<T>(Func<T> stopped, Func<IReadOnlyList<Exception>, T> faulted) => faulted(Causes);
    }
    public static DiagnosticCollectorOutcome Success { get; } = new Stopped();
    internal static DiagnosticCollectorOutcome Fault(IEnumerable<Exception> causes) => new Faulted(Array.AsReadOnly(causes.ToArray()));
}

public abstract record DiagnosticCollectorStart
{
    private DiagnosticCollectorStart() { }
    public abstract T Match<T>(Func<ContinuousDiagnostics, T> ready, Func<T> invalidSink);
    private sealed record Ready(ContinuousDiagnostics Collector) : DiagnosticCollectorStart
    {
        public override T Match<T>(Func<ContinuousDiagnostics, T> ready, Func<T> invalidSink) => ready(Collector);
    }
    private sealed record InvalidSink : DiagnosticCollectorStart
    {
        public override T Match<T>(Func<ContinuousDiagnostics, T> ready, Func<T> invalidSink) => invalidSink();
    }
    internal static DiagnosticCollectorStart Create(ContinuousDiagnostics collector) => new Ready(collector);
    internal static DiagnosticCollectorStart Reject { get; } = new InvalidSink();
}
