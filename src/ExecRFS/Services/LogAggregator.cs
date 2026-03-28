namespace ExecRFS.Services;

public sealed class LogAggregator
{
    private readonly List<LogEntry> _entries = new();
    private readonly object _lock = new();
    private const int MaxEntries = 5000;

    public event Action<LogEntry>? OnEntry;

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    public void AddLine(string source, string line)
    {
        var entry = new LogEntry(DateTime.Now, source, ParseLevel(line), line);
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries) _entries.RemoveAt(0);
        }
        OnEntry?.Invoke(entry);
    }

    public void Clear() { lock (_lock) _entries.Clear(); }

    private static string ParseLevel(string line)
    {
        if (line.Contains("[ERR]") || line.Contains("\"fatal\":true")) return "error";
        if (line.Contains("[WRN]") || line.Contains("Failed")) return "warning";
        if (line.Contains("[DEL")) return "delete";
        return "info";
    }
}

public record LogEntry(DateTime Timestamp, string Source, string Level, string Message);
