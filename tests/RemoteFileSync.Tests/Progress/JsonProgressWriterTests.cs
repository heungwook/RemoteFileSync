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
        writer.WritePlan(10, 2, 141);
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
    public void NullWriter_NoOutput()
    {
        var writer = JsonProgressWriter.Null;
        writer.WriteStatus("connecting");
        writer.WriteComplete(0, 0, 0, 0, 0);
    }
}
