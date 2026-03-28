# RemoteFileSync — SQLite File Version Tracking Design Specification

**Date:** 2026-03-28
**Version:** 1.0
**Status:** Draft
**Parent Spec:** 2026-03-28-deletion-sync-design.md
**Branch:** feature/sqlite-version-tracking
**Platform:** Windows 10 / Windows 11
**Runtime:** .NET 10, C#

---

## 1. Problem Statement

The current binary state file (`sync-state.bin`) has fundamental flaws that cause file resurrection and new-vs-deleted confusion:

### 1.1 Bug A: File Resurrection After Partial Syncs

The state file is saved only on fully successful syncs (exit code 0). If 95 of 100 files sync but 5 fail, the entire state is discarded. On the next run, files that were intentionally deleted appear "not in state" and are treated as new — resurrecting them.

**Root cause:** All-or-nothing state save. No per-file granularity.

### 1.2 Bug B: New vs Deleted Confusion

The state stores only a flat manifest of existing files. There is no distinction between:
- A file that was synced before and then intentionally deleted → should propagate deletion
- A file that has never been synced in this pair → should be copied as new

Both cases look identical in the current binary state: "file not in manifest."

**Root cause:** No deletion records. No file lifecycle tracking.

### 1.3 Additional Weaknesses

| Issue | Description |
|-------|-------------|
| No per-file sync timestamp | Global `LastSyncUtc` used for all files, not per-file |
| Uni-directional deletion hole | Server-deleted files in uni mode silently disappear from both sides |
| Client-only state | Switching which machine is the client loses all history |
| No audit trail | Impossible to debug "why was this file deleted/restored?" |

---

## 2. Solution: SQLite Database

Replace the binary state file with a SQLite database at the same location:

```
%LOCALAPPDATA%\RemoteFileSync\{pairId}\sync.db
```

### 2.1 Why SQLite

- **Per-file ACID transactions** — each file state update is committed individually, surviving partial syncs
- **Indexed lookups** — `files` table indexed by path for O(1) lookups during plan computation
- **Explicit deletion records** — `status = 'deleted'` distinguishes intentional deletions from never-synced files
- **Full history** — `file_versions` table records every action on every file across all syncs
- **Session tracking** — `sync_sessions` table detects interrupted/crashed syncs
- **Query debugging** — `sqlite3 sync.db "SELECT * FROM file_versions WHERE path='docs/report.docx'"` for troubleshooting

### 2.2 NuGet Dependency

**Package:** `Microsoft.Data.Sqlite` (latest stable)

This is the only external NuGet dependency added to the main project. It bundles SQLite natively for Windows — no separate install required. The "zero NuGet" principle is relaxed because file version tracking with history, indexing, and partial-sync resilience IS a database problem.

### 2.3 No Content Hashing

File comparison continues to use size + timestamp only. No SHA256 content hashing is performed during scanning to avoid CPU/IO overhead on large folders.

---

## 3. SQLite Schema

```sql
-- Current state of every known file (one row per path)
CREATE TABLE IF NOT EXISTS files (
    path            TEXT PRIMARY KEY COLLATE NOCASE,
    file_size       INTEGER NOT NULL,
    last_modified   INTEGER NOT NULL,       -- UTC ticks
    status          TEXT NOT NULL,           -- 'exists', 'deleted', 'new'
    last_synced     INTEGER NOT NULL,        -- UTC ticks, per-file
    side            TEXT NOT NULL             -- 'both', 'client', 'server'
) WITHOUT ROWID;

-- Full history of every action on every file
CREATE TABLE IF NOT EXISTS file_versions (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    path            TEXT NOT NULL COLLATE NOCASE,
    action          TEXT NOT NULL,           -- 'synced', 'deleted', 'restored', 'skipped', 'created', 'conflict'
    file_size       INTEGER,
    last_modified   INTEGER,                -- UTC ticks
    sync_session_id INTEGER NOT NULL,
    direction       TEXT,                   -- 'to_server', 'to_client', 'both', NULL
    detail          TEXT,                   -- optional context string
    timestamp       INTEGER NOT NULL         -- UTC ticks when action happened
);

CREATE INDEX IF NOT EXISTS idx_versions_path ON file_versions(path);
CREATE INDEX IF NOT EXISTS idx_versions_session ON file_versions(sync_session_id);

-- One row per sync run
CREATE TABLE IF NOT EXISTS sync_sessions (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    started_utc     INTEGER NOT NULL,
    completed_utc   INTEGER,                -- NULL if incomplete/crashed
    mode            TEXT NOT NULL,           -- 'uni', 'bidi', 'uni+delete', 'bidi+delete'
    files_transferred INTEGER DEFAULT 0,
    files_deleted   INTEGER DEFAULT 0,
    files_skipped   INTEGER DEFAULT 0,
    exit_code       INTEGER,                -- NULL if still running
    client_folder   TEXT,
    server_host     TEXT,
    server_port     INTEGER
);
```

