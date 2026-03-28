using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace RemoteFileSync.State;

public record FileState(
    string Path,
    long FileSize,
    DateTime LastModified,
    string Status,
    DateTime LastSynced,
    string Side);

public record FileVersionEntry(
    string Path,
    string Action,
    long? FileSize,
    DateTime? LastModified,
    string? Direction,
    string? Detail,
    DateTime Timestamp);

public record SyncSessionEntry(
    long Id,
    DateTime StartedUtc,
    DateTime? CompletedUtc,
    string Mode,
    int FilesTransferred,
    int FilesDeleted,
    int FilesSkipped,
    int? ExitCode);

/// <summary>
/// SQLite-backed file state tracking. NOT thread-safe — use from a single thread only.
/// </summary>
public sealed class SyncDatabase : IDisposable
{
    private readonly SqliteConnection _conn;

    public SyncDatabase(string dbPath)
    {
        var dir = System.IO.Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            System.IO.Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        InitSchema();
    }

    public static string DefaultBaseDir =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteFileSync");

    public static string GetDbPath(string baseDir, string localFolder, string remoteHost, int port)
    {
        var input = $"{localFolder.TrimEnd('\\', '/')}|{remoteHost}:{port}".ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var pairId = Convert.ToHexString(hash)[..16].ToLowerInvariant();
        return System.IO.Path.Combine(baseDir, pairId, "sync.db");
    }

    private void InitSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = OFF;
PRAGMA cache_size = -2000;

CREATE TABLE IF NOT EXISTS files (
    path TEXT PRIMARY KEY COLLATE NOCASE,
    file_size INTEGER NOT NULL,
    last_modified INTEGER NOT NULL,
    status TEXT NOT NULL,
    last_synced INTEGER NOT NULL,
    side TEXT NOT NULL
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS file_versions (
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
CREATE INDEX IF NOT EXISTS idx_versions_path ON file_versions(path);
CREATE INDEX IF NOT EXISTS idx_versions_session ON file_versions(sync_session_id);

CREATE TABLE IF NOT EXISTS sync_sessions (
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
);
";
        cmd.ExecuteNonQuery();
    }

    // ── Sessions ──────────────────────────────────────────────────────────────

    public long StartSession(string mode, string clientFolder, string serverHost, int serverPort)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO sync_sessions (started_utc, mode, client_folder, server_host, server_port)
VALUES ($started, $mode, $folder, $host, $port);
SELECT last_insert_rowid();";
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
        cmd.CommandText = @"
UPDATE sync_sessions
SET completed_utc = $completed,
    files_transferred = $transferred,
    files_deleted = $deleted,
    files_skipped = $skipped,
    exit_code = $exitCode
WHERE id = $id;";
        cmd.Parameters.AddWithValue("$completed", DateTime.UtcNow.Ticks);
        cmd.Parameters.AddWithValue("$transferred", transferred);
        cmd.Parameters.AddWithValue("$deleted", deleted);
        cmd.Parameters.AddWithValue("$skipped", skipped);
        cmd.Parameters.AddWithValue("$exitCode", exitCode);
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.ExecuteNonQuery();
    }

    public IEnumerable<SyncSessionEntry> GetRecentSessions(int limit = 20)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, started_utc, completed_utc, mode,
       files_transferred, files_deleted, files_skipped, exit_code
FROM sync_sessions
ORDER BY id DESC
LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<SyncSessionEntry>();
        while (reader.Read())
        {
            long? completedTicks = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            int? exitCode = reader.IsDBNull(7) ? null : reader.GetInt32(7);
            list.Add(new SyncSessionEntry(
                Id: reader.GetInt64(0),
                StartedUtc: new DateTime(reader.GetInt64(1), DateTimeKind.Utc),
                CompletedUtc: completedTicks.HasValue ? new DateTime(completedTicks.Value, DateTimeKind.Utc) : null,
                Mode: reader.GetString(3),
                FilesTransferred: reader.GetInt32(4),
                FilesDeleted: reader.GetInt32(5),
                FilesSkipped: reader.GetInt32(6),
                ExitCode: exitCode));
        }
        return list;
    }

    // ── File state ────────────────────────────────────────────────────────────

    public FileState? GetFileState(string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
SELECT path, file_size, last_modified, status, last_synced, side
FROM files
WHERE path = $path COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$path", path);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new FileState(
            Path: reader.GetString(0),
            FileSize: reader.GetInt64(1),
            LastModified: new DateTime(reader.GetInt64(2), DateTimeKind.Utc),
            Status: reader.GetString(3),
            LastSynced: new DateTime(reader.GetInt64(4), DateTimeKind.Utc),
            Side: reader.GetString(5));
    }

    public IEnumerable<FileState> GetAllTrackedFiles()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
