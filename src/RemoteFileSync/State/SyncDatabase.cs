using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RemoteFileSync.Sync;

namespace RemoteFileSync.State;

/// <summary>
/// Legacy schema v1 projection of a row, kept for callers not yet migrated to
/// <see cref="AncestorRow"/>. Schema v2 has no `side` column, so <c>Side</c> is always
/// reported as "both" — the value v1 MarkSynced wrote for every synced row — and
/// <c>FileSize</c>/<c>LastModified</c> report the CLIENT side. Never use this projection to
/// reason about deletions: it cannot express the two sides disagreeing, which is exactly the
/// case the merge engine has to decide.
/// </summary>
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
/// One conflict or resurrection recorded during a sync session. <c>Detail</c> is a
/// <see cref="ConflictDetail"/>-encoded string; pass it to
/// <see cref="ConflictDetail.Decode"/> rather than parsing it by hand.
/// </summary>
public record ConflictEntry(string Path, string Detail, DateTime Timestamp);

/// <summary>
/// SQLite-backed file state tracking. NOT thread-safe — use from a single thread only.
/// </summary>
public sealed class SyncDatabase : IDisposable
{
    /// <summary>Stamped into PRAGMA user_version. Bump only alongside a migration step in InitSchema.</summary>
    public const int SchemaVersion = 2;

    private readonly SqliteConnection _conn;

    public SyncDatabase(string dbPath)
    {
        var dir = System.IO.Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            System.IO.Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        try
        {
            InitSchema();
        }
        catch
        {
            // A throw here (e.g. a migration failure) would otherwise propagate out of the
            // constructor with _conn open and unreachable via Dispose, holding the file lock
            // for the rest of the process. Releasing it the same way Dispose does lets a user
            // whose migration failed retry immediately instead of restarting the process.
            _conn.Close();
            _conn.Dispose();
            SqliteConnection.ClearAllPools();
            throw;
        }
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

    /// <summary>
    /// Creates or upgrades the schema. Schema v1 never stamped PRAGMA user_version, so a
    /// user_version of 0 is ambiguous — it means either "brand new file" or "v1 database".
    /// The presence of the v1-only `file_size` column is what tells the two apart.
    /// </summary>
    private void InitSchema()
    {
        // journal_mode cannot be changed inside a transaction, so the pragmas run first,
        // alone, and outside the upgrade transaction below.
        using (var pragmas = _conn.CreateCommand())
        {
            pragmas.CommandText = @"
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = OFF;
PRAGMA cache_size = -2000;";
            pragmas.ExecuteNonQuery();
        }

        CreateAuxTables();

        // Idempotence: a database already stamped at the current version is left untouched,
        // so reopening it never re-runs a rebuild against a table that no longer has the
        // columns the rebuild reads.
        if (ReadUserVersion() >= SchemaVersion) return;

        // Probed before BeginTransaction: Microsoft.Data.Sqlite rejects any command whose
        // Transaction property is unset while a transaction is open on the connection.
        bool isV1 = TableExists("files") && ColumnExists("files", "file_size");

        using var txn = _conn.BeginTransaction();
        try
        {
            // WARNING for the next schema bump: the else branch assumes any non-v1 existing
            // table already matches SchemaVersion, because CreateFilesV2's `CREATE TABLE IF NOT
            // EXISTS` is a no-op against a table that already exists, followed by an
            // unconditional user_version stamp. A future v3 must not reuse this shape as-is —
            // it needs its own explicit source-version detection and migration branch (like
            // MigrateV1ToV2), or it will silently stamp the new version onto a table nothing
            // actually migrated.
            if (isV1) MigrateV1ToV2(txn);
            else      CreateFilesV2(txn);

            using var stamp = _conn.CreateCommand();
            stamp.Transaction = txn;
            // user_version lives in the database header and is journalled, so the stamp
            // commits with the table work or not at all. That atomicity is what makes a
            // process killed mid-upgrade safe: the file is still v1 and simply upgrades again.
            stamp.CommandText = $"PRAGMA user_version = {SchemaVersion};";
            stamp.ExecuteNonQuery();

            txn.Commit();
        }
        catch
        {
            txn.Rollback();
            throw;
        }
    }

    private void CreateAuxTables()
    {
        // file_versions.action carries no CHECK constraint, so the v2 values 'conflict' and
        // 'resurrected' need no DDL change here.
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
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
);";
        cmd.ExecuteNonQuery();
    }

