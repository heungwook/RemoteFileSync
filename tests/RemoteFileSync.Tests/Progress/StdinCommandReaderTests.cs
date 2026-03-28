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
