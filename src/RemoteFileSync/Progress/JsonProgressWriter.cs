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

    public void WritePlan(int transfers, int deletes, int skipped, long bytes)
    {
        WriteLine(new { @event = "plan", transfers, deletes, skipped, bytes });
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

    // One line per reviewed item, like file_end and delete, because ProgressEvent is a flat bag
    // of nullables and cannot carry a nested array. kind is "conflict", "resurrection" or
    // "overwrite". A size of -1 paired with an empty mtime means the stored detail could not be
    // decoded, so the GUI must show "unknown" rather than render it as a 0-byte file.
    // renamed_to is omitted (not null) when nothing was renamed: a null would make the GUI draw
    // an empty "kept as" row for every resurrection and overwrite.
    public void WriteReview(string kind, string path,
                            long client_size, string client_mtime,
                            long server_size, string server_mtime,
                            string? renamed_to = null)
    {
        var obj = new Dictionary<string, object>
        {
            ["event"] = "review",
            ["kind"] = kind,
            ["path"] = path,
            ["client_size"] = client_size,
            ["client_mtime"] = client_mtime,
            ["server_size"] = server_size,
            ["server_mtime"] = server_mtime,
        };
        if (renamed_to != null) obj["renamed_to"] = renamed_to;
        WriteLine(obj);
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