    private void CreateFilesV2(SqliteTransaction txn)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = txn;
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS files (
    path          TEXT PRIMARY KEY COLLATE NOCASE,
    client_size   INTEGER NOT NULL,
    client_mtime  INTEGER NOT NULL,
    server_size   INTEGER NOT NULL,
    server_mtime  INTEGER NOT NULL,
    status        TEXT    NOT NULL,
    last_synced   INTEGER NOT NULL,
    deleted_utc   INTEGER
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS idx_files_status ON files(status);";
        cmd.ExecuteNonQuery();
    }

    private void MigrateV1ToV2(SqliteTransaction txn)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = txn;
        // `side` must go and this project targets SQLite builds without DROP COLUMN, so the
        // table is rebuilt: create / copy / drop / rename, all inside the CALLER's transaction
        // so a crash between any two statements leaves a clean v1 file that simply migrates
        // again on the next open. v1 stored one size+mtime shared by both sides, so both
        // per-side columns seed from it — the correct ancestor for a pair that has only ever
        // synced through v1's one-way model.
        cmd.CommandText = @"
CREATE TABLE files_v2 (
    path          TEXT PRIMARY KEY COLLATE NOCASE,
    client_size   INTEGER NOT NULL,
    client_mtime  INTEGER NOT NULL,
    server_size   INTEGER NOT NULL,
    server_mtime  INTEGER NOT NULL,
    status        TEXT    NOT NULL,
    last_synced   INTEGER NOT NULL,
    deleted_utc   INTEGER
) WITHOUT ROWID;

INSERT INTO files_v2 (path, client_size, client_mtime, server_size, server_mtime,
                      status, last_synced, deleted_utc)
SELECT path,
       file_size, last_modified,
       file_size, last_modified,
       status,
       last_synced,
       CASE WHEN status = 'deleted' THEN last_synced ELSE NULL END
FROM files;

DROP TABLE files;
ALTER TABLE files_v2 RENAME TO files;
CREATE INDEX IF NOT EXISTS idx_files_status ON files(status);";
        cmd.ExecuteNonQuery();
    }

    private int ReadUserVersion()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private bool TableExists(string name)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private bool ColumnExists(string table, string column)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
        cmd.Parameters.AddWithValue("$table", table);
        cmd.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
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

    // Unlike the ancestor path below (IsRepresentableTicks) and ReviewReport.TryUtc, the tick
    // columns read here are trusted unguarded. That is a decision, not an oversight: this
    // history is written and read by this same class from its own sessions, never fed from an
    // externally-corrupted source in the current call graph, so adding a guard would be
    // defending against input that cannot occur — YAGNI, not a gap.
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

    // ── Ancestor rows (schema v2) ─────────────────────────────────────────────

    private const string AncestorSelect = @"
