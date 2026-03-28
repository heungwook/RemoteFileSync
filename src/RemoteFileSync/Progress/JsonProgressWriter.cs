using System.Text.Json;

namespace RemoteFileSync.Progress;

public sealed class JsonProgressWriter
{
    private readonly TextWriter? _writer;
    private readonly object _lock = new();

    public static readonly JsonProgressWriter Null = new(null);

    public JsonProgressWriter(TextWriter? writer)
    {
        _writer = writer;
    }

    public void WriteStatus(string state, string? host = null, int? port = null, string? mode = null)
    {
        var obj = new Dictionary<string, object> { ["event"] = "status", ["state"] = state };
        if (host != null) obj["host"] = host;
        if (port != null) obj["port"] = port;
        if (mode != null) obj["mode"] = mode;
        WriteLine(obj);
    }

    public void WriteManifest(string side, int files, long bytes)
    {
        WriteLine(new { @event = "manifest", side, files, bytes });
    }

    public void WritePlan(int transfers, int deletes, int skipped)
    {
        WriteLine(new { @event = "plan", transfers, deletes, skipped });
    }

    public void WriteFileStart(string action, string path, long size, bool compressed, int thread)
    {
        WriteLine(new { @event = "file_start", action, path, size, compressed, thread });
    }

    public void WriteFileProgress(string path, long bytes_sent, long total_bytes, int thread)
    {
        WriteLine(new { @event = "file_progress", path, bytes_sent, total_bytes, thread });
    }

    public void WriteFileEnd(string path, bool success, string? error = null, int thread = 0)
    {
        var obj = new Dictionary<string, object> { ["event"] = "file_end", ["path"] = path, ["success"] = success, ["thread"] = thread };
        if (error != null) obj["error"] = error;
        WriteLine(obj);
    }

    public void WriteDelete(string path, bool backed_up, bool success, string? error = null)
    {
        var obj = new Dictionary<string, object> { ["event"] = "delete", ["path"] = path, ["backed_up"] = backed_up, ["success"] = success };
        if (error != null) obj["error"] = error;
        WriteLine(obj);
    }

    public void WriteComplete(int files_transferred, int files_deleted, long bytes, long elapsed_ms, int exit_code)
    {
        WriteLine(new { @event = "complete", files_transferred, files_deleted, bytes, elapsed_ms, exit_code });
    }

    public void WriteError(string message, bool fatal)
    {
        WriteLine(new { @event = "error", message, fatal });
    }

    private void WriteLine(object obj)
    {
        if (_writer == null) return;
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        lock (_lock)
        {
            _writer.WriteLine(json);
            _writer.Flush();
        }
    }
}
