# RemoteFileSync — Deletion Sync Design Specification

**Date:** 2026-03-28
**Version:** 1.1
**Status:** Draft
**Parent Spec:** 2026-03-26-remote-file-sync-design.md
**Branch:** feature/deletion-sync
**Platform:** Windows 10 / Windows 11
**Runtime:** .NET 10, C#

---

## 1. Problem Statement

The current `SyncEngine` has no concept of file deletion. When a file is deleted on one side, the next sync detects it as "exists only on the other side" and copies it back — effectively resurrecting deleted files.

### 1.1 Target Scenarios

| Case | Condition | Expected Behavior |
|------|-----------|-------------------|
| Case 1 | File deleted on Side-A, **untouched** on Side-B (mod time ≤ last sync) | Propagate deletion: delete on Side-B |
| Case 2 | File deleted on Side-A, **modified** on Side-B (mod time > last sync) | Restore: copy from Side-B to Side-A |

### 1.2 Design Constraints

- Zero external NuGet dependencies (same as base project)
- Opt-in behavior via `--delete` CLI flag (safe by default)
- Backward-compatible with existing protocol (v1 servers reject gracefully)
- Deleted files are always backed up before removal (safety net)
- No behavior change for users who do not use `--delete`

---

## 2. Approach: Client-Side Manifest Snapshot

After each successful sync (exit code 0), the client saves a snapshot of what both sides looked like post-sync. On the next run with `--delete`, it compares current manifests against the snapshot to detect deletions.

### 2.1 Why This Approach

- The client already computes the sync plan — adding deletion detection fits naturally
- Single state file per sync pair, stored outside the sync folder
- No protocol changes needed for state tracking (state is client-local)
- Handles both uni-directional and bi-directional modes
- First run without state is safely additive (no deletions)

### 2.2 Alternatives Considered

| Approach | Verdict | Reason |
|----------|---------|--------|
| Dual-side state (both sides track) | Rejected | Over-engineered for a two-machine tool; server becomes stateful |
| Tombstone markers in manifest | Rejected | Bootstrapping problem — needs local state anyway to detect deletions |

---

## 3. State File

### 3.1 Storage Location

```
%LOCALAPPDATA%\RemoteFileSync\{pairId}\sync-state.bin
```

Where `pairId` = first 16 hex characters of `SHA256("{localFolderPath}|{remoteHost}:{port}")`, all lowercase and trimmed.

**Example:**
```
C:\Users\heung\AppData\Local\RemoteFileSync\a3b1c9f2e4d5678a\sync-state.bin
```

### 3.2 Binary Format

```
┌──────────────────┐
│ Magic "RFS1"     │  4 bytes (ASCII, version identifier)
├──────────────────┤
│ LastSyncUtc      │  int64 (ticks)
├──────────────────┤
│ EntryCount       │  int32
├──────────────────┤
│ Entry[0]         │
│  PathLength      │  int16
│  RelativePath    │  UTF-8 bytes
│  FileSize        │  int64
│  LastModUtc      │  int64 (ticks)
├──────────────────┤
│ Entry[1]...      │
└──────────────────┘
```

### 3.3 Lifecycle

| Event | Action |
|-------|--------|
| Sync succeeds (exit code 0) | Write state: merged manifest (union of all files on both sides post-sync) + current UTC timestamp |
| Sync partial (exit code 1) | Do NOT write state (inaccurate snapshot) |
| Sync fails (exit code 2, 3) | Do NOT write state |
| `--delete` active, state file exists | Load and use for deletion detection |
| `--delete` active, state file missing | First-run mode: fully additive, save state at end |
| `--delete` active, state file corrupted | Treat as missing: first-run mode, log warning |
| `--delete` NOT active | State file neither read nor written (zero overhead) |

### 3.4 Atomic Write Strategy

1. Write to `sync-state.bin.tmp`
2. `File.Move(tmp, target, overwrite: true)`
3. Prevents corruption if process is killed mid-write

---

## 4. Deletion Detection Algorithm

### 4.1 Overview

When `--delete` is enabled and a state file exists, the `SyncEngine` gains a new phase before the existing comparison logic.

### 4.2 Detection Steps

**Step 1 — Load previous snapshot** (merged manifest + `LastSyncUtc` from state file)

**Step 2 — Identify deletions:**

```
For each file in the snapshot:
  EXISTS in snapshot + MISSING from client manifest → Client deleted it
  EXISTS in snapshot + MISSING from server manifest → Server deleted it
```