SELECT path, file_size, last_modified, status, last_synced, side
FROM files;";
        using var reader = cmd.ExecuteReader();
        var list = new List<FileState>();
        while (reader.Read())
        {
            list.Add(new FileState(
                Path: reader.GetString(0),
                FileSize: reader.GetInt64(1),
                LastModified: new DateTime(reader.GetInt64(2), DateTimeKind.Utc),
                Status: reader.GetString(3),
                LastSynced: new DateTime(reader.GetInt64(4), DateTimeKind.Utc),
                Side: reader.GetString(5)));
        }
        return list;
    }

    public IEnumerable<FileState> GetDeletedFiles()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
SELECT path, file_size, last_modified, status, last_synced, side
FROM files
WHERE status = 'deleted';";
        using var reader = cmd.ExecuteReader();
        var list = new List<FileState>();
        while (reader.Read())
        {
            list.Add(new FileState(
                Path: reader.GetString(0),
                FileSize: reader.GetInt64(1),
                LastModified: new DateTime(reader.GetInt64(2), DateTimeKind.Utc),
                Status: reader.GetString(3),
                LastSynced: new DateTime(reader.GetInt64(4), DateTimeKind.Utc),
                Side: reader.GetString(5)));
        }
        return list;
    }

    // ── Mutations ─────────────────────────────────────────────────────────────

    public void MarkSynced(string path, long fileSize, DateTime lastModified, long sessionId, string direction)
    {
        using var txn = _conn.BeginTransaction();
        try
        {
            using var upsert = _conn.CreateCommand();
            upsert.Transaction = txn;
            upsert.CommandText = @"
INSERT INTO files (path, file_size, last_modified, status, last_synced, side)
VALUES ($path, $size, $modified, 'exists', $synced, 'both')
ON CONFLICT(path) DO UPDATE SET
    file_size    = excluded.file_size,
    last_modified = excluded.last_modified,
    status       = 'exists',
    last_synced  = excluded.last_synced,
    side         = 'both';";
            upsert.Parameters.AddWithValue("$path", path);
            upsert.Parameters.AddWithValue("$size", fileSize);
            upsert.Parameters.AddWithValue("$modified", lastModified.ToUniversalTime().Ticks);
            upsert.Parameters.AddWithValue("$synced", DateTime.UtcNow.Ticks);
            upsert.ExecuteNonQuery();

            using var ver = _conn.CreateCommand();
            ver.Transaction = txn;
            ver.CommandText = @"
INSERT INTO file_versions (path, action, file_size, last_modified, sync_session_id, direction, detail, timestamp)
VALUES ($path, 'synced', $size, $modified, $session, $direction, NULL, $ts);";
            ver.Parameters.AddWithValue("$path", path);
            ver.Parameters.AddWithValue("$size", fileSize);
            ver.Parameters.AddWithValue("$modified", lastModified.ToUniversalTime().Ticks);
            ver.Parameters.AddWithValue("$session", sessionId);
            ver.Parameters.AddWithValue("$direction", direction);
            ver.Parameters.AddWithValue("$ts", DateTime.UtcNow.Ticks);
            ver.ExecuteNonQuery();

            txn.Commit();
        }
        catch
        {
            txn.Rollback();
            throw;
        }
    }

    public void MarkDeleted(string path, long sessionId, string? detail)
    {
        using var txn = _conn.BeginTransaction();
        try
        {
            using var upd = _conn.CreateCommand();
            upd.Transaction = txn;
            upd.CommandText = @"
UPDATE files SET status = 'deleted', last_synced = $synced
WHERE path = $path COLLATE NOCASE;";
            upd.Parameters.AddWithValue("$synced", DateTime.UtcNow.Ticks);
            upd.Parameters.AddWithValue("$path", path);
            var rowsAffected = upd.ExecuteNonQuery();
            if (rowsAffected == 0)
            {
                txn.Rollback();
                return; // Path not tracked — nothing to delete
            }

            using var ver = _conn.CreateCommand();
            ver.Transaction = txn;
            ver.CommandText = @"
INSERT INTO file_versions (path, action, file_size, last_modified, sync_session_id, direction, detail, timestamp)
VALUES ($path, 'deleted', NULL, NULL, $session, NULL, $detail, $ts);";
            ver.Parameters.AddWithValue("$path", path);
            ver.Parameters.AddWithValue("$session", sessionId);
            ver.Parameters.AddWithValue("$detail", detail ?? (object)DBNull.Value);
            ver.Parameters.AddWithValue("$ts", DateTime.UtcNow.Ticks);
            ver.ExecuteNonQuery();

            txn.Commit();
        }
        catch
        {
            txn.Rollback();
            throw;
        }
    }

    public void MarkSkipped(string path, long sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO file_versions (path, action, file_size, last_modified, sync_session_id, direction, detail, timestamp)
VALUES ($path, 'skipped', NULL, NULL, $session, NULL, NULL, $ts);";
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$session", sessionId);
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.Ticks);
        cmd.ExecuteNonQuery();
    }

    public void MarkNew(string path, long fileSize, DateTime lastModified, string side)
    {
        using var txn = _conn.BeginTransaction();
        try
        {
            using var upsert = _conn.CreateCommand();
            upsert.Transaction = txn;
            upsert.CommandText = @"
INSERT INTO files (path, file_size, last_modified, status, last_synced, side)
VALUES ($path, $size, $modified, 'new', $synced, $side)
ON CONFLICT(path) DO UPDATE SET
    file_size     = excluded.file_size,
    last_modified = excluded.last_modified,
    status        = 'new',
    last_synced   = excluded.last_synced,
    side          = excluded.side;";
            upsert.Parameters.AddWithValue("$path", path);
            upsert.Parameters.AddWithValue("$size", fileSize);
            upsert.Parameters.AddWithValue("$modified", lastModified.ToUniversalTime().Ticks);
            upsert.Parameters.AddWithValue("$synced", DateTime.UtcNow.Ticks);
            upsert.Parameters.AddWithValue("$side", side);
            upsert.ExecuteNonQuery();

            // Use session id 0 as a sentinel for discovery events (no active sync session)
            using var ver = _conn.CreateCommand();
            ver.Transaction = txn;
            ver.CommandText = @"
INSERT INTO file_versions (path, action, file_size, last_modified, sync_session_id, direction, detail, timestamp)
VALUES ($path, 'created', $size, $modified, 0, NULL, NULL, $ts);";
            ver.Parameters.AddWithValue("$path", path);
            ver.Parameters.AddWithValue("$size", fileSize);
            ver.Parameters.AddWithValue("$modified", lastModified.ToUniversalTime().Ticks);
            ver.Parameters.AddWithValue("$ts", DateTime.UtcNow.Ticks);
            ver.ExecuteNonQuery();

            txn.Commit();
        }
        catch
        {
            txn.Rollback();
            throw;
        }
    }

    // ── History ───────────────────────────────────────────────────────────────

    public IEnumerable<FileVersionEntry> GetFileHistory(string path, int limit = 50)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