### 3.1 Schema Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| `files` PRIMARY KEY | `path` (WITHOUT ROWID) | Path IS the identity. No surrogate key needed. WITHOUT ROWID avoids extra B-tree. |
| `COLLATE NOCASE` on path | Case-insensitive | Windows file system is case-insensitive. Matches existing `StringComparer.OrdinalIgnoreCase`. |
| `last_modified` as INTEGER | UTC ticks (int64) | Matches .NET `DateTime.Ticks`. No string parsing overhead. |
| `status` as TEXT | 'exists' / 'deleted' / 'new' | Human-readable, queryable, extensible. Enum-like values enforced by application. |
| `file_versions` separate table | Append-only history | Never updated, never deleted. Fast inserts, indexed queries by path or session. |
| `sync_sessions.completed_utc` | NULL when incomplete | Detects crashed/interrupted syncs. Engine can skip incomplete session data. |

### 3.2 SQLite Pragmas

```sql
PRAGMA journal_mode = WAL;          -- Write-Ahead Logging for concurrent reads
PRAGMA synchronous = NORMAL;        -- Balance between safety and performance
PRAGMA foreign_keys = OFF;          -- No FK constraints (performance, simplicity)
PRAGMA cache_size = -2000;          -- 2MB page cache
```

---

## 4. Deletion Detection Algorithm (Revised)

### 4.1 Three-Way Status

The `files.status` field provides the missing distinction:

| Status | Meaning | What happens when file is missing from one side |
|--------|---------|--------------------------------------------------|
| `exists` | Was synced before, currently known to be on both sides | **Deletion detected** — apply Case 1/Case 2 logic |
| `deleted` | Was intentionally deleted in a previous sync | **Already handled** — no action unless file re-appeared |
| `new` | Just appeared, never been synced | N/A — new files are copied, not treated as deletions |
| (not in DB) | Never seen in this sync pair | **Genuinely new** — copy to other side |

### 4.2 Revised Algorithm

```
For each file in the current manifests (client + server):

CASE 1: File on BOTH sides
  → Normal conflict resolution (timestamp/size)
  → db.MarkSynced(path, ...) — status='exists'

CASE 2: File on ONE side only
  db_state = db.GetFileState(path)

  IF db_state is NULL:
    → Genuinely new file. Copy to other side.
    → db.MarkNew(path, ...) then db.MarkSynced(path, ...) after transfer

  IF db_state.Status == 'exists':
    → Was synced before, now missing = DELETION
    → Compare surviving file's lastModified against db_state.LastSynced (per-file!)
      • survivingFile.lastModified ≤ db_state.LastSynced + 2s → propagate deletion
      • survivingFile.lastModified > db_state.LastSynced + 2s → restore (copy to deleting side)
    → db.MarkDeleted(path, ...) or db.MarkSynced(path, ...) after action

  IF db_state.Status == 'deleted':
    → Was previously deleted intentionally
    → File re-appeared on one side = external restoration
    → Copy to other side, db.MarkSynced(path, ...) — status='exists'

  IF db_state.Status == 'new':
    → Was seen as new but never successfully synced
    → Treat as genuinely new. Copy to other side.

CASE 3: File in DB but on NEITHER side
  → Both sides deleted. db.MarkDeleted(path, ...) if not already.

UNI-DIRECTIONAL FIX:
  When bidirectional=false and deleteEnabled=true:
  • Client deleted → DeleteOnServer (propagate) — same as before
  • Server deleted → client still has it → re-push as ClientOnly (FIX: not silently dropped)
```

### 4.3 Per-File Timestamp Comparison

The critical improvement: deletion detection uses `db_state.LastSynced` (the per-file timestamp from the `files` table) instead of a global `LastSyncUtc`. This means:

- A file synced 5 runs ago but unchanged since → uses its own `LastSynced` from run 5
- Not affected by later syncs of other files
- Much more accurate "untouched" detection

---

## 5. SyncDatabase Class

### 5.1 Location

```
src/RemoteFileSync/State/SyncDatabase.cs
```

Replaces `SyncStateManager` as the primary state backend.

### 5.2 Public API