**Step 3 — Apply Case 1 / Case 2 decision:**

| Deleted Side | Other Side Status | Condition | Action |
|-------------|-------------------|-----------|--------|
| Client deleted | Server has file, `modTime ≤ lastSyncUtc + 2s` | Untouched | `DeleteOnServer` |
| Client deleted | Server has file, `modTime > lastSyncUtc + 2s` | Modified after sync | `SendToClient` (restore) |
| Server deleted | Client has file, `modTime ≤ lastSyncUtc + 2s` | Untouched | `DeleteOnClient` |
| Server deleted | Client has file, `modTime > lastSyncUtc + 2s` | Modified after sync | `SendToServer` (restore) |
| Both deleted | Neither has file | — | No action |

**Step 4 — Uni-directional mode constraint:**

When `--delete` is active but `--bidirectional` is NOT:
- Client is source of truth
- Client deleted → `DeleteOnServer` (propagate)
- Server deleted → **ignored** (server is not authoritative)

**Step 5 — Files NOT in the snapshot** follow existing logic unchanged:
- `ClientOnly` → copy to server
- `ServerOnly` → copy to client (bidi) or ignore (uni)
- Both exist → conflict resolution (timestamp/size)

### 4.3 Timestamp Tolerance

The existing ±2 second tolerance applies to the `modTime ≤ lastSyncUtc` comparison:
- `modTime ≤ lastSyncUtc + 2 seconds` → "untouched" (Case 1)
- `modTime > lastSyncUtc + 2 seconds` → "modified" (Case 2)

This accounts for FAT32/NTFS granularity and minor clock drift, consistent with existing conflict resolution behavior.

---

## 5. New SyncAction Types

### 5.1 Updated Enum

```csharp
public enum SyncActionType : byte
{
    SendToServer   = 0,    // Client file is newer → push to server
    SendToClient   = 1,    // Server file is newer → pull to client
    ClientOnly     = 2,    // File exists only on client → copy to server
    ServerOnly     = 3,    // File exists only on server → copy to client (bidi)
    Skip           = 4,    // Files are identical → no action
    DeleteOnServer = 5,    // NEW: Propagate client-side deletion to server
    DeleteOnClient = 6     // NEW: Propagate server-side deletion to client
}
```

### 5.2 Action Summary Table

| Action | Trigger | Direction |
|--------|---------|-----------|
| `DeleteOnServer` | Client deleted file, server copy untouched | Client instructs server to delete |
| `DeleteOnClient` | Server deleted file, client copy untouched | Server instructs client to delete |

---

## 6. Protocol Changes

### 6.1 New Message Types

**DeleteFile (0x0A):**

```
┌──────────────────────────────────────────────────┐
│ MessageType 0x0A                                  │
│  Direction: Plan executor → file owner            │
│  Payload:                                         │
│    RelativePathLength  (int16)                    │
│    RelativePath        (UTF-8 bytes)              │
│    BackupFirst         (byte: 1=yes, 0=no)        │
└──────────────────────────────────────────────────┘
```

**DeleteConfirm (0x0B):**

```
┌──────────────────────────────────────────────────┐
│ MessageType 0x0B                                  │
│  Direction: File owner → plan executor            │
│  Payload:                                         │
│    RelativePathLength  (int16)                    │
│    RelativePath        (UTF-8 bytes)              │
│    Success             (byte: 1=deleted, 0=failed)│
└──────────────────────────────────────────────────┘
```

### 6.2 Updated MessageType Enum

```csharp
public enum MessageType : byte
{
    Handshake     = 0x01,
    HandshakeAck  = 0x02,
    Manifest      = 0x03,
    SyncPlan      = 0x04,
    FileStart     = 0x05,
    FileChunk     = 0x06,
    FileEnd       = 0x07,
    BackupConfirm = 0x08,
    SyncComplete  = 0x09,
    DeleteFile    = 0x0A,   // NEW
    DeleteConfirm = 0x0B,   // NEW
    Error         = 0xFF
}
```

### 6.3 Handshake Extension

The `SyncMode` byte in the Handshake payload expands:

| Value | Mode |
|-------|------|
| `0` | Uni-directional |
| `1` | Bi-directional |
| `2` | Uni-directional + delete |
| `3` | Bi-directional + delete |

A v1 server receiving mode `2` or `3` can reject with `HandshakeAck(status=Reject)` — forward-compatible.

### 6.4 Updated SyncComplete Payload

