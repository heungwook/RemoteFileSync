using System;
using System.IO;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.State;

public sealed class PairMarkerTests : IDisposable
{
    private readonly string _tempDir;

    public PairMarkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rfs_pairmarker_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void PathFor_PlacesMarkerBesideDatabase()
    {
        var dbPath = Path.Combine(_tempDir, "sync.db");
        Assert.Equal(Path.Combine(_tempDir, "pair.marker"), PairMarker.PathFor(dbPath));
    }

    [Fact]
    public void PathFor_BareFileName_ReturnsBareMarkerName()
    {
        Assert.Equal("pair.marker", PairMarker.PathFor("sync.db"));
    }

    [Fact]
    public void Exists_FalseBeforeWrite_TrueAfterWrite()
    {
        var dbPath = Path.Combine(_tempDir, "sync.db");
        Assert.False(PairMarker.Exists(dbPath));
        PairMarker.Write(dbPath);
        Assert.True(PairMarker.Exists(dbPath));
    }

    [Fact]
    public void Exists_IgnoresTheDatabaseItself()
    {
        // The safety gate keys on marker-present + db-absent, so a marker must never be
        // inferred from the presence of sync.db — otherwise the gate can never fire.
        var dbPath = Path.Combine(_tempDir, "sync.db");
        File.WriteAllText(dbPath, "not a real database");
        Assert.False(PairMarker.Exists(dbPath));
    }

    [Fact]
    public void Write_CreatesMissingDirectory()
    {
        var dbPath = Path.Combine(_tempDir, "nested", "pairid", "sync.db");
        PairMarker.Write(dbPath);
        Assert.True(File.Exists(Path.Combine(_tempDir, "nested", "pairid", "pair.marker")));
    }

    [Fact]
    public void Write_IsIdempotent()
    {
        var dbPath = Path.Combine(_tempDir, "sync.db");
        PairMarker.Write(dbPath);
        PairMarker.Write(dbPath);
        Assert.True(PairMarker.Exists(dbPath));
    }
}
