using System;
using System.IO;
using System.Text;

namespace Dreamsleeve.Server.Infrastructure;

// Single diagnostic writer thread owns this sink, including rotation and disposal.
// Unlike a general logging pipeline, write failures propagate to the collector.
public sealed class DiagnosticFile : IDisposable
{
    private readonly string directory;
    private readonly long limit;
    private int part;
    private FileStream? file;
    public DiagnosticFile(string path, long limit)
    {
        if (limit < 1024) throw new ArgumentOutOfRangeException(nameof(limit));
        var fullPath = Path.GetFullPath(path);
        directory = Path.Combine(Path.GetDirectoryName(fullPath)!,
            Path.GetFileNameWithoutExtension(fullPath) + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-ffffff") + "-" + Guid.NewGuid().ToString("N"));
        this.limit = limit;
    }
    private void Open()
    {
        file?.Dispose(); file = null;
        Directory.CreateDirectory(directory);
        file = new FileStream(Path.Combine(directory, $"trace-{part++:D6}.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }
    public void Write(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        try {
            if (file == null || (file.Length != 0 && file.Length + bytes.Length > limit)) Open();
            file!.Write(bytes);
            file.Flush();
        } catch { file?.Dispose(); file = null; throw; }
    }
    public void Dispose() { file?.Dispose(); file = null; }
}