SELECT path, client_size, client_mtime, server_size, server_mtime, status, last_synced, deleted_utc
FROM files";

    private static bool IsRepresentableTicks(long ticks) =>
        ticks >= 0 && ticks <= DateTime.MaxValue.Ticks;

    /// <summary>
    /// Projects a row, rejecting any whose tick columns are outside the representable
    /// DateTime range. SQLite INTEGER columns hold any 64-bit value, so a truncated or
    /// corrupted database can hand back anything — and two call sites turn that into a crash:
    /// <c>new DateTime(ticks)</c> in the legacy shims throws ArgumentOutOfRangeException, and
    /// ChangeDetector.Unchanged calls Math.Abs on the mtime difference, which throws on
    /// exactly long.MinValue. Rejecting is the only safe answer: a missing row reads as
    /// "no ancestor", which the decision tables route down the additive newest-wins path.
    /// Clamping would instead fabricate an ancestor that can read as "unchanged" and so
    /// authorise a deletion the two sides never agreed on.
    /// </summary>
    private static bool TryReadAncestorRow(SqliteDataReader reader, out AncestorRow row)
    {
        row = null!;

        var clientMtime = reader.GetInt64(2);
        var serverMtime = reader.GetInt64(4);
        var lastSynced  = reader.GetInt64(6);
        long? deletedUtc = reader.IsDBNull(7) ? null : reader.GetInt64(7);

        if (!IsRepresentableTicks(clientMtime)) return false;
        if (!IsRepresentableTicks(serverMtime)) return false;
        if (!IsRepresentableTicks(lastSynced))  return false;
        if (deletedUtc.HasValue && !IsRepresentableTicks(deletedUtc.Value)) return false;

        row = new AncestorRow(
            Path: reader.GetString(0),
            ClientSize: reader.GetInt64(1),
            ClientMtimeTicks: clientMtime,
            ServerSize: reader.GetInt64(3),
            ServerMtimeTicks: serverMtime,
            Status: reader.GetString(5),
            LastSyncedTicks: lastSynced,
            DeletedUtcTicks: deletedUtc);
        return true;
    }

    public AncestorRow? GetRow(string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = AncestorSelect + " WHERE path = $path COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$path", path);
        using var reader = cmd.ExecuteReader();
        return reader.Read() && TryReadAncestorRow(reader, out var row) ? row : null;
    }

    public Dictionary<string, AncestorRow> LoadAll()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = AncestorSelect + ";";
        using var reader = cmd.ExecuteReader();
        // OrdinalIgnoreCase mirrors the table's NOCASE primary key. An ordinal dictionary
        // would miss rows whose casing drifted between scans on Windows, and a missed
        // ancestor reads as "never synced" — which is how a file gets re-sent or deleted.
        var rows = new Dictionary<string, AncestorRow>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            if (TryReadAncestorRow(reader, out var row))
                rows[row.Path] = row;
        }
        return rows;
    }

    // ── Legacy v1 read surface (thin shims over the v2 rows) ──────────────────

    private static FileState ToFileState(AncestorRow row) => new FileState(
        Path: row.Path,
        FileSize: row.ClientSize,
        LastModified: new DateTime(row.ClientMtimeTicks, DateTimeKind.Utc),
        Status: row.Status,
        LastSynced: new DateTime(row.LastSyncedTicks, DateTimeKind.Utc),
        Side: "both");

    public FileState? GetFileState(string path)
    {
        var row = GetRow(path);
        return row == null ? null : ToFileState(row);
    }

    public IEnumerable<FileState> GetAllTrackedFiles() =>
        LoadAll().Values.Select(ToFileState).ToList();

    public IEnumerable<FileState> GetDeletedFiles() =>
        LoadAll().Values.Where(r => r.Status == "deleted").Select(ToFileState).ToList();

    // ── Mutations ─────────────────────────────────────────────────────────────

    public void UpsertSynced(string path,
                             long clientSize, long clientMtimeTicks,
                             long serverSize, long serverMtimeTicks,
                             long sessionId, string direction)
    {
        var now = DateTime.UtcNow.Ticks;
        using var txn = _conn.BeginTransaction();
        try
        {
            using var upsert = _conn.CreateCommand();
            upsert.Transaction = txn;
            // deleted_utc is cleared on every successful sync. A resurrected path that kept
            // its tombstone date would be silently dropped by PurgeTombstonesOlderThan,
            // losing the ancestor and re-opening the delete loop this schema exists to close.
            upsert.CommandText = @"
INSERT INTO files (path, client_size, client_mtime, server_size, server_mtime,
                   status, last_synced, deleted_utc)
VALUES ($path, $cSize, $cMtime, $sSize, $sMtime, 'exists', $synced, NULL)
ON CONFLICT(path) DO UPDATE SET
    client_size  = excluded.client_size,
    client_mtime = excluded.client_mtime,
    server_size  = excluded.server_size,
    server_mtime = excluded.server_mtime,
    status       = 'exists',
    last_synced  = excluded.last_synced,
    deleted_utc  = NULL;";
            upsert.Parameters.AddWithValue("$path", path);
            upsert.Parameters.AddWithValue("$cSize", clientSize);
            upsert.Parameters.AddWithValue("$cMtime", clientMtimeTicks);
            upsert.Parameters.AddWithValue("$sSize", serverSize);
            upsert.Parameters.AddWithValue("$sMtime", serverMtimeTicks);
            upsert.Parameters.AddWithValue("$synced", now);
            upsert.ExecuteNonQuery();

            // History records the client side only; it is a human-facing audit log, and the
            // ancestor the engine actually reads is the `files` row written above.
            using var ver = _conn.CreateCommand();
            ver.Transaction = txn;
            ver.CommandText = @"
INSERT INTO file_versions (path, action, file_size, last_modified, sync_session_id, direction, detail, timestamp)
VALUES ($path, 'synced', $size, $modified, $session, $direction, NULL, $ts);";
            ver.Parameters.AddWithValue("$path", path);
            ver.Parameters.AddWithValue("$size", clientSize);
            ver.Parameters.AddWithValue("$modified", clientMtimeTicks);
            ver.Parameters.AddWithValue("$session", sessionId);
            ver.Parameters.AddWithValue("$direction", direction);
            ver.Parameters.AddWithValue("$ts", now);
            ver.ExecuteNonQuery();

            txn.Commit();
        }
        catch
        {
            txn.Rollback();
            throw;
        }
    }

    public void Tombstone(string path, long sessionId, string? detail)
    {
        var now = DateTime.UtcNow.Ticks;
        using var txn = _conn.BeginTransaction();
        try
        {
            using var upd = _conn.CreateCommand();
            upd.Transaction = txn;
            upd.CommandText = @"
UPDATE files SET status = 'deleted', last_synced = $synced, deleted_utc = $synced
WHERE path = $path COLLATE NOCASE;";
            upd.Parameters.AddWithValue("$synced", now);
            upd.Parameters.AddWithValue("$path", path);
            var rowsAffected = upd.ExecuteNonQuery();
            if (rowsAffected == 0)
            {
                // Untracked path: writing history here would invent a deletion the pair never
                // observed, and a later run would read that phantom entry as evidence that
                // the peer once had the file.
                txn.Rollback();
                return;
            }

            using var ver = _conn.CreateCommand();
            ver.Transaction = txn;
            ver.CommandText = @"
INSERT INTO file_versions (path, action, file_size, last_modified, sync_session_id, direction, detail, timestamp)
VALUES ($path, 'deleted', NULL, NULL, $session, NULL, $detail, $ts);";
            ver.Parameters.AddWithValue("$path", path);
            ver.Parameters.AddWithValue("$session", sessionId);
            ver.Parameters.AddWithValue("$detail", detail ?? (object)DBNull.Value);
            ver.Parameters.AddWithValue("$ts", now);
            ver.ExecuteNonQuery();

            txn.Commit();
        }
        catch
        {
            txn.Rollback();
            throw;
        }
    }

    public int PurgeTombstonesOlderThan(TimeSpan age)
    {
        // Zero and negative are NOT the same case, on purpose. Zero is this project's
        // documented convention for "retention disabled" (ArchiveKeepDays = 0; the sibling
        // ArchiveManager.Prune per CONTRACT.md correction 3) -- a legitimate value a caller
        // passes deliberately. Without a guard, TimeSpan.Zero computes
        // cutoff = UtcNow.Ticks - 0, which deletes every tombstone whose deleted_utc is not in
        // the future -- i.e. all of them -- destroying the evidence that distinguishes
        // "re-appeared after deletion" from "never seen", so it gets an explicit no-op.
        // Negative has no such meaning; it can only come from a caller defect (inverted
        // subtraction, bad config arithmetic), and this project's guiding rule is that
        // nonsensical state must be loud, never silently treated as "nothing to do" -- the
        // same reason the pair.marker gate errors out instead of guessing. Silently returning
        // 0 here would let a broken retention setting run forever while tombstones pile up and
        // nobody notices.
        if (age == TimeSpan.Zero)
            return 0;
        if (age < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(age),
                "Retention age must not be negative.");

        using var cmd = _conn.CreateCommand();
        // status is the gate, not deleted_utc alone: an 'exists' row must survive a stale
        // deleted_utc, and a tombstone whose deleted_utc is NULL is kept because its age is
        // unknowable — dropping it would silently discard an ancestor.
        cmd.CommandText = @"
DELETE FROM files
WHERE status = 'deleted' AND deleted_utc IS NOT NULL AND deleted_utc < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.Ticks - age.Ticks);
        return cmd.ExecuteNonQuery();
    }

    // ── Legacy v1 write surface (thin shims over the v2 API) ──────────────────

    /// <summary>
    /// Legacy one-sided upsert: v1 stored a single size+mtime, so both v2 sides receive it.
    /// SAFE only where the caller genuinely knows both sides hold that value — which today
    /// means <see cref="MigrateFromBinary"/> and SyncClient's post-transfer recording, once a
    /// file has actually landed on both peers. SyncClient's Skip loop no longer calls this: it
    /// was split into a both-sides-present <see cref="UpsertSynced"/> and a one-sided
    /// <see cref="MarkSkipped"/>, so a Push or Pull run can no longer fabricate a peer state
    /// that never existed from a skip alone.
    /// </summary>
    public void MarkSynced(string path, long fileSize, DateTime lastModified, long sessionId, string direction)
    {
        var ticks = lastModified.ToUniversalTime().Ticks;
        UpsertSynced(path, fileSize, ticks, fileSize, ticks, sessionId, direction);
    }

    public void MarkDeleted(string path, long sessionId, string? detail) =>
        Tombstone(path, sessionId, detail);

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

    /// <summary>
    /// Legacy discovery marker, retargeted onto the v2 columns. The <paramref name="side"/>
    /// argument is accepted but not stored — v2 dropped the column. Rows land with
    /// status='new'. CONTRACT.md ("Why Status=="new" belongs on the additive path") REQUIRES
    /// Phase 6's decision tables to treat 'new' exactly like a missing row: no two-sided
    /// agreement about this file has ever happened, so it is not a usable ancestor. This class
    /// cannot see SyncEngine's dispatch to enforce that itself, so it falls to whoever writes
    /// it: a Phase 6 author who dispatches on `row != null && row.Status != "deleted"` would
    /// route 'new' down the delete-capable path instead, and delete a file on the strength of
    /// a sync that never occurred.
    /// </summary>
    public void MarkNew(string path, long fileSize, DateTime lastModified, string side)
    {
        var modified = lastModified.ToUniversalTime().Ticks;
        var now = DateTime.UtcNow.Ticks;
        using var txn = _conn.BeginTransaction();
        try
        {
            using var upsert = _conn.CreateCommand();
            upsert.Transaction = txn;
            upsert.CommandText = @"
INSERT INTO files (path, client_size, client_mtime, server_size, server_mtime,
                   status, last_synced, deleted_utc)
VALUES ($path, $size, $modified, $size, $modified, 'new', $synced, NULL)
ON CONFLICT(path) DO UPDATE SET
    client_size  = excluded.client_size,
    client_mtime = excluded.client_mtime,
    server_size  = excluded.server_size,
    server_mtime = excluded.server_mtime,
    status       = 'new',
    last_synced  = excluded.last_synced,
    deleted_utc  = NULL;";
            upsert.Parameters.AddWithValue("$path", path);
            upsert.Parameters.AddWithValue("$size", fileSize);
            upsert.Parameters.AddWithValue("$modified", modified);
            upsert.Parameters.AddWithValue("$synced", now);
            upsert.ExecuteNonQuery();

            // Use session id 0 as a sentinel for discovery events (no active sync session)
            using var ver = _conn.CreateCommand();
            ver.Transaction = txn;
            ver.CommandText = @"
INSERT INTO file_versions (path, action, file_size, last_modified, sync_session_id, direction, detail, timestamp)
VALUES ($path, 'created', $size, $modified, 0, NULL, NULL, $ts);";
            ver.Parameters.AddWithValue("$path", path);
            ver.Parameters.AddWithValue("$size", fileSize);
            ver.Parameters.AddWithValue("$modified", modified);
            ver.Parameters.AddWithValue("$ts", now);
            ver.ExecuteNonQuery();

            txn.Commit();
        }
        catch
        {
            txn.Rollback();
            throw;
        }
    }

    // ── Conflict / resurrection log ───────────────────────────────────────────

    /// <summary>
    /// Records a both-sides-changed conflict. <paramref name="detail"/> must be a
    /// <see cref="ConflictDetail.Encode"/> string, never free-form English — the review
    /// report decodes it back into per-side sizes and mtimes.
    /// </summary>
    public void LogConflict(string path, long sessionId, string detail) =>
        LogVersionAction(path, "conflict", sessionId, detail);

    /// <summary>
    /// Records a path kept because this side modified it after the peer deleted it.
    /// A separate method rather than a flag inside <paramref name="detail"/>: the kind of
    /// event is a property of the call site, and inferring it from the payload means a
    /// user's filename can silently reclassify their own conflict.
    /// </summary>
    public void LogResurrection(string path, long sessionId, string detail) =>
        LogVersionAction(path, "resurrected", sessionId, detail);

    private void LogVersionAction(string path, string action, long sessionId, string detail)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO file_versions (path, action, file_size, last_modified, sync_session_id, direction, detail, timestamp)
