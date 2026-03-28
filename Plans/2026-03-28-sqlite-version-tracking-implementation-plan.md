# SQLite File Version Tracking — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the binary `sync-state.bin` with a SQLite database (`sync.db`) that tracks per-file state, deletion records, and full sync history — fixing file resurrection (Bug A) and new-vs-deleted confusion (Bug B).

**Architecture:** `SyncDatabase` wraps all SQLite operations behind a clean API. The `SyncEngine` queries per-file state from the database instead of a flat manifest snapshot. Each file's state is updated individually during sync (not all-or-nothing), surviving partial syncs. The old binary state is auto-migrated on first run.

**Tech Stack:** .NET 10, C# 13, `Microsoft.Data.Sqlite`, xUnit.

**Design Spec:** `Plans/2026-03-28-sqlite-version-tracking-design.md`

---

## File Structure

```
src/RemoteFileSync/
├── RemoteFileSync.csproj              # MODIFY: add Microsoft.Data.Sqlite
├── State/
│   ├── SyncDatabase.cs                # CREATE: SQLite state backend
│   └── SyncStateManager.cs            # MODIFY: mark [Obsolete], keep for migration
├── Sync/
│   ├── SyncEngine.cs                  # MODIFY: new overload accepting SyncDatabase
│   └── ConflictResolver.cs            # (unchanged — already accepts per-file DateTime)
├── Network/
│   ├── SyncClient.cs                  # MODIFY: per-file state updates, session lifecycle
│   └── SyncServer.cs                  # MODIFY: per-file state updates
└── Program.cs                         # MODIFY: create SyncDatabase instead of SyncStateManager

tests/RemoteFileSync.Tests/
├── State/
│   └── SyncDatabaseTests.cs           # CREATE: unit tests for SyncDatabase
├── Sync/
│   └── SyncEngineTests.cs             # MODIFY: add database-backed tests
└── Integration/
    └── DeleteSyncTests.cs             # MODIFY: update for database-backed deletion
```

---

## Task 1: Add Microsoft.Data.Sqlite NuGet Reference

**Files:**
- Modify: `src/RemoteFileSync/RemoteFileSync.csproj`

- [ ] **Step 1: Add the NuGet package**

```bash
cd E:\RemoteFileSync
dotnet add src/RemoteFileSync/RemoteFileSync.csproj package Microsoft.Data.Sqlite
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/RemoteFileSync`
Expected: Build succeeded.

- [ ] **Step 3: Verify tests still pass**

Run: `dotnet test tests/RemoteFileSync.Tests`
Expected: All 133 tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/RemoteFileSync/RemoteFileSync.csproj
git commit -m "feat: add Microsoft.Data.Sqlite NuGet dependency"
```

---

## Task 2: SyncDatabase — Core Implementation

**Files:**
- Create: `src/RemoteFileSync/State/SyncDatabase.cs`
- Create: `tests/RemoteFileSync.Tests/State/SyncDatabaseTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/RemoteFileSync.Tests/State/SyncDatabaseTests.cs`:

```csharp
using RemoteFileSync.State;
using RemoteFileSync.Models;

namespace RemoteFileSync.Tests.State;

