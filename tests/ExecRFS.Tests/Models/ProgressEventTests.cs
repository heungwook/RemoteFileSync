using ExecRFS.Models;

namespace ExecRFS.Tests.Models;

public class ProgressEventTests
{
    [Fact]
    public void TryParse_StatusEvent()
    {
        var evt = ProgressEvent.TryParse(@"{""event"":""status"",""state"":""connecting"",""host"":""10.0.1.50"",""port"":15782}");
        Assert.NotNull(evt);
        Assert.Equal("status", evt.Event);
        Assert.Equal("connecting", evt.State);
        Assert.Equal("10.0.1.50", evt.Host);
        Assert.Equal(15782, evt.Port);
    }

    [Fact]
    public void TryParse_FileProgressEvent()
    {
        var evt = ProgressEvent.TryParse(@"{""event"":""file_progress"",""path"":""docs/report.docx"",""bytes_sent"":1400000,""total_bytes"":2100000,""thread"":1}");
        Assert.NotNull(evt);
        Assert.Equal("file_progress", evt.Event);
        Assert.Equal(1400000, evt.BytesSent);
        Assert.Equal(1, evt.Thread);
    }

    [Fact]
    public void TryParse_CompleteEvent()
    {
        var evt = ProgressEvent.TryParse(@"{""event"":""complete"",""files_transferred"":10,""files_deleted"":2,""bytes"":89700000,""elapsed_ms"":5200,""exit_code"":0}");
        Assert.NotNull(evt);
        Assert.Equal(10, evt.FilesTransferred);
        Assert.Equal(0, evt.ExitCode);
    }

    [Fact]
    public void TryParse_InvalidJson_ReturnsNull()
    {
        Assert.Null(ProgressEvent.TryParse("not json"));
    }

    [Fact]
    public void TryParse_DeleteEvent()
    {
        var evt = ProgressEvent.TryParse(@"{""event"":""delete"",""path"":""old.docx"",""backed_up"":true,""success"":true}");
        Assert.NotNull(evt);
        Assert.Equal("delete", evt.Event);
        Assert.True(evt.BackedUp);
    }

    [Fact]
    public void TryParse_ErrorEvent()
    {
        var evt = ProgressEvent.TryParse(@"{""event"":""error"",""message"":""Connection refused"",""fatal"":true}");
        Assert.NotNull(evt);
        Assert.True(evt.Fatal);
    }
}