```csharp
public sealed class SyncDatabase : IDisposable
{
    // Construction
    public SyncDatabase(string dbPath);
    public static string GetDbPath(string baseDir, string localFolder, string remoteHost, int port);
    public static string DefaultBaseDir { get; }

    // Session management
    public long StartSession(string mode, string clientFolder, string serverHost, int serverPort);
    public void CompleteSession(long sessionId, int transferred, int deleted, int skipped, int exitCode);

    // File state queries
    public FileState? GetFileState(string relativePath);
    public List<FileState> GetAllTrackedFiles();
    public List<FileState> GetDeletedFiles();

    // File state updates (per-file, called during sync)
    public void MarkSynced(string path, long fileSize, DateTime lastModified, long sessionId, string direction);
    public void MarkDeleted(string path, long sessionId, string detail);
    public void MarkSkipped(string path, long sessionId);
    public void MarkNew(string path, long fileSize, DateTime lastModified, string side);

    // Audit queries
    public List<FileVersionEntry> GetFileHistory(string path, int limit = 50);
    public List<SyncSessionEntry> GetRecentSessions(int limit = 20);

    // Cleanup
    public void Dispose();
}

public record FileState(
    string Path, long FileSize, DateTime LastModified,
    string Status, DateTime LastSynced, string Side);

public record FileVersionEntry(
    string Path, string Action, long? FileSize,
    DateTime? LastModified, string? Direction,
    string? Detail, DateTime Timestamp);

public record SyncSessionEntry(
    long Id, DateTime StartedUtc, DateTime? CompletedUtc,
    string Mode, int FilesTransferred, int FilesDeleted,
    int FilesSkipped, int? ExitCode);
```

### 5.3 Implementation Details

- Constructor calls `CREATE TABLE IF NOT EXISTS` and sets pragmas
- Each `Mark*` method runs in a transaction: UPDATE/INSERT `files` + INSERT `file_versions`
- `GetFileState` is the hot path: single indexed SELECT by path
- Connection kept open for the duration of the sync session (one connection per `SyncDatabase` instance)
- WAL mode allows the database to be read by external tools (e.g., `sqlite3` CLI) while sync is running

---

## 6. Migration from Binary State

### 6.1 Automatic Migration

On first run with the new code, if `sync-state.bin` exists alongside where `sync.db` would be:

1. Read the binary manifest + `LastSyncUtc`
2. Create `sync.db` with the schema
3. Insert each file from the binary manifest into `files` with `status = 'exists'`, `last_synced = LastSyncUtc`
4. Create a migration session in `sync_sessions` with `mode = 'migration'`
5. Rename `sync-state.bin` → `sync-state.bin.migrated`

### 6.2 No Migration

If no `sync-state.bin` exists, the database starts empty. First sync is additive (same as current behavior).

---

## 7. Integration Points

### 7.1 Modified Files

| File | Change |
|------|--------|
| `src/RemoteFileSync/RemoteFileSync.csproj` | Add `Microsoft.Data.Sqlite` NuGet reference |
| `src/RemoteFileSync/State/SyncDatabase.cs` | NEW: SQLite state backend |
| `src/RemoteFileSync/Sync/SyncEngine.cs` | New overload accepting `SyncDatabase`, revised deletion logic |
| `src/RemoteFileSync/Sync/ConflictResolver.cs` | Update `ResolveDeleteConflict` to accept per-file `lastSynced` |
| `src/RemoteFileSync/Network/SyncClient.cs` | Per-file state updates, session lifecycle, replace `SyncStateManager` |
| `src/RemoteFileSync/Network/SyncServer.cs` | Per-file state updates |
| `src/RemoteFileSync/Program.cs` | Create `SyncDatabase` instead of `SyncStateManager` |

### 7.2 Deprecated Files

| File | Status |
|------|--------|
| `src/RemoteFileSync/State/SyncStateManager.cs` | Kept for migration only. Marked `[Obsolete]`. |

### 7.3 New SyncEngine Overload

```csharp
public static List<SyncPlanEntry> ComputePlan(
    FileManifest clientManifest,
    FileManifest serverManifest,
    bool bidirectional,
    SyncDatabase? db,           // NEW: replaces SyncState?
    bool deleteEnabled)
```

The old `SyncState?` overload is preserved for backward compatibility with existing tests.

### 7.4 Per-File Update Flow in SyncClient