```
┌────────────────────┐
│ FilesTransferred   │  int32
│ BytesTransferred   │  int64
│ FilesDeleted       │  int32    ← NEW
│ ElapsedMs          │  int64
└────────────────────┘
```

---

## 7. Updated Protocol Flow

```
CLIENT                                       SERVER
  │                                            │
  │─── 0x01 Handshake (v1, bidi+delete) ─────>│
  │<── 0x02 HandshakeAck (OK) ────────────────│
  │                                            │
  │  [Client loads state file if --delete]     │
  │                                            │
  │─── 0x03 Manifest (client files) ─────────>│
  │<── 0x03 Manifest (server files) ──────────│
  │                                            │
  │  [Client computes sync plan]               │
  │  [Including deletion actions from state]   │
  │─── 0x04 SyncPlan ────────────────────────>│
  │                                            │
  │  === File Transfer Phase ===               │
  │─── 0x05/06/07 Files → Server ────────────>│
  │<── 0x08 BackupConfirm ───────────────────│
  │                                            │
  │  === Deletion Phase (Server) ===           │
  │─── 0x0A DeleteFile ──────────────────────>│
  │    [Server: backup → delete]               │
  │<── 0x0B DeleteConfirm ───────────────────│
  │                                            │
  │  === Bi-directional Transfer Phase ===     │
  │<── 0x05/06/07 Files → Client ────────────│
  │─── 0x08 BackupConfirm ──────────────────>│
  │                                            │
  │  === Deletion Phase (Client) ===           │
  │<── 0x0A DeleteFile ──────────────────────│
  │    [Client: backup → delete]               │
  │─── 0x0B DeleteConfirm ──────────────────>│
  │                                            │
  │  [Client saves state file]                 │
  │                                            │
  │─── 0x09 SyncComplete ───────────────────>│
  │<── 0x09 SyncComplete ──────────────────│
```

**Ordering guarantee:** File transfers complete before deletions execute. This ensures restored files (Case 2) arrive before any deletions run.

---

## 8. CLI Changes

### 8.1 New Option

| Option | Short | Required | Default | Description |
|--------|-------|----------|---------|-------------|
| `--delete` | `-d` | No | `false` | Enable deletion propagation (detects files deleted since last sync and propagates to the other side; requires state tracking) |

### 8.2 Updated SyncOptions Model

```csharp
public class SyncOptions
{
    // ... existing properties ...
    public bool DeleteEnabled { get; init; }   // NEW
}
```

### 8.3 Usage Examples

```bash
# Bi-directional sync WITH deletion propagation
RemoteFileSync.exe client -h 192.168.1.100 -p 15782 -f "C:\SyncFolder" -b -d

# Uni-directional sync WITH deletion (client is source of truth)
RemoteFileSync.exe client -h 192.168.1.100 -p 15782 -f "C:\SyncFolder" -d

# Bi-directional WITHOUT deletion (existing behavior, unchanged)
RemoteFileSync.exe client -h 192.168.1.100 -p 15782 -f "C:\SyncFolder" -b
```

### 8.4 Console Output Examples

**Default (quiet) with deletions:**
```
[07:30:01] Connecting to 192.168.1.100:15782...
[07:30:01] Connected. Bi-directional sync + delete.
[07:30:05] Sync complete: 8 files transferred, 2 deleted, 3 backed up, 45.2 MB total.
```

**Verbose with deletions:**
```
[07:30:03] Sync plan: 6 → server, 2 → client, 1 new, 2 delete, 141 skip
[07:30:04] [DEL→] docs/old-report.docx (deleted on client, untouched on server → delete)
[07:30:04] [backup] docs/old-report.docx → 20260328/docs/old-report.docx
[07:30:04] [←] data/updated.csv (deleted on client, modified on server → restore)
```

---

## 9. New Component: SyncStateManager

### 9.1 Location

```
src/RemoteFileSync/State/SyncStateManager.cs
```

### 9.2 Public API

```csharp
namespace RemoteFileSync.State;

public class SyncStateManager
{
    /// <summary>
    /// Loads the previous sync state for the given sync pair.
    /// Returns null if no state file exists or if the file is corrupted.
    /// </summary>
    public SyncState? LoadState(string localFolder, string remoteHost, int port);

    /// <summary>
    /// Saves the current sync state atomically (write to temp, then rename).
    /// Only call after a fully successful sync (exit code 0).
    /// </summary>
    public void SaveState(string localFolder, string remoteHost, int port,
                          FileManifest mergedManifest, DateTime syncUtc);

    /// <summary>
    /// Computes the state file path for a given sync pair.
    /// </summary>
    public string GetStatePath(string localFolder, string remoteHost, int port);
}

public record SyncState(FileManifest Manifest, DateTime LastSyncUtc);
```

