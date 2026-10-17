using System;
using System.IO;
using System.Text;

namespace Dreamsleeve.Server.Infrastructure;

// One diagnostic writer owns this sink, rotation and cleanup. File failures
// are optional; unknown owner/cleanup faults are a different lifetime outcome.
public sealed class DiagnosticFile
{
    private readonly string directory;
    private readonly long limit;
    private int part;
    private FileStream? file;

    private DiagnosticFile(string directory, long limit)
    {
        this.directory = directory;
        this.limit = limit;
    }

    public static DiagnosticFileCreation TryCreate(string path, long limit)
    {
        if (limit < 1024) return DiagnosticFileCreation.Reject(new DiagnosticFileStartError.InvalidLimit(limit));
        if (string.IsNullOrWhiteSpace(path)) return DiagnosticFileCreation.Reject(new DiagnosticFileStartError.InvalidPath());
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        { return DiagnosticFileCreation.Reject(new DiagnosticFileStartError.PathResolution(error)); }
        var parent = Path.GetDirectoryName(fullPath);
        if (parent is null) return DiagnosticFileCreation.Reject(new DiagnosticFileStartError.InvalidPath());
        var directory = Path.Combine(parent,
            Path.GetFileNameWithoutExtension(fullPath) + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-ffffff") + "-" + Guid.NewGuid().ToString("N"));
        return DiagnosticFileCreation.Create(new DiagnosticFile(directory, limit));
    }

    private static bool ExpectedStorageError(Exception error) => error is IOException or UnauthorizedAccessException;

    public DiagnosticOperationResult TryClose()
    {
        var owned = file;
        file = null;
        // This cleanup boundary owns the released stream. Unknown Dispose faults
        // remain explicit owner faults, not ordinary unavailable storage.
        try { owned?.Dispose(); return DiagnosticOperationResult.Success; }
        catch (Exception error) when (ExpectedStorageError(error)) { return DiagnosticOperationResult.Io(error); }
        catch (Exception error) { return DiagnosticOperationResult.Fault([error]); }
    }

    private DiagnosticOperationResult Open()
    {
        var closed = TryClose();
        return closed.Match(
            () => {
                try {
                    Directory.CreateDirectory(directory);
                    file = new FileStream(Path.Combine(directory, $"trace-{part++:D6}.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    return DiagnosticOperationResult.Success;
                } catch (Exception error) when (ExpectedStorageError(error)) { return DiagnosticOperationResult.Io(error); }
            }, error => DiagnosticOperationResult.Io(error.Causes), DiagnosticOperationResult.Fault);
    }

    public DiagnosticOperationResult TryWrite(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        DiagnosticOperationResult result;
        try {
            if (file == null || (file.Length != 0 && file.Length + bytes.Length > limit)) {
                result = Open();
                if (!result.Match(() => true, _ => false, _ => false)) return result;
            }
            file!.Write(bytes);
            file.Flush();
            return DiagnosticOperationResult.Success;
        } catch (Exception error) when (ExpectedStorageError(error)) { result = DiagnosticOperationResult.Io(error); }
        // Reset the failed part before the next independent optional write. Keep
        // original I/O and any cleanup causes; an unexpected cleanup stops owner.
        return result.Match(
            () => DiagnosticOperationResult.Success,
            original => TryClose().Match(
                () => DiagnosticOperationResult.Io(original.Causes),
                cleanup => DiagnosticOperationResult.Io(System.Linq.Enumerable.Concat(original.Causes, cleanup.Causes)),
                cleanup => DiagnosticOperationResult.Fault(System.Linq.Enumerable.Concat(original.Causes, cleanup))),
            DiagnosticOperationResult.Fault);
    }
}
