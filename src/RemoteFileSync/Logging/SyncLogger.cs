namespace RemoteFileSync.Logging;

public sealed class SyncLogger : IDisposable
{
    private readonly bool _verbose;
    private readonly bool _suppressConsole;
    private readonly StreamWriter? _logWriter;
    private readonly object _lock = new();

    public SyncLogger(bool verbose, string? logFile, bool suppressConsole = false)
    {
        _verbose = verbose;
        _suppressConsole = suppressConsole;
        if (!string.IsNullOrWhiteSpace(logFile))
        {
            try
            {
                var dir = Path.GetDirectoryName(logFile);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                // FileShare.ReadWrite so a concurrent client+server pair can share one log
                // path — the GUI launches both, and StreamWriter's default share mode made
                // the second one throw.
                var fs = new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _logWriter = new StreamWriter(fs) { AutoFlush = true };
            }
            catch (Exception ex)
            {
                // Degrade to console-only rather than aborting the sync, but say so: a
                // silently missing log is worse than a noisy one.
                _logWriter = null;
                Console.Error.WriteLine($"Warning: cannot open log file '{logFile}': {ex.Message}. Continuing without a log file.");
            }
        }
    }

    public void Error(string message) => Log("ERR", message, consoleAlways: true);
    public void Warning(string message) => Log("WRN", message, consoleAlways: true);
    public void Info(string message) => Log("INF", message, consoleAlways: false);
    public void Debug(string message) => Log("DBG", message, consoleAlways: false);
    public void Summary(string message) => Log("INF", message, consoleAlways: true);

    private void Log(string level, string message, bool consoleAlways)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var fullTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var consoleLine = $"[{timestamp}] {message}";
        var fileLine = $"[{fullTimestamp}] [{level}] {message}";

        lock (_lock)
        {
            if (!_suppressConsole && (consoleAlways || _verbose))
                Console.WriteLine(consoleLine);
            _logWriter?.WriteLine(fileLine);
        }
    }

    public void Dispose()
    {
        _logWriter?.Flush();
        _logWriter?.Dispose();
    }
}
