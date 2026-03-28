using RemoteFileSync.Models;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.State;

public class SyncDatabaseMigrationTests : IDisposable
{
    private readonly string _tempDir;

    public SyncDatabaseMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rfs_migration_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Migration_ImportsBinaryState()
    {
        var pairDir = Path.Combine(_tempDir, "testpair");
        Directory.CreateDirectory(pairDir);
        var binPath = Path.Combine(pairDir, "sync-state.bin");
        var dbPath = Path.Combine(pairDir, "sync.db");

        // Write a binary state file manually (same format as SyncStateManager)
        using (var fs = File.Create(binPath))
        using (var writer = new BinaryWriter(fs, System.Text.Encoding.UTF8))
        {
            writer.Write("RFS1"u8);
            writer.Write(new DateTime(2026, 3, 28, 10, 0, 0, DateTimeKind.Utc).Ticks);
            writer.Write(2); // entry count
            // Entry 1
            var p1 = System.Text.Encoding.UTF8.GetBytes("docs/report.docx");
            writer.Write((short)p1.Length);
            writer.Write(p1);
            writer.Write(1024L);
            writer.Write(new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc).Ticks);
            // Entry 2
            var p2 = System.Text.Encoding.UTF8.GetBytes("data/export.csv");
            writer.Write((short)p2.Length);
            writer.Write(p2);
            writer.Write(2048L);
            writer.Write(new DateTime(2026, 3, 27, 8, 0, 0, DateTimeKind.Utc).Ticks);
        }

        SyncDatabase.MigrateFromBinary(binPath, dbPath);

        using var db = new SyncDatabase(dbPath);
        var all = db.GetAllTrackedFiles();
        Assert.Equal(2, all.Count());

        var report = db.GetFileState("docs/report.docx");
        Assert.NotNull(report);
        Assert.Equal("exists", report.Status);
        Assert.Equal(1024, report.FileSize);

        // Binary file should be renamed
        Assert.False(File.Exists(binPath));
        Assert.True(File.Exists(binPath + ".migrated"));
    }

    [Fact]
    public void Migration_NoBinaryFile_DoesNothing()
    {
        var binPath = Path.Combine(_tempDir, "nonexistent.bin");
        var dbPath = Path.Combine(_tempDir, "sync.db");

        SyncDatabase.MigrateFromBinary(binPath, dbPath);

        Assert.False(File.Exists(dbPath));
    }

    [Fact]
    public void Migration_DbAlreadyExists_SkipsMigration()
    {
        var pairDir = Path.Combine(_tempDir, "existing");
        Directory.CreateDirectory(pairDir);
        var binPath = Path.Combine(pairDir, "sync-state.bin");
        var dbPath = Path.Combine(pairDir, "sync.db");

        // Create both files
        File.WriteAllBytes(binPath, new byte[] { 0x52, 0x46, 0x53, 0x31 }); // "RFS1" magic
        using (var db = new SyncDatabase(dbPath)) { } // creates empty db

        SyncDatabase.MigrateFromBinary(binPath, dbPath);

        // Binary file should NOT be renamed (migration skipped)
        Assert.True(File.Exists(binPath));
    }
}
