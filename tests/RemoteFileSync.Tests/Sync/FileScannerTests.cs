using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class FileScannerTests : IDisposable
{
    private readonly string _testDir;

    public FileScannerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"rfs_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    private void CreateFile(string relativePath, string content = "hello")
    {
        var fullPath = Path.Combine(_testDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    [Fact]
    public void Scan_EmptyFolder_ReturnsEmptyManifest()
    {
        var scanner = new FileScanner(_testDir, new(), new());
        var manifest = scanner.Scan();
        Assert.Equal(0, manifest.Count);
    }

    [Fact]
    public void Scan_SingleFile_ReturnsOneEntry()
    {
        CreateFile("file.txt");
        var scanner = new FileScanner(_testDir, new(), new());
        var manifest = scanner.Scan();
        Assert.Equal(1, manifest.Count);
        Assert.True(manifest.Contains("file.txt"));
    }

    [Fact]
    public void Scan_NestedFolders_ReturnsAllFiles()
    {
        CreateFile("a.txt");
        CreateFile("sub/b.txt");
        CreateFile("sub/deep/c.txt");
        var scanner = new FileScanner(_testDir, new(), new());
        var manifest = scanner.Scan();
        Assert.Equal(3, manifest.Count);
        Assert.True(manifest.Contains("sub/b.txt"));
        Assert.True(manifest.Contains("sub/deep/c.txt"));
    }

    [Fact]
    public void Scan_IncludePattern_FiltersCorrectly()
    {
        CreateFile("doc.docx");
        CreateFile("image.png");
        CreateFile("data.csv");
        var scanner = new FileScanner(_testDir, include: new List<string> { "*.docx" }, exclude: new());
        var manifest = scanner.Scan();
        Assert.Equal(1, manifest.Count);
        Assert.True(manifest.Contains("doc.docx"));
    }

    [Fact]
    public void Scan_ExcludePattern_FiltersCorrectly()
    {
        CreateFile("doc.docx");
        CreateFile("temp.tmp");
        CreateFile("backup.bak");
        var scanner = new FileScanner(_testDir, include: new(), exclude: new List<string> { "*.tmp", "*.bak" });
        var manifest = scanner.Scan();
        Assert.Equal(1, manifest.Count);
        Assert.True(manifest.Contains("doc.docx"));
    }

    [Fact]
    public void Scan_IncludeAndExclude_IncludeFirst()
    {
        CreateFile("report.docx");
        CreateFile("draft.docx");
        CreateFile("image.png");
        var scanner = new FileScanner(_testDir, include: new List<string> { "*.docx" }, exclude: new List<string> { "draft*" });
        var manifest = scanner.Scan();
        Assert.Equal(1, manifest.Count);
        Assert.True(manifest.Contains("report.docx"));
    }

    [Fact]
    public void Scan_FileEntry_HasCorrectSize()
    {
        CreateFile("sized.txt", "12345");
        var scanner = new FileScanner(_testDir, new(), new());
        var manifest = scanner.Scan();
        var entry = manifest.Get("sized.txt");
        Assert.NotNull(entry);
        Assert.Equal(5, entry.FileSize);
    }

    [Fact]
    public void Scan_FileEntry_HasUtcTimestamp()
    {
        CreateFile("ts.txt");
        var scanner = new FileScanner(_testDir, new(), new());
        var manifest = scanner.Scan();
        var entry = manifest.Get("ts.txt");
        Assert.NotNull(entry);
        Assert.Equal(DateTimeKind.Utc, entry.LastModifiedUtc.Kind);
    }
}
