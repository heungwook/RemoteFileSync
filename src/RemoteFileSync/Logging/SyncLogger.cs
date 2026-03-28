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
            var dir = Path.GetDirectoryName(logFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            _logWriter = new StreamWriter(logFile, append: true) { AutoFlush = true };
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