```csharp
// Start session
var sessionId = db.StartSession(mode, clientFolder, serverHost, port);

// Per-file in send loop:
try {
    await sender.SendFileAsync(...);
    db.MarkSynced(path, fi.Length, fi.LastWriteTimeUtc, sessionId, "to_server");
} catch { /* file state not updated — retains previous state */ }

// Per-file in deletion loop:
db.MarkDeleted(path, sessionId, "deleted on client, untouched on server");

// Skipped files:
db.MarkSkipped(path, sessionId);

// Complete session (always called, even on partial success):
db.CompleteSession(sessionId, filesTransferred, filesDeleted, filesSkipped, exitCode);
```

**Critical difference from current code:** `CompleteSession` is ALWAYS called, regardless of exit code. The per-file state is already accurate because each file was updated individually. The session record simply closes the session and records the final stats.

---

## 8. Error Handling

| Scenario | Behavior |
|----------|----------|
| Database file corrupted | Delete and recreate. First sync is additive (same as current). Log warning. |
| Database locked by another process | Retry with 1-second delay, up to 3 times. Then fail with error. |
| Crash mid-sync | Session has `completed_utc = NULL`. Per-file state reflects actual progress. |
| Disk full during database write | SQLite rolls back current transaction. File state unchanged. Sync continues. |
| Migration from binary fails | Log warning, start fresh (no binary import). Binary file not renamed. |

---

## 9. Testing Strategy

### 9.1 Unit Tests — SyncDatabase

| Test | Verifies |
|------|----------|
| `CreateDatabase_InitializesSchema` | Tables and indexes exist |
| `StartSession_ReturnsId` | Session created with started_utc |
| `CompleteSession_SetsCompletedUtc` | completed_utc and stats populated |
| `MarkSynced_CreatesFileAndVersion` | files row + file_versions row |
| `MarkSynced_UpdatesExistingFile` | status changes, last_synced updates |
| `MarkDeleted_SetsStatusDeleted` | status='deleted', version record created |
| `GetFileState_ReturnsCorrectState` | Lookup by path works |
| `GetFileState_CaseInsensitive` | `docs/Report.docx` matches `docs/report.docx` |
| `GetFileState_NotFound_ReturnsNull` | Unknown path returns null |
| `GetDeletedFiles_ReturnsOnlyDeleted` | Filters by status='deleted' |
| `GetFileHistory_ReturnsChronological` | Ordered by timestamp |
| `PartialSync_PreservesPerFileState` | 3 files synced, 2 fail, 3 have updated state |
| `Migration_ImportsOldBinaryState` | Binary manifest imported correctly |

### 9.2 Updated SyncEngine Tests

| Test | Verifies |
|------|----------|
| `DeletedFile_InDb_ProducesDeleteAction` | status='exists' + missing → DeleteOnServer |
| `NewFile_NotInDb_ProducesCopyAction` | Not in DB → ClientOnly/ServerOnly |
| `PreviouslyDeleted_Reappeared_CopiesAgain` | status='deleted' + reappeared → copy + mark exists |
| `UniDirectional_ServerLostFile_RePushed` | Uni mode: server missing, client has → ClientOnly (FIX) |
| `PerFileTimestamp_UsedForDeletion` | Uses files.last_synced, not global timestamp |

### 9.3 Integration Tests

| Test | Verifies |
|------|----------|
| `E2E_PartialSync_NoResurrection` | Fail some files, verify deleted files stay deleted |
| `E2E_NewVsDeleted_Distinguishes` | New file copied, deleted file not resurrected |
| `E2E_CrashRecovery_AccurateState` | Kill mid-sync, verify per-file state is correct |

---

## 10. Design Decisions Summary

| Decision | Choice | Rationale |
|----------|--------|-----------|
| State backend | SQLite via `Microsoft.Data.Sqlite` | ACID, indexed, queryable, per-file granularity |
| Content hashing | None — size + timestamp only | Avoid CPU/IO overhead on large folders |
| State save granularity | Per-file (on each transfer/delete) | Survives partial syncs, no resurrection |
| Deletion tracking | Explicit `status='deleted'` | Distinguishes "deleted" from "never existed" |
| History | Full `file_versions` table | Audit trail for debugging |
| Session tracking | `sync_sessions` with NULL completion | Detects crashes and interrupted syncs |
| Migration | Auto-import binary state on first run | Smooth upgrade, no manual steps |
| Backward compat | Old `SyncState?` overload preserved | Existing tests still work |

---

## 11. Future Enhancements (Out of Scope)

- Content hashing as an opt-in flag (`--verify-content`)
- Database compaction (purge old versions older than N days)
- Dual-side state (both machines track state independently)
- Database replication between sync pairs
- Web UI for browsing sync history
