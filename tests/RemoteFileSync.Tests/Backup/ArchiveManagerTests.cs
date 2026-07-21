using System.Diagnostics;
using System.Globalization;
using RemoteFileSync.Backup;
using RemoteFileSync.Security;

namespace RemoteFileSync.Tests.Backup;

public class ArchiveManagerTests : IDisposable
{
    private readonly string _syncDir;
    private readonly string _archiveDir;

    public ArchiveManagerTests()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rfs_arc_{Guid.NewGuid()}");
        _syncDir = Path.Combine(root, "sync");
        _archiveDir = Path.Combine(root, "archive");
        Directory.CreateDirectory(_syncDir);
        Directory.CreateDirectory(_archiveDir);
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_syncDir)!;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private void CreateSyncFile(string relativePath, string content = "original")
    {
        var full = Path.Combine(_syncDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private ArchiveManager NewManager(DateTime sessionStartUtc) =>
        new(_syncDir, _archiveDir, sessionStartUtc);

    private static DateTime Stamp => new(2026, 7, 19, 14, 30, 52, DateTimeKind.Utc);

    private const string StampFolder = "20260719-143052";

    [Fact]
    public void SessionFolderName_IsSessionStartStamp_AndSessionRootHangsOffArchiveRoot()
    {
        var mgr = NewManager(Stamp);

        Assert.Equal(StampFolder, mgr.SessionFolderName);
        Assert.Equal(Path.Combine(Path.GetFullPath(_archiveDir), StampFolder), mgr.SessionRoot);
    }

    [Theory]
    [InlineData(ArchiveReason.Deleted, "deleted")]
    [InlineData(ArchiveReason.Overwritten, "overwritten")]
    [InlineData(ArchiveReason.Conflict, "conflict")]
    public void Archive_PartitionsByReason(ArchiveReason reason, string expectedFolder)
    {
        CreateSyncFile("report.docx");
        var mgr = NewManager(Stamp);

        Assert.True(mgr.Archive("report.docx", reason, removeOriginal: false));
        Assert.True(File.Exists(Path.Combine(_archiveDir, StampFolder, expectedFolder, "report.docx")));
    }

    [Fact]
    public void Archive_PreservesNestedStructureUnderTheReasonFolder()
    {
        CreateSyncFile("docs/sub/file.txt");
        var mgr = NewManager(Stamp);

        Assert.True(mgr.Archive("docs/sub/file.txt", ArchiveReason.Overwritten, removeOriginal: false));
        Assert.True(File.Exists(Path.Combine(
            _archiveDir, StampFolder, "overwritten", "docs", "sub", "file.txt")));
    }

    [Fact]
    public void Archive_RemoveOriginalFalse_LeavesOriginalInPlace()
    {
        CreateSyncFile("report.docx");
        var mgr = NewManager(Stamp);

        Assert.True(mgr.Archive("report.docx", ArchiveReason.Overwritten, removeOriginal: false));
        // Copy, not move: a failed transfer must not leave the sync folder without the file.
        Assert.True(File.Exists(Path.Combine(_syncDir, "report.docx")));
        Assert.Equal("original", File.ReadAllText(
            Path.Combine(_archiveDir, StampFolder, "overwritten", "report.docx")));
    }

    [Fact]
    public void Archive_RemoveOriginalTrue_CopiesThenDeletesOriginal()
    {
        CreateSyncFile("report.docx");
        var mgr = NewManager(Stamp);

        Assert.True(mgr.Archive("report.docx", ArchiveReason.Deleted, removeOriginal: true));
        // Deletion propagation: the original goes away, but only after the copy succeeded.
        Assert.False(File.Exists(Path.Combine(_syncDir, "report.docx")));
        Assert.Equal("original", File.ReadAllText(
            Path.Combine(_archiveDir, StampFolder, "deleted", "report.docx")));
    }

    [Fact]
    public void Archive_SamePathTwiceInOneSession_AppendsNumericSuffix()
    {
        var mgr = NewManager(Stamp);
        CreateSyncFile("report.docx", "version1");
        Assert.True(mgr.Archive("report.docx", ArchiveReason.Overwritten, removeOriginal: false));
        CreateSyncFile("report.docx", "version2");
        Assert.True(mgr.Archive("report.docx", ArchiveReason.Overwritten, removeOriginal: false));

        // One path can be archived twice in a session; a clobbering copy would destroy the
        // earlier version and the session would no longer be a faithful restore point.
        var dir = Path.Combine(_archiveDir, StampFolder, "overwritten");
        Assert.Equal("version1", File.ReadAllText(Path.Combine(dir, "report.docx")));
        Assert.Equal("version2", File.ReadAllText(Path.Combine(dir, "report_1.docx")));
    }

    [Fact]
    public void Archive_RejectsPathEscapingTheSyncRoot()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_syncDir)!, "outside.txt");
        File.WriteAllText(outside, "secret");
        var mgr = NewManager(Stamp);

        // relativePath arrives from the network on deletion propagation, so containment must
        // hold before the path reaches the filesystem.
        Assert.False(mgr.Archive("../outside.txt", ArchiveReason.Deleted, removeOriginal: true));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void Archive_MissingFile_ReturnsFalse()
    {
        var mgr = NewManager(Stamp);
        Assert.False(mgr.Archive("nonexistent.txt", ArchiveReason.Deleted, removeOriginal: true));
    }

    [Fact]
    public async Task Archive_ConcurrentCalls_AllSucceed()
    {
        for (int i = 0; i < 10; i++) CreateSyncFile($"file{i}.txt", $"content{i}");
        var mgr = NewManager(Stamp);

        var tasks = Enumerable.Range(0, 10)
            .Select(i => Task.Run(() => mgr.Archive($"file{i}.txt", ArchiveReason.Deleted, removeOriginal: false)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, Assert.True);
    }

    [Fact]
    public void Archive_RunSpanningMidnightUtc_LandsInExactlyOneSessionFolder()
    {
        // Regression lock: BackupManager derived its folder from DateTime.UtcNow on EVERY call,
        // so a run starting at 23:59:59 and finishing at 00:00:01 split into two dated folders
        // and neither half was a complete restore point. The stamp is now fixed at construction,
        // so the folder is a function of the session start alone, never of the wall clock.
        var sessionStart = new DateTime(2026, 7, 19, 23, 59, 59, DateTimeKind.Utc);
        var mgr = NewManager(sessionStart);

        CreateSyncFile("before-midnight.txt", "before");
        Assert.True(mgr.Archive("before-midnight.txt", ArchiveReason.Deleted, removeOriginal: true));
        CreateSyncFile("after-midnight.txt", "after");
        Assert.True(mgr.Archive("after-midnight.txt", ArchiveReason.Deleted, removeOriginal: true));

        var sessionFolders = Directory.GetDirectories(_archiveDir);
        Assert.Single(sessionFolders);
        Assert.Equal("20260719-235959", Path.GetFileName(sessionFolders[0]));

        // sessionStart is a fixed past instant, so the wall clock cannot coincide with it:
        // this proves the folder name did not come from DateTime.UtcNow.
        Assert.NotEqual(
            DateTime.UtcNow.ToString(ArchiveManager.SessionFolderFormat, CultureInfo.InvariantCulture),
            Path.GetFileName(sessionFolders[0]));

        var deletedDir = Path.Combine(_archiveDir, "20260719-235959", "deleted");
        Assert.Equal("before", File.ReadAllText(Path.Combine(deletedDir, "before-midnight.txt")));
        Assert.Equal("after", File.ReadAllText(Path.Combine(deletedDir, "after-midnight.txt")));
    }

    /// <summary>
    /// Builds a peer-supplied path that PathGuard ACCEPTS — it resolves to a file inside the
    /// sync root — but which walks out of the archive root if it is used to build the
    /// destination: enough ".." to clamp at the drive root, then back down into the sync folder.
    /// </summary>
    private string BuildAliasPathIntoSyncRoot(string fileName)
    {
        var syncFull = Path.GetFullPath(_syncDir);
        var driveRoot = Path.GetPathRoot(syncFull)!;
        var tail = syncFull.Substring(driveRoot.Length);   // no drive letter: PathGuard rejects ':'
        var climb = string.Concat(Enumerable.Repeat(".." + Path.DirectorySeparatorChar, 40));
        return climb + Path.Combine(tail, fileName);
    }

    [Fact]
    public void Archive_DotSegmentAliasOfAnInsideFile_StillLandsUnderTheSessionFolder()
    {
        CreateSyncFile("aliased.txt", "aliased");
        var alias = BuildAliasPathIntoSyncRoot("aliased.txt");
        var mgr = NewManager(Stamp);

        // Precondition: this alias is ACCEPTED by PathGuard (it resolves inside the root), so
        // the source-side guard cannot be what protects the destination.
        Assert.True(PathGuard.TryResolveWithinRoot(_syncDir, alias, out var resolved));
        Assert.Equal(Path.Combine(Path.GetFullPath(_syncDir), "aliased.txt"), resolved);

        Assert.True(mgr.Archive(alias, ArchiveReason.Deleted, removeOriginal: true));

        // The copy must be in the archive, not squatting in the live sync tree where the next
        // scan would re-sync it to the peer and where Prune could never reclaim it.
        Assert.Equal("aliased", File.ReadAllText(
            Path.Combine(_archiveDir, StampFolder, "deleted", "aliased.txt")));
        Assert.Empty(Directory.GetFiles(_syncDir));
    }

    [Fact]
    public void Archive_DeletedReason_IsTheOnlyDeletionPathAndAlwaysLeavesARestorePoint()
    {
        // Deletion propagation obeys a peer-supplied "back up first" flag. The flag is now
        // ignored: whatever the peer asks for, the file is archived before it is removed, so
        // a hostile or buggy peer cannot make us delete without a restore point.
        CreateSyncFile("victim.txt", "irreplaceable");
        var mgr = NewManager(Stamp);

        Assert.True(mgr.Archive("victim.txt", ArchiveReason.Deleted, removeOriginal: true));
        Assert.False(File.Exists(Path.Combine(_syncDir, "victim.txt")));
        Assert.Equal("irreplaceable", File.ReadAllText(
            Path.Combine(_archiveDir, StampFolder, "deleted", "victim.txt")));
    }

    /// <summary>Fabricates an already-archived session folder of a known size.</summary>
    private string CreateArchivedSession(DateTime startUtc, string fileName, int sizeBytes)
    {
        var sessionRoot = Path.Combine(
            _archiveDir, startUtc.ToString(ArchiveManager.SessionFolderFormat, CultureInfo.InvariantCulture));
        var reasonDir = Path.Combine(sessionRoot, "deleted");
        Directory.CreateDirectory(reasonDir);
        File.WriteAllBytes(Path.Combine(reasonDir, fileName), new byte[sizeBytes]);
        return sessionRoot;
    }

    [Fact]
    public void Prune_RemovesSessionsOlderThanKeepAge_AndKeepsNewerOnes()
    {
        var stale = CreateArchivedSession(DateTime.UtcNow.AddDays(-40), "a.txt", 16);
        var fresh = CreateArchivedSession(DateTime.UtcNow.AddDays(-2), "b.txt", 16);

        var result = ArchiveManager.Prune(_archiveDir, TimeSpan.FromDays(30), maxBytes: 0);

        Assert.Equal(1, result.SessionsRemoved);
        Assert.Equal(16L, result.BytesFreed);
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void Prune_ZeroKeepAge_KeepsEverythingForever()
    {
        var ancient = CreateArchivedSession(DateTime.UtcNow.AddDays(-4000), "a.txt", 16);

        // --archive-keep-days 0 means keep forever, not delete everything.
        var result = ArchiveManager.Prune(_archiveDir, TimeSpan.Zero, maxBytes: 0);

        Assert.Equal(0, result.SessionsRemoved);
        Assert.True(Directory.Exists(ancient));
    }

    [Fact]
    public void Prune_KeepAgeLargerThanTheCalendar_KeepsEverythingInsteadOfThrowing()
    {
        // DateTime.UtcNow - TimeSpan.MaxValue underflows DateTime.MinValue and throws
        // ArgumentOutOfRangeException. Prune runs at session start, before any transfer, so an
        // out-of-range keepAge must degrade to "keep everything", never abort the whole sync.
        var ancient = CreateArchivedSession(DateTime.UtcNow.AddDays(-4000), "a.txt", 16);

        var result = ArchiveManager.Prune(_archiveDir, TimeSpan.MaxValue, maxBytes: 0);

        Assert.Equal(0, result.SessionsRemoved);
        Assert.True(Directory.Exists(ancient));
    }

    [Fact]
    public void Prune_EnforcesSizeCap_DeletingWholeSessionsOldestFirst()
    {
        var oldest = CreateArchivedSession(DateTime.UtcNow.AddHours(-3), "a.txt", 1000);
        var middle = CreateArchivedSession(DateTime.UtcNow.AddHours(-2), "b.txt", 1000);
        var newest = CreateArchivedSession(DateTime.UtcNow.AddHours(-1), "c.txt", 1000);

        var result = ArchiveManager.Prune(_archiveDir, TimeSpan.Zero, maxBytes: 2000);

        Assert.Equal(1, result.SessionsRemoved);
        Assert.Equal(1000L, result.BytesFreed);
        Assert.False(Directory.Exists(oldest));
        // Whole folders only: a partially-emptied session is not a restore point.
        Assert.True(File.Exists(Path.Combine(middle, "deleted", "b.txt")));
        Assert.True(File.Exists(Path.Combine(newest, "deleted", "c.txt")));
    }

    [Fact]
    public void Prune_IgnoresDirectoriesWhoseNameDoesNotParseAsASessionStamp()
    {
        var legacy = Path.Combine(_archiveDir, "20260101");            // pre-ArchiveManager dated backup
        var foreign = Path.Combine(_archiveDir, "my-important-stuff"); // user dropped it here
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(foreign);
        File.WriteAllBytes(Path.Combine(legacy, "a.txt"), new byte[64]);
        File.WriteAllBytes(Path.Combine(foreign, "b.txt"), new byte[64]);

        // Maximally aggressive retention: anything we had written would go. Nothing here was.
        var result = ArchiveManager.Prune(_archiveDir, TimeSpan.FromTicks(1), maxBytes: 1);

        Assert.Equal(0, result.SessionsRemoved);
        Assert.Equal(0L, result.BytesFreed);
        Assert.True(File.Exists(Path.Combine(legacy, "a.txt")));
        Assert.True(File.Exists(Path.Combine(foreign, "b.txt")));
    }

    [Fact]
    public void Prune_NegativeKeepAge_ThrowsInsteadOfSilentlyKeepingForever()
    {
        // 0 = disabled is this project's convention (SyncDatabase.PurgeTombstonesOlderThan);
        // a NEGATIVE age has no meaning and can only come from a caller defect. Folding it into
        // "keep forever" would let a broken --archive-keep-days run forever while the archive
        // grows without bound and nobody ever learns why.
        var ancient = CreateArchivedSession(DateTime.UtcNow.AddDays(-4000), "a.txt", 16);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ArchiveManager.Prune(_archiveDir, TimeSpan.FromDays(-1), maxBytes: 0));
        Assert.True(Directory.Exists(ancient));
    }

    [Fact]
    public void Prune_MissingArchiveRoot_IsANoOp()
    {
        // First run: retention executes before anything has ever been archived.
        var never = Path.Combine(Path.GetDirectoryName(_syncDir)!, "no-such-archive");

        var result = ArchiveManager.Prune(never, TimeSpan.FromDays(30), maxBytes: 0);

        Assert.Equal(0, result.SessionsRemoved);
        Assert.Equal(0L, result.BytesFreed);
    }

    [Fact]
    public void Prune_RootCannotBeEnumerated_ReturnsEmptyResultInsteadOfThrowing()
    {
        // Regression for the IMPORTANT finding: Prune runs between StartSession (SyncClient.cs)
        // and the try/finally that guarantees CompleteSession. An exception escaping the
        // Directory.GetDirectories(rootFull) enumeration — permissions, an AV lock, or the
        // TOCTOU against the Directory.Exists check just above it — would leak an open session
        // row, reintroducing the bug commit 2266c93 fixed. Retention is explicitly best-effort
        // (TryDeleteSession already treats a locked SESSION folder this way); this locks the
        // same treatment for the archive ROOT itself.
        var locked = Path.Combine(_archiveDir, "locked-root");
        Directory.CreateDirectory(locked);

        // Deny "list folder contents" for the current user on `locked` itself. Directory.Exists
        // still reports true — it only reads the entry's attributes, not its contents — so this
        // reaches Directory.GetDirectories rather than the earlier Directory.Exists guard.
        RunIcacls(locked, "/deny", $"{Environment.UserName}:(RD)");
        try
        {
            var result = ArchiveManager.Prune(locked, TimeSpan.FromDays(30), maxBytes: 0);

            Assert.Equal(0, result.SessionsRemoved);
            Assert.Equal(0L, result.BytesFreed);
        }
        finally
        {
            // Dispose() deletes _archiveDir recursively; a still-denied ACL would make that
            // throw too, so access must be restored before the directory is ever removed.
            RunIcacls(locked, "/remove:d", Environment.UserName);
        }
    }

    private static void RunIcacls(string path, params string[] args)
    {
        var psi = new ProcessStartInfo("icacls")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(path);
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
    }
}
