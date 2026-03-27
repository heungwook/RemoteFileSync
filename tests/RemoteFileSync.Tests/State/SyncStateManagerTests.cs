using RemoteFileSync.Models;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.State;

public class SyncStateManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SyncStateManager _manager;

    public SyncStateManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rfs_state_test_{Guid.NewGuid()}");
        _manager = new SyncStateManager(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void PairId_DeterministicAndCaseInsensitive()
    {
        var id1 = SyncStateManager.ComputePairId(@"C:\SyncFolder", "192.168.1.100", 15782);
        var id2 = SyncStateManager.ComputePairId(@"c:\syncfolder", "192.168.1.100", 15782);
        Assert.Equal(id1, id2);
        Assert.Equal(16, id1.Length);
    }

    [Fact]
    public void PairId_DifferentPairs_DifferentIds()
    {
        var id1 = SyncStateManager.ComputePairId(@"C:\FolderA", "host1", 1000);
        var id2 = SyncStateManager.ComputePairId(@"C:\FolderB", "host2", 2000);
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void MissingFile_ReturnsNull()
    {
        var result = _manager.LoadState(@"C:\NonExistent", "host", 9999);
        Assert.Null(result);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var manifest = new FileManifest();
        manifest.Add(new FileEntry("docs/report.docx", 1024, new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc)));
        manifest.Add(new FileEntry("data/export.csv", 2048, new DateTime(2026, 3, 27, 8, 0, 0, DateTimeKind.Utc)));
        var syncUtc = new DateTime(2026, 3, 28, 10, 0, 0, DateTimeKind.Utc);

        _manager.SaveState(@"C:\TestFolder", "localhost", 15782, manifest, syncUtc);
        var loaded = _manager.LoadState(@"C:\TestFolder", "localhost", 15782);

        Assert.NotNull(loaded);
        Assert.Equal(syncUtc, loaded.LastSyncUtc);
        Assert.Equal(2, loaded.Manifest.Count);
        var entry = loaded.Manifest.Get("docs/report.docx");
        Assert.NotNull(entry);
        Assert.Equal(1024, entry.FileSize);
        Assert.Equal(new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc), entry.LastModifiedUtc);
    }

    [Fact]
    public void CorruptedFile_ReturnsNull()
    {
        var statePath = _manager.GetStatePath(@"C:\TestFolder", "localhost", 15782);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllBytes(statePath, new byte[] { 0xFF, 0xFE, 0xFD });

        var result = _manager.LoadState(@"C:\TestFolder", "localhost", 15782);
        Assert.Null(result);
    }

    [Fact]
    public void AtomicWrite_TempFileCleanedUp()
    {
        var manifest = new FileManifest();
        manifest.Add(new FileEntry("file.txt", 100, DateTime.UtcNow));
        _manager.SaveState(@"C:\TestFolder", "localhost", 15782, manifest, DateTime.UtcNow);

        var statePath = _manager.GetStatePath(@"C:\TestFolder", "localhost", 15782);
        var tmpPath = statePath + ".tmp";
        Assert.False(File.Exists(tmpPath));
        Assert.True(File.Exists(statePath));
    }

    [Fact]
    public void GetStatePath_ReturnsExpectedStructure()
    {
        var path = _manager.GetStatePath(@"C:\SyncFolder", "192.168.1.100", 15782);
        Assert.EndsWith("sync-state.bin", path);
        Assert.Contains(_tempDir, path);
    }
}
