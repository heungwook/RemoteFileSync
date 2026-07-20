using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.State;

public sealed class SyncDatabaseSchemaMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    private static readonly DateTime Mtime     = new(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SyncedAt  = new(2026, 3, 28, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DeletedAt = new(2026, 4,  2,  8, 0, 0, DateTimeKind.Utc);

    public SyncDatabaseSchemaMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rfs_schema_migration_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "sync.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Builds a byte-accurate schema v1 database: no user_version, one size+mtime, a `side` column.</summary>
    private void CreateV1Database()
    {
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();

            using (var ddl = conn.CreateCommand())
            {
                ddl.CommandText = @"
CREATE TABLE files (
    path TEXT PRIMARY KEY COLLATE NOCASE,
    file_size INTEGER NOT NULL,
    last_modified INTEGER NOT NULL,
    status TEXT NOT NULL,
    last_synced INTEGER NOT NULL,
    side TEXT NOT NULL
) WITHOUT ROWID;

CREATE TABLE file_versions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    path TEXT NOT NULL COLLATE NOCASE,
    action TEXT NOT NULL,
    file_size INTEGER,
    last_modified INTEGER,
    sync_session_id INTEGER NOT NULL,
    direction TEXT,
    detail TEXT,
    timestamp INTEGER NOT NULL
);
CREATE INDEX idx_versions_path ON file_versions(path);
CREATE INDEX idx_versions_session ON file_versions(sync_session_id);

CREATE TABLE sync_sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    started_utc INTEGER NOT NULL,
    completed_utc INTEGER,
    mode TEXT NOT NULL,
    files_transferred INTEGER DEFAULT 0,
    files_deleted INTEGER DEFAULT 0,
    files_skipped INTEGER DEFAULT 0,
    exit_code INTEGER,
    client_folder TEXT,
    server_host TEXT,
    server_port INTEGER
);";
                ddl.ExecuteNonQuery();
            }

            using (var ins = conn.CreateCommand())
            {
                ins.CommandText = @"
INSERT INTO files (path, file_size, last_modified, status, last_synced, side) VALUES
    ('docs/report.docx', 1024, $mtime, 'exists',  $synced,  'both'),
    ('data/export.csv',  2048, $mtime, 'deleted', $deleted, 'both');";
                ins.Parameters.AddWithValue("$mtime", Mtime.Ticks);
                ins.Parameters.AddWithValue("$synced", SyncedAt.Ticks);
                ins.Parameters.AddWithValue("$deleted", DeletedAt.Ticks);
                ins.ExecuteNonQuery();
            }

            using (var sess = conn.CreateCommand())
            {
                sess.CommandText = @"
INSERT INTO sync_sessions (started_utc, completed_utc, mode, files_transferred,
                           files_deleted, files_skipped, exit_code)
VALUES ($started, $started, 'push', 2, 0, 0, 0);";
                sess.Parameters.AddWithValue("$started", SyncedAt.Ticks);
                sess.ExecuteNonQuery();
            }

            using (var ver = conn.CreateCommand())
            {
                ver.CommandText = @"
INSERT INTO file_versions (path, action, file_size, last_modified, sync_session_id, direction, detail, timestamp)
VALUES ('docs/report.docx', 'synced', 1024, $mtime, 1, 'to_server', NULL, $synced);";
                ver.Parameters.AddWithValue("$mtime", Mtime.Ticks);
                ver.Parameters.AddWithValue("$synced", SyncedAt.Ticks);
                ver.ExecuteNonQuery();
            }
        }

        SqliteConnection.ClearAllPools();
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

    private bool TableExists(string name)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private string FilesTableSql()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'files';";
        return (string)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void V1Database_HasNoUserVersionStamp()
    {
        CreateV1Database();
        Assert.Equal(0, UserVersion());
        Assert.Contains("side", ColumnsOf("files"));
    }

    [Fact]
    public void OpeningV1Database_RebuildsTableInV2Shape()
    {
        CreateV1Database();

        using (var db = new SyncDatabase(_dbPath))
        {
            // ColumnsOf proves names only. A rebuild that dropped COLLATE NOCASE would still
            // pass every column-name assertion here and only surface as a case-sensitive miss
            // on a MIGRATED database -- the case-insensitivity tests elsewhere all run against
            // a fresh v2 database, so this is the only place that gap would be caught.
            Assert.NotNull(db.GetRow("DOCS/REPORT.docx"));
        }
        SqliteConnection.ClearAllPools();

        Assert.Equal(2, UserVersion());
        Assert.Equal(
            new[] { "path", "client_size", "client_mtime", "server_size", "server_mtime",
                    "status", "last_synced", "deleted_utc" },
            ColumnsOf("files"));
        // The create/copy/drop/rename scratch table must not survive the transaction.
        Assert.False(TableExists("files_v2"));

        // Belt-and-braces alongside the case-insensitive lookup above: read the DDL back
        // directly so a rebuild that silently dropped either clause is caught even if some
        // future change to GetRow stopped exercising COLLATE NOCASE end-to-end.
        var sql = FilesTableSql();
        Assert.Contains("WITHOUT ROWID", sql);
        Assert.Contains("COLLATE NOCASE", sql);
    }

    [Fact]
    public void OpeningV1Database_CopiesSizeAndMtimeToBothSides()
    {
        CreateV1Database();

        using var db = new SyncDatabase(_dbPath);
        var row = db.GetRow("docs/report.docx");

        Assert.NotNull(row);
        Assert.Equal(1024, row!.ClientSize);
        Assert.Equal(1024, row.ServerSize);
        Assert.Equal(Mtime.Ticks, row.ClientMtimeTicks);
        Assert.Equal(Mtime.Ticks, row.ServerMtimeTicks);
        Assert.Equal("exists", row.Status);
        Assert.Equal(SyncedAt.Ticks, row.LastSyncedTicks);
        Assert.Null(row.DeletedUtcTicks);
    }

    [Fact]
    public void OpeningV1Database_SeedsDeletedUtcFromLastSyncedForTombstonesOnly()
    {
        CreateV1Database();

        using var db = new SyncDatabase(_dbPath);
        var row = db.GetRow("data/export.csv");

        Assert.NotNull(row);
        Assert.Equal("deleted", row!.Status);
        Assert.Equal(DeletedAt.Ticks, row.LastSyncedTicks);
        Assert.Equal(DeletedAt.Ticks, row.DeletedUtcTicks);
    }

    [Fact]
    public void OpeningV1Database_PreservesVersionHistoryAndSessions()
    {
        CreateV1Database();

        using var db = new SyncDatabase(_dbPath);

        var history = db.GetFileHistory("docs/report.docx").ToList();
        Assert.Single(history);
        Assert.Equal("synced", history[0].Action);
        Assert.Equal(SyncedAt, history[0].Timestamp);

        var sessions = db.GetRecentSessions().ToList();
        Assert.Single(sessions);
        Assert.Equal(2, sessions[0].FilesTransferred);
    }

    [Fact]
    public void OpeningMigratedDatabaseAgain_IsANoOp()
    {
        CreateV1Database();

        using (var db = new SyncDatabase(_dbPath)) { }
        SqliteConnection.ClearAllPools();

        using (var db = new SyncDatabase(_dbPath))
        {
            // A second open sees user_version=2 and must skip the rebuild; re-running the
            // rebuild against a v2 table would find no file_size column and throw.
            Assert.Equal(2, db.LoadAll().Count);
            Assert.NotNull(db.GetRow("docs/report.docx"));
            Assert.NotNull(db.GetRow("data/export.csv"));
        }
        SqliteConnection.ClearAllPools();

        Assert.Equal(2, UserVersion());
        Assert.False(TableExists("files_v2"));
    }

    [Fact]
    public void FailedMigration_LeavesTheV1DatabaseIntact()
    {
        CreateV1Database();

        // Poison the rebuild: a pre-existing files_v2 makes the scratch CREATE fail, which
        // must roll the whole upgrade back rather than leave a half-dropped files table.
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE files_v2 (bogus INTEGER);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        Assert.ThrowsAny<Exception>(() => { using var db = new SyncDatabase(_dbPath); });
        SqliteConnection.ClearAllPools();

        Assert.Equal(0, UserVersion());
        Assert.Contains("file_size", ColumnsOf("files"));
        Assert.Contains("side", ColumnsOf("files"));
    }

    [Fact]
    public void MigrationFailingAtItsLastStatement_StillRollsBackTheDropAndRename()
    {
        CreateV1Database();

        // The sibling test above poisons the FIRST statement, so it would pass even with no
        // transaction at all — there is nothing to undo yet. This one poisons the LAST
        // statement: a table squatting on the index name makes CREATE INDEX fail after
        // files_v2 was built, filled, and renamed over a dropped `files`. Only a transaction
        // spanning every statement can put v1 back. Without one the user loses the whole
        // ancestor table, and the next run sees an empty database — which under --mirror
        // deletes the peer's entire tree.
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE idx_files_status (bogus INTEGER);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var ex = Assert.ThrowsAny<Exception>(() => { using var db = new SyncDatabase(_dbPath); });
        // Pins the injection point: if this ever fails earlier the test silently degrades into
        // the weaker first-statement case and stops proving anything about DROP/RENAME.
        Assert.Contains("idx_files_status", ex.Message);
        SqliteConnection.ClearAllPools();

        Assert.Equal(0, UserVersion());
        Assert.False(TableExists("files_v2"));

        var cols = ColumnsOf("files");
        Assert.Contains("file_size", cols);
        Assert.Contains("side", cols);
        Assert.DoesNotContain("client_size", cols);

        // The rows must come back too: a rolled-back DROP that lost its data would leave a
        // structurally correct but empty v1 table, which is just as destructive.
        using var conn2 = new SqliteConnection($"Data Source={_dbPath}");
        conn2.Open();
        using var count = conn2.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM files;";
        Assert.Equal(2L, Convert.ToInt64(count.ExecuteScalar()));
    }

    [Fact]
    public void PragmaUserVersion_IsTransactional_RollsBackWithItsTransaction()
    {
        // InitSchema's whole crash-safety argument -- "the stamp commits with the table work
        // or not at all" -- rests on PRAGMA user_version being covered by the same transaction
        // as the surrounding DDL. It is, because user_version lives in the database header,
        // which SQLite journals like any other page, but nothing else in this suite pins that
        // SQLite behaviour directly rather than inferring it from InitSchema's own outcome.
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        using (var txn = conn.BeginTransaction())
        {
            using (var stamp = conn.CreateCommand())
            {
                stamp.Transaction = txn;
                stamp.CommandText = "PRAGMA user_version = 2;";
                stamp.ExecuteNonQuery();
            }

            using var readInTxn = conn.CreateCommand();
            readInTxn.Transaction = txn;
            readInTxn.CommandText = "PRAGMA user_version;";
            Assert.Equal(2, Convert.ToInt32(readInTxn.ExecuteScalar()));

            txn.Rollback();
        }

        using var readAfter = conn.CreateCommand();
        readAfter.CommandText = "PRAGMA user_version;";
        Assert.Equal(0, Convert.ToInt32(readAfter.ExecuteScalar()));
    }

    [Fact]
    public void MigratedDatabase_AcceptsPerSideUpdates()
    {
        CreateV1Database();

        using var db = new SyncDatabase(_dbPath);
        var session = db.StartSession("two-way", "/folder", "host", 8765);
        db.UpsertSynced("docs/report.docx", 1024, Mtime.Ticks, 4096, Mtime.AddHours(3).Ticks,
                        session, "to_client");

        var row = db.GetRow("docs/report.docx");
        Assert.NotNull(row);
        Assert.Equal(1024, row!.ClientSize);
        Assert.Equal(4096, row.ServerSize);
        Assert.Equal(Mtime.AddHours(3).Ticks, row.ServerMtimeTicks);
    }
}
