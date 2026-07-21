using ExecRFS.Models;

namespace ExecRFS.Tests.Models;

public class ProgressEventTests
{
    [Fact]
    public void TryParse_PlanEvent_CarriesTotalsForTheProgressBar()
    {
        // The GUI previously listened for a "scan_complete" event the CLI never emits, so
        // _totalBytes was never set and the progress bar sat at 0% forever.
        var evt = ProgressEvent.TryParse(
            @"{""event"":""plan"",""transfers"":10,""deletes"":2,""skipped"":141,""bytes"":4096}");
        Assert.NotNull(evt);
        Assert.Equal("plan", evt.Event);
        Assert.Equal(10, evt.Transfers);
        Assert.Equal(4096, evt.Bytes);
    }

    [Fact]
    public void TryParse_NonJsonLine_ReturnsNullWithoutThrowing()
    {
        // Human-readable log lines share the stream; they must not crash the handler.
        Assert.Null(ProgressEvent.TryParse("[12:00:00] Connecting to host..."));
        Assert.Null(ProgressEvent.TryParse(""));
    }

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

    [Fact]
    public void TryParse_ReviewConflictEvent_CarriesBothSidesAndTheRenamedCopy()
    {
        var evt = ProgressEvent.TryParse(
            @"{""event"":""review"",""kind"":""conflict"",""path"":""docs/report.docx""," +
            @"""client_size"":2100000,""client_mtime"":""2026-07-20T14:30:52.0000000Z""," +
            @"""server_size"":2050112,""server_mtime"":""2026-07-20T14:31:10.0000000Z""," +
            @"""renamed_to"":""docs/report.conflict-20260720-143052-server.docx""}");
        Assert.NotNull(evt);
        Assert.Equal("review", evt.Event);
        Assert.Equal("conflict", evt.Kind);
        Assert.Equal("docs/report.docx", evt.Path);
        Assert.Equal(2100000, evt.ClientSize);
        Assert.Equal("2026-07-20T14:30:52.0000000Z", evt.ClientMtime);
        Assert.Equal(2050112, evt.ServerSize);
        Assert.Equal("2026-07-20T14:31:10.0000000Z", evt.ServerMtime);
        Assert.Equal("docs/report.conflict-20260720-143052-server.docx", evt.RenamedTo);
    }

    [Fact]
    public void TryParse_ReviewResurrectionEvent_HasNoRenameAndKeepsUnknownSizesNegative()
    {
        // -1 is the CLI's "detail could not be decoded" sentinel. If it arrived as 0 the GUI
        // would render a real file as empty; if RenamedTo defaulted to "" it would draw a blank
        // "kept as" row for a file that was never renamed.
        var evt = ProgressEvent.TryParse(
            @"{""event"":""review"",""kind"":""resurrection"",""path"":""notes/todo.txt""," +
            @"""client_size"":-1,""client_mtime"":"""",""server_size"":-1,""server_mtime"":""""}");
        Assert.NotNull(evt);
        Assert.Equal("resurrection", evt.Kind);
        Assert.Equal(-1, evt.ClientSize);
        Assert.Equal(-1, evt.ServerSize);
        Assert.Equal("", evt.ServerMtime);
        Assert.Null(evt.RenamedTo);
    }
}