VALUES ($path, $action, NULL, NULL, $session, NULL, $detail, $ts);";
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$action", action);
        cmd.Parameters.AddWithValue("$session", sessionId);
        cmd.Parameters.AddWithValue("$detail", detail);
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.Ticks);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ConflictEntry> GetSessionConflicts(long sessionId) =>
        GetSessionEntries(sessionId, "conflict");

    public IReadOnlyList<ConflictEntry> GetSessionResurrections(long sessionId) =>
        GetSessionEntries(sessionId, "resurrected");

    // Backs GetSessionConflicts/GetSessionResurrections. Its Timestamp column, like
    // GetRecentSessions' above, is read unguarded on purpose: these rows are this session's own
    // just-written file_versions entries, not externally-corrupted input, so there is nothing
    // for an IsRepresentableTicks-style guard to defend against here.
    private IReadOnlyList<ConflictEntry> GetSessionEntries(long sessionId, string action)
    {
        using var cmd = _conn.CreateCommand();
        // id breaks ties: two entries logged inside the same tick must still report in write
        // order, otherwise the review report shuffles rows between otherwise identical runs.
        cmd.CommandText = @"
SELECT path, detail, timestamp
FROM file_versions
WHERE sync_session_id = $session AND action = $action
ORDER BY timestamp ASC, id ASC;";
        cmd.Parameters.AddWithValue("$session", sessionId);
        cmd.Parameters.AddWithValue("$action", action);
        using var reader = cmd.ExecuteReader();
        var list = new List<ConflictEntry>();
        while (reader.Read())
        {
            list.Add(new ConflictEntry(
                Path: reader.GetString(0),
                Detail: reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Timestamp: new DateTime(reader.GetInt64(2), DateTimeKind.Utc)));
        }
        return list;
    }

    // ── History ───────────────────────────────────────────────────────────────

    // Same asymmetry with the ancestor path as GetRecentSessions/GetSessionEntries above: the
    // LastModified/Timestamp columns are read unguarded because this reader has no production
    // caller fed from externally-corrupted ticks today, not because the guard was forgotten.
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