### 9.3 Pair ID Computation

```csharp
// Input:  "C:\SyncFolder|192.168.1.100:15782"
// Output: "a3b1c9f2e4d5678a" (first 16 hex chars of SHA256)
public static string ComputePairId(string localFolder, string remoteHost, int port)
{
    var input = $"{localFolder.TrimEnd('\\', '/')}|{remoteHost}:{port}".ToLowerInvariant();
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
    return Convert.ToHexString(hash)[..16].ToLowerInvariant();
}
```

---

## 10. Error Handling

### 10.1 Deletion-Specific Errors

| Scenario | Behavior |
|----------|----------|
| Delete target file is locked | Skip deletion, log warning, continue sync |
| Backup before delete fails (disk full, permission) | Skip deletion, log error, file preserved |
| Delete succeeds but DeleteConfirm network failure | File already deleted; state NOT saved (sync incomplete) |
| State file missing with `--delete` active | First-run: fully additive, no deletions, state saved at end |
| State file corrupted (bad magic, truncated) | Treat as missing: first-run mode, log warning |
| File deleted on BOTH sides since last sync | Not in either manifest → no action |
| File renamed (old deleted, new created) | Old path: deletion propagated. New path: copied as new. Correct. |
| Clock skew between machines | ±2s tolerance mitigates small drift. Large skew: same limitation as existing conflict resolution. Use NTP. |

### 10.2 Exit Code and State Saving

| Exit Code | State Saved? | Reason |
|-----------|-------------|--------|
| 0 (Success) | Yes | All actions completed, state is accurate |
| 1 (Partial) | No | Some files skipped — state would be inaccurate |
| 2 (Connection failure) | No | Sync didn't complete |
| 3 (Fatal) | No | Unrecoverable error |

### 10.3 Backup Guarantee

Every file deleted via `--delete` is backed up first through the existing `BackupManager`:

```
{BackupFolder}/{yyyyMMdd}/{RelativeDirectory}/{FileName}
```

A deletion that fails backup also fails the delete — the file is always preserved until successfully backed up.

---

## 11. Affected Files

### 11.1 New Files

| File | Purpose |
|------|---------|
| `src/RemoteFileSync/State/SyncStateManager.cs` | State file read/write/path computation |
| `tests/RemoteFileSync.Tests/State/SyncStateManagerTests.cs` | Unit tests for state persistence |
| `tests/RemoteFileSync.Tests/Integration/DeleteSyncTests.cs` | E2E tests for deletion scenarios |

### 11.2 Modified Files

| File | Changes |
|------|---------|
| `src/RemoteFileSync/Models/SyncAction.cs` | Add `DeleteOnServer = 5`, `DeleteOnClient = 6` to enum |
| `src/RemoteFileSync/Models/SyncOptions.cs` | Add `bool DeleteEnabled` property |
| `src/RemoteFileSync/Network/MessageType.cs` | Add `DeleteFile = 0x0A`, `DeleteConfirm = 0x0B` |
| `src/RemoteFileSync/Network/ProtocolHandler.cs` | Serialize/deserialize DeleteFile and DeleteConfirm messages |
| `src/RemoteFileSync/Network/SyncServer.cs` | Handle DeleteFile messages (backup + delete + confirm), send DeleteFile for client-side deletions |
| `src/RemoteFileSync/Network/SyncClient.cs` | Send DeleteFile for server deletions, handle DeleteFile for client deletions, save state on success |
| `src/RemoteFileSync/Sync/SyncEngine.cs` | New overload accepting previous `SyncState`, deletion detection logic |
| `src/RemoteFileSync/Sync/ConflictResolver.cs` | New method for delete-vs-modify decision (Case 1 / Case 2) |
| `src/RemoteFileSync/Program.cs` | Parse `--delete` / `-d` flag |

### 11.3 Unchanged Files

| File | Reason |
|------|--------|
| `Backup/BackupManager.cs` | Existing backup logic reused as-is for pre-deletion backups |
| `Transfer/FileTransfer.cs` | No changes — file transfer logic unaffected |
| `Transfer/CompressionHelper.cs` | No changes |
| `Logging/SyncLogger.cs` | No changes — existing logging infrastructure sufficient |
| `Models/FileEntry.cs` | No changes |
| `Models/FileManifest.cs` | No changes |
| `Sync/FileScanner.cs` | No changes |

