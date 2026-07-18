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
}
