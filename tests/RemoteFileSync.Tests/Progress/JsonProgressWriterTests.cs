using System.Text.Json;
using RemoteFileSync.Progress;

namespace RemoteFileSync.Tests.Progress;

public class JsonProgressWriterTests
{
    [Fact]
    public void WriteStatus_EmitsValidJson()
    {
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);
        writer.WriteStatus("connecting", host: "10.0.1.50", port: 15782);
        var json = sw.ToString().Trim();
        var doc = JsonDocument.Parse(json);
        Assert.Equal("status", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("connecting", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal("10.0.1.50", doc.RootElement.GetProperty("host").GetString());
        Assert.Equal(15782, doc.RootElement.GetProperty("port").GetInt32());
    }

    [Fact]
    public void WriteManifest_EmitsValidJson()
    {
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);
        writer.WriteManifest("local", 156, 234500000);
        var json = sw.ToString().Trim();
        var doc = JsonDocument.Parse(json);
        Assert.Equal("manifest", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("local", doc.RootElement.GetProperty("side").GetString());
        Assert.Equal(156, doc.RootElement.GetProperty("files").GetInt32());
    }

    [Fact]
    public void WritePlan_EmitsValidJson()
    {
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);
        writer.WritePlan(10, 2, 141, 4096);
        var json = sw.ToString().Trim();
        var doc = JsonDocument.Parse(json);
        Assert.Equal("plan", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal(10, doc.RootElement.GetProperty("transfers").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("deletes").GetInt32());
        Assert.Equal(141, doc.RootElement.GetProperty("skipped").GetInt32());
    }

    [Fact]
    public void WriteFileStart_EmitsValidJson()
    {
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);
        writer.WriteFileStart("send", "docs/report.docx", 2100000, true, 1);
        var json = sw.ToString().Trim();
        var doc = JsonDocument.Parse(json);
        Assert.Equal("file_start", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("send", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal("docs/report.docx", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal(2100000, doc.RootElement.GetProperty("size").GetInt64());
        Assert.True(doc.RootElement.GetProperty("compressed").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("thread").GetInt32());
    }

    [Fact]
    public void WriteFileProgress_EmitsValidJson()
    {
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);
        writer.WriteFileProgress("docs/report.docx", 1400000, 2100000, 1);
        var json = sw.ToString().Trim();
        var doc = JsonDocument.Parse(json);
        Assert.Equal("file_progress", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal(1400000, doc.RootElement.GetProperty("bytes_sent").GetInt64());
        Assert.Equal(2100000, doc.RootElement.GetProperty("total_bytes").GetInt64());
    }

    [Fact]
    public void WriteComplete_EmitsValidJson()
    {
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);
        writer.WriteComplete(10, 2, 89700000, 5200, 0);
        var json = sw.ToString().Trim();
        var doc = JsonDocument.Parse(json);
        Assert.Equal("complete", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal(10, doc.RootElement.GetProperty("files_transferred").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("files_deleted").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public void WriteError_EmitsValidJson()
    {
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);
        writer.WriteError("Connection refused", fatal: true);
        var json = sw.ToString().Trim();
        var doc = JsonDocument.Parse(json);
        Assert.Equal("error", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("Connection refused", doc.RootElement.GetProperty("message").GetString());
        Assert.True(doc.RootElement.GetProperty("fatal").GetBoolean());
    }

    [Fact]
    public void WriteReview_Conflict_EmitsBothSidesAndTheRenamedCopy()
    {
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);
        writer.WriteReview("conflict", "docs/report.docx",
            2100000, "2026-07-20T14:30:52.0000000Z",
            2050112, "2026-07-20T14:31:10.0000000Z",
            renamed_to: "docs/report.conflict-20260720-143052-server.docx");
        var doc = JsonDocument.Parse(sw.ToString().Trim());
        Assert.Equal("review", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("conflict", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("docs/report.docx", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal(2100000, doc.RootElement.GetProperty("client_size").GetInt64());
        Assert.Equal("2026-07-20T14:30:52.0000000Z", doc.RootElement.GetProperty("client_mtime").GetString());
        Assert.Equal(2050112, doc.RootElement.GetProperty("server_size").GetInt64());
        Assert.Equal("2026-07-20T14:31:10.0000000Z", doc.RootElement.GetProperty("server_mtime").GetString());
        Assert.Equal("docs/report.conflict-20260720-143052-server.docx",
            doc.RootElement.GetProperty("renamed_to").GetString());
    }

    [Fact]
    public void WriteReview_NoRename_OmitsTheKeyEntirely()
    {
        // A resurrection renames nothing. Emitting renamed_to:null would make the GUI render an
        // empty "kept as" row for every resurrected file.
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);
        writer.WriteReview("resurrection", "notes/todo.txt",
            1024, "2026-07-20T09:15:00.0000000Z",
            900, "2026-07-19T17:00:00.0000000Z");
        var doc = JsonDocument.Parse(sw.ToString().Trim());
        Assert.Equal("resurrection", doc.RootElement.GetProperty("kind").GetString());
        Assert.False(doc.RootElement.TryGetProperty("renamed_to", out _));
    }

    [Fact]
    public void WriteReview_EmitsOneSelfContainedLinePerItem()
    {
        // The GUI parses this stream line by line; a multi-line or batched payload would be
        // dropped by ProgressEvent.TryParse.
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);
        writer.WriteReview("conflict", "a.docx", 1, "2026-07-20T09:15:00.0000000Z", 2, "2026-07-19T17:00:00.0000000Z");
        writer.WriteReview("resurrection", "b.txt", 3, "2026-07-20T09:15:00.0000000Z", 4, "2026-07-19T17:00:00.0000000Z");

        var lines = sw.ToString().Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("conflict", JsonDocument.Parse(lines[0]).RootElement.GetProperty("kind").GetString());
        Assert.Equal("resurrection", JsonDocument.Parse(lines[1]).RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public void NullWriter_NoOutput()
    {
        var writer = JsonProgressWriter.Null;
        writer.WriteStatus("connecting");
        writer.WriteComplete(0, 0, 0, 0, 0);
        writer.WriteReview("conflict", "a.docx", 1, "", 2, "");
    }
}
