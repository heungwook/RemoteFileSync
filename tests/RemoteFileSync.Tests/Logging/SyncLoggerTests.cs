using RemoteFileSync.Logging;

namespace RemoteFileSync.Tests.Logging;

public class SyncLoggerTests : IDisposable
{
    private readonly StringWriter _consoleOut = new();
    private readonly TextWriter _originalOut;

    public SyncLoggerTests()
    {
        _originalOut = Console.Out;
        Console.SetOut(_consoleOut);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        _consoleOut.Dispose();
    }

    [Fact]
    public void Error_AlwaysShownOnConsole()
    {
        var logger = new SyncLogger(verbose: false, logFile: null);
        logger.Error("something broke");
        var output = _consoleOut.ToString();
        Assert.Contains("something broke", output);
    }

    [Fact]
    public void Info_HiddenWhenNotVerbose()
    {
        var logger = new SyncLogger(verbose: false, logFile: null);
        logger.Info("detailed info");
        var output = _consoleOut.ToString();
        Assert.DoesNotContain("detailed info", output);
    }

    [Fact]
    public void Info_ShownWhenVerbose()
    {
        var logger = new SyncLogger(verbose: true, logFile: null);
        logger.Info("detailed info");
        var output = _consoleOut.ToString();
        Assert.Contains("detailed info", output);
    }

    [Fact]
    public void Debug_HiddenWhenNotVerbose()
    {
        var logger = new SyncLogger(verbose: false, logFile: null);
        logger.Debug("debug stuff");
        var output = _consoleOut.ToString();
        Assert.DoesNotContain("debug stuff", output);
    }

    [Fact]
    public void Debug_ShownWhenVerbose()
    {
        var logger = new SyncLogger(verbose: true, logFile: null);
        logger.Debug("debug stuff");
        var output = _consoleOut.ToString();
        Assert.Contains("debug stuff", output);
    }

    [Fact]
    public void Summary_AlwaysShown()
    {
        var logger = new SyncLogger(verbose: false, logFile: null);
        logger.Summary("sync complete");
        var output = _consoleOut.ToString();
        Assert.Contains("sync complete", output);
    }

    [Fact]
    public void LogAfterDispose_DoesNotThrow()
    {
        // _logWriter is readonly and cannot be nulled on Dispose, so a naive fix would leave
        // Log() calling WriteLine on a disposed StreamWriter. The Ctrl+C handler's shutdown
        // Warning is a real caller that can land after Dispose.
        var logPath = Path.Combine(Path.GetTempPath(), $"synclogger_test_{Guid.NewGuid()}.log");
        try
        {
            var logger = new SyncLogger(verbose: false, logFile: logPath);
            logger.Info("before dispose");
            logger.Dispose();

            var ex = Record.Exception(() =>
            {
                logger.Warning("after dispose");
                logger.Info("also after dispose");
            });
            Assert.Null(ex);
        }
        finally
        {
            if (File.Exists(logPath)) File.Delete(logPath);
        }
    }

    [Fact]
    public void DoubleDispose_DoesNotThrow()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"synclogger_test_{Guid.NewGuid()}.log");
        try
        {
            var logger = new SyncLogger(verbose: false, logFile: logPath);
            logger.Info("some line");
            logger.Dispose();

            var ex = Record.Exception(logger.Dispose);
            Assert.Null(ex);
        }
        finally
        {
            if (File.Exists(logPath)) File.Delete(logPath);
        }
    }

    [Fact]
    public void LogFile_WritesAllLevels()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"synclogger_test_{Guid.NewGuid()}.log");
        try
        {
            var logger = new SyncLogger(verbose: false, logFile: logPath);
            logger.Info("info line");
            logger.Debug("debug line");
            logger.Error("error line");
            logger.Dispose();
            var content = File.ReadAllText(logPath);
            Assert.Contains("info line", content);
            Assert.Contains("debug line", content);
            Assert.Contains("error line", content);
        }
        finally
        {
            if (File.Exists(logPath)) File.Delete(logPath);
        }
    }
}
