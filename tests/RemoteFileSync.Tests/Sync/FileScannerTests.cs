using RemoteFileSync.Sync;
using RemoteFileSync.Transfer;

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

    [Theory]
    // Path-shaped patterns previously matched against the filename only and silently did
    // nothing — the most common real-world exclude was a complete no-op.
    [InlineData("node_modules/*", "node_modules/index.js", false)]
    [InlineData("node_modules/*", "node_modules/deep/nested/a.js", false)]
    [InlineData("node_modules/*", "src/index.js", true)]
    // Windows users type backslashes; those must be normalised, not misclassified as names.
    [InlineData("node_modules\\*", "node_modules/index.js", false)]
    [InlineData("build\\out\\*", "build/out/app.exe", false)]
    // Plain name patterns keep working at any depth.
    [InlineData("*.tmp", "a.tmp", false)]
    [InlineData("*.tmp", "deep/sub/a.tmp", false)]
    [InlineData("*.tmp", "deep/sub/a.txt", true)]
    public void Scan_AppliesPathAndNamePatterns(string excludePattern, string relativePath, bool expectIncluded)
    {
        CreateFile(relativePath.Replace('/', Path.DirectorySeparatorChar));
        var scanner = new FileScanner(_testDir, new(), new List<string> { excludePattern });

        var manifest = scanner.Scan();

        Assert.Equal(expectIncluded, manifest.Get(relativePath) != null);
    }

    [Fact]
    public void Scan_ExcludesStagingFiles()
    {
        CreateFile("real.txt");
        CreateFile($"real.txt{FileTransferReceiver.StagingSuffix}abc123");

        var manifest = new FileScanner(_testDir, new(), new()).Scan();

        Assert.NotNull(manifest.Get("real.txt"));
        // Partial receives must never reach the peer.
        Assert.Single(manifest.Entries);
    }

    [Fact]
    public void Scan_SweepsStaleStagingFiles()
    {
        var stale = Path.Combine(_testDir, $"old.txt{FileTransferReceiver.StagingSuffix}deadbeef");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "abandoned");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        new FileScanner(_testDir, new(), new()).Scan();

        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void Scan_ReportsZeroInaccessibleDirectories_OnAHealthyTree()
    {
        CreateFile("a.txt");
        CreateFile("sub/b.txt".Replace('/', Path.DirectorySeparatorChar));

        var scanner = new FileScanner(_testDir, new(), new());
        scanner.Scan();

        // Non-zero would mean the manifest is incomplete and deletions must be refused.
        Assert.Equal(0, scanner.InaccessibleDirectories);
    }
}
