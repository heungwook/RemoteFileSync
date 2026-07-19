using RemoteFileSync.Progress;

namespace RemoteFileSync.Tests.Progress;

public class StdinCommandReaderTests
{
    [Fact]
    public void PauseGate_InitiallyOpen()
    {
        using var reader = new StdinCommandReader(new StringReader(""));
        Assert.True(reader.PauseGate.IsSet);
    }

    [Fact]
    public void PauseThenStop_ReleasesTheGate_InsteadOfHangingForever()
    {
        // STOP exits the read loop permanently, so RESUME can never arrive. If STOP did not
        // also open the gate, a paused sync thread would block for the life of the process.
        var input = new StringReader("PAUSE\nSTOP\n");
        using var sw = new StringWriter();
        using var reader = new StdinCommandReader(input, sw);
        reader.Start();

        // Wait for STOP to actually be processed before asserting; the gate starts open, so
        // checking too early would pass regardless of the fix.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!reader.StopToken.IsCancellationRequested && DateTime.UtcNow < deadline)
            Thread.Sleep(10);

        Assert.True(reader.StopToken.IsCancellationRequested, "STOP was never processed.");
        // The invariant that prevents the deadlock: after STOP the read loop has exited, so
        // RESUME can never arrive and the gate must not be left closed.
        Assert.True(reader.PauseGate.IsSet,
            "PAUSE followed by STOP left the pause gate closed — a paused sync would hang forever.");
    }

    [Fact]
    public void WaitWhilePaused_ReturnsFalse_AfterStop()
    {
        var input = new StringReader("STOP\n");
        using var sw = new StringWriter();
        using var reader = new StdinCommandReader(input, sw);
        reader.Start();
        Thread.Sleep(200);

        Assert.False(reader.WaitWhilePaused(CancellationToken.None));
    }

    [Fact]
    public void WaitWhilePaused_HonoursExternalCancellation_WhilePaused()
    {
        // Ctrl+C must break a paused sync, not just STOP.
        var input = new StringReader("PAUSE\n");
        using var sw = new StringWriter();
        using var reader = new StdinCommandReader(input, sw);
        reader.Start();
        Thread.Sleep(200);
        Assert.False(reader.PauseGate.IsSet);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Assert.Throws<OperationCanceledException>(() => reader.WaitWhilePaused(cts.Token));
    }

    [Fact]
    public void PauseCommand_ClosesGate()
    {
        var input = new StringReader("PAUSE\n");
        using var sw = new StringWriter();
        using var reader = new StdinCommandReader(input, sw);
        reader.Start();
        Thread.Sleep(200);
        Assert.False(reader.PauseGate.IsSet);
    }

    [Fact]
    public void ResumeCommand_OpensGate()
    {
        var input = new StringReader("PAUSE\nRESUME\n");
        using var sw = new StringWriter();
        using var reader = new StdinCommandReader(input, sw);
        reader.Start();
        Thread.Sleep(200);
        Assert.True(reader.PauseGate.IsSet);
    }

    [Fact]
    public void StopCommand_CancelsToken()
    {
        var input = new StringReader("STOP\n");
        using var sw = new StringWriter();
        using var reader = new StdinCommandReader(input, sw);
        reader.Start();
        Thread.Sleep(200);
        Assert.True(reader.StopToken.IsCancellationRequested);
    }

    [Fact]
    public void PauseCommand_EmitsJsonStatus()
    {
        var input = new StringReader("PAUSE\n");
        using var sw = new StringWriter();
        using var reader = new StdinCommandReader(input, sw);
        reader.Start();
        Thread.Sleep(200);
        var output = sw.ToString();
        Assert.Contains("\"state\":\"paused\"", output);
    }

    [Fact]
    public void NullReader_NoOp()
    {
        using var reader = StdinCommandReader.Null;
        reader.Start();
        Assert.True(reader.PauseGate.IsSet);
        Assert.False(reader.StopToken.IsCancellationRequested);
    }
}