SELECT path, action, file_size, last_modified, direction, detail, timestamp
FROM file_versions
WHERE path = $path COLLATE NOCASE
ORDER BY timestamp ASC, id ASC
LIMIT $limit;";
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<FileVersionEntry>();
        while (reader.Read())
        {
            long? sizeTicks = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            long? modTicks  = reader.IsDBNull(3) ? null : reader.GetInt64(3);
            string? dir     = reader.IsDBNull(4) ? null : reader.GetString(4);
            string? detail  = reader.IsDBNull(5) ? null : reader.GetString(5);
            list.Add(new FileVersionEntry(
                Path: reader.GetString(0),
                Action: reader.GetString(1),
                FileSize: sizeTicks,
                LastModified: modTicks.HasValue ? new DateTime(modTicks.Value, DateTimeKind.Utc) : null,
                Direction: dir,
                Detail: detail,
                Timestamp: new DateTime(reader.GetInt64(6), DateTimeKind.Utc)));
        }
        return list;
    }

    // ── Migration ─────────────────────────────────────────────────────────────

    public static void MigrateFromBinary(string binPath, string dbPath)
    {
        if (!File.Exists(binPath)) return;
        if (File.Exists(dbPath)) return; // Already migrated

        try
        {
            List<(string path, long size, long modTicks)> entries;

            // Read binary file in its own scope so the file handle is released before rename
            using (var fs = File.OpenRead(binPath))
            using (var reader = new BinaryReader(fs, Encoding.UTF8))
            {
                var magic = reader.ReadBytes(4);
                if (!magic.AsSpan().SequenceEqual("RFS1"u8)) return;

                reader.ReadInt64(); // lastSyncTicks (unused)
                int count = reader.ReadInt32();
                entries = new List<(string path, long size, long modTicks)>(count);
                for (int i = 0; i < count; i++)
                {
                    short pathLen = reader.ReadInt16();
                    var path = Encoding.UTF8.GetString(reader.ReadBytes(pathLen));
                    long size = reader.ReadInt64();
                    long modTicks = reader.ReadInt64();
                    entries.Add((path, size, modTicks));
                }
            }

            using (var db = new SyncDatabase(dbPath))
            {
                var sessionId = db.StartSession("migration", "", "", 0);
                foreach (var (path, size, modTicks) in entries)
                {
                    db.MarkSynced(path, size, new DateTime(modTicks, DateTimeKind.Utc), sessionId, "migration");
                }
                db.CompleteSession(sessionId, entries.Count, 0, 0, 0);
            }

            File.Move(binPath, binPath + ".migrated");
        }
        catch
        {
            // Migration failed — delete partial db so next run starts fresh
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _conn.Close();
        _conn.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