public class SyncDatabaseTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SyncDatabase _db;

    public SyncDatabaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rfs_db_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "sync.db");
        _db = new SyncDatabase(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void CreateDatabase_InitializesSchema()
    {
        // If constructor succeeds, schema was created
        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public void StartSession_ReturnsPositiveId()
    {
        var id = _db.StartSession("bidi+delete", @"C:\Sync", "192.168.1.100", 15782);
        Assert.True(id > 0);
    }

    [Fact]
    public void CompleteSession_SetsCompletedUtcAndStats()
    {
        var id = _db.StartSession("bidi", @"C:\Sync", "localhost", 15782);
        _db.CompleteSession(id, transferred: 10, deleted: 2, skipped: 50, exitCode: 0);
        var sessions = _db.GetRecentSessions(1);
        Assert.Single(sessions);
        Assert.NotNull(sessions[0].CompletedUtc);
        Assert.Equal(10, sessions[0].FilesTransferred);
        Assert.Equal(2, sessions[0].FilesDeleted);
        Assert.Equal(0, sessions[0].ExitCode);
    }

    [Fact]
    public void MarkSynced_CreatesFileAndVersion()
    {
        var sessionId = _db.StartSession("bidi", @"C:\Sync", "localhost", 15782);
        var now = DateTime.UtcNow;
        _db.MarkSynced("docs/report.docx", 1024, now, sessionId, "to_server");

        var state = _db.GetFileState("docs/report.docx");
        Assert.NotNull(state);
        Assert.Equal("exists", state.Status);
        Assert.Equal(1024, state.FileSize);
        Assert.Equal("both", state.Side);

        var history = _db.GetFileHistory("docs/report.docx");
        Assert.Single(history);
        Assert.Equal("synced", history[0].Action);
        Assert.Equal("to_server", history[0].Direction);
    }

    [Fact]
    public void MarkSynced_UpdatesExistingFile()
    {
        var s1 = _db.StartSession("bidi", @"C:\Sync", "localhost", 15782);
        var t1 = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        _db.MarkSynced("file.txt", 100, t1, s1, "to_server");

        var s2 = _db.StartSession("bidi", @"C:\Sync", "localhost", 15782);
        var t2 = new DateTime(2026, 3, 27, 10, 0, 0, DateTimeKind.Utc);
        _db.MarkSynced("file.txt", 200, t2, s2, "to_client");

        var state = _db.GetFileState("file.txt");
        Assert.Equal(200, state!.FileSize);
        Assert.Equal(t2, state.LastModified);

        var history = _db.GetFileHistory("file.txt");
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void MarkDeleted_SetsStatusDeleted()
    {
        var s1 = _db.StartSession("bidi", @"C:\Sync", "localhost", 15782);
        _db.MarkSynced("old.txt", 100, DateTime.UtcNow, s1, "to_server");

        var s2 = _db.StartSession("bidi", @"C:\Sync", "localhost", 15782);
        _db.MarkDeleted("old.txt", s2, "deleted on client, untouched on server");

        var state = _db.GetFileState("old.txt");
        Assert.NotNull(state);
        Assert.Equal("deleted", state.Status);

        var history = _db.GetFileHistory("old.txt");
        Assert.Equal(2, history.Count);
        Assert.Equal("deleted", history[1].Action);
    }

    [Fact]
    public void GetFileState_CaseInsensitive()
    {
        var s = _db.StartSession("bidi", @"C:\Sync", "localhost", 15782);
        _db.MarkSynced("Docs/Report.DOCX", 100, DateTime.UtcNow, s, "to_server");

        var state = _db.GetFileState("docs/report.docx");
        Assert.NotNull(state);
    }

    [Fact]
    public void GetFileState_NotFound_ReturnsNull()
    {
        Assert.Null(_db.GetFileState("nonexistent.txt"));
    }

    [Fact]
    public void GetDeletedFiles_ReturnsOnlyDeleted()
    {
        var s = _db.StartSession("bidi", @"C:\Sync", "localhost", 15782);
        _db.MarkSynced("exists.txt", 100, DateTime.UtcNow, s, "to_server");
        _db.MarkSynced("deleted.txt", 100, DateTime.UtcNow, s, "to_server");
        _db.MarkDeleted("deleted.txt", s, "test");

        var deleted = _db.GetDeletedFiles();
        Assert.Single(deleted);
        Assert.Equal("deleted.txt", deleted[0].Path);
    }

    [Fact]
    public void GetAllTrackedFiles_ReturnsAll()
    {
        var s = _db.StartSession("bidi", @"C:\Sync", "localhost", 15782);
        _db.MarkSynced("a.txt", 100, DateTime.UtcNow, s, "to_server");
        _db.MarkSynced("b.txt", 200, DateTime.UtcNow, s, "to_client");
        _db.MarkDeleted("b.txt", s, "test");

        var all = _db.GetAllTrackedFiles();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void MarkSkipped_CreatesVersionEntry()
    {
        var s = _db.StartSession("bidi", @"C:\Sync", "localhost", 15782);
        _db.MarkSynced("skip.txt", 100, DateTime.UtcNow, s, "to_server");
        _db.MarkSkipped("skip.txt", s);

        var history = _db.GetFileHistory("skip.txt");
        Assert.Equal(2, history.Count);
        Assert.Equal("skipped", history[1].Action);
    }

    [Fact]
    public void MarkNew_SetsStatusNew()
    {
        _db.MarkNew("brand-new.txt", 500, DateTime.UtcNow, "client");
        var state = _db.GetFileState("brand-new.txt");
        Assert.NotNull(state);
        Assert.Equal("new", state.Status);
        Assert.Equal("client", state.Side);
    }

    [Fact]
    public void PartialSync_PreservesPerFileState()
    {
        var s = _db.StartSession("bidi", @"C:\Sync", "localhost", 15782);
        _db.MarkSynced("ok1.txt", 100, DateTime.UtcNow, s, "to_server");
        _db.MarkSynced("ok2.txt", 200, DateTime.UtcNow, s, "to_server");
        // Simulate: ok3.txt fails, never marked
        _db.CompleteSession(s, transferred: 2, deleted: 0, skipped: 0, exitCode: 1);

        // ok1 and ok2 should have updated state
        Assert.NotNull(_db.GetFileState("ok1.txt"));
        Assert.NotNull(_db.GetFileState("ok2.txt"));
        // ok3 has no state (was never tracked)
        Assert.Null(_db.GetFileState("ok3.txt"));

        // Session recorded as incomplete (exit code 1)
        var sessions = _db.GetRecentSessions(1);
        Assert.Equal(1, sessions[0].ExitCode);
    }

    [Fact]
    public void GetDbPath_DeterministicAndCaseInsensitive()
    {
        var p1 = SyncDatabase.GetDbPath("/tmp", @"C:\SyncFolder", "192.168.1.100", 15782);
        var p2 = SyncDatabase.GetDbPath("/tmp", @"c:\syncfolder", "192.168.1.100", 15782);
        Assert.Equal(p1, p2);
        Assert.EndsWith("sync.db", p1);
    }

    [Fact]
    public void PreviouslyDeleted_Reappeared_CanBeMarkedExists()
    {
        var s1 = _db.StartSession("bidi", @"C:\Sync", "localhost", 15782);
        _db.MarkSynced("revived.txt", 100, DateTime.UtcNow, s1, "to_server");
        _db.MarkDeleted("revived.txt", s1, "intentional delete");

        Assert.Equal("deleted", _db.GetFileState("revived.txt")!.Status);

        // File re-appears — mark as synced again
        var s2 = _db.StartSession("bidi", @"C:\Sync", "localhost", 15782);
        _db.MarkSynced("revived.txt", 150, DateTime.UtcNow, s2, "to_client");

        Assert.Equal("exists", _db.GetFileState("revived.txt")!.Status);
        Assert.Equal(150, _db.GetFileState("revived.txt")!.FileSize);

        var history = _db.GetFileHistory("revived.txt");
        Assert.Equal(3, history.Count); // synced, deleted, synced
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "SyncDatabaseTests"`
Expected: FAIL — `SyncDatabase` type does not exist.

- [ ] **Step 3: Implement SyncDatabase**

Create `src/RemoteFileSync/State/SyncDatabase.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RemoteFileSync.Models;

namespace RemoteFileSync.State;

public record FileState(string Path, long FileSize, DateTime LastModified,
                        string Status, DateTime LastSynced, string Side);

public record FileVersionEntry(string Path, string Action, long? FileSize,
                               DateTime? LastModified, string? Direction,
                               string? Detail, DateTime Timestamp);

public record SyncSessionEntry(long Id, DateTime StartedUtc, DateTime? CompletedUtc,
                               string Mode, int FilesTransferred, int FilesDeleted,
                               int FilesSkipped, int? ExitCode);

public sealed class SyncDatabase : IDisposable
{
    private readonly SqliteConnection _conn;

    public SyncDatabase(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        InitSchema();
    }

    public static string DefaultBaseDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteFileSync");

    public static string GetDbPath(string baseDir, string localFolder, string remoteHost, int port)
    {
        var input = $"{localFolder.TrimEnd('\\', '/')}|{remoteHost}:{port}".ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var pairId = Convert.ToHexString(hash)[..16].ToLowerInvariant();
        return Path.Combine(baseDir, pairId, "sync.db");
    }

    private void InitSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = OFF;
            PRAGMA cache_size = -2000;

            CREATE TABLE IF NOT EXISTS files (
                path            TEXT PRIMARY KEY COLLATE NOCASE,
                file_size       INTEGER NOT NULL,
                last_modified   INTEGER NOT NULL,
                status          TEXT NOT NULL,
                last_synced     INTEGER NOT NULL,
                side            TEXT NOT NULL
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS file_versions (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                path            TEXT NOT NULL COLLATE NOCASE,
                action          TEXT NOT NULL,
                file_size       INTEGER,
                last_modified   INTEGER,
                sync_session_id INTEGER NOT NULL,
                direction       TEXT,
                detail          TEXT,
                timestamp       INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_versions_path ON file_versions(path);
            CREATE INDEX IF NOT EXISTS idx_versions_session ON file_versions(sync_session_id);

            CREATE TABLE IF NOT EXISTS sync_sessions (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                started_utc     INTEGER NOT NULL,
                completed_utc   INTEGER,
                mode            TEXT NOT NULL,
                files_transferred INTEGER DEFAULT 0,
                files_deleted   INTEGER DEFAULT 0,
                files_skipped   INTEGER DEFAULT 0,
                exit_code       INTEGER,
                client_folder   TEXT,
                server_host     TEXT,
                server_port     INTEGER
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // --- Session management ---

    public long StartSession(string mode, string clientFolder, string serverHost, int serverPort)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_sessions (started_utc, mode, client_folder, server_host, server_port)
            VALUES ($started, $mode, $folder, $host, $port);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$started", DateTime.UtcNow.Ticks);
        cmd.Parameters.AddWithValue("$mode", mode);
        cmd.Parameters.AddWithValue("$folder", clientFolder);
        cmd.Parameters.AddWithValue("$host", serverHost);
        cmd.Parameters.AddWithValue("$port", serverPort);
        return (long)cmd.ExecuteScalar()!;
    }

    public void CompleteSession(long sessionId, int transferred, int deleted, int skipped, int exitCode)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sync_sessions
            SET completed_utc = $completed, files_transferred = $transferred,
                files_deleted = $deleted, files_skipped = $skipped, exit_code = $exitCode
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$completed", DateTime.UtcNow.Ticks);
        cmd.Parameters.AddWithValue("$transferred", transferred);
        cmd.Parameters.AddWithValue("$deleted", deleted);
        cmd.Parameters.AddWithValue("$skipped", skipped);
        cmd.Parameters.AddWithValue("$exitCode", exitCode);
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.ExecuteNonQuery();
    }

    // --- File state queries ---

    public FileState? GetFileState(string relativePath)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT path, file_size, last_modified, status, last_synced, side FROM files WHERE path = $path;";
        cmd.Parameters.AddWithValue("$path", relativePath.Replace('\\', '/'));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return ReadFileState(reader);
    }

    public List<FileState> GetAllTrackedFiles()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT path, file_size, last_modified, status, last_synced, side FROM files ORDER BY path;";
        return ReadFileStates(cmd);
    }

    public List<FileState> GetDeletedFiles()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT path, file_size, last_modified, status, last_synced, side FROM files WHERE status = 'deleted' ORDER BY path;";
        return ReadFileStates(cmd);
    }

    // --- File state updates ---

    public void MarkSynced(string path, long fileSize, DateTime lastModified, long sessionId, string direction)
    {
        var normalizedPath = path.Replace('\\', '/');
        var now = DateTime.UtcNow.Ticks;
        using var tx = _conn.BeginTransaction();
        try
        {
            using var upsert = _conn.CreateCommand();
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO files (path, file_size, last_modified, status, last_synced, side)
                VALUES ($path, $size, $modified, 'exists', $synced, 'both')
                ON CONFLICT(path) DO UPDATE SET
                    file_size = $size, last_modified = $modified,
                    status = 'exists', last_synced = $synced, side = 'both';
                """;
            upsert.Parameters.AddWithValue("$path", normalizedPath);
            upsert.Parameters.AddWithValue("$size", fileSize);
            upsert.Parameters.AddWithValue("$modified", lastModified.Ticks);
            upsert.Parameters.AddWithValue("$synced", now);
            upsert.ExecuteNonQuery();

            InsertVersion(tx, normalizedPath, "synced", fileSize, lastModified.Ticks, sessionId, direction, null, now);
            tx.Commit();
        }
        catch { tx.Rollback(); throw; }
    }

    public void MarkDeleted(string path, long sessionId, string detail)
    {
        var normalizedPath = path.Replace('\\', '/');
        var now = DateTime.UtcNow.Ticks;
        using var tx = _conn.BeginTransaction();
        try
        {
            using var update = _conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE files SET status = 'deleted' WHERE path = $path;";
            update.Parameters.AddWithValue("$path", normalizedPath);
            update.ExecuteNonQuery();

            InsertVersion(tx, normalizedPath, "deleted", null, null, sessionId, null, detail, now);
            tx.Commit();
        }
        catch { tx.Rollback(); throw; }
    }

    public void MarkSkipped(string path, long sessionId)
    {
        var normalizedPath = path.Replace('\\', '/');
        InsertVersion(null, normalizedPath, "skipped", null, null, sessionId, null, null, DateTime.UtcNow.Ticks);
    }

    public void MarkNew(string path, long fileSize, DateTime lastModified, string side)
    {
        var normalizedPath = path.Replace('\\', '/');
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO files (path, file_size, last_modified, status, last_synced, side)
            VALUES ($path, $size, $modified, 'new', $synced, $side)
            ON CONFLICT(path) DO UPDATE SET
                file_size = $size, last_modified = $modified, status = 'new', side = $side;
            """;
        cmd.Parameters.AddWithValue("$path", normalizedPath);
        cmd.Parameters.AddWithValue("$size", fileSize);
        cmd.Parameters.AddWithValue("$modified", lastModified.Ticks);
        cmd.Parameters.AddWithValue("$synced", DateTime.UtcNow.Ticks);
        cmd.Parameters.AddWithValue("$side", side);
        cmd.ExecuteNonQuery();
    }

    // --- Audit queries ---

    public List<FileVersionEntry> GetFileHistory(string path, int limit = 50)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT path, action, file_size, last_modified, direction, detail, timestamp
            FROM file_versions WHERE path = $path ORDER BY timestamp ASC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$path", path.Replace('\\', '/'));
        cmd.Parameters.AddWithValue("$limit", limit);
        var result = new List<FileVersionEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new FileVersionEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : new DateTime(reader.GetInt64(3), DateTimeKind.Utc),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                new DateTime(reader.GetInt64(6), DateTimeKind.Utc)));
        }
        return result;
    }

    public List<SyncSessionEntry> GetRecentSessions(int limit = 20)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, started_utc, completed_utc, mode, files_transferred, files_deleted, files_skipped, exit_code
            FROM sync_sessions ORDER BY id DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        var result = new List<SyncSessionEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new SyncSessionEntry(
                reader.GetInt64(0),
                new DateTime(reader.GetInt64(1), DateTimeKind.Utc),
                reader.IsDBNull(2) ? null : new DateTime(reader.GetInt64(2), DateTimeKind.Utc),
                reader.GetString(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7)));
        }
        return result;
    }

    // --- Helpers ---

    private void InsertVersion(SqliteTransaction? tx, string path, string action,
                               long? fileSize, long? lastModified, long sessionId,
                               string? direction, string? detail, long timestamp)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO file_versions (path, action, file_size, last_modified, sync_session_id, direction, detail, timestamp)
            VALUES ($path, $action, $size, $modified, $session, $direction, $detail, $timestamp);
            """;
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$action", action);
        cmd.Parameters.AddWithValue("$size", (object?)fileSize ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$modified", (object?)lastModified ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$session", sessionId);
        cmd.Parameters.AddWithValue("$direction", (object?)direction ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$timestamp", timestamp);
        cmd.ExecuteNonQuery();
    }

    private static FileState ReadFileState(SqliteDataReader reader)
    {
        return new FileState(
            reader.GetString(0),
            reader.GetInt64(1),
            new DateTime(reader.GetInt64(2), DateTimeKind.Utc),
            reader.GetString(3),
            new DateTime(reader.GetInt64(4), DateTimeKind.Utc),
            reader.GetString(5));
    }

    private List<FileState> ReadFileStates(SqliteCommand cmd)
    {
        var result = new List<FileState>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(ReadFileState(reader));
        return result;
    }

    public void Dispose()
    {
        _conn.Close();
        _conn.Dispose();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "SyncDatabaseTests"`
Expected: All 15 tests pass.

- [ ] **Step 5: Run full test suite**

Run: `dotnet test`
Expected: All tests pass (existing + new).

- [ ] **Step 6: Commit**

```bash
git add src/RemoteFileSync/State/SyncDatabase.cs tests/RemoteFileSync.Tests/State/SyncDatabaseTests.cs
git commit -m "feat: add SyncDatabase with SQLite-backed per-file state tracking"
```

---

## Task 3: SyncEngine — Database-Backed Deletion Detection

**Files:**
- Modify: `src/RemoteFileSync/Sync/SyncEngine.cs`
- Modify: `tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs`:

```csharp
// === Database-backed deletion tests ===

private SyncDatabase CreateTestDb()
{
    var dir = Path.Combine(Path.GetTempPath(), $"rfs_engine_db_{Guid.NewGuid()}");
    Directory.CreateDirectory(dir);
    return new SyncDatabase(Path.Combine(dir, "sync.db"));
}

[Fact]
public void Db_DeletedFile_InDb_ProducesDeleteAction()
{
    using var db = CreateTestDb();
    var s = db.StartSession("bidi+delete", @"C:\Sync", "localhost", 15782);
    db.MarkSynced("file.txt", 100, BeforeSync, s, "to_server");
    db.CompleteSession(s, 1, 0, 0, 0);

    // file.txt missing from client (deleted), still on server (untouched)
    var client = new FileManifest();
    var server = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));

    var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, db: db, deleteEnabled: true);
    Assert.Single(plan);
    Assert.Equal(SyncActionType.DeleteOnServer, plan[0].Action);
}

[Fact]
public void Db_NewFile_NotInDb_ProducesCopyAction()
{
    using var db = CreateTestDb();
    // No files in db — brand new file
    var client = MakeManifest(new FileEntry("brand-new.txt", 50, AfterSync));
    var server = new FileManifest();

    var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, db: db, deleteEnabled: true);
    Assert.Single(plan);
    Assert.Equal(SyncActionType.ClientOnly, plan[0].Action);
}

[Fact]
public void Db_PreviouslyDeleted_Reappeared_CopiesAgain()
{
    using var db = CreateTestDb();
    var s = db.StartSession("bidi+delete", @"C:\Sync", "localhost", 15782);
    db.MarkSynced("revived.txt", 100, BeforeSync, s, "to_server");
    db.MarkDeleted("revived.txt", s, "intentional");
    db.CompleteSession(s, 1, 1, 0, 0);

    // File re-appears on client
    var client = MakeManifest(new FileEntry("revived.txt", 150, AfterSync));
    var server = new FileManifest();

    var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, db: db, deleteEnabled: true);
    Assert.Single(plan);
    Assert.Equal(SyncActionType.ClientOnly, plan[0].Action);
}

[Fact]
public void Db_UniDirectional_ServerLostFile_RePushed()
{
    using var db = CreateTestDb();
    var s = db.StartSession("uni+delete", @"C:\Sync", "localhost", 15782);
    db.MarkSynced("file.txt", 100, BeforeSync, s, "to_server");
    db.CompleteSession(s, 1, 0, 0, 0);

    // Server lost the file, client still has it
    var client = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));
    var server = new FileManifest();

    // Uni-directional: client is authoritative → re-push, don't silently drop
    var plan = SyncEngine.ComputePlan(client, server, bidirectional: false, db: db, deleteEnabled: true);
    Assert.Single(plan);
    Assert.Equal(SyncActionType.ClientOnly, plan[0].Action);
}

[Fact]
public void Db_PerFileTimestamp_UsedForDeletion()
{
    using var db = CreateTestDb();
    // Sync file.txt at BeforeSync time
    var s1 = db.StartSession("bidi+delete", @"C:\Sync", "localhost", 15782);
    db.MarkSynced("file.txt", 100, BeforeSync, s1, "to_server");
    db.CompleteSession(s1, 1, 0, 0, 0);

    // Later: file deleted on client, server has MODIFIED version (after the per-file last_synced)
    var client = new FileManifest();
    var server = MakeManifest(new FileEntry("file.txt", 200, AfterSync));

    var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, db: db, deleteEnabled: true);
    Assert.Single(plan);
    Assert.Equal(SyncActionType.SendToClient, plan[0].Action); // Restore — server modified after last sync
}

[Fact]
public void Db_DeleteEnabled_False_NormalBehavior()
{
    using var db = CreateTestDb();
    var s = db.StartSession("bidi", @"C:\Sync", "localhost", 15782);
    db.MarkSynced("file.txt", 100, BeforeSync, s, "to_server");
    db.CompleteSession(s, 1, 0, 0, 0);

    var client = new FileManifest();
    var server = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));

    // deleteEnabled=false: treat as ServerOnly (bidi) — no deletion logic
    var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, db: db, deleteEnabled: false);
    Assert.Single(plan);
    Assert.Equal(SyncActionType.ServerOnly, plan[0].Action);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "Db_"`
Expected: FAIL — overload does not exist.

- [ ] **Step 3: Add the database-backed ComputePlan overload**

Add to `src/RemoteFileSync/Sync/SyncEngine.cs`:

```csharp
public static List<SyncPlanEntry> ComputePlan(
    FileManifest clientManifest,
    FileManifest serverManifest,
    bool bidirectional,
    SyncDatabase? db,
    bool deleteEnabled)
{
    var plan = new List<SyncPlanEntry>();
    var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Collect all paths from both manifests
    var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var path in clientManifest.AllPaths) allPaths.Add(path);
    foreach (var path in serverManifest.AllPaths) allPaths.Add(path);

    // Also include tracked files from DB that might have been deleted from both sides
    if (deleteEnabled && db != null)
    {
        foreach (var tracked in db.GetAllTrackedFiles())
        {
            if (tracked.Status == "exists") allPaths.Add(tracked.Path);
        }
    }

    foreach (var path in allPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
    {
        var clientEntry = clientManifest.Get(path);
        var serverEntry = serverManifest.Get(path);

        if (clientEntry != null && serverEntry != null)
        {
            // Both sides have the file — normal conflict resolution
            var action = ConflictResolver.Resolve(clientEntry, serverEntry);
            plan.Add(new SyncPlanEntry(action, path));
        }
        else if (clientEntry != null && serverEntry == null)
        {
            // Client has it, server doesn't
            if (deleteEnabled && db != null)
            {
                var dbState = db.GetFileState(path);
                if (dbState != null && dbState.Status == "exists")
                {
                    // Was synced before, now missing from server
                    if (bidirectional)
                    {
                        // Bidi: server deleted it — check if client modified since last sync
                        var action = ConflictResolver.ResolveDeleteConflict(
                            deletedOnClient: false, survivingEntry: clientEntry, lastSyncUtc: dbState.LastSynced);
                        plan.Add(new SyncPlanEntry(action, path));
                    }
                    else
                    {
                        // Uni: client is authoritative — re-push to server
                        plan.Add(new SyncPlanEntry(SyncActionType.ClientOnly, path));
                    }
                }
                else
                {
                    // Not in DB, or status='deleted'/'new' — genuinely new or re-appeared
                    plan.Add(new SyncPlanEntry(SyncActionType.ClientOnly, path));
                }
            }
            else
            {
                plan.Add(new SyncPlanEntry(SyncActionType.ClientOnly, path));
            }
        }
        else if (clientEntry == null && serverEntry != null)
        {
            // Server has it, client doesn't
            if (deleteEnabled && db != null)
            {
                var dbState = db.GetFileState(path);
                if (dbState != null && dbState.Status == "exists")
                {
                    // Was synced before, now missing from client = client deleted it
                    var action = ConflictResolver.ResolveDeleteConflict(
                        deletedOnClient: true, survivingEntry: serverEntry, lastSyncUtc: dbState.LastSynced);
                    plan.Add(new SyncPlanEntry(action, path));
                }
                else
                {
                    // Not in DB or previously deleted/new — copy to client (if bidi)
                    if (bidirectional)
                        plan.Add(new SyncPlanEntry(SyncActionType.ServerOnly, path));
                }
            }
            else
            {
                if (bidirectional)
                    plan.Add(new SyncPlanEntry(SyncActionType.ServerOnly, path));
            }
        }
        else
        {
            // Neither side has it (from DB tracked files) — both deleted, no action
        }
    }

    return plan;
}
```

- [ ] **Step 4: Add the `using` import at the top of SyncEngine.cs**

Ensure this exists:
```csharp
using RemoteFileSync.State;
```

(It's already there for the old `SyncState` type.)

- [ ] **Step 5: Run all tests**

Run: `dotnet test`
Expected: All pass (old `SyncState?` overload still works, new `SyncDatabase?` overload passes new tests).

- [ ] **Step 6: Commit**

```bash
git add src/RemoteFileSync/Sync/SyncEngine.cs tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs
git commit -m "feat: add database-backed deletion detection to SyncEngine"
```

---

## Task 4: Migration from Binary State

**Files:**
- Modify: `src/RemoteFileSync/State/SyncDatabase.cs`
- Modify: `src/RemoteFileSync/State/SyncStateManager.cs`
- Create: `tests/RemoteFileSync.Tests/State/SyncDatabaseMigrationTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/RemoteFileSync.Tests/State/SyncDatabaseMigrationTests.cs`:

```csharp
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
        // Create a binary state file using the old SyncStateManager
        var pairDir = Path.Combine(_tempDir, "abc123");
        Directory.CreateDirectory(pairDir);
        var binPath = Path.Combine(pairDir, "sync-state.bin");
        var dbPath = Path.Combine(pairDir, "sync.db");

        var oldManager = new SyncStateManager(_tempDir);
        var manifest = new FileManifest();
        manifest.Add(new FileEntry("docs/report.docx", 1024, new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc)));
        manifest.Add(new FileEntry("data/export.csv", 2048, new DateTime(2026, 3, 27, 8, 0, 0, DateTimeKind.Utc)));

        // Write binary state manually to the known pairDir
        using (var fs = File.Create(binPath))
        using (var writer = new BinaryWriter(fs, System.Text.Encoding.UTF8))
        {
            writer.Write("RFS1"u8);
            writer.Write(new DateTime(2026, 3, 28, 10, 0, 0, DateTimeKind.Utc).Ticks);
            writer.Write(2); // count
            foreach (var entry in manifest.Entries)
            {
                var pathBytes = System.Text.Encoding.UTF8.GetBytes(entry.RelativePath);
                writer.Write((short)pathBytes.Length);
                writer.Write(pathBytes);
                writer.Write(entry.FileSize);
                writer.Write(entry.LastModifiedUtc.Ticks);
            }
        }

        // Migrate
        SyncDatabase.MigrateFromBinary(binPath, dbPath);

        // Verify
        using var db = new SyncDatabase(dbPath);
        var all = db.GetAllTrackedFiles();
        Assert.Equal(2, all.Count);

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

        // No database should be created by migration alone
        Assert.False(File.Exists(dbPath));
    }
}
```

- [ ] **Step 2: Implement MigrateFromBinary static method**

Add to `src/RemoteFileSync/State/SyncDatabase.cs`:

```csharp
public static void MigrateFromBinary(string binPath, string dbPath)
{
    if (!File.Exists(binPath)) return;
    if (File.Exists(dbPath)) return; // Already migrated

    try
    {
        // Read old binary state
        using var fs = File.OpenRead(binPath);
        using var reader = new BinaryReader(fs, Encoding.UTF8);

        var magic = reader.ReadBytes(4);
        if (!magic.AsSpan().SequenceEqual("RFS1"u8)) return;

        var lastSyncTicks = reader.ReadInt64();
        var lastSyncUtc = new DateTime(lastSyncTicks, DateTimeKind.Utc);

        int count = reader.ReadInt32();
        var entries = new List<(string path, long size, long modTicks)>();
        for (int i = 0; i < count; i++)
        {
            short pathLen = reader.ReadInt16();
            var path = Encoding.UTF8.GetString(reader.ReadBytes(pathLen));
            long size = reader.ReadInt64();
            long modTicks = reader.ReadInt64();
            entries.Add((path, size, modTicks));
        }

        // Create database and import
        using var db = new SyncDatabase(dbPath);
        var sessionId = db.StartSession("migration", "", "", 0);

        foreach (var (path, size, modTicks) in entries)
        {
            db.MarkSynced(path, size, new DateTime(modTicks, DateTimeKind.Utc), sessionId, "migration");
        }

        db.CompleteSession(sessionId, count, 0, 0, 0);

        // Rename old file
        File.Move(binPath, binPath + ".migrated");
    }
    catch
    {
        // Migration failed — start fresh
    }
}
```

- [ ] **Step 3: Mark SyncStateManager as Obsolete**

Add `[Obsolete]` attribute to `SyncStateManager` class in `src/RemoteFileSync/State/SyncStateManager.cs`:

```csharp
[Obsolete("Use SyncDatabase instead. Kept for migration from sync-state.bin.")]
public sealed class SyncStateManager
```

- [ ] **Step 4: Run tests**

Run: `dotnet test`
Expected: All pass. May have compiler warnings for `[Obsolete]` usage in existing tests — that's expected.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteFileSync/State/ tests/RemoteFileSync.Tests/State/SyncDatabaseMigrationTests.cs
git commit -m "feat: add binary state migration and mark SyncStateManager obsolete"
```

---

## Task 5: Wire SyncDatabase into SyncClient and Program.cs

**Files:**
- Modify: `src/RemoteFileSync/Network/SyncClient.cs`
- Modify: `src/RemoteFileSync/Program.cs`

- [ ] **Step 1: Update SyncClient constructor to accept SyncDatabase**

Add a `SyncDatabase?` parameter and field:

```csharp
private readonly SyncDatabase? _db;

public SyncClient(SyncOptions options, SyncLogger logger,
                  SyncStateManager? stateManager = null,
                  JsonProgressWriter? progressWriter = null,
                  StdinCommandReader? stdinReader = null,
                  SyncDatabase? db = null)
{
    _options = options;
    _logger = logger;
    _stateManager = stateManager;
    _progress = progressWriter ?? JsonProgressWriter.Null;
    _stdinReader = stdinReader ?? StdinCommandReader.Null;
    _db = db;
}
```

- [ ] **Step 2: Replace state loading and plan computation in HandleConnectionAsync**

Replace the old state loading block (lines 93-102) with:

```csharp
// 3. Start database session (if delete enabled with db)
long sessionId = 0;
if (_options.DeleteEnabled && _db != null)
{
    var mode = $"{(_options.Bidirectional ? "bidi" : "uni")}+delete";
    sessionId = _db.StartSession(mode, _options.Folder, _options.Host!, _options.Port);
    _logger.Info($"Sync session started (id={sessionId})");
}
```

Replace the ComputePlan call (line 119-121) with:

```csharp
var syncPlan = (_db != null)
    ? SyncEngine.ComputePlan(clientManifest, serverManifest, _options.Bidirectional, _db, _options.DeleteEnabled)
    : SyncEngine.ComputePlan(clientManifest, serverManifest, _options.Bidirectional);
```

- [ ] **Step 3: Add per-file state updates in each loop**

In the send loop, after `filesTransferred++` and `_progress.WriteFileEnd(...)`:
```csharp
_db?.MarkSynced(action.RelativePath, fi.Length, fi.LastWriteTimeUtc, sessionId, "to_server");
```

In the send loop catch block, after `skippedFiles++`:
```csharp
// Don't mark in db — file state unchanged on failure
```

In the server deletion loop, after `filesDeleted++`:
```csharp
_db?.MarkDeleted(del.RelativePath, sessionId, "deleted on client, propagated to server");
```

In the receive loop, after `filesTransferred++`:
```csharp
var rfi = new FileInfo(Path.Combine(_options.Folder, result.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
_db?.MarkSynced(result.RelativePath, rfi.Length, rfi.LastWriteTimeUtc, sessionId, "to_client");
```

In the client deletion loop, after `filesDeleted++`:
```csharp
_db?.MarkDeleted(path, sessionId, "deleted on server, propagated to client");
```

For skipped files (in the plan), after the sync plan is computed:
```csharp
if (_db != null)
{
    foreach (var skip in syncPlan.Where(p => p.Action == SyncActionType.Skip))
        _db.MarkSkipped(skip.RelativePath, sessionId);
}
```

- [ ] **Step 4: Replace the old state save block with session completion**

Replace the old state save block (the `BuildMergedManifest` + `SaveState` section) with:

```csharp
// Complete database session (always — regardless of exit code)
if (_db != null && sessionId > 0)
{
    _db.CompleteSession(sessionId, filesTransferred, filesDeleted,
        syncPlan.Count(p => p.Action == SyncActionType.Skip), exitCode);
    _logger.Debug($"Sync session {sessionId} completed (exit code {exitCode})");
}
```

- [ ] **Step 5: Update Program.cs to create SyncDatabase**

Replace the client creation block in `Main`:

```csharp
else
{
    SyncDatabase? db = null;
    if (options.DeleteEnabled)
    {
        var dbPath = SyncDatabase.GetDbPath(SyncDatabase.DefaultBaseDir, options.Folder, options.Host!, options.Port);

        // Auto-migrate from old binary state if needed
        var binPath = Path.ChangeExtension(dbPath, null);
        binPath = Path.Combine(Path.GetDirectoryName(dbPath)!, "sync-state.bin");
        SyncDatabase.MigrateFromBinary(binPath, dbPath);

        db = new SyncDatabase(dbPath);
    }

    try
    {
        var client = new Network.SyncClient(options, logger, db: db,
            progressWriter: progressWriter, stdinReader: stdinReader);
        return await client.RunAsync(cts.Token);
    }
    finally
    {
        db?.Dispose();
    }
}
```

- [ ] **Step 6: Verify build and tests**

Run: `dotnet build && dotnet test`
Expected: Build succeeds. All tests pass. Old `SyncStateManager`-based tests may show `[Obsolete]` warnings — that's expected.

- [ ] **Step 7: Commit**

```bash
git add src/RemoteFileSync/Network/SyncClient.cs src/RemoteFileSync/Program.cs
git commit -m "feat: wire SyncDatabase into SyncClient with per-file state updates"
```

---

## Task 6: Update SyncServer for Per-File State Updates

**Files:**
- Modify: `src/RemoteFileSync/Network/SyncServer.cs`

- [ ] **Step 1: Add SyncDatabase parameter to SyncServer constructor**

```csharp
private readonly SyncDatabase? _db;

public SyncServer(SyncOptions options, SyncLogger logger,
                  JsonProgressWriter? progressWriter = null,
                  StdinCommandReader? stdinReader = null,
                  SyncDatabase? db = null)
{
    _options = options;
    _logger = logger;
    _progress = progressWriter ?? JsonProgressWriter.Null;
    _stdinReader = stdinReader ?? StdinCommandReader.Null;
    _db = db;
}
```

- [ ] **Step 2: Add per-file state updates**

In the file receive loop, after successful receive:
```csharp
var rfi = new FileInfo(Path.Combine(_options.Folder, result.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
_db?.MarkSynced(result.RelativePath, rfi.Length, rfi.LastWriteTimeUtc, 0, "to_server");
```

In the server deletion section, after successful backup/delete:
```csharp
_db?.MarkDeleted(path, 0, "propagated from client");
```

Note: Server uses `sessionId=0` because session tracking is client-side only. The server's `_db` is optional and primarily for auditing if both sides maintain a database in the future.

- [ ] **Step 3: Update Program.cs server branch to pass db**

In `Program.cs`, update the server creation:

```csharp
if (options.IsServer)
{
    var server = new Network.SyncServer(options, logger, progressWriter, stdinReader);
    return await server.RunAsync(cts.Token);
}
```

(No database for server yet — server state tracking is a future enhancement. The constructor accepts `db: null` by default.)

- [ ] **Step 4: Verify build and tests**

Run: `dotnet build && dotnet test`
Expected: All pass.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteFileSync/Network/SyncServer.cs
git commit -m "feat: add SyncDatabase parameter to SyncServer for future per-file tracking"
```

---

## Task 7: Integration Tests — Database-Backed Deletion

**Files:**
- Create: `tests/RemoteFileSync.Tests/Integration/DatabaseDeleteSyncTests.cs`

- [ ] **Step 1: Create E2E tests**

Create `tests/RemoteFileSync.Tests/Integration/DatabaseDeleteSyncTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.Integration;

public class DatabaseDeleteSyncTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _serverDir;
    private readonly string _clientDir;
    private readonly string _dbDir;

    public DatabaseDeleteSyncTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"rfs_dbdel_e2e_{Guid.NewGuid()}");
        _serverDir = Path.Combine(_testRoot, "server");
        _clientDir = Path.Combine(_testRoot, "client");
        _dbDir = Path.Combine(_testRoot, "db");
        Directory.CreateDirectory(_serverDir);
        Directory.CreateDirectory(_clientDir);
        Directory.CreateDirectory(_dbDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }

    private void CreateFileWithTimestamp(string baseDir, string relativePath, string content, DateTime utcTimestamp)
    {
        var fullPath = Path.Combine(baseDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        File.SetLastWriteTimeUtc(fullPath, utcTimestamp);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task<(int clientResult, int serverResult)> RunSyncAsync(
        int port, bool bidirectional, bool deleteEnabled, SyncDatabase? db = null)
    {
        var serverOpts = new SyncOptions { IsServer = true, Port = port, Folder = _serverDir, DeleteEnabled = deleteEnabled };
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Bidirectional = bidirectional, DeleteEnabled = deleteEnabled };

        using var serverLogger = new SyncLogger(false, null);
        using var clientLogger = new SyncLogger(false, null);

        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger, db: db);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(500);
        var clientResult = await client.RunAsync(cts.Token);
        var serverResult = await serverTask;
        return (clientResult, serverResult);
    }

    [Fact]
    public async Task PartialSync_NoResurrection()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);

        // Run 1: sync both files
        CreateFileWithTimestamp(_clientDir, "keep.txt", "keep", ts);
        CreateFileWithTimestamp(_serverDir, "keep.txt", "keep", ts);
        CreateFileWithTimestamp(_clientDir, "to-delete.txt", "delete me", ts);
        CreateFileWithTimestamp(_serverDir, "to-delete.txt", "delete me", ts);

        var dbPath = Path.Combine(_dbDir, "sync.db");
        int port = GetFreePort();

        using (var db = new SyncDatabase(dbPath))
        {
            var (r1c, r1s) = await RunSyncAsync(port, bidirectional: true, deleteEnabled: true, db);
            Assert.Equal(0, r1c);
        }

        // Between syncs: client deletes a file
        File.Delete(Path.Combine(_clientDir, "to-delete.txt"));

        // Run 2: should detect deletion and propagate
        int port2 = GetFreePort();
        using (var db = new SyncDatabase(dbPath))
        {
            var (r2c, r2s) = await RunSyncAsync(port2, bidirectional: true, deleteEnabled: true, db);
        }

        // to-delete.txt should be gone from server (backed up)
        Assert.False(File.Exists(Path.Combine(_serverDir, "to-delete.txt")));
        // keep.txt should still exist
        Assert.True(File.Exists(Path.Combine(_clientDir, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "keep.txt")));
    }

    [Fact]
    public async Task NewVsDeleted_DistinguishesCorrectly()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var dbPath = Path.Combine(_dbDir, "sync2.db");

        // Run 1: sync a file, then mark it deleted in db
        CreateFileWithTimestamp(_clientDir, "was-synced.txt", "synced once", ts);
        CreateFileWithTimestamp(_serverDir, "was-synced.txt", "synced once", ts);

        int port = GetFreePort();
        using (var db = new SyncDatabase(dbPath))
        {
            await RunSyncAsync(port, bidirectional: true, deleteEnabled: true, db);
        }

        // Remove from client (intentional delete)
        File.Delete(Path.Combine(_clientDir, "was-synced.txt"));

        // Add a brand-new file on server (never synced before)
        CreateFileWithTimestamp(_serverDir, "brand-new.txt", "new file", ts);

        // Run 2:
        int port2 = GetFreePort();
        using (var db = new SyncDatabase(dbPath))
        {
            await RunSyncAsync(port2, bidirectional: true, deleteEnabled: true, db);
        }

        // was-synced.txt: was in db as 'exists', now deleted on client → should be deleted on server
        Assert.False(File.Exists(Path.Combine(_serverDir, "was-synced.txt")));

        // brand-new.txt: NOT in db → treated as genuinely new → copied to client
        Assert.True(File.Exists(Path.Combine(_clientDir, "brand-new.txt")));
    }
}
```

- [ ] **Step 2: Run integration tests**

Run: `dotnet test --filter "DatabaseDeleteSyncTests"`
Expected: All pass.

- [ ] **Step 3: Run full suite**

Run: `dotnet test`
Expected: All pass.

- [ ] **Step 4: Commit**

```bash
git add tests/RemoteFileSync.Tests/Integration/DatabaseDeleteSyncTests.cs
git commit -m "test: add E2E integration tests for database-backed deletion sync"
```

---

## Task 8: Final Verification and Push

- [ ] **Step 1: Full build and test**

```bash
cd E:\RemoteFileSync
dotnet build
dotnet test
```

Expected: Build succeeded, all tests pass.

- [ ] **Step 2: Push**

```bash
git push -u origin feature/sqlite-version-tracking
```

---

## Self-Review Checklist

| Check | Result |
|-------|--------|
| Spec coverage | Schema (T2), Algorithm (T3), Migration (T4), SyncClient (T5), SyncServer (T6), E2E (T7) |
| No placeholders | All code complete |
| Type consistency | `SyncDatabase`, `FileState`, `FileVersionEntry`, `SyncSessionEntry` — consistent |
| Bug A (resurrection) fix | Per-file `MarkSynced` called per transfer. Partial syncs preserve state. |
| Bug B (new vs deleted) fix | `GetFileState` returns `status='exists'/'deleted'/null` — three-way distinction |
| Uni-directional fix | `SyncEngine`: server lost file + uni mode → `ClientOnly` (re-push) |
| Backward compat | Old `SyncState?` overload preserved. Old tests still work. Migration auto-imports. |
