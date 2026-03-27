using RemoteFileSync.Models;

namespace RemoteFileSync.Tests.Models;

public class FileEntryTests
{
    [Fact]
    public void Constructor_SetsPropertiesCorrectly()
    {
        var timestamp = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
        var entry = new FileEntry("docs/report.docx", 1024, timestamp);
        Assert.Equal("docs/report.docx", entry.RelativePath);
        Assert.Equal(1024, entry.FileSize);
        Assert.Equal(timestamp, entry.LastModifiedUtc);
    }

    [Fact]
    public void RelativePath_UsesForwardSlashes()
    {
        var entry = new FileEntry("docs\\sub\\file.txt", 100, DateTime.UtcNow);
        Assert.Equal("docs/sub/file.txt", entry.RelativePath);
    }

    [Fact]
    public void Equals_SamePathSizeTimestamp_ReturnsTrue()
    {
        var ts = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
        var a = new FileEntry("file.txt", 100, ts);
        var b = new FileEntry("file.txt", 100, ts);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Equals_DifferentSize_ReturnsFalse()
    {
        var ts = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
        var a = new FileEntry("file.txt", 100, ts);
        var b = new FileEntry("file.txt", 200, ts);
        Assert.NotEqual(a, b);
    }
}
