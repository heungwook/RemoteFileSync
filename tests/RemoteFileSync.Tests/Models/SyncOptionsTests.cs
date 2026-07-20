using RemoteFileSync.Models;

namespace RemoteFileSync.Tests.Models;

public class SyncOptionsTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _syncDir;

    public SyncOptionsTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"rfs_opts_{Guid.NewGuid()}");
        _syncDir = Path.Combine(_testRoot, "data");
        Directory.CreateDirectory(_syncDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }

    [Fact]
    public void EffectiveBackupFolder_DefaultsBesideSyncFolder_NotInsideIt()
    {
        var options = new SyncOptions { IsServer = true, Folder = _syncDir };

        var backup = Path.GetFullPath(options.EffectiveBackupFolder);

        Assert.Equal(Path.Combine(_testRoot, ".rfs-backups-data"), backup);
        Assert.False(backup.StartsWith(Path.GetFullPath(_syncDir) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EffectiveBackupFolder_HonoursExplicitOverride()
    {
        var explicitPath = Path.Combine(_testRoot, "my-backups");
        var options = new SyncOptions { IsServer = true, Folder = _syncDir, BackupFolder = explicitPath };

        Assert.Equal(explicitPath, options.EffectiveBackupFolder);
    }

    [Fact]
    public void EffectiveBackupFolder_ThrowsForDriveRoot()
    {
        // A drive root has no parent, so there is no safe default. Falling back to the sync
        // folder would put backups inside the synced tree, which is the bug being fixed.
        var root = Path.GetPathRoot(Path.GetFullPath(_syncDir))!;
        var options = new SyncOptions { IsServer = true, Folder = root };

        var ex = Assert.Throws<ArgumentException>(() => options.EffectiveBackupFolder);
        Assert.Contains("--backup-folder", ex.Message);
    }

    [Fact]
    public void Validate_RejectsBackupFolderInsideSyncFolder()
    {
        var options = new SyncOptions
        {
            IsServer = true,
            Folder = _syncDir,
            BackupFolder = Path.Combine(_syncDir, "backups"),
        };

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("outside the sync folder", ex.Message);
    }

    [Fact]
    public void Validate_AcceptsBackupFolderOutsideSyncFolder()
    {
        var options = new SyncOptions
        {
            IsServer = true,
            Folder = _syncDir,
            BackupFolder = Path.Combine(_testRoot, "backups"),
        };

        options.Validate();   // must not throw
    }

    [Fact]
    public void Validate_AcceptsTheDefaultBackupFolder()
    {
        var options = new SyncOptions { IsServer = true, Folder = _syncDir };

        options.Validate();   // the default must not trip its own containment guard
    }

    [Fact]
    public void Mode_DefaultsToPush()
    {
        var options = new SyncOptions { IsServer = true, Folder = _syncDir };

        Assert.Equal(SyncMode.Push, options.Mode);
    }

    [Theory]
    [InlineData(SyncMode.Push, false)]
    [InlineData(SyncMode.Pull, false)]
    [InlineData(SyncMode.TwoWay, true)]
    public void Bidirectional_TracksMode(SyncMode mode, bool expected)
    {
        // Bidirectional is a read-only shim over Mode. A settable copy would let the two
        // drift apart, which is how a Pull sync could silently keep taking the branches
        // that write to the server.
        var options = new SyncOptions { IsServer = true, Folder = _syncDir, Mode = mode };

        Assert.Equal(expected, options.Bidirectional);
    }

    [Fact]
    public void SyncMode_ValuesAreStableWireNumbers()
    {
        // These numbers travel in the low 2 bits of the handshake's syncMode byte.
        // Renumbering them silently repoints an existing peer's sync direction.
        Assert.Equal(1, (byte)SyncMode.Push);
        Assert.Equal(2, (byte)SyncMode.Pull);
        Assert.Equal(3, (byte)SyncMode.TwoWay);
    }

    [Fact]
    public void EffectiveArchiveFolder_DefaultsBesideSyncFolder_NotInsideIt()
    {
        var options = new SyncOptions { IsServer = true, Folder = _syncDir };

        var archive = Path.GetFullPath(options.EffectiveArchiveFolder);

        Assert.Equal(Path.Combine(_testRoot, ".rfs-archive-data"), archive);
        Assert.False(archive.StartsWith(Path.GetFullPath(_syncDir) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EffectiveArchiveFolder_HonoursExplicitOverride()
    {
        var explicitPath = Path.Combine(_testRoot, "my-archive");
        var options = new SyncOptions { IsServer = true, Folder = _syncDir, ArchiveFolder = explicitPath };

        Assert.Equal(explicitPath, options.EffectiveArchiveFolder);
    }

    [Fact]
    public void EffectiveArchiveFolder_ThrowsForDriveRoot()
    {
        // Same reasoning as the backup folder: a drive root has no parent, and defaulting into
        // the sync folder would make archived deletions re-sync and resurrect themselves.
        var root = Path.GetPathRoot(Path.GetFullPath(_syncDir))!;
        var options = new SyncOptions { IsServer = true, Folder = root };

        var ex = Assert.Throws<ArgumentException>(() => options.EffectiveArchiveFolder);
        Assert.Contains("--archive-folder", ex.Message);
    }

    [Fact]
    public void ArchiveRetention_HasSafeDefaults()
    {
        var options = new SyncOptions { IsServer = true, Folder = _syncDir };

        Assert.Equal(30, options.ArchiveKeepDays);
        Assert.Equal(0L, options.ArchiveMaxBytes);   // 0 = no size cap
        Assert.False(options.MirrorDeletes);
        Assert.Equal(60, SyncOptions.SuspiciousSkewSeconds);
    }

    [Fact]
    public void Validate_RejectsArchiveFolderInsideSyncFolder()
    {
        // An archive inside the synced tree re-syncs to the peer, which recreates every file
        // the archive is holding — the deletion undoes itself on the next run.
        var options = new SyncOptions
        {
            IsServer = true,
            Folder = _syncDir,
            ArchiveFolder = Path.Combine(_syncDir, "archive"),
        };

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("--archive-folder", ex.Message);
        Assert.Contains("outside the sync folder", ex.Message);
    }

    [Fact]
    public void Validate_RejectsArchiveFolderEqualToSyncFolder()
    {
        // The worst case, and the one a naive prefix test misses: archiveFull has no trailing
        // separator, so "is the archive under the sync folder?" answers false for the sync
        // folder itself. Every archived deletion would then be written straight back into the
        // tree it was deleted from.
        var options = new SyncOptions
        {
            IsServer = true,
            Folder = _syncDir,
            ArchiveFolder = _syncDir,
        };

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("--archive-folder", ex.Message);
    }

    [Fact]
    public void Validate_RejectsNegativeArchiveKeepDays()
    {
        // A negative keep-age makes every session older than the cutoff, so the first prune
        // would empty the archive that is holding the user's only copy of deleted files.
        var options = new SyncOptions { IsServer = true, Folder = _syncDir, ArchiveKeepDays = -1 };

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("--archive-keep-days", ex.Message);
    }

    [Fact]
    public void Validate_RejectsNegativeArchiveMaxBytes()
    {
        // A negative cap is below any real archive size, so the size rule would prune every
        // session on every run. 0 — and only 0 — means "no cap".
        var options = new SyncOptions { IsServer = true, Folder = _syncDir, ArchiveMaxBytes = -1 };

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("--archive-max-size", ex.Message);
    }
}
