using RemoteFileSync.Backup;
using RemoteFileSync.Models;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class ConflictKeepBothExecutorTests : IDisposable
{
    private static readonly DateTime Stamp = new(2026, 7, 20, 14, 30, 52, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rfs_ckb_{Guid.NewGuid()}");
    private readonly string _sync;
    private readonly string _elsewhere;
    private readonly string _archiveRoot;

    public ConflictKeepBothExecutorTests()
    {
        _sync = Path.Combine(_root, "sync");
        _elsewhere = Path.Combine(_root, "elsewhere");
        _archiveRoot = Path.Combine(_root, "archive");
        Directory.CreateDirectory(_sync);
        Directory.CreateDirectory(_elsewhere);
        Directory.CreateDirectory(_archiveRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void Write(string relativePath, string content, DateTime mtimeUtc)
    {
        var full = Path.Combine(_sync, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        File.SetLastWriteTimeUtc(full, mtimeUtc);
    }

    private ArchiveManager WorkingArchive() => new(_sync, _archiveRoot, Stamp);

    /// <summary>An ArchiveManager rooted somewhere else, so it never finds the file it is asked
    /// to preserve and reports NothingToArchive without throwing.</summary>
    private ArchiveManager MisrootedArchive() => new(_elsewhere, _archiveRoot, Stamp);

    /// <summary>
    /// An ArchiveManager that finds the file but cannot preserve it: the archive root is an
    /// existing FILE, so Directory.CreateDirectory throws inside TryArchive and it returns
    /// Failed. This is the only branch on which a delete-after-archive would destroy the user's
    /// only copy, so it needs a test that reaches it for the stated reason rather than by
    /// tripping over a later guard.
    /// </summary>
    private ArchiveManager BlockedArchive()
    {
        var blocked = Path.Combine(_root, "blocked-archive");
        if (!File.Exists(blocked)) File.WriteAllText(blocked, "not a directory");
        return new ArchiveManager(_sync, blocked, Stamp);
    }

    private static FileManifest Manifest(string path, long size, DateTime mtimeUtc)
    {
        var m = new FileManifest();
        m.Add(new FileEntry(path, size, mtimeUtc));
        return m;
    }

    [Fact]
    public void Expand_ServerNewer_RenamesClientCopyAndMovesOneFileEachWay()
    {
        var client = Manifest("report.txt", 10, Stamp);
        var server = Manifest("report.txt", 20, Stamp.AddHours(1));
        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, "report.txt") };

        var result = ConflictKeepBothExecutor.Expand(plan, client, server, ClockSkew.None, Stamp, _sync);

        var expectedName = "report.conflict-20260720-143052-client.txt";
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(SyncActionType.ConflictKeepBoth, result.Entries[0].Action);
        Assert.Equal(expectedName, result.Entries[0].RelativePath);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[1].Action);
        Assert.Equal(expectedName, result.Entries[1].RelativePath);
        Assert.Equal(SyncActionType.SendToClient, result.Entries[2].Action);
        Assert.Equal("report.txt", result.Entries[2].RelativePath);
        Assert.Equal(expectedName, result.RenamedTo["report.txt"]);
    }

    [Fact]
    public void Expand_ClientNewer_RenamesServerCopyAndMovesOneFileEachWay()
    {
        var client = Manifest("report.txt", 20, Stamp.AddHours(1));
        var server = Manifest("report.txt", 10, Stamp);
        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, "report.txt") };

        var result = ConflictKeepBothExecutor.Expand(plan, client, server, ClockSkew.None, Stamp, _sync);

        var expectedName = "report.conflict-20260720-143052-server.txt";
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(SyncActionType.ConflictKeepBoth, result.Entries[0].Action);
        Assert.Equal(expectedName, result.Entries[0].RelativePath);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[1].Action);
        Assert.Equal("report.txt", result.Entries[1].RelativePath);
        Assert.Equal(SyncActionType.SendToClient, result.Entries[2].Action);
        Assert.Equal(expectedName, result.Entries[2].RelativePath);
        Assert.Equal(expectedName, result.RenamedTo["report.txt"]);
    }

    [Fact]
    public void Expand_WinnerIsDecidedAfterSkewNormalisation()
    {
        // Server clock runs one hour fast. Raw mtimes say the server is newer; in client time it
        // is older. Without normalisation the loser would be decided by how wrong a clock is.
        var client = Manifest("report.txt", 10, Stamp.AddMinutes(30));
        var server = Manifest("report.txt", 10, Stamp.AddMinutes(45));
        var skew = new ClockSkew(TimeSpan.FromHours(1));

        var result = ConflictKeepBothExecutor.Expand(plan: new List<SyncPlanEntry>
            { new(SyncActionType.ConflictKeepBoth, "report.txt") },
            clientManifest: client, serverManifest: server, skew: skew,
            sessionStartUtc: Stamp, clientFolder: _sync);

        Assert.Equal("report.conflict-20260720-143052-server.txt", result.Entries[0].RelativePath);
    }

    [Fact]
    public void Expand_EveryConflictMovesExactlyOneFileEachDirection()
    {
        // The desync invariant, asserted directly: both peers derive their transfer sets from
        // this one list, so the counts must balance whichever side loses.
        var client = new FileManifest();
        client.Add(new FileEntry("a.txt", 10, Stamp));
        client.Add(new FileEntry("b.txt", 10, Stamp.AddHours(5)));
        var server = new FileManifest();
        server.Add(new FileEntry("a.txt", 10, Stamp.AddHours(5)));
        server.Add(new FileEntry("b.txt", 10, Stamp));
        var plan = new List<SyncPlanEntry>
        {
            new(SyncActionType.ConflictKeepBoth, "a.txt"),
            new(SyncActionType.ConflictKeepBoth, "b.txt"),
        };

        var result = ConflictKeepBothExecutor.Expand(plan, client, server, ClockSkew.None, Stamp, _sync);

        Assert.Equal(2, result.Entries.Count(e => e.Action == SyncActionType.SendToServer));
        Assert.Equal(2, result.Entries.Count(e => e.Action == SyncActionType.SendToClient));
        Assert.Equal(2, result.Entries.Count(e => e.Action == SyncActionType.ConflictKeepBoth));
    }

    [Fact]
    public void Expand_LeavesNonConflictEntriesUntouched()
    {
        var plan = new List<SyncPlanEntry>
        {
            new(SyncActionType.SendToServer, "x.txt"),
            new(SyncActionType.Skip, "y.txt"),
            new(SyncActionType.DeleteOnClient, "z.txt"),
        };

        var result = ConflictKeepBothExecutor.Expand(
            plan, new FileManifest(), new FileManifest(), ClockSkew.None, Stamp, _sync);

        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[0].Action);
        Assert.Equal(SyncActionType.Skip, result.Entries[1].Action);
        Assert.Equal(SyncActionType.DeleteOnClient, result.Entries[2].Action);
        Assert.Empty(result.RenamedTo);
    }

    [Fact]
    public void ApplyLocalRenames_LosingSideRenamesArchivesAndPreservesMtime()
    {
        var mtime = Stamp.AddHours(-3);
        Write("report.txt", "client edit", mtime);
        var name = "report.conflict-20260720-143052-client.txt";
        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, name) };
        var archive = WorkingArchive();

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ClientSide, _sync, archive);

        Assert.Equal(1, outcome.Renamed);
        Assert.Empty(outcome.Failures);
        Assert.Empty(outcome.NotArchived);
        Assert.False(File.Exists(Path.Combine(_sync, "report.txt")));
        var renamed = Path.Combine(_sync, name);
        Assert.Equal("client edit", File.ReadAllText(renamed));
        // Mtime must survive: the peer receives this file and must see it unchanged next scan.
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(renamed));
        // Archived under the conflict reason, per CONTRACT.md's archive layout.
        Assert.True(File.Exists(Path.Combine(archive.SessionRoot, "conflict", "report.txt")));
    }

    [Fact]
    public void ApplyLocalRenames_WinningSideTouchesNothing()
    {
        Write("report.txt", "server edit", Stamp);
        var plan = new List<SyncPlanEntry>
        {
            new(SyncActionType.ConflictKeepBoth, "report.conflict-20260720-143052-client.txt"),
        };

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ServerSide, _sync, WorkingArchive());

        Assert.Equal(0, outcome.Renamed);
        Assert.Empty(outcome.Failures);
        Assert.Equal("server edit", File.ReadAllText(Path.Combine(_sync, "report.txt")));
    }

    [Fact]
    public void ApplyLocalRenames_MissingOriginalIsAFailureNotASilentSkip()
    {
        // The plan already promises the peer a transfer under this name; a sender that cannot
        // open its source throws before any frame is written and the peer blocks forever. Fail
        // loudly so the caller can abort before sending.
        var plan = new List<SyncPlanEntry>
        {
            new(SyncActionType.ConflictKeepBoth, "gone.conflict-20260720-143052-client.txt"),
        };

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ClientSide, _sync, WorkingArchive());

        Assert.Equal(0, outcome.Renamed);
        Assert.Single(outcome.Failures);
    }

    [Fact]
    public void ApplyLocalRenames_RejectsPathOutsideRoot()
    {
        var plan = new List<SyncPlanEntry>
        {
            new(SyncActionType.ConflictKeepBoth, "../evil.conflict-20260720-143052-client.txt"),
        };

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ClientSide, _sync, WorkingArchive());

        Assert.Equal(0, outcome.Renamed);
        Assert.Single(outcome.Failures);
    }

    [Fact]
    public void ApplyLocalRenames_MalformedEntryIsAFailure()
    {
        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, "not-a-conflict.txt") };

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ClientSide, _sync, WorkingArchive());

        Assert.Equal(0, outcome.Renamed);
        Assert.Single(outcome.Failures);
    }

    [Fact]
    public void ApplyLocalRenames_ArchivesALocalSquatterRatherThanDivergingFromThePlanName()
    {
        var name = "report.conflict-20260720-143052-client.txt";
        Write("report.txt", "loser", Stamp);
        Write(name, "unrelated squatter", Stamp);
        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, name) };
        var archive = WorkingArchive();

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ClientSide, _sync, archive);

        Assert.Equal(1, outcome.Renamed);
        Assert.Empty(outcome.Failures);
        Assert.Equal("loser", File.ReadAllText(Path.Combine(_sync, name)));
        Assert.True(File.Exists(Path.Combine(archive.SessionRoot, "conflict", name)));
    }

    [Fact]
    public void ApplyLocalRenames_SquatterSurvivesWhenTheArchiveTrulyFails()
    {
        // THE data-loss regression. TryArchive returns Failed WITHOUT throwing when it finds the
        // file but cannot preserve it. A delete that is not gated on that outcome destroys the
        // user's file in exactly the case where no archived copy exists.
        var name = "report.conflict-20260720-143052-client.txt";
        Write("report.txt", "loser", Stamp);
        Write(name, "irreplaceable squatter", Stamp);
        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, name) };

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ClientSide, _sync, BlockedArchive());

        Assert.Equal(0, outcome.Renamed);
        Assert.Single(outcome.Failures);
        Assert.Equal("irreplaceable squatter", File.ReadAllText(Path.Combine(_sync, name)));
        Assert.Equal("loser", File.ReadAllText(Path.Combine(_sync, "report.txt")));
    }

    [Fact]
    public void ApplyLocalRenames_SquatterSurvivesWhenTheArchiveDidNotPreserveIt()
    {
        // An archive that reports NothingToArchive has preserved nothing either. The survivor
        // re-check must catch that too, or an archive pointed at the wrong root would silently
        // license the overwrite it was supposed to protect against.
        var name = "report.conflict-20260720-143052-client.txt";
        Write("report.txt", "loser", Stamp);
        Write(name, "irreplaceable squatter", Stamp);
        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, name) };

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ClientSide, _sync, MisrootedArchive());

        Assert.Equal(0, outcome.Renamed);
        Assert.Single(outcome.Failures);
        Assert.Equal("irreplaceable squatter", File.ReadAllText(Path.Combine(_sync, name)));
        Assert.Equal("loser", File.ReadAllText(Path.Combine(_sync, "report.txt")));
    }

    [Fact]
    public void ApplyLocalRenames_RenameStillHappensWhenOnlyThePrecautionaryCopyFails()
    {
        // The removeOriginal:false archive is a belt-and-braces snapshot; File.Move preserves
        // the bytes regardless. Aborting the whole session over a redundant copy would be a
        // worse outcome than proceeding and reporting it.
        Write("report.txt", "loser", Stamp);
        var name = "report.conflict-20260720-143052-client.txt";
        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, name) };

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ClientSide, _sync, BlockedArchive());

        Assert.Equal(1, outcome.Renamed);
        Assert.Empty(outcome.Failures);
        Assert.Single(outcome.NotArchived);
        Assert.Equal("loser", File.ReadAllText(Path.Combine(_sync, name)));
    }

    [Fact]
    public void CountOccupiedTargets_CountsOnlyThisSidesOccupiedNames()
    {
        Write("a.conflict-20260720-143052-client.txt", "squatter", Stamp);
        var plan = new List<SyncPlanEntry>
        {
            new(SyncActionType.ConflictKeepBoth, "a.conflict-20260720-143052-client.txt"), // occupied, ours
            new(SyncActionType.ConflictKeepBoth, "b.conflict-20260720-143052-client.txt"), // free, ours
            new(SyncActionType.ConflictKeepBoth, "c.conflict-20260720-143052-server.txt"), // not ours
            new(SyncActionType.SendToServer, "d.txt"),
        };

        Assert.Equal(1, ConflictKeepBothExecutor.CountOccupiedTargets(
            plan, ConflictNamer.ClientSide, _sync));
    }
}