---

## 12. Testing Strategy

### 12.1 Unit Tests — SyncStateManager

| Test | Verifies |
|------|----------|
| `SaveAndLoad_RoundTrips` | Binary serialization integrity |
| `MissingFile_ReturnsNull` | First-run behavior |
| `CorruptedFile_ReturnsNull` | Graceful degradation (bad magic, truncated) |
| `AtomicWrite_TempFileCleanedUp` | No leftover `.tmp` files |
| `PairId_DeterministicAndCaseInsensitive` | Same input always produces same hash |
| `PairId_DifferentPairs_DifferentIds` | No collisions for different sync pairs |

### 12.2 Unit Tests — SyncEngine Deletion Logic

| Test | Verifies |
|------|----------|
| `DeletedOnClient_UntouchedOnServer_ProducesDeleteOnServer` | Case 1 |
| `DeletedOnClient_ModifiedOnServer_ProducesSendToClient` | Case 2 (restore) |
| `DeletedOnServer_UntouchedOnClient_ProducesDeleteOnClient` | Case 1 reverse |
| `DeletedOnServer_ModifiedOnClient_ProducesSendToServer` | Case 2 reverse |
| `BothDeleted_NoAction` | Both sides agree |
| `NoState_FullyAdditive` | First-run safety |
| `UniDirectional_OnlyClientDeletionsPropagate` | Uni-directional constraint |
| `UniDirectional_ServerDeletionsIgnored` | Server not authoritative in uni mode |
| `NewFileNotInSnapshot_NormalCopyBehavior` | Existing logic unchanged |
| `TimestampTolerance_WithinTwoSeconds_TreatedAsUntouched` | ±2s tolerance on lastSyncUtc |

### 12.3 Unit Tests — Protocol

| Test | Verifies |
|------|----------|
| `DeleteFile_SerializeDeserialize_RoundTrips` | Binary protocol for 0x0A |
| `DeleteConfirm_SerializeDeserialize_RoundTrips` | Binary protocol for 0x0B |
| `SyncPlan_WithDeleteActions_SerializesCorrectly` | New action types in plan |
| `SyncComplete_WithFilesDeleted_SerializesCorrectly` | Updated payload |

### 12.4 Integration Tests (E2E)

| Test | Scenario |
|------|----------|
| `DeleteSync_Case1_PropagatesDeletion` | Delete file on client, run sync with `--delete`, verify server copy deleted and backed up |
| `DeleteSync_Case2_RestoresModifiedFile` | Delete file on client, modify on server, run sync, verify client gets restored copy |
| `DeleteSync_BidiSymmetric` | Delete different files on each side, verify both propagate correctly |
| `DeleteSync_BackupBeforeDelete` | Verify deleted files appear in backup folder |
| `DeleteSync_FirstRun_NoState_AdditiveOnly` | First sync with `--delete` copies files, saves state, no deletions |
| `DeleteSync_SecondRun_DetectsDeletions` | Two sequential syncs — first establishes state, second detects deletions |
| `DeleteSync_UniDirectional_ServerDeletionIgnored` | Delete on server in uni mode, verify client copy preserved |

---

## 13. Design Decisions Summary

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Deletion opt-in | `--delete` flag, off by default | Safety first — no surprise deletions |
| State storage | `%LOCALAPPDATA%\RemoteFileSync\{pairId}\` | Keeps sync folder clean, per-pair isolation |
| State format | Binary with magic header "RFS1" | Compact, fast, consistent with protocol format |
| Detection method | Client-side manifest snapshot | Fits existing architecture (client computes plan) |
| Delete vs. restore decision | `modTime` compared against `lastSyncUtc ± 2s` | Deterministic, no human intervention |
| Backup before delete | Always | Safety net — every deletion is recoverable |
| State save condition | Exit code 0 only | Prevents inaccurate snapshots |
| Transfer before delete | File transfers first, deletions second | Case 2 restores arrive before any deletes execute |
| Handshake extension | SyncMode byte expanded (0-3) | Forward-compatible — old servers reject gracefully |

---

## 14. Future Enhancements (Out of Scope)

These are explicitly **not** part of this design:

- `--purge-state` flag to clean up orphaned state files
- Configurable backup retention for deleted files
- Dry-run mode for `--delete` (preview deletions without executing)
- Multi-client state sharing (server-side state)
- Deletion propagation depth limit (e.g., only propagate deletes within N days)
