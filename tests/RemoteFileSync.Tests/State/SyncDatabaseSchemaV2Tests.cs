using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using RemoteFileSync.State;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.State;

public sealed class SyncDatabaseSchemaV2Tests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public SyncDatabaseSchemaV2Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rfs_schema_v2_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "sync.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private List<string> ColumnsOf(string table)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info($table);";
        cmd.Parameters.AddWithValue("$table", table);
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private int UserVersion()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public void NewDatabase_HasSchemaVersion2AndPerSideColumns()
    {
        using (var db = new SyncDatabase(_dbPath)) { }
        SqliteConnection.ClearAllPools();

        Assert.Equal(2, SyncDatabase.SchemaVersion);
        Assert.Equal(2, UserVersion());

        var cols = ColumnsOf("files");
        Assert.Equal(
            new[] { "path", "client_size", "client_mtime", "server_size", "server_mtime",
                    "status", "last_synced", "deleted_utc" },
            cols);
        Assert.DoesNotContain("side", cols);
        Assert.DoesNotContain("file_size", cols);
    }

    [Fact]
    public void UpsertSynced_RoundTripsDifferentClientAndServerMtimes()
    {
        var clientMtime = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc).Ticks;
        var serverMtime = new DateTime(2026, 7, 2, 17, 30, 0, DateTimeKind.Utc).Ticks;

        using var db = new SyncDatabase(_dbPath);
        var session = db.StartSession("two-way", "/folder", "host", 8765);
        db.UpsertSynced("docs/report.docx",
            clientSize: 1024, clientMtimeTicks: clientMtime,
            serverSize: 2048, serverMtimeTicks: serverMtime,
            sessionId: session, direction: "to_server");

        var row = db.GetRow("docs/report.docx");
        Assert.NotNull(row);
        Assert.Equal(1024, row!.ClientSize);
        Assert.Equal(clientMtime, row.ClientMtimeTicks);
        Assert.Equal(2048, row.ServerSize);
        Assert.Equal(serverMtime, row.ServerMtimeTicks);
        Assert.Equal("exists", row.Status);
        Assert.Null(row.DeletedUtcTicks);

        // The whole point of v2: the two sides must not be collapsed into one value.
        Assert.NotEqual(row.ClientMtimeTicks, row.ServerMtimeTicks);
        Assert.NotEqual(row.ClientSize, row.ServerSize);
    }

    [Fact]
    public void UpsertSynced_Twice_OverwritesBothSidesIndependently()
    {
        using var db = new SyncDatabase(_dbPath);
        var session = db.StartSession("two-way", "/folder", "host", 8765);
        db.UpsertSynced("a.txt", 1, 100, 2, 200, session, "to_server");
        db.UpsertSynced("a.txt", 3, 300, 4, 400, session, "to_client");

        var row = db.GetRow("a.txt");
        Assert.NotNull(row);
        Assert.Equal(3, row!.ClientSize);
        Assert.Equal(300, row.ClientMtimeTicks);
        Assert.Equal(4, row.ServerSize);
        Assert.Equal(400, row.ServerMtimeTicks);

        Assert.Equal(2, db.GetFileHistory("a.txt").Count());
    }

    [Fact]
    public void GetRow_IsCaseInsensitive_AndNullWhenAbsent()
    {
        using var db = new SyncDatabase(_dbPath);
        var session = db.StartSession("two-way", "/folder", "host", 8765);
        db.UpsertSynced("Docs/Report.DOCX", 10, 111, 20, 222, session, "to_server");

        Assert.NotNull(db.GetRow("docs/report.docx"));
        Assert.Null(db.GetRow("docs/missing.docx"));
    }

    [Fact]
    public void LoadAll_IsKeyedCaseInsensitively()
    {
        using var db = new SyncDatabase(_dbPath);
        var session = db.StartSession("two-way", "/folder", "host", 8765);
        db.UpsertSynced("A/one.txt", 1, 100, 2, 200, session, "to_server");
        db.UpsertSynced("B/two.txt", 3, 300, 4, 400, session, "to_client");

        var all = db.LoadAll();
        Assert.Equal(2, all.Count);
        Assert.True(all.ContainsKey("a/ONE.txt"));
        Assert.Equal(400, all["b/two.txt"].ServerMtimeTicks);
    }

    [Fact]
    public void UpsertSynced_AfterTombstone_ClearsDeletedUtc()
    {
        using var db = new SyncDatabase(_dbPath);
        var session = db.StartSession("two-way", "/folder", "host", 8765);
        db.UpsertSynced("revived.txt", 1, 100, 1, 100, session, "to_server");
        db.Tombstone("revived.txt", session, "gone on both sides");

        var dead = db.GetRow("revived.txt");
        Assert.NotNull(dead);
        Assert.Equal("deleted", dead!.Status);
        Assert.NotNull(dead.DeletedUtcTicks);

        db.UpsertSynced("revived.txt", 5, 500, 5, 500, session, "to_client");

        var alive = db.GetRow("revived.txt");
        Assert.NotNull(alive);
        Assert.Equal("exists", alive!.Status);
        Assert.Null(alive.DeletedUtcTicks);
    }

    [Fact]
    public void Tombstone_UntrackedPath_WritesNothing()
    {
        using var db = new SyncDatabase(_dbPath);
        var session = db.StartSession("two-way", "/folder", "host", 8765);
        db.Tombstone("never-seen.txt", session, "should not be recorded");

        Assert.Null(db.GetRow("never-seen.txt"));
        Assert.Empty(db.GetFileHistory("never-seen.txt"));
    }

    [Fact]
    public void MarkSynced_Shim_StampsOneSidesValuesOntoBothSides()
    {
        // Characterisation test, NOT an endorsement. The v1 shim has exactly one honest
        // caller (MigrateFromBinary, whose source genuinely records a single size+mtime).
        // SyncClient.cs:187-194 is the dishonest caller: it feeds one side's manifest entry
        // in for a Skip, fabricating a peer state that never existed, and the Push/Pull
        // tables then read that row as "the peer had it" and delete. Phase 6 owns
        // SyncClient.cs:185-206 and must replace that call with a both-sides-present
        // UpsertSynced / MarkSkipped split (CONTRACT.md correction 6). If this test ever
        // changes, that fix has landed or regressed — either way, look at SyncClient.
        using var db = new SyncDatabase(_dbPath);
        var session = db.StartSession("push", "/folder", "host", 8765);
        var mtime = new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc);

        db.MarkSynced("one-sided.txt", fileSize: 77, lastModified: mtime, sessionId: session, direction: "skipped");

        var row = db.GetRow("one-sided.txt");
        Assert.NotNull(row);
        Assert.Equal(77, row!.ClientSize);
        Assert.Equal(77, row.ServerSize);
        Assert.Equal(mtime.Ticks, row.ClientMtimeTicks);
        Assert.Equal(mtime.Ticks, row.ServerMtimeTicks);
        Assert.Equal("exists", row.Status);
    }

    /// <summary>Ages a tombstone behind the public API's back, which always stamps "now".</summary>
    private void SetDeletedUtc(string path, long? ticks)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE files SET deleted_utc = $ticks WHERE path = $path COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$ticks", ticks.HasValue ? ticks.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$path", path);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void PurgeTombstonesOlderThan_RemovesOldTombstoneKeepsRecentOne()
    {
        using var db = new SyncDatabase(_dbPath);
        var session = db.StartSession("two-way", "/folder", "host", 8765);

        db.UpsertSynced("alive.txt", 1, 100, 1, 100, session, "to_server");
        db.UpsertSynced("old-tombstone.txt", 2, 200, 2, 200, session, "to_server");
        db.UpsertSynced("fresh-tombstone.txt", 3, 300, 3, 300, session, "to_server");

        db.Tombstone("old-tombstone.txt", session, "deleted long ago");
        db.Tombstone("fresh-tombstone.txt", session, "deleted just now");
        SetDeletedUtc("old-tombstone.txt", DateTime.UtcNow.AddDays(-90).Ticks);

        Assert.Equal(1, db.PurgeTombstonesOlderThan(TimeSpan.FromDays(30)));
        Assert.Null(db.GetRow("old-tombstone.txt"));
        Assert.Equal("deleted", db.GetRow("fresh-tombstone.txt")!.Status);
        Assert.Equal("exists", db.GetRow("alive.txt")!.Status);
    }

    [Fact]
    public void PurgeTombstonesOlderThan_NeverTouchesExistingRows()
    {
        using var db = new SyncDatabase(_dbPath);
        var session = db.StartSession("two-way", "/folder", "host", 8765);
        db.UpsertSynced("alive.txt", 1, 100, 1, 100, session, "to_server");

        // A stale deleted_utc left on a live row must not make it purgeable: status is the
        // gate. Purging a live ancestor makes the next run see the file as brand new.
        SetDeletedUtc("alive.txt", DateTime.UtcNow.AddYears(-5).Ticks);

        Assert.Equal(0, db.PurgeTombstonesOlderThan(TimeSpan.FromDays(30)));
        Assert.NotNull(db.GetRow("alive.txt"));
    }

    [Fact]
    public void PurgeTombstonesOlderThan_KeepsTombstonesWithNullDeletedUtc()
    {
        using var db = new SyncDatabase(_dbPath);
        var session = db.StartSession("two-way", "/folder", "host", 8765);
        db.UpsertSynced("unknown-age.txt", 1, 100, 1, 100, session, "to_server");
        db.Tombstone("unknown-age.txt", session, "deleted");
        SetDeletedUtc("unknown-age.txt", null);

        Assert.Equal(0, db.PurgeTombstonesOlderThan(TimeSpan.FromDays(30)));
        Assert.Equal("deleted", db.GetRow("unknown-age.txt")!.Status);
    }

    [Fact]
    public void PurgeTombstonesOlderThan_ZeroAge_IsANoOp()
    {
        // Zero means "no age rule" everywhere else in this project (ArchiveKeepDays,
        // ArchiveManager.Prune per CONTRACT.md correction 3), never "cutoff is right now". A
        // literal cutoff of UtcNow.Ticks - 0 would delete every tombstone whose deleted_utc
        // isn't in the future -- i.e. all of them -- destroying the evidence that distinguishes
        // "re-appeared after deletion" from "never seen".
        using var db = new SyncDatabase(_dbPath);
        var session = db.StartSession("two-way", "/folder", "host", 8765);
        db.UpsertSynced("old-tombstone.txt", 1, 100, 1, 100, session, "to_server");
        db.Tombstone("old-tombstone.txt", session, "deleted long ago");
        SetDeletedUtc("old-tombstone.txt", DateTime.UtcNow.AddYears(-5).Ticks);

        Assert.Equal(0, db.PurgeTombstonesOlderThan(TimeSpan.Zero));
        Assert.Equal("deleted", db.GetRow("old-tombstone.txt")!.Status);
    }

    [Fact]
    public void PurgeTombstonesOlderThan_NegativeAge_IsANoOp()
    {
        // A negative age puts a naive cutoff even further in the future than zero does, so it
        // gets the same no-op rather than throwing -- there is no reading of "negative
        // retention" that should behave differently from "zero retention" here.
        using var db = new SyncDatabase(_dbPath);
        var session = db.StartSession("two-way", "/folder", "host", 8765);
        db.UpsertSynced("old-tombstone.txt", 1, 100, 1, 100, session, "to_server");
        db.Tombstone("old-tombstone.txt", session, "deleted long ago");
        SetDeletedUtc("old-tombstone.txt", DateTime.UtcNow.AddYears(-5).Ticks);

        Assert.Equal(0, db.PurgeTombstonesOlderThan(TimeSpan.FromDays(-1)));
        Assert.Equal("deleted", db.GetRow("old-tombstone.txt")!.Status);
    }

    /// <summary>Writes a raw value into a tick column, bypassing the API's own range checks.</summary>
    private void SetRawColumn(string path, string column, long ticks)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE files SET {column} = $ticks WHERE path = $path COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$ticks", ticks);
        cmd.Parameters.AddWithValue("$path", path);
        cmd.ExecuteNonQuery();
    }

    [Theory]
    [InlineData("client_mtime", long.MinValue)]
    [InlineData("server_mtime", long.MinValue)]
    [InlineData("client_mtime", -1L)]
    [InlineData("last_synced", long.MaxValue)]
    [InlineData("deleted_utc", long.MinValue)]
    public void GetRow_TreatsOutOfRangeTicksAsACacheMiss(string column, long ticks)
    {
        // A SQLite INTEGER column can hold any 64-bit value, and a truncated or corrupted
        // database is a first-class scenario here. long.MinValue is the sharp edge:
        // ChangeDetector.Unchanged calls Math.Abs on the difference, and Math.Abs(long.MinValue)
        // throws. Rejecting the row makes it read as "no ancestor", which the decision tables
        // route down the additive newest-wins path — never down a delete path. Clamping would
        // instead fabricate an ancestor that could read as "unchanged" and authorise a delete.
        using var db = new SyncDatabase(_dbPath);
        var session = db.StartSession("two-way", "/folder", "host", 8765);
        db.UpsertSynced("corrupt.txt", 1, 100, 1, 100, session, "to_server");
        SetRawColumn("corrupt.txt", column, ticks);

        Assert.Null(db.GetRow("corrupt.txt"));
    }

    [Fact]
    public void LoadAll_SkipsCorruptRowsAndKeepsTheRest()
    {
        using var db = new SyncDatabase(_dbPath);
        var session = db.StartSession("two-way", "/folder", "host", 8765);
        db.UpsertSynced("good.txt", 1, 100, 1, 100, session, "to_server");
        db.UpsertSynced("corrupt.txt", 2, 200, 2, 200, session, "to_server");
        SetRawColumn("corrupt.txt", "server_mtime", long.MinValue);

        var all = db.LoadAll();
        Assert.True(all.ContainsKey("good.txt"));
        Assert.False(all.ContainsKey("corrupt.txt"));
    }

    [Fact]
    public void LegacyReadShims_DoNotThrowOnACorruptRow()
    {
        // GetAllTrackedFiles projects ticks through `new DateTime(ticks, Utc)`, which throws
        // ArgumentOutOfRangeException on the same values. One corrupt row must not take down
        // an entire sync.
        using var db = new SyncDatabase(_dbPath);
        var session = db.StartSession("two-way", "/folder", "host", 8765);
        db.UpsertSynced("good.txt", 1, 100, 1, 100, session, "to_server");
        db.UpsertSynced("corrupt.txt", 2, 200, 2, 200, session, "to_server");
        SetRawColumn("corrupt.txt", "client_mtime", long.MinValue);

        var tracked = db.GetAllTrackedFiles().ToList();
        Assert.Equal("good.txt", Assert.Single(tracked).Path);
        Assert.Null(db.GetFileState("corrupt.txt"));
    }

    [Fact]
    public void GetRow_AcceptsTheExtremesOfTheValidTickRange()
    {
        // The guard must reject only genuinely unrepresentable values; clipping the legal
        // range would discard good ancestors and re-open the same delete loop.
        using var db = new SyncDatabase(_dbPath);
        var session = db.StartSession("two-way", "/folder", "host", 8765);
        db.UpsertSynced("edge.txt", 1, 0, 1, DateTime.MaxValue.Ticks, session, "to_server");

        var row = db.GetRow("edge.txt");
        Assert.NotNull(row);
        Assert.Equal(0, row!.ClientMtimeTicks);
        Assert.Equal(DateTime.MaxValue.Ticks, row.ServerMtimeTicks);
    }

    private static string Detail(long cSize, long sSize, string? renamedTo = null) =>
        new ConflictDetail(cSize, 1_000, sSize, 2_000, renamedTo).Encode();

    [Fact]
    public void LogConflictAndLogResurrection_AreSeparatedByActionNotByDetail()
    {
        using var db = new SyncDatabase(_dbPath);
        var s1 = db.StartSession("two-way", "/folder", "host", 8765);
        var s2 = db.StartSession("two-way", "/folder", "host", 8765);

        var conflictDetail    = Detail(10, 20, "report.conflict-20260720-143052-server.docx");
        var resurrectionDetail = Detail(30, 40);

        db.LogConflict("docs/report.docx", s1, conflictDetail);
        db.LogResurrection("docs/notes.txt", s1, resurrectionDetail);
        db.LogConflict("other/file.txt", s2, Detail(50, 60));

        var conflicts = db.GetSessionConflicts(s1);
        Assert.Equal("docs/report.docx", Assert.Single(conflicts).Path);
        Assert.Equal(conflictDetail, conflicts[0].Detail);
        Assert.Equal(DateTimeKind.Utc, conflicts[0].Timestamp.Kind);

        var resurrections = db.GetSessionResurrections(s1);
        Assert.Equal("docs/notes.txt", Assert.Single(resurrections).Path);
        Assert.Equal(resurrectionDetail, resurrections[0].Detail);

        // Neither kind may leak into the other's report, nor across session boundaries.
        Assert.Empty(db.GetSessionResurrections(s2));
        Assert.Single(db.GetSessionConflicts(s2));
    }

    [Fact]
    public void LogConflict_NeverRoutesOnTheDetailString()
    {
        // Guards against re-introducing prefix sniffing: the ONLY discriminator is which
        // method was called. A detail that reads like a resurrection must still be a conflict.
        using var db = new SyncDatabase(_dbPath);
        var s = db.StartSession("two-way", "/folder", "host", 8765);

        db.LogConflict("looks-like-a-resurrection.txt", s, "resurrected:\tv1\t1\t2\t3\t4\t-");

        Assert.Single(db.GetSessionConflicts(s));
        Assert.Empty(db.GetSessionResurrections(s));
    }

    [Fact]
    public void SessionEntryDetails_DecodeBackToConflictDetail()
    {
        using var db = new SyncDatabase(_dbPath);
        var s = db.StartSession("two-way", "/folder", "host", 8765);
        var original = new ConflictDetail(11, 22, 33, 44, "a.conflict-20260720-000000-client.txt");

        db.LogConflict("a.txt", s, original.Encode());

        var stored = Assert.Single(db.GetSessionConflicts(s));
        Assert.Equal(original, ConflictDetail.Decode(stored.Detail));
    }

    [Fact]
    public void GetSessionConflictsAndResurrections_NoneLogged_ReturnEmpty()
    {
        using var db = new SyncDatabase(_dbPath);
        var s = db.StartSession("push", "/folder", "host", 8765);
        Assert.Empty(db.GetSessionConflicts(s));
        Assert.Empty(db.GetSessionResurrections(s));
    }

    [Fact]
    public void LogConflict_DoesNotDisturbTheAncestorRow()
    {
        using var db = new SyncDatabase(_dbPath);
        var s = db.StartSession("two-way", "/folder", "host", 8765);
        db.UpsertSynced("docs/report.docx", 10, 1000, 20, 2000, s, "to_server");
        db.LogConflict("docs/report.docx", s, Detail(10, 20));

        var row = db.GetRow("docs/report.docx");
        Assert.NotNull(row);
        Assert.Equal("exists", row!.Status);
        Assert.Equal(10, row.ClientSize);
        Assert.Equal(20, row.ServerSize);

        var history = db.GetFileHistory("docs/report.docx").ToList();
        Assert.Equal(2, history.Count);
        Assert.Equal("conflict", history[1].Action);
    }

    [Fact]
    public void LogResurrection_UntrackedPath_IsStillRecorded()
    {
        // Unlike Tombstone, a resurrection is an observation about a live file and does not
        // require a pre-existing ancestor row — the row is written later, by the caller.
        using var db = new SyncDatabase(_dbPath);
        var s = db.StartSession("two-way", "/folder", "host", 8765);
        db.LogResurrection("never-synced.txt", s, Detail(1, 0));

        Assert.Single(db.GetSessionResurrections(s));
        Assert.Null(db.GetRow("never-synced.txt"));
    }
}
