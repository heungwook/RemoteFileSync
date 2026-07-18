# RemoteFileSync — Security & Sync-Correctness Remediation Plan

| | |
|---|---|
| **Date** | 2026-07-19 |
| **Branch** | `fix/security-and-sync-correctness` (from `main` @ `6e2c106`) |
| **Baseline** | Build clean (0 errors, 26 warnings), 181/181 tests pass |
| **Scope** | `src/RemoteFileSync`, `src/ExecRFS`, `tests/**` |
| **Findings addressed** | 62 verified + 12 hand-verified from an unverified batch of 51 |
| **Phases** | 9, each independently buildable, testable, and committable |

---

## 1. Executive summary

A two-pass multi-agent review of the solution surfaced **62 verified defects** (each independently confirmed by a refuter agent and a re-deriver agent, with a tiebreaker on disagreement) plus **51 further candidates** whose automated verification could not complete. Twelve of those 51 were hand-verified against source and are included here; the rest are carried as a backlog in [Appendix B](#appendix-b--unverified-backlog).

The build is green and every test passes. That is precisely the problem: the tests exercise components in isolation, while nearly every serious defect lives in the *interaction* between scan → plan → transfer → state-commit. No test performs two consecutive syncs, which is why the most severe bug in the codebase went unnoticed.

### The four compounding chains

| # | Chain | Root cause | Consequence |
|---|---|---|---|
| 1 | **Sync never converges** | `FileStart` omits mtime; receiver never calls `SetLastWriteTimeUtc` | Every transferred file is permanently "different"; bidirectional sync ping-pongs forever |
| 2 | **Backup destroys data** | `BackupManager` uses `File.Move`; backup folder defaults *into* the sync root | Failed transfer leaves no copy; backups re-enter the sync set and multiply |
| 3 | **Unauthenticated remote file access** | No auth + `Path.Combine(root, wirePath)` with no containment check | Arbitrary remote file write (RCE), read (exfiltration), and delete — in both directions |
| 4 | **Failed transfer recorded as success** | `BackupConfirm` success flag discarded at `SyncClient.cs:184` | Next run sees DB `exists` + file absent on peer → deletes the surviving good copy |

Chains 1 and 2 interlock: because nothing ever converges, every sync re-transfers every file, and every re-transfer moves the previous copy into a dated backup directory *inside the sync folder*. Disk usage grows without bound and the corruption propagates to the peer.

### Phase ordering rationale

Correctness precedes hardening because several security fixes touch the same call sites, and because a deletion-safety threshold (Phase 5) is worthless while Phase 1 is still marking every file as changed. Phases 1–4 are the release blockers; 5–9 are strongly recommended before any untrusted-network use.

| Phase | Title | Blocks release? |
|---|---|---|
| 0 | Baseline & branch | — |
| 1 | Timestamp propagation (convergence) | **Yes** |
| 2 | Backup safety | **Yes** |
| 3 | Atomic receive & transfer-commit correctness | **Yes** |
| 4 | Path containment & protocol hardening | **Yes** |
| 5 | Deletion safety & filter correctness | Recommended |
| 6 | Control flow: pause/stop, cancellation, lockstep | Recommended |
| 7 | CLI robustness & error visibility | Recommended |
| 8 | GUI process lifecycle & progress contract | Recommended |
| 9 | Regression test suite | Recommended |

---

## 2. Working conventions

### Branch

```bash
git checkout main
git pull
git checkout -b fix/security-and-sync-correctness
```

### Per-phase commit & push

Every phase ends with a green build, green tests, and a pushed commit. The first push sets upstream:

```bash
dotnet build RemoteFileSync.slnx
dotnet test  RemoteFileSync.slnx --no-build
git add -A
git commit -m "<phase message>"
git push -u origin fix/security-and-sync-correctness   # first push only; later pushes: git push
```

Do not proceed to the next phase on a red build. Each phase's "Verification" section lists the specific behaviour to confirm beyond the test suite.

### Protocol compatibility

Phase 1 changes the `FileStart` frame layout and Phase 4 adds validation. Both peers must run the same build. The handshake version is bumped `1 → 2` and the server now **rejects** mismatched versions rather than silently misparsing frames. This is a deliberate breaking change; mixed-version pairs would otherwise corrupt data.

---

## 3. Findings inventory

Severity reflects user impact. **V** = verified by two independent agents; **H** = hand-verified against source during plan authoring.

### Critical

| ID | File:Line | Finding | Phase | Status |
|---|---|---|---|---|
| C1 | `Transfer/FileTransfer.cs:107` | mtime never transmitted or preserved → sync never converges | 1 | V |
| C2 | `Transfer/FileTransfer.cs:103` | Path traversal on receive → arbitrary file write | 4 | V |
| C3 | `Transfer/FileTransfer.cs:18` | Path traversal on send → arbitrary file read | 4 | V |
| C4 | `Network/SyncServer.cs:174` | Path traversal on delete (server side) | 4 | V |
| C5 | `Network/SyncClient.cs:312` | Path traversal on delete (client side, malicious server) | 4 | V |

### High

| ID | File:Line | Finding | Phase | Status |
|---|---|---|---|---|
| H1 | `Backup/BackupManager.cs:38` | `File.Move` — "backup" removes the file | 2 | V |
| H2 | `Models/SyncOptions.cs:20` | Backup folder defaults to the sync folder | 2 | V |
| H3 | `Network/SyncClient.cs:184` | `BackupConfirm` success flag discarded → failed transfer marked synced | 3 | V |
| H4 | `Transfer/FileTransfer.cs:114` | Checksum mismatch deletes destination after overwrite | 3 | V |
| H5 | `Network/SyncServer.cs:36` | Unauthenticated listener on `IPAddress.Any` | 4 | V |
| H6 | `Network/ProtocolHandler.cs:23` | Unbounded wire-controlled allocation → OOM/crash | 4 | H |
| H7 | `Sync/SyncEngine.cs:143` | Empty/repointed server folder → mass `DeleteOnClient` | 5 | V |
| H8 | `Sync/FileScanner.cs:36` | Globs match filename only → path excludes silently no-op | 5 | V |
| H9 | `Network/SyncClient.cs:194` | Send-failure breaks protocol lockstep | 6 | V |
| H10 | `Network/SyncServer.cs:112` | Index-based backup vs self-describing transfers → wrong file moved | 6 | V |
| H11 | `Network/SyncServer.cs:116` | Uncaught `ReceiveFileAsync` exception tears down connection | 6 | H |
| H12 | `Program.cs:108` | `args[++i]` unbounded; parse exceptions escape handler | 7 | H |
| H13 | `Progress/JsonProgressWriter.cs` | `WriteError` is dead code → fatal errors totally silent | 7 | H |
| H14 | `Components/Shared/ProgressBar.razor:84` | Progress event names mismatch CLI → bar stuck at 0% | 8 | V+H |
| H15 | `Services/ProcessManager.cs:82` | Delayed kill captures field → kills restarted process | 8 | V |
| H16 | `Services/ProcessManager.cs:30` | `Start()` throws after `State=Starting` → UI wedged | 8 | V |

### Medium / Low (selected — full list in Appendix A)

| ID | File:Line | Finding | Phase | Status |
|---|---|---|---|---|
| M1 | `Progress/StdinCommandReader.cs:45` | STOP never releases `PauseGate` → hang | 6 | V |
| M2 | `Network/SyncClient.cs:174` | `PauseGate.Wait()` ignores cancellation → Ctrl+C hangs | 6 | H |
| M3 | `Network/SyncClient.cs:38` | Retry reuses a failed `TcpClient` → retries always fail | 6 | H |
| M4 | `Sync/FileScanner.cs:23` | Unreadable subdirectory aborts entire scan | 9→5 | H |
| M5 | `Sync/FileScanner.cs:29` | File vanishing mid-scan aborts entire scan | 5 | H |
| M6 | `Transfer/FileTransfer.cs:29` | `CompressFile` outside try/finally → temp leak | 3 | H |
| M7 | `Logging/SyncLogger.cs:19` | Log opened without sharing, outside try/catch → crash | 7 | H |
| M8 | `MainWindow.xaml.cs:29` | `AutoSave()` before `Dispose()` → orphaned children | 8 | H |
| M9 | `Services/ProcessManager.cs:70` | `State=Running` overwrites fast-exit state → UI wedged | 8 | H |
| M10 | `Network/ProtocolHandler.cs:60` | Unchecked `(short)` path-length cast | 4 | V |
| M11 | `Network/SyncServer.cs:43` | Single-accept server; stray connection kills it | 6 | H |
| M12 | `Sync/SyncEngine.cs:200` | Both-sides-deleted rows keep `status='exists'` forever | 5 | V |

---

## 4. Phase 0 — Baseline & branch

**Goal.** Establish a reproducible starting point and pin current behaviour before changing it.

```bash
cd E:/RemoteFileSync
git checkout main && git pull
git checkout -b fix/security-and-sync-correctness
dotnet build RemoteFileSync.slnx
dotnet test  RemoteFileSync.slnx --no-build
```

Expected: `0 Errors`, `Passed: 162` (RemoteFileSync.Tests) and `Passed: 19` (ExecRFS.Tests).

Save this plan to `Plans/` and commit it as the phase-0 artifact.

```bash
git add Plans/2026-07-19-security-and-sync-correctness-remediation-plan.md
git commit -m "docs: add security and sync-correctness remediation plan"
git push -u origin fix/security-and-sync-correctness
```

---

## 5. Phase 1 — Timestamp propagation (convergence)

> **Addresses:** C1. **Release blocker.** Nothing else in this plan matters while the sync cannot converge.

### Problem

`SerializeFileStart` carries `fileId`, path, size, compressed-flag, and block size — **no modification time**. `FileTransferReceiver.ReceiveFileAsync` never calls `File.SetLastWriteTimeUtc`, so the destination gets a wall-clock "now" timestamp. `ConflictResolver.Resolve` treats any mtime delta over 2 seconds as "changed", so every file that has ever been transferred is permanently out-of-sync. In bidirectional mode this ping-pongs indefinitely.

The timestamp data is *already on the wire* — `SerializeManifest` writes `entry.LastModifiedUtc.Ticks` at `ProtocolHandler.cs:63`. Only the per-file frame omits it.

### 1.1 — Add mtime to the `FileStart` frame

**File:** `src/RemoteFileSync/Network/ProtocolHandler.cs`

Add a protocol version constant near the top of the class:

```csharp
public static class ProtocolHandler
{
    /// <summary>
    /// Wire protocol version. v2 added lastModifiedUtcTicks to the FileStart frame.
    /// Peers running different versions are rejected during handshake.
    /// </summary>
    public const byte ProtocolVersion = 2;
```

Replace `SerializeFileStart` / `DeserializeFileStart` (lines 118–144):

```csharp
    public static byte[] SerializeFileStart(short fileId, string relativePath, long originalSize,
                                            bool isCompressed, int blockSize, long lastModifiedUtcTicks)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(fileId);
        WritePath(writer, relativePath);
        writer.Write(originalSize);
        writer.Write((byte)(isCompressed ? 1 : 0));
        writer.Write(blockSize);
        writer.Write(lastModifiedUtcTicks);
        writer.Flush();
        return ms.ToArray();
    }

    public static (short fileId, string relativePath, long originalSize, bool isCompressed,
                   int blockSize, long lastModifiedUtcTicks) DeserializeFileStart(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms, Encoding.UTF8);
        short fileId = reader.ReadInt16();
        string path = ReadPath(reader);
        long originalSize = reader.ReadInt64();
        bool isCompressed = reader.ReadByte() == 1;
        int blockSize = reader.ReadInt32();
        long lastModifiedUtcTicks = reader.ReadInt64();
        return (fileId, path, originalSize, isCompressed, blockSize, lastModifiedUtcTicks);
    }
```

`WritePath` / `ReadPath` are introduced in Phase 4 (§8.2). To keep Phase 1 self-contained and independently committable, use the inline form now and let Phase 4 replace it:

```csharp
        // Phase 1 inline form — replaced by WritePath/ReadPath in Phase 4
        var pathBytes = Encoding.UTF8.GetBytes(relativePath);
        writer.Write((short)pathBytes.Length);
        writer.Write(pathBytes);
```

### 1.2 — Send the source timestamp

**File:** `src/RemoteFileSync/Transfer/FileTransfer.cs`

`sourceInfo` is already captured at line 19. Pass its timestamp through:

```diff
             var sha256 = CompressionHelper.ComputeSha256(sourcePath);
-            var startPayload = ProtocolHandler.SerializeFileStart(fileId, relativePath, sourceInfo.Length, isCompressed: !alreadyCompressed, _blockSize);
+            var startPayload = ProtocolHandler.SerializeFileStart(
+                fileId, relativePath, sourceInfo.Length,
+                isCompressed: !alreadyCompressed, _blockSize,
+                lastModifiedUtcTicks: sourceInfo.LastWriteTimeUtc.Ticks);
             await ProtocolHandler.WriteMessageAsync(networkStream, MessageType.FileStart, startPayload, ct);
```

### 1.3 — Apply the timestamp on receive

Update the destructuring at line 82 and apply the timestamp. The full rewrite of this method lands in Phase 3 (§7.2, atomic staging); the minimal Phase-1 change is:

```diff
-        var (fileId, relativePath, originalSize, isCompressed, blockSize) = ProtocolHandler.DeserializeFileStart(startData);
+        var (fileId, relativePath, originalSize, isCompressed, blockSize, lastModifiedUtcTicks) =
+            ProtocolHandler.DeserializeFileStart(startData);
```

```diff
                         var actualHash = CompressionHelper.ComputeSha256(destPath);
                         if (!actualHash.SequenceEqual(expectedHash))
                         {
                             File.Delete(destPath);
                             return new FileReceiveResult(false, relativePath, "Checksum mismatch");
                         }
+                        // Preserve the source timestamp so the file compares equal on the next sync.
+                        File.SetLastWriteTimeUtc(destPath, new DateTime(lastModifiedUtcTicks, DateTimeKind.Utc));
                         return new FileReceiveResult(true, relativePath);
```

### 1.4 — Enforce version match at handshake

**File:** `src/RemoteFileSync/Network/SyncClient.cs`

```diff
-        byte syncMode = (byte)((_options.Bidirectional ? 1 : 0) | (_options.DeleteEnabled ? 2 : 0));
-        var hsPayload = ProtocolHandler.SerializeHandshake(1, syncMode);
+        byte syncMode = (byte)((_options.Bidirectional ? 1 : 0) | (_options.DeleteEnabled ? 2 : 0));
+        var hsPayload = ProtocolHandler.SerializeHandshake(ProtocolHandler.ProtocolVersion, syncMode);
```

```diff
         var (_, accepted) = ProtocolHandler.DeserializeHandshakeAck(ackData);
         if (!accepted)
         {
-            _logger.Error("Server rejected the connection.");
+            _logger.Error($"Server rejected the connection (protocol v{ProtocolHandler.ProtocolVersion} " +
+                           "not accepted — ensure both sides run the same build).");
             return 2;
         }
```

**File:** `src/RemoteFileSync/Network/SyncServer.cs`

```diff
         var (version, syncMode) = ProtocolHandler.DeserializeHandshake(hsData);
         bool bidirectional = (syncMode & 1) != 0;
         bool deleteEnabled = (syncMode & 2) != 0;
-        _logger.Info($"Handshake: v{version}, {(bidirectional ? "bidirectional" : "unidirectional")}");
-
-        // 2. Send HandshakeAck
-        var ackPayload = ProtocolHandler.SerializeHandshakeAck(1, accepted: true);
-        await ProtocolHandler.WriteMessageAsync(stream, MessageType.HandshakeAck, ackPayload, ct);
+        _logger.Info($"Handshake: v{version}, {(bidirectional ? "bidirectional" : "unidirectional")}");
+
+        // 2. Send HandshakeAck — reject version mismatches rather than misparse frames.
+        bool versionOk = version == ProtocolHandler.ProtocolVersion;
+        var ackPayload = ProtocolHandler.SerializeHandshakeAck(ProtocolHandler.ProtocolVersion, accepted: versionOk);
+        await ProtocolHandler.WriteMessageAsync(stream, MessageType.HandshakeAck, ackPayload, ct);
+        if (!versionOk)
+        {
+            _logger.Error($"Rejected client: protocol v{version}, this build speaks v{ProtocolHandler.ProtocolVersion}.");
+            return 3;
+        }
```

### 1.5 — Update existing tests

`tests/RemoteFileSync.Tests/Network/ProtocolHandlerTests.cs` constructs `FileStart` frames. Update every call site to supply the new argument, e.g.:

```diff
-        var payload = ProtocolHandler.SerializeFileStart(1, "a/b.txt", 1234, true, 65536);
-        var (id, path, size, compressed, block) = ProtocolHandler.DeserializeFileStart(payload);
+        var ticks = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;
+        var payload = ProtocolHandler.SerializeFileStart(1, "a/b.txt", 1234, true, 65536, ticks);
+        var (id, path, size, compressed, block, mtime) = ProtocolHandler.DeserializeFileStart(payload);
+        Assert.Equal(ticks, mtime);
```

### Tests to add

`tests/RemoteFileSync.Tests/Transfer/FileTransferTests.cs`:

```csharp
[Fact]
public async Task ReceiveFile_PreservesSourceTimestamp()
{
    // Round-trip a file through sender+receiver over a MemoryStream pair and assert
    // File.GetLastWriteTimeUtc(dest) == source mtime (within filesystem granularity).
}
```

`tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs` — **the highest-value test in this plan**:

```csharp
[Fact]
public async Task SecondSync_IsANoOp_WhenNothingChanged()
{
    // Run a full bidirectional sync twice against the same folders.
    // Assert the second run's plan contains zero transfers and only Skip actions.
    // Without Phase 1 this fails: every file re-transfers forever.
}
```

### Verification

- `SecondSync_IsANoOp_WhenNothingChanged` passes.
- Manual: sync a folder twice; the second run logs `0 transfers`.
- A v1 client against a v2 server is cleanly rejected with a readable message, not a corrupt transfer.

### Commit

```bash
git add -A
git commit -m "fix(protocol): transmit and preserve file mtime so sync converges

FileStart now carries lastModifiedUtcTicks and the receiver applies it via
SetLastWriteTimeUtc. Without this every transferred file compared as changed
forever and bidirectional sync ping-ponged indefinitely.

Protocol version bumped 1 -> 2; the server now rejects mismatched peers
instead of misparsing frames.

Fixes: C1"
git push
```

---

## 6. Phase 2 — Backup safety

> **Addresses:** H1, H2. **Release blocker.**

### Problem

`BackupManager.BackupFile` calls `File.Move` (line 38) — backing a file up **removes it** from the sync folder. Two distinct call sites rely on this method with incompatible needs:

| Call site | Intent | Correct semantic |
|---|---|---|
| `SyncClient.cs:244`, `SyncServer.cs:112` | Snapshot before overwriting with an incoming file | **Copy** (leave the original until the new one is committed) |
| `SyncClient.cs:297`, `SyncServer.cs:160` | Preserve a file before propagating a deletion | **Copy then delete** |

Both currently get "move", so a failed receive leaves neither the original nor a complete replacement.

Separately, `SyncOptions.EffectiveBackupFolder` (line 20) defaults to `Folder` — backups land *inside the synced tree*, are re-scanned as new files, and propagate to the peer.

### 2.1 — Split the two semantics

**File:** `src/RemoteFileSync/Backup/BackupManager.cs`

Replace the class body:

```csharp
namespace RemoteFileSync.Backup;

public sealed class BackupManager
{
    private readonly string _syncFolder;
    private readonly string _backupFolder;
    private readonly object _lock = new();

    public BackupManager(string syncFolder, string backupFolder)
    {
        _syncFolder = Path.GetFullPath(syncFolder);
        _backupFolder = Path.GetFullPath(backupFolder);
    }

    /// <summary>
    /// Copies the file into the dated backup tree, leaving the original in place.
    /// Use before overwriting a file with an incoming transfer.
    /// </summary>
    public bool BackupFile(string relativePath) => Snapshot(relativePath, removeOriginal: false);

    /// <summary>
    /// Copies the file into the dated backup tree, then deletes the original.
    /// Use when propagating a deletion.
    /// </summary>
    public bool BackupAndRemove(string relativePath) => Snapshot(relativePath, removeOriginal: true);

    private bool Snapshot(string relativePath, bool removeOriginal)
    {
        var sourcePath = Path.Combine(_syncFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(sourcePath)) return false;

        lock (_lock)
        {
            var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
            var backupDir = Path.Combine(_backupFolder, dateStr,
                Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? "");
            Directory.CreateDirectory(backupDir);

            var fileName = Path.GetFileNameWithoutExtension(relativePath);
            var ext = Path.GetExtension(relativePath);
            var destPath = Path.Combine(backupDir, Path.GetFileName(relativePath));

            int suffix = 1;
            while (File.Exists(destPath))
            {
                destPath = Path.Combine(backupDir, $"{fileName}_{suffix}{ext}");
                suffix++;
            }

            // Copy first: if the copy fails we must not have destroyed the original.
            File.Copy(sourcePath, destPath, overwrite: false);
            if (removeOriginal) File.Delete(sourcePath);
            return true;
        }
    }
}
```

> **Note on path safety.** `Snapshot` still combines an untrusted `relativePath` into `_syncFolder`. Phase 4 routes every caller through `PathGuard`; this phase deliberately changes only the copy/move semantics so the two concerns stay reviewable in isolation.

### 2.2 — Point deletion call sites at the new method

**File:** `src/RemoteFileSync/Network/SyncClient.cs` (line ~297)

```diff
                     if (backupFirst)
                     {
-                        if (backup.BackupFile(path))
+                        if (backup.BackupAndRemove(path))
```

**File:** `src/RemoteFileSync/Network/SyncServer.cs` (line ~160)

```diff
                     if (backupFirst)
                     {
-                        if (backup.BackupFile(path))
+                        if (backup.BackupAndRemove(path))
```

The pre-overwrite sites (`SyncClient.cs:244`, `SyncServer.cs:112`) keep calling `BackupFile` and now get copy semantics — no edit required, but re-read them to confirm intent.

### 2.3 — Move backups out of the sync root by default

**File:** `src/RemoteFileSync/Models/SyncOptions.cs`

```diff
-    public string EffectiveBackupFolder => BackupFolder ?? Folder;
+    /// <summary>
+    /// Backup destination. Defaults to a sibling ".rfs-backups" directory OUTSIDE the sync
+    /// folder — placing backups inside the synced tree makes them re-scan as new files and
+    /// propagate to the peer, growing without bound.
+    /// </summary>
+    public string EffectiveBackupFolder =>
+        BackupFolder ?? Path.Combine(
+            Path.GetDirectoryName(Path.GetFullPath(Folder).TrimEnd(Path.DirectorySeparatorChar))
+                ?? Path.GetFullPath(Folder),
+            $".rfs-backups-{Path.GetFileName(Path.GetFullPath(Folder).TrimEnd(Path.DirectorySeparatorChar))}");
```

Add a guard to `Validate()` so an explicit `--backup-folder` inside the sync root is rejected:

```csharp
        var syncFull = Path.GetFullPath(Folder);
        if (!syncFull.EndsWith(Path.DirectorySeparatorChar)) syncFull += Path.DirectorySeparatorChar;
        var backupFull = Path.GetFullPath(EffectiveBackupFolder);
        if (backupFull.StartsWith(syncFull, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"--backup-folder must be outside the sync folder (got '{backupFull}' inside '{syncFull}'). " +
                "Backups inside the sync folder are re-synced to the peer and grow without bound.");
```

### 2.4 — Fix the misleading GUI label

**File:** `src/ExecRFS/Components/Panels/ClientPanel.razor` (line ~39)

```diff
-           placeholder="Leave empty to disable backups" />
+           placeholder="Leave empty for default (.rfs-backups-&lt;folder&gt;, beside the sync folder)" />
```

### Tests to add

```csharp
[Fact] public void BackupFile_LeavesOriginalInPlace()          // copy semantics
[Fact] public void BackupAndRemove_DeletesOriginalAfterCopy()  // move semantics
[Fact] public void Validate_RejectsBackupFolderInsideSyncFolder()
[Fact] public void EffectiveBackupFolder_DefaultsOutsideSyncFolder()
```

Existing `BackupManagerTests.cs` asserts move semantics for `BackupFile` — update those assertions to the new contract.

### Verification

- After a sync, the sync folder contains no `yyyyMMdd/` backup directories.
- Interrupting a transfer mid-flight leaves the original file intact.

### Commit

```bash
git add -A
git commit -m "fix(backup): copy instead of move, and default backups outside the sync root

BackupFile now copies (pre-overwrite snapshot); BackupAndRemove copies then
deletes (deletion propagation). Previously both moved, so a failed receive
left neither the original nor a complete replacement.

EffectiveBackupFolder no longer defaults to the sync folder itself, which
caused backups to re-scan as new files and propagate to the peer.

Fixes: H1, H2"
git push
```

---

## 7. Phase 3 — Atomic receive & transfer-commit correctness

> **Addresses:** H3, H4, M6. **Release blocker.**

### Problem

Three defects converge on the receive path:

1. **H4** — the receiver decompresses *directly onto the destination* (`FileTransfer.cs:107`), then deletes it on checksum mismatch (line 114). A pre-existing good file is destroyed by a failed transfer, and a crash mid-decompress leaves a truncated file where a valid one was.
2. **H3** — `SyncClient.cs:184` reads `BackupConfirm` as `var (cType, _)`, discarding the success flag the server deliberately sends (`SyncServer.cs:134`), then calls `MarkSynced` unconditionally. A file the peer failed to write is recorded as synced; the next run resolves it to `DeleteOnClient` and deletes the surviving copy.
3. **M6** — `CompressFile` (line 29) sits *outside* the `try`/`finally` that deletes its temp file, leaking a partial temp on failure.

### 3.1 — Stage, verify, then atomically commit

**File:** `src/RemoteFileSync/Transfer/FileTransfer.cs`

Replace `ReceiveFileAsync` (lines 76–130) entirely:

```csharp
    public async Task<FileReceiveResult> ReceiveFileAsync(Stream networkStream, CancellationToken ct)
    {
        var (startType, startData) = await ProtocolHandler.ReadMessageAsync(networkStream, ct);
        if (startType != MessageType.FileStart)
            return new FileReceiveResult(false, "", $"Expected FileStart, got {startType}");

        var (fileId, relativePath, originalSize, isCompressed, blockSize, lastModifiedUtcTicks) =
            ProtocolHandler.DeserializeFileStart(startData);

        // Phase 4 replaces this with PathGuard.TryResolveWithinRoot.
        var destPath = Path.Combine(_rootFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));

        var tempPath = Path.Combine(Path.GetTempPath(), $"rfs_recv_{Guid.NewGuid()}.tmp");
        // Staging file lives beside the destination so the final commit is a same-volume move.
        var stagingPath = destPath + $".rfs-part-{Guid.NewGuid():N}";

        try
        {
            using (var tempStream = File.Create(tempPath))
            {
                while (true)
                {
                    var (msgType, msgData) = await ProtocolHandler.ReadMessageAsync(networkStream, ct);
                    if (msgType == MessageType.FileChunk)
                    {
                        var (_, _, chunkData) = ProtocolHandler.DeserializeFileChunk(msgData);
                        await tempStream.WriteAsync(chunkData, ct);
                    }
                    else if (msgType == MessageType.FileEnd)
                    {
                        var (_, expectedHash) = ProtocolHandler.DeserializeFileEnd(msgData);
                        await tempStream.FlushAsync(ct);
                        tempStream.Close();

                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

                        // Materialise into staging — the destination is untouched until we commit.
                        if (isCompressed)
                            CompressionHelper.DecompressFile(tempPath, stagingPath);
                        else
                            File.Copy(tempPath, stagingPath, overwrite: true);

                        var actualHash = CompressionHelper.ComputeSha256(stagingPath);
                        if (!actualHash.SequenceEqual(expectedHash))
                        {
                            // Destination is still the previous good file. Nothing is destroyed.
                            return new FileReceiveResult(false, relativePath, "Checksum mismatch");
                        }

                        // Stamp the source timestamp before committing; File.Move preserves it.
                        File.SetLastWriteTimeUtc(stagingPath, new DateTime(lastModifiedUtcTicks, DateTimeKind.Utc));
                        File.Move(stagingPath, destPath, overwrite: true);
                        return new FileReceiveResult(true, relativePath);
                    }
                    else
                    {
                        return new FileReceiveResult(false, relativePath, $"Unexpected message type: {msgType}");
                    }
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            if (File.Exists(stagingPath)) File.Delete(stagingPath);
        }
    }
```

Key properties: the destination is only ever replaced by a **checksum-verified** file, via a single `File.Move` on the same volume. A crash at any point leaves either the old file or the old file plus an orphaned `.rfs-part-*` — never a truncated destination.

> Add `*.rfs-part-*` to the scanner's implicit exclusions (Phase 5, §9.2) so staging files are never picked up by a concurrent scan.

### 3.2 — Close the temp-file leak on the send path

Move the compression step inside the `try`:

```diff
         string transferSource;
         string? tempCompressed = null;
 
-        if (!alreadyCompressed)
-        {
-            tempCompressed = Path.Combine(Path.GetTempPath(), $"rfs_gz_{Guid.NewGuid()}.tmp");
-            CompressionHelper.CompressFile(sourcePath, tempCompressed);
-            transferSource = tempCompressed;
-        }
-        else
-        {
-            transferSource = sourcePath;
-        }
-
         try
         {
+            if (!alreadyCompressed)
+            {
+                tempCompressed = Path.Combine(Path.GetTempPath(), $"rfs_gz_{Guid.NewGuid()}.tmp");
+                CompressionHelper.CompressFile(sourcePath, tempCompressed);
+                transferSource = tempCompressed;
+            }
+            else
+            {
+                transferSource = sourcePath;
+            }
+
             var sha256 = CompressionHelper.ComputeSha256(sourcePath);
```

(Declare `string transferSource;` before the `try` and assign inside, or simply declare it `string transferSource = sourcePath;` and reassign.)

### 3.3 — Honour the `BackupConfirm` success flag

**File:** `src/RemoteFileSync/Network/SyncClient.cs`, send loop (lines ~176–199):

```diff
             try
             {
                 short fileId = (short)(filesTransferred % short.MaxValue);
                 await sender.SendFileAsync(stream, fileId, action.RelativePath, ct);
-                _logger.Info($"[→] {action.RelativePath}");
-                filesTransferred++;
-                var fi = new FileInfo(Path.Combine(_options.Folder, action.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
-                bytesTransferred += fi.Length;
-                var (cType, _) = await ProtocolHandler.ReadMessageAsync(stream, ct);
-                if (cType != MessageType.BackupConfirm)
-                    _logger.Warning($"Expected BackupConfirm, got {cType}");
-                _progress.WriteFileEnd(action.RelativePath, success: true, thread: 0);
-                if (_db != null)
-                {
-                    var sfi = new FileInfo(Path.Combine(_options.Folder, action.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
-                    _db.MarkSynced(action.RelativePath, sfi.Length, sfi.LastWriteTimeUtc, sessionId, "to_server");
-                }
+
+                var (cType, cData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
+                if (cType != MessageType.BackupConfirm)
+                {
+                    _logger.Error($"Expected BackupConfirm for {action.RelativePath}, got {cType}. Aborting sync.");
+                    throw new InvalidDataException($"Protocol desync: expected BackupConfirm, got {cType}");
+                }
+
+                // The peer reports whether it actually committed the file. Trusting the
+                // transfer alone caused failed writes to be recorded as synced, which the
+                // next run then resolved to a deletion of the surviving local copy.
+                bool peerCommitted = cData.Length > 0 && cData[^1] == 1;
+                if (!peerCommitted)
+                {
+                    _logger.Error($"Peer failed to commit {action.RelativePath}; not recording as synced.");
+                    skippedFiles++;
+                    _progress.WriteFileEnd(action.RelativePath, success: false, error: "peer rejected file");
+                    continue;
+                }
+
+                _logger.Info($"[→] {action.RelativePath}");
+                filesTransferred++;
+                var sfi = new FileInfo(Path.Combine(_options.Folder,
+                    action.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
+                bytesTransferred += sfi.Length;
+                _progress.WriteFileEnd(action.RelativePath, success: true, thread: 0);
+                _db?.MarkSynced(action.RelativePath, sfi.Length, sfi.LastWriteTimeUtc, sessionId, "to_server");
             }
```

Apply the symmetric change to the server's send loop (`SyncServer.cs:216`), which discards the flag the same way.

### Tests to add

```csharp
[Fact] public async Task ChecksumMismatch_LeavesExistingDestinationUntouched()
[Fact] public async Task FailedPeerCommit_IsNotRecordedAsSynced()
[Fact] public void CompressFileFailure_LeavesNoTempFile()
```

### Verification

- Corrupt a chunk in transit → destination retains its previous content, and no `.rfs-part-*` remains.
- `%TEMP%` accumulates no `rfs_gz_*.tmp` after a failing send.

### Commit

```bash
git add -A
git commit -m "fix(transfer): stage-verify-commit receives and honour BackupConfirm

Receives now materialise into a staging file beside the destination, verify
the checksum there, and commit with a single same-volume File.Move. A failed
or interrupted transfer can no longer destroy the existing good file.

The client and server now read the BackupConfirm success flag instead of
discarding it; a peer-side write failure previously got recorded as synced
and the next run deleted the surviving local copy.

Also moves CompressFile inside the try/finally that cleans up its temp file.

Fixes: H3, H4, M6"
git push
```

---

## 8. Phase 4 — Path containment & protocol hardening

> **Addresses:** C2, C3, C4, C5, H5, H6, M10. **Release blocker.**

### Problem

`SyncServer` binds `IPAddress.Any` and unconditionally accepts every handshake — anyone who can reach the port is a trusted peer. Every wire-supplied relative path is then fed to `Path.Combine(root, path)` with no containment check. `Path.Combine` does not neutralise `..`, and an absolute second argument discards the root entirely. Four sinks are affected: receive-write, send-read, server-delete, client-delete. The SHA-256 check is no defence — the attacker supplies the expected hash too.

Separately, `ReadMessageAsync` allocates `new byte[length]` from a **signed** wire-controlled Int32 with no bound (H6), and several `(short)` path-length casts are unchecked (M10).

### 4.1 — Add a path containment guard

**New file:** `src/RemoteFileSync/Security/PathGuard.cs`

```csharp
namespace RemoteFileSync.Security;

/// <summary>
/// Validates that a peer-supplied relative path resolves inside the sync root.
/// Every path that arrives over the network must pass through here before it
/// reaches the filesystem — Path.Combine does not neutralise "..", and a rooted
/// second argument silently discards the root.
/// </summary>
public static class PathGuard
{
    public static bool TryResolveWithinRoot(string root, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        if (relativePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;

        // Reject anything drive-qualified, UNC, or otherwise rooted, plus NTFS
        // alternate-data-stream syntax ("file.txt:hidden").
        if (Path.IsPathRooted(relativePath)) return false;
        if (relativePath.Contains(':')) return false;

        var rootFull = Path.GetFullPath(root);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
            rootFull += Path.DirectorySeparatorChar;

        string combined;
        try
        {
            combined = Path.GetFullPath(
                Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // Trailing separator on rootFull means the root itself is also rejected.
        if (!combined.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return false;

        fullPath = combined;
        return true;
    }

    public static string ResolveWithinRoot(string root, string relativePath) =>
        TryResolveWithinRoot(root, relativePath, out var full)
            ? full
            : throw new UnauthorizedAccessException(
                $"Rejected path outside sync root: '{relativePath}'");
}
```

### 4.2 — Route all four sinks through the guard

**`src/RemoteFileSync/Transfer/FileTransfer.cs`** — send (line 18):

```diff
-        var sourcePath = Path.Combine(_rootFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
+        var sourcePath = PathGuard.ResolveWithinRoot(_rootFolder, relativePath);
```

— receive (the `destPath` line introduced in §7.1):

```diff
-        var destPath = Path.Combine(_rootFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
+        if (!PathGuard.TryResolveWithinRoot(_rootFolder, relativePath, out var destPath))
+            return new FileReceiveResult(false, relativePath, "Rejected path outside sync root");
```

**`src/RemoteFileSync/Network/SyncServer.cs`** — delete (line ~174):

```diff
                     else
                     {
-                        var fullPath = Path.Combine(_options.Folder, path.Replace('/', Path.DirectorySeparatorChar));
-                        if (File.Exists(fullPath))
+                        if (!PathGuard.TryResolveWithinRoot(_options.Folder, path, out var fullPath))
+                        {
+                            _logger.Error($"Rejected delete for path outside sync root: {path}");
+                            skippedFiles++;
+                        }
+                        else if (File.Exists(fullPath))
```

Apply the identical change to `src/RemoteFileSync/Network/SyncClient.cs` (line ~312).

**`src/RemoteFileSync/Backup/BackupManager.cs`** — `Snapshot`:

```diff
-        var sourcePath = Path.Combine(_syncFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
-        if (!File.Exists(sourcePath)) return false;
+        if (!PathGuard.TryResolveWithinRoot(_syncFolder, relativePath, out var sourcePath)) return false;
+        if (!File.Exists(sourcePath)) return false;
```

Add `using RemoteFileSync.Security;` to each edited file.

### 4.3 — Bound the message length

**File:** `src/RemoteFileSync/Network/ProtocolHandler.cs`

```diff
 public static class ProtocolHandler
 {
     public const byte ProtocolVersion = 2;
+
+    /// <summary>Upper bound on a single frame. Guards against a hostile length prefix.</summary>
+    public const int MaxMessageBytes = 64 * 1024 * 1024;
```

```diff
         var type = (MessageType)header[0];
         var length = BitConverter.ToInt32(header, 1);
+        if (length < 0 || length > MaxMessageBytes)
+            throw new InvalidDataException(
+                $"Invalid message length {length} (allowed 0..{MaxMessageBytes}).");
         var payload = new byte[length];
```

### 4.4 — Checked path helpers

Add to `ProtocolHandler`:

```csharp
    private static void WritePath(BinaryWriter writer, string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        if (bytes.Length > short.MaxValue)
            throw new InvalidDataException(
                $"Path exceeds {short.MaxValue} UTF-8 bytes and cannot be framed: {path}");
        writer.Write((short)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadPath(BinaryReader reader)
    {
        short len = reader.ReadInt16();
        if (len < 0) throw new InvalidDataException($"Negative path length: {len}");
        var bytes = reader.ReadBytes(len);
        if (bytes.Length != len) throw new InvalidDataException("Truncated path in frame.");
        return Encoding.UTF8.GetString(bytes);
    }
```

Replace all six unchecked cast sites with these helpers: `SerializeManifest`/`DeserializeManifest`, `SerializeSyncPlan`/`DeserializeSyncPlan`, `SerializeFileStart`/`DeserializeFileStart`, `SerializeDeleteFile`/`DeserializeDeleteFile`, `SerializeDeleteConfirm`/`DeserializeDeleteConfirm`.

### 4.5 — Bind to loopback by default; require opt-in to expose

**File:** `src/RemoteFileSync/Models/SyncOptions.cs`

```csharp
    /// <summary>
    /// Interface to bind the server to. Defaults to loopback: this protocol has no
    /// authentication, so exposing it on all interfaces grants anyone who can reach
    /// the port arbitrary read/write/delete within the sync folder.
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";
```

**File:** `src/RemoteFileSync/Program.cs` — add to `ParseArgs`:

```csharp
                case "--bind":
                    options.BindAddress = NextValue(args, ref i, "--bind");
                    break;
```

**File:** `src/RemoteFileSync/Network/SyncServer.cs`

```diff
-        var listener = new TcpListener(IPAddress.Any, _options.Port);
+        if (!IPAddress.TryParse(_options.BindAddress, out var bindIp))
+            throw new ArgumentException($"Invalid --bind address: {_options.BindAddress}");
+        var listener = new TcpListener(bindIp, _options.Port);
         listener.Start();
-        _logger.Summary($"Listening on port {_options.Port}...");
+        _logger.Summary($"Listening on {bindIp}:{_options.Port}...");
+        if (!IPAddress.IsLoopback(bindIp))
+            _logger.Warning(
+                "This server is reachable from the network and has NO AUTHENTICATION. " +
+                "Any peer can read, write, and delete within the sync folder. " +
+                "Use only on a trusted network or over a VPN/SSH tunnel.");
```

Update `PrintUsage()` accordingly.

> **Residual risk — read this.** Path containment and a loopback default reduce blast radius but **do not make this protocol safe on an untrusted network.** There is still no authentication, no encryption, and no integrity protection on the channel. Anyone permitted to connect retains full read/write/delete within the sync folder, and all traffic is cleartext. Real authentication (pre-shared key at handshake, minimum) and TLS are follow-up work tracked in [Appendix C](#appendix-c--follow-up-work-not-in-this-plan); they are out of scope here because they warrant their own design review.

### Tests to add

```csharp
[Theory]
[InlineData("../escape.txt")]
[InlineData("..\\..\\escape.txt")]
[InlineData("sub/../../escape.txt")]
[InlineData("C:\\Windows\\System32\\evil.dll")]
[InlineData("\\\\server\\share\\evil.dll")]
[InlineData("file.txt:stream")]
[InlineData("")]
public void PathGuard_RejectsEscapes(string candidate)
    => Assert.False(PathGuard.TryResolveWithinRoot(@"C:\sync", candidate, out _));

[Theory]
[InlineData("a.txt")]
[InlineData("sub/dir/a.txt")]
[InlineData("sub/../a.txt")]     // normalises to a.txt — still inside
public void PathGuard_AcceptsPathsInsideRoot(string candidate)
    => Assert.True(PathGuard.TryResolveWithinRoot(@"C:\sync", candidate, out _));

[Fact] public void ReadMessage_RejectsNegativeLength();
[Fact] public void ReadMessage_RejectsOversizedLength();
[Fact] public void SerializePath_ThrowsOnOversizedPath();
[Fact] public async Task ReceiveFile_RejectsTraversalPath();
```

### Verification

- A crafted `FileStart` with `../../evil.txt` returns "Rejected path outside sync root" and writes nothing outside the root.
- A 5-byte frame declaring length `0x7FFFFFFF` raises `InvalidDataException` rather than exhausting memory.
- Server started without `--bind` is not reachable from another machine.

### Commit

```bash
git add -A
git commit -m "fix(security): contain wire paths, bound frame sizes, default to loopback

Adds PathGuard and routes all four network-supplied path sinks through it
(receive-write, send-read, server-delete, client-delete). Path.Combine does
not neutralise '..' and a rooted argument discards the root, so a peer could
previously read, write, or delete anywhere on disk.

Bounds the wire-controlled message length (was an unvalidated signed Int32
feeding a byte[] allocation) and replaces unchecked (short) path-length casts
with checked helpers.

Server now binds loopback by default; --bind is required to expose it, and
doing so logs an explicit no-authentication warning.

Fixes: C2, C3, C4, C5, H5, H6, M10"
git push
```

---

## 9. Phase 5 — Deletion safety & filter correctness

> **Addresses:** H7, H8, M4, M5, M12.

### 9.1 — Deletion sanity threshold

The single most valuable safety net in this plan: a bounded blast radius for every remaining deletion-logic defect.

**File:** `src/RemoteFileSync/Models/SyncOptions.cs`

```csharp
    /// <summary>
    /// Abort the sync if deletions would exceed this percentage of tracked files.
    /// Guards against an empty or repointed peer folder wiping the other side.
    /// Set to 100 (or pass --force-delete) to disable.
    /// </summary>
    public int MaxDeletePercent { get; set; } = 25;

    public bool ForceDelete { get; set; }
```

`Program.ParseArgs`:

```csharp
                case "--max-delete-percent":
                    options.MaxDeletePercent = NextInt(args, ref i, "--max-delete-percent");
                    break;
                case "--force-delete":
                    options.ForceDelete = true;
                    break;
```

**File:** `src/RemoteFileSync/Network/SyncClient.cs`, immediately after the plan is computed and counted (after line ~140) and **before** the plan is sent to the peer:

```csharp
        // Deletion blast-radius guard. A repointed or empty peer folder makes every tracked
        // file look deleted; without this, one misconfigured run wipes the other side.
        if (_options.DeleteEnabled && deleteCount > 0 && !_options.ForceDelete)
        {
            int tracked = Math.Max(clientManifest.Count, serverManifest.Count);
            if (tracked > 0)
            {
                double pct = deleteCount * 100.0 / tracked;
                if (pct > _options.MaxDeletePercent)
                {
                    var msg = $"Refusing to sync: {deleteCount} of {tracked} files " +
                              $"({pct:F0}%) would be deleted, exceeding --max-delete-percent " +
                              $"{_options.MaxDeletePercent}. If this is intentional, re-run with --force-delete.";
                    _logger.Error(msg);
                    _progress.WriteError(msg, fatal: true);
                    return 4;
                }
            }
        }
```

Exit code `4` is new and means "aborted by a safety guard"; document it in `PrintUsage()`.

### 9.2 — Match globs against the relative path, not just the filename

**File:** `src/RemoteFileSync/Sync/FileScanner.cs`

```csharp
    private static readonly string[] AlwaysExclude = { "*.rfs-part-*" };

    private bool MatchesFilters(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);

        // Never sync our own staging files (see FileTransferReceiver).
        foreach (var pattern in AlwaysExclude)
            if (GlobMatch(fileName, pattern)) return false;

        // A pattern containing '/' is a path pattern and is matched against the full
        // relative path; otherwise it is a name pattern. Previously every pattern was
        // matched against the filename only, so path patterns like "node_modules/*"
        // could never match and silently did nothing.
        bool Matches(string pattern) =>
            pattern.Contains('/')
                ? GlobMatch(relativePath, pattern)
                : GlobMatch(fileName, pattern);

        if (_includePatterns.Count > 0)
        {
            bool included = false;
            foreach (var pattern in _includePatterns)
            {
                if (Matches(pattern)) { included = true; break; }
            }
            if (!included) return false;
        }

        foreach (var pattern in _excludePatterns)
        {
            if (Matches(pattern)) return false;
        }
        return true;
    }
```

> `GlobMatch`'s `*` matches any character including `/`, so `node_modules/*` correctly covers nested entries. Document this in the usage text so the semantic is not surprising.

### 9.3 — Make the scan resilient

**File:** `src/RemoteFileSync/Sync/FileScanner.cs`

```csharp
    public FileManifest Scan()
    {
        var manifest = new FileManifest();
        if (!Directory.Exists(_rootPath)) return manifest;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,               // one locked subdir must not abort the sync
            AttributesToSkip = FileAttributes.ReparsePoint, // don't follow junctions/symlinks out of the root
        };

        foreach (var fullPath in Directory.EnumerateFiles(_rootPath, "*", options))
        {
            var relativePath = Path.GetRelativePath(_rootPath, fullPath).Replace('\\', '/');
            if (!MatchesFilters(relativePath)) continue;

            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists) continue;          // vanished between enumeration and stat
                manifest.Add(new FileEntry(relativePath, info.Length, info.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
                                          or UnauthorizedAccessException or IOException)
            {
                // Transient or permission-denied: skip this file, keep scanning.
            }
        }
        return manifest;
    }
```

> **Behaviour note.** `AttributesToSkip` defaults to `Hidden | System`; setting it explicitly to `ReparsePoint` preserves the previous behaviour of including hidden and system files while newly skipping reparse points. This is intentional — following a junction out of the sync root is a containment hole.

### 9.4 — Retire both-sides-deleted rows

**File:** `src/RemoteFileSync/Sync/SyncEngine.cs` (line ~198)

```diff
             else
             {
-                // Neither has it (from DB tracked paths) — both deleted, no action
+                // Neither side has the file. Leave no plan entry, but the DB row must be
+                // retired: if it stays 'exists', a later restore on one side is resolved
+                // as a deletion on the other.
+                if (deleteEnabled && db != null && dbIndex.TryGetValue(path, out var orphan)
+                    && orphan.Status == "exists")
+                {
+                    db.MarkDeleted(path, sessionId: 0, "absent on both sides; retiring tracked row");
+                }
             }
```

> `ComputePlan` is currently `static` and has no `sessionId`. Either thread the active session id through as a parameter, or perform this reconciliation in `SyncClient` after the plan is computed. The latter is the smaller diff and is recommended.

### Tests to add

```csharp
[Fact] public void Plan_AbortsWhenDeletionsExceedThreshold();
[Fact] public void Plan_ProceedsWhenForceDeleteSet();
[Theory]
[InlineData("node_modules/*", "node_modules/x/a.js", false)]  // now excluded
[InlineData("*.tmp",          "sub/a.tmp",           false)]
[InlineData("*.tmp",          "sub/a.txt",           true)]
public void Scanner_AppliesPathAndNamePatterns(string exclude, string path, bool expectIncluded);
[Fact] public void Scan_SkipsVanishedFileWithoutAborting();
[Fact] public void Scan_SkipsInaccessibleSubdirectory();
```

### Commit

```bash
git add -A
git commit -m "fix(sync): add deletion threshold, fix glob path matching, harden scan

Adds --max-delete-percent (default 25) so a repointed or empty peer folder
cannot wipe the other side; --force-delete overrides. New exit code 4.

Glob patterns containing '/' now match the relative path instead of the
filename only, so excludes like 'node_modules/*' actually take effect rather
than silently no-opping.

Scanning now tolerates inaccessible subdirectories and files that vanish
mid-scan, and no longer follows reparse points out of the sync root.

Fixes: H7, H8, M4, M5, M12"
git push
```

---

## 10. Phase 6 — Control flow: pause/stop, cancellation, lockstep

> **Addresses:** H9, H10, H11, M1, M2, M3, M11.

### 10.1 — STOP must release the pause gate

**File:** `src/RemoteFileSync/Progress/StdinCommandReader.cs`

```diff
                     case "STOP":
                         StopToken.Cancel();
+                        // Release anyone blocked in WaitWhilePaused(); otherwise a PAUSE
+                        // followed by STOP blocks the sync thread forever and this loop
+                        // has already exited, so RESUME can never arrive.
+                        PauseGate.Set();
                         WriteStatus("stopping");
                         return;
```

Add a cancellation-aware wait helper:

```csharp
    /// <summary>
    /// Blocks while paused. Honours both the caller's token (Ctrl+C) and STOP.
    /// Returns false if the sync should stop.
    /// </summary>
    public bool WaitWhilePaused(CancellationToken ct)
    {
        PauseGate.Wait(ct);
        return !StopToken.IsCancellationRequested;
    }
```

Replace all eight call sites in `SyncClient.cs` (lines 174, 208, 240, 281) and `SyncServer.cs` (lines 108, 144, 208, 234):

```diff
-            _stdinReader.PauseGate.Wait();
-            if (_stdinReader.StopToken.IsCancellationRequested) { _logger.Warning("Stop requested."); break; }
+            if (!_stdinReader.WaitWhilePaused(ct)) { _logger.Warning("Stop requested."); break; }
```

Guard `Dispose` against tearing down primitives still being waited on:

```diff
     public void Dispose()
     {
         if (_input == null) return;
+        PauseGate.Set();          // never leave a waiter blocked on a disposed handle
+        if (!StopToken.IsCancellationRequested) StopToken.Cancel();
         StopToken.Dispose();
         PauseGate.Dispose();
     }
```

### 10.2 — A stopped sync is not a successful sync

**File:** `src/RemoteFileSync/Network/SyncClient.cs`

Track the stop explicitly so the exit code and the persisted state reflect reality:

```diff
         var sw = Stopwatch.StartNew();
         int skippedFiles = 0;
+        bool stopped = false;
```

At each `break` on stop, set `stopped = true;` first. Then:

```diff
-        int exitCode = skippedFiles > 0 ? 1 : 0;
+        int exitCode = (skippedFiles > 0 || stopped) ? 1 : 0;
```

This also prevents the binary-state fallback at line 350 (`exitCode == 0`) from persisting a merged manifest that claims never-transferred files were synced — which on the next run resolves to deletions.

### 10.3 — Fix the connection retry loop

**File:** `src/RemoteFileSync/Network/SyncClient.cs`

A `TcpClient` whose `ConnectAsync` failed cannot be reused; attempts 2 and 3 currently fail immediately, possibly with an exception type the `catch (SocketException)` filter does not match. Construct one per attempt:

```diff
     public async Task<int> RunAsync(CancellationToken ct)
     {
-        using var tcp = new TcpClient();
         int retries = 3;
+        TcpClient? tcp = null;
 
         for (int attempt = 1; attempt <= retries; attempt++)
         {
             try
             {
+                tcp?.Dispose();
+                tcp = new TcpClient();
                 _logger.Summary($"Connecting to {_options.Host}:{_options.Port}...");
                 await tcp.ConnectAsync(_options.Host!, _options.Port, ct);
                 break;
             }
             catch (SocketException) when (attempt < retries)
             {
                 _logger.Warning($"Connection attempt {attempt} failed. Retrying in 2s...");
                 await Task.Delay(2000, ct);
             }
             catch (SocketException ex)
             {
-                _logger.Error($"Connection failed after {retries} attempts: {ex.Message}");
+                var msg = $"Connection failed after {retries} attempts: {ex.Message}";
+                _logger.Error(msg);
+                _progress.WriteError(msg, fatal: true);
+                tcp?.Dispose();
                 return 2;
             }
         }
+
+        if (tcp is null) return 2;
+        using var owned = tcp;
```

Replace the subsequent `using var stream = tcp.GetStream();` with `using var stream = owned.GetStream();`.

### 10.4 — Isolate per-file receive failures

**File:** `src/RemoteFileSync/Network/SyncServer.cs` (line ~116)

The send loop catches per-file exceptions; the receive loop does not, so one corrupt gzip or locked destination tears down the whole connection.

```diff
-            var result = await receiver.ReceiveFileAsync(stream, ct);
+            FileReceiveResult result;
+            try
+            {
+                result = await receiver.ReceiveFileAsync(stream, ct);
+            }
+            catch (Exception ex) when (ex is not OperationCanceledException)
+            {
+                _logger.Error($"Error receiving {action.RelativePath}: {ex.Message}");
+                result = new FileReceiveResult(false, action.RelativePath, ex.Message);
+            }
```

Because `BackupConfirm` is still sent afterwards with `result.Success == false`, the peer (post-§7.3) correctly declines to mark the file synced and lockstep is preserved.

Apply the same wrapping to the client's receive loop (`SyncClient.cs:248`).

### 10.5 — Do not destroy a file before a transfer that may not arrive

**File:** `src/RemoteFileSync/Network/SyncServer.cs` (line ~112)

Backing up by plan-list index while transfers are self-describing by path means any sender-side skip moves away the *wrong* file. Since Phase 3 made receives non-destructive (staging + atomic commit), the pre-emptive backup is no longer needed for safety and can be driven by the actual received path:

```diff
         foreach (var action in toReceive)
         {
             if (!_stdinReader.WaitWhilePaused(ct)) { _logger.Warning("Stop requested."); break; }
-            if (action.Action == SyncActionType.SendToServer)
-            {
-                if (!backup.BackupFile(action.RelativePath))
-                    _logger.Debug($"No existing file to backup: {action.RelativePath}");
-            }
-
-            var result = await receiver.ReceiveFileAsync(stream, ct);
+            // NOTE: no pre-emptive backup here. Transfers are self-describing by path, so
+            // backing up by plan index moved the wrong file whenever the sender skipped one.
+            // FileTransferReceiver stages and atomically commits, so the existing file is
+            // safe until a verified replacement is ready; we snapshot the real path below.
+            FileReceiveResult result;
+            try { result = await receiver.ReceiveFileAsync(stream, ct, onBeforeCommit: backup.BackupFile); }
+            catch (Exception ex) when (ex is not OperationCanceledException)
+            {
+                _logger.Error($"Error receiving {action.RelativePath}: {ex.Message}");
+                result = new FileReceiveResult(false, action.RelativePath, ex.Message);
+            }
```

This requires an optional pre-commit hook on the receiver. In `FileTransfer.cs`:

```diff
     public async Task<FileReceiveResult> ReceiveFileAsync(Stream networkStream, CancellationToken ct)
+        => await ReceiveFileAsync(networkStream, ct, onBeforeCommit: null);
+
+    /// <summary>
+    /// <paramref name="onBeforeCommit"/> receives the verified file's relative path immediately
+    /// before the destination is replaced, so callers can snapshot the outgoing version.
+    /// </summary>
+    public async Task<FileReceiveResult> ReceiveFileAsync(Stream networkStream, CancellationToken ct,
+                                                          Func<string, bool>? onBeforeCommit)
```

and immediately before the commit:

```diff
                         File.SetLastWriteTimeUtc(stagingPath, new DateTime(lastModifiedUtcTicks, DateTimeKind.Utc));
+                        onBeforeCommit?.Invoke(relativePath);
                         File.Move(stagingPath, destPath, overwrite: true);
```

Apply the same at `SyncClient.cs:244`.

### 10.6 — Keep the server listening

**File:** `src/RemoteFileSync/Network/SyncServer.cs`

`RunAsync` accepts exactly one client and returns, so a stray connection (a port scan, a failed attempt) permanently kills the server before the real client arrives — and the GUI presents it as a persistent listener.

```csharp
    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (!IPAddress.TryParse(_options.BindAddress, out var bindIp))
            throw new ArgumentException($"Invalid --bind address: {_options.BindAddress}");

        var listener = new TcpListener(bindIp, _options.Port);
        listener.Start();
        _logger.Summary($"Listening on {bindIp}:{_options.Port}...");
        _progress.WriteStatus("listening", port: _options.Port);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stdinReader.StopToken.Token);
        int lastExit = 0;

        try
        {
            while (!linked.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(linked.Token);
                _logger.Summary("Client connected.");
                _progress.WriteStatus("connected");
                try
                {
                    using var stream = client.GetStream();
                    lastExit = await HandleConnectionAsync(stream, linked.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad session must not kill the listener.
                    _logger.Error($"Session failed: {ex.Message}");
                    _progress.WriteError($"Session failed: {ex.Message}", fatal: false);
                    lastExit = 3;
                }
                _progress.WriteStatus("listening", port: _options.Port);
            }
        }
        catch (OperationCanceledException) { /* graceful stop */ }
        finally
        {
            listener.Stop();
        }
        return lastExit;
    }
```

Linking `StopToken` also makes the GUI's Stop button work on an idle listening server, which previously could not be interrupted.

> **Decision required.** This changes server mode from one-shot to persistent. If one-shot is intentional for scripted use, add `--once` and keep the loop as the default. Confirm before implementing.

### Tests to add

```csharp
[Fact] public void Stop_ReleasesPauseGate();                        // PAUSE then STOP must not hang
[Fact] public void WaitWhilePaused_HonoursExternalCancellation();
[Fact] public void StoppedSync_ReturnsNonZeroExitCode();
[Fact] public async Task Server_SurvivesAConnectionThatDropsImmediately();
[Fact] public async Task Server_KeepsListeningAfterASession();
```

### Commit

```bash
git add -A
git commit -m "fix(control): release pause gate on stop, honour cancellation, keep listening

STOP now sets PauseGate, so PAUSE followed by STOP no longer blocks the sync
thread forever; waits honour the caller's token so Ctrl+C works while paused.

A stopped sync now reports a non-zero exit code and no longer persists state
claiming never-transferred files were synced.

Connection retries construct a fresh TcpClient per attempt (a failed client
cannot be reconnected, so retries always failed).

Per-file receive failures are isolated instead of tearing down the connection,
and the pre-overwrite snapshot is driven by the received path rather than the
plan index, which moved the wrong file whenever the sender skipped one.

Server now loops on accept and links StopToken, so a stray connection cannot
kill it and the GUI Stop button works while idle.

Fixes: H9, H10, H11, M1, M2, M3, M11"
git push
```

---

## 11. Phase 7 — CLI robustness & error visibility

> **Addresses:** H12, H13, M7.

### 11.1 — Fatal errors must not be silent

`JsonProgressWriter.WriteError` is **dead code** — its only occurrence in the entire solution is its own definition. Combined with `suppressConsole: options.JsonProgress` (`Program.cs:26`), a fatal error in the mode the GUI actually uses produces nothing on stdout, nothing on the console, and nothing on stderr. `ProcessManager.cs:56`'s `evt.Event == "error"` branch is unreachable, so the GUI can never show a failure.

**File:** `src/RemoteFileSync/Program.cs`

```diff
         catch (OperationCanceledException)
         {
             logger.Summary("Operation cancelled.");
+            progressWriter.WriteError("Operation cancelled.", fatal: false);
             return 1;
         }
         catch (Exception ex)
         {
             logger.Error($"Fatal error: {ex.Message}");
+            progressWriter.WriteError($"Fatal error: {ex.Message}", fatal: true);
             return 3;
         }
```

Also emit on the argument-parsing path, which currently reaches stderr only:

```diff
         catch (ArgumentException ex)
         {
             Console.Error.WriteLine($"Error: {ex.Message}");
             PrintUsage();
             return 3;
         }
```

> Parse errors occur before the writer exists, so stderr is correct here — but `ProcessManager` already surfaces stderr as `[STDERR]` log lines, so the GUI does see these. No change needed; noted so a reviewer does not "fix" it.

### 11.2 — Argument parsing must not crash

**File:** `src/RemoteFileSync/Program.cs`

Add helpers (requires `using System.Globalization;`):

```csharp
    private static string NextValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {flag}.");
        return args[++i];
    }

    private static int NextInt(string[] args, ref int i, string flag)
    {
        var raw = NextValue(args, ref i, flag);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"{flag} expects an integer, got '{raw}'.");
        return value;
    }
```

Rewrite every value-taking case:

```diff
                 case "--host" or "-h":
-                    options.Host = args[++i];
+                    options.Host = NextValue(args, ref i, "--host");
                     break;
                 case "--port" or "-p":
-                    options.Port = int.Parse(args[++i]);
+                    options.Port = NextInt(args, ref i, "--port");
                     break;
                 case "--folder" or "-f":
-                    options.Folder = args[++i];
+                    options.Folder = NextValue(args, ref i, "--folder");
                     break;
                 case "--backup-folder":
-                    options.BackupFolder = args[++i];
+                    options.BackupFolder = NextValue(args, ref i, "--backup-folder");
                     break;
                 case "--include":
-                    options.IncludePatterns.Add(args[++i]);
+                    options.IncludePatterns.Add(NextValue(args, ref i, "--include"));
                     break;
                 case "--exclude":
-                    options.ExcludePatterns.Add(args[++i]);
+                    options.ExcludePatterns.Add(NextValue(args, ref i, "--exclude"));
                     break;
                 case "--block-size" or "-bs":
-                    options.BlockSize = int.Parse(args[++i]);
+                    options.BlockSize = NextInt(args, ref i, "--block-size");
                     break;
                 case "--max-threads" or "-t":
-                    options.MaxThreads = int.Parse(args[++i]);
+                    options.MaxThreads = NextInt(args, ref i, "--max-threads");
                     break;
                 case "--log" or "-l":
-                    options.LogFile = args[++i];
+                    options.LogFile = NextValue(args, ref i, "--log");
                     break;
```

Every failure is now an `ArgumentException`, which the existing handler already turns into a clean usage message.

### 11.3 — Logger construction must not crash the process

`new SyncLogger(...)` sits at `Program.cs:26`, outside every `try`. A bad or locked `--log` path throws unhandled. `StreamWriter(path, append: true)` also opens without sharing, so two instances writing the same log (exactly what the GUI does when it runs client and server together) collide.

**File:** `src/RemoteFileSync/Logging/SyncLogger.cs`

```diff
         if (!string.IsNullOrWhiteSpace(logFile))
         {
             var dir = Path.GetDirectoryName(logFile);
             if (!string.IsNullOrEmpty(dir))
                 Directory.CreateDirectory(dir);
-            _logWriter = new StreamWriter(logFile, append: true) { AutoFlush = true };
+            // FileShare.ReadWrite so a concurrent client+server pair can share one log path.
+            var fs = new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
+            _logWriter = new StreamWriter(fs) { AutoFlush = true };
         }
```

**File:** `src/RemoteFileSync/Program.cs` — bring construction inside a guarded block:

```diff
-        using var logger = new SyncLogger(options.Verbose, options.LogFile, suppressConsole: options.JsonProgress);
+        SyncLogger logger;
+        try
+        {
+            logger = new SyncLogger(options.Verbose, options.LogFile, suppressConsole: options.JsonProgress);
+        }
+        catch (Exception ex)
+        {
+            Console.Error.WriteLine($"Error: cannot open log file '{options.LogFile}': {ex.Message}");
+            return 3;
+        }
+        using var _loggerScope = logger;
```

### Tests to add

```csharp
[Theory]
[InlineData("client", "--host")]          // missing value
[InlineData("client", "--port")]
public void ParseArgs_MissingValue_ThrowsArgumentException(params string[] args)
    => Assert.Throws<ArgumentException>(() => Program.ParseArgs(args));

[Fact] public void ParseArgs_NonNumericPort_ThrowsArgumentException();
[Fact] public void ParseArgs_UsesInvariantCultureForNumbers();
[Fact] public void SyncLogger_AllowsTwoConcurrentWritersToSameFile();
```

### Commit

```bash
git add -A
git commit -m "fix(cli): surface fatal errors, validate arguments, share the log file

WriteError was dead code and the console is suppressed in --json-progress
mode, so fatal errors produced no output at all and the GUI could never
display a failure. Fatal paths now emit an error event.

Argument parsing no longer indexes past the end or lets FormatException
escape as an unhandled crash; all failures become ArgumentException and
print the usage text. Integers parse with InvariantCulture.

The log file opens with FileShare.ReadWrite and inside a guarded block, so a
concurrent client+server pair sharing a --log path no longer crashes.

Fixes: H12, H13, M7"
git push
```

---

## 12. Phase 8 — GUI process lifecycle & progress contract

> **Addresses:** H14, H15, H16, M8, M9.

### 12.1 — Fix the progress event contract

The CLI and GUI disagree in both directions:

| CLI emits | GUI listens for | Result |
|---|---|---|
| `manifest` | `scan_complete` | `_totalBytes` never set → bar stuck at 0% |
| `file_progress` (never called) | `progress` | no per-file progress |
| `file_start` (never called) | `file_start` | no active-transfer rows |
| `plan` | — (ignored) | total work unknown |

**Decision: the CLI's design-document names are canonical; the GUI adapts.** The CLI additionally starts emitting the per-file events it already has methods for.

**File:** `src/RemoteFileSync/Progress/JsonProgressWriter.cs` — carry bytes on the plan event:

```diff
-    public void WritePlan(int transfers, int deletes, int skipped)
+    public void WritePlan(int transfers, int deletes, int skipped, long bytes)
     {
-        WriteLine(new { @event = "plan", transfers, deletes, skipped });
+        WriteLine(new { @event = "plan", transfers, deletes, skipped, bytes });
     }
```

**File:** `src/RemoteFileSync/Transfer/FileTransfer.cs` — report progress as chunks go out:

```diff
     public async Task SendFileAsync(Stream networkStream, short fileId, string relativePath,
-                                    CancellationToken ct)
+                                    CancellationToken ct, Action<long>? onBytesSent = null)
```

```diff
             using var fileStream = File.OpenRead(transferSource);
             var buffer = new byte[_blockSize];
             int chunkIndex = 0;
             int bytesRead;
+            long totalSent = 0;
             while ((bytesRead = await fileStream.ReadAsync(buffer, ct)) > 0)
             {
                 var chunkData = bytesRead == buffer.Length ? buffer : buffer[..bytesRead];
                 var chunkPayload = ProtocolHandler.SerializeFileChunk(fileId, chunkIndex, chunkData);
                 await ProtocolHandler.WriteMessageAsync(networkStream, MessageType.FileChunk, chunkPayload, ct);
                 chunkIndex++;
+                totalSent += bytesRead;
+                onBytesSent?.Invoke(totalSent);
             }
```

**File:** `src/RemoteFileSync/Network/SyncClient.cs` — emit the bracketing events:

```diff
         var deleteSummary = deleteCount > 0 ? $", {deleteCount} delete" : "";
         _logger.Info($"Sync plan: {transferCount} transfers{deleteSummary}, {skipCount} skipped");
-        _progress.WritePlan(transferCount, deleteCount, skipCount);
+        long plannedBytes = syncPlan
+            .Where(p => p.Action is SyncActionType.SendToServer or SyncActionType.ClientOnly)
+            .Sum(p => clientManifest.Get(p.RelativePath)?.FileSize ?? 0);
+        _progress.WritePlan(transferCount, deleteCount, skipCount, plannedBytes);
```

```diff
                 short fileId = (short)(filesTransferred % short.MaxValue);
+                var planned = clientManifest.Get(action.RelativePath);
+                _progress.WriteFileStart("to_server", action.RelativePath,
+                    planned?.FileSize ?? 0,
+                    compressed: !CompressionHelper.IsAlreadyCompressed(Path.GetExtension(action.RelativePath)),
+                    thread: 0);
-                await sender.SendFileAsync(stream, fileId, action.RelativePath, ct);
+                await sender.SendFileAsync(stream, fileId, action.RelativePath, ct,
+                    onBytesSent: sent => _progress.WriteFileProgress(
+                        action.RelativePath, sent, planned?.FileSize ?? 0, thread: 0));
```

Emit `WriteDelete` in both deletion phases:

```diff
                     if (success)
                     {
                         filesDeleted++;
                         _logger.Info($"[DEL→] {del.RelativePath} (deleted on server)");
+                        _progress.WriteDelete(del.RelativePath, backed_up: true, success: true);
                         _db?.MarkDeleted(del.RelativePath, sessionId, "deleted on client, propagated to server");
                     }
                     else
                     {
                         _logger.Warning($"Server failed to delete {del.RelativePath}");
+                        _progress.WriteDelete(del.RelativePath, backed_up: false, success: false);
                         skippedFiles++;
                     }
```

**File:** `src/ExecRFS/Components/Shared/ProgressBar.razor` — align the listener:

```diff
         switch (evt.Event)
         {
-            case "scan_complete":
-                _totalFiles = evt.Files ?? _totalFiles;
-                _totalBytes = evt.Bytes ?? _totalBytes;
+            case "plan":
+                _totalFiles = evt.Transfers ?? _totalFiles;
+                _totalBytes = evt.Bytes ?? _totalBytes;
                 break;
 
             case "file_start":
                 if (evt.Thread.HasValue && evt.Size.HasValue)
                     _threadCurrentBytes[evt.Thread.Value] = 0;
                 break;
 
-            case "progress":
+            case "file_progress":
                 if (evt.Thread.HasValue && evt.BytesSent.HasValue)
```

`ProgressEvent` already declares `Transfers`, `Bytes`, `BytesSent`, `TotalBytes`, `Size`, and `Thread`, so no model change is needed. Apply the same `file_start` / `file_progress` rename in `ClientPanel.razor:158`.

### 12.2 — Process lifecycle

**File:** `src/ExecRFS/Services/ProcessManager.cs`

```diff
     public void Start(SyncProfile profile, string? exePath = null)
     {
         if (_process != null && !HasExitedSafely(_process)) return;
-        State = SyncInstanceState.Starting;
-        var resolvedExe = exePath ?? ResolveExePath();
-        var fullCmd = CommandBuilder.BuildForProcess(profile, _role == "server");
-        var args = fullCmd.Substring(fullCmd.IndexOf(' ') + 1);
-
-        _process = new Process
+
+        // Dispose the previous Process object; restarting without this leaks a handle
+        // and a stdin pipe on every run.
+        _process?.Dispose();
+        _process = null;
+
+        State = SyncInstanceState.Starting;
+
+        string resolvedExe;
+        string args;
+        try
+        {
+            resolvedExe = exePath ?? ResolveExePath();
+            var fullCmd = CommandBuilder.BuildForProcess(profile, _role == "server");
+            args = fullCmd.Substring(fullCmd.IndexOf(' ') + 1);
+        }
+        catch (Exception ex)
+        {
+            // Never leave the UI wedged at Starting, and never throw into a Blazor handler.
+            State = SyncInstanceState.Error;
+            OnLogLine?.Invoke($"[ERR] {ex.Message}");
+            return;
+        }
+
+        var process = new Process
         { /* StartInfo unchanged */ };
```

Bind every handler to the **local** `process`, not the mutable field:

```diff
-        _process.Exited += (_, _) =>
-        {
-            var code = _process.ExitCode;
-            if (State != SyncInstanceState.Error) State = SyncInstanceState.Stopped;
-            OnExited?.Invoke(code);
-        };
+        process.Exited += (_, _) =>
+        {
+            var code = process.ExitCode;      // local capture: survives restart/dispose
+            if (State != SyncInstanceState.Error) State = SyncInstanceState.Stopped;
+            OnExited?.Invoke(code);
+        };
 
-        _process.Start();
-        _process.BeginOutputReadLine();
-        _process.BeginErrorReadLine();
-        State = SyncInstanceState.Running;
+        try
+        {
+            process.Start();
+        }
+        catch (Exception ex)
+        {
+            State = SyncInstanceState.Error;
+            OnLogLine?.Invoke($"[ERR] Failed to start {resolvedExe}: {ex.Message}");
+            process.Dispose();
+            return;
+        }
+
+        _process = process;
+        process.BeginOutputReadLine();
+        process.BeginErrorReadLine();
+
+        // A child that exits instantly (bad args) already fired Exited; do not
+        // overwrite Stopped/Error with Running or Stop() will no-op and wedge the UI.
+        if (State == SyncInstanceState.Starting)
+            State = SyncInstanceState.Running;
     }
+
+    private static bool HasExitedSafely(Process p)
+    {
+        // HasExited throws InvalidOperationException on a Process that was never started.
+        try { return p.HasExited; }
+        catch (InvalidOperationException) { return true; }
+    }
```

Fix the delayed kill to target the process it was asked to stop:

```diff
     public void Stop()
     {
-        if (_process == null || _process.HasExited) return;
+        var target = _process;
+        if (target == null || HasExitedSafely(target)) return;
         State = SyncInstanceState.Stopping;
         WriteStdin("STOP");
-        Task.Run(async () => {
-            await Task.Delay(5000);
-            if (_process != null && !_process.HasExited) { _process.Kill(entireProcessTree: true); State = SyncInstanceState.Stopped; }
-        });
+        // Capture `target`: binding to the field killed a *newly restarted* process
+        // when a stop/start cycle happened inside the 5s window.
+        _ = Task.Run(async () =>
+        {
+            try
+            {
+                await Task.Delay(5000);
+                if (!HasExitedSafely(target))
+                {
+                    target.Kill(entireProcessTree: true);
+                    if (ReferenceEquals(target, _process)) State = SyncInstanceState.Stopped;
+                }
+            }
+            catch (Exception ex) { OnLogLine?.Invoke($"[ERR] Kill failed: {ex.Message}"); }
+        });
     }
```

```diff
     public void Dispose()
     {
-        if (_process != null && !_process.HasExited) _process.Kill(entireProcessTree: true);
-        _process?.Dispose();
+        var p = _process;
+        if (p == null) return;
+        try { if (!HasExitedSafely(p)) p.Kill(entireProcessTree: true); }
+        catch { /* already gone */ }
+        p.Dispose();
+        _process = null;
     }
```

### 12.3 — Never orphan children on window close

**File:** `src/ExecRFS/MainWindow.xaml.cs`

`AutoSave()` runs *before* the processes are disposed, so an AutoSave exception orphans both children.

```diff
         Closing += (_, _) =>
         {
-            sp.GetService<ProfileService>()?.AutoSave();
             var procs = sp.GetService<SyncProcesses>();
-            procs?.Server.Dispose();
-            procs?.Client.Dispose();
+            try
+            {
+                sp.GetService<ProfileService>()?.AutoSave();
+            }
+            catch (Exception ex)
+            {
+                System.Diagnostics.Debug.WriteLine($"AutoSave failed on close: {ex}");
+            }
+            finally
+            {
+                // Must run even if AutoSave threw, or both CLI children are orphaned.
+                procs?.Server.Dispose();
+                procs?.Client.Dispose();
+            }
         };
```

### Tests to add

```csharp
[Fact] public void Start_WhenExeMissing_SetsErrorStateAndDoesNotThrow();
[Fact] public void Start_AfterStop_DisposesPreviousProcess();
[Fact] public void ProgressBar_PlanEvent_SetsTotals();      // via ProgressEvent parsing
[Fact] public void ProgressEvent_ParsesFileProgress();
```

### Verification

Launch the GUI, run a real sync, and confirm the progress bar advances past 0%, per-file rows appear, and closing the window leaves no `RemoteFileSync.exe` in Task Manager.

### Commit

```bash
git add -A
git commit -m "fix(gui): align progress event contract and harden process lifecycle

The CLI never emitted file_start/file_progress/delete and the GUI listened for
'scan_complete'/'progress' that the CLI never sends, so the progress bar sat at
0% permanently. The CLI now emits the per-file events it already had methods
for, the plan event carries total bytes, and the GUI listens for the canonical
names.

ProcessManager no longer throws into Blazor handlers, no longer overwrites a
fast-exiting child's state with Running, captures the target process in the
delayed kill (it previously killed a newly restarted process), disposes the
previous Process on restart, and tolerates HasExited on an unstarted Process.

MainWindow disposes child processes in a finally block so an AutoSave failure
cannot orphan them.

Fixes: H14, H15, H16, M8, M9"
git push
```

---

## 13. Phase 9 — Regression test suite

> **Goal:** pin the behaviours this plan establishes so they cannot silently regress.

The existing 181 tests all passed against thoroughly broken code. The gap is not test *count* — it is that no test runs a second sync, no test asserts on a hostile peer, and no test covers a failure path.

### 13.1 — Convergence (highest value)

```csharp
[Fact] public async Task SecondSync_IsANoOp_WhenNothingChanged();
[Fact] public async Task BidirectionalSync_DoesNotPingPong_OverThreeRuns();
[Fact] public async Task TransferredFile_HasSourceTimestamp();
```

### 13.2 — Hostile peer

```csharp
[Fact] public async Task ReceiveFile_WithTraversalPath_WritesNothingOutsideRoot();
[Fact] public async Task DeleteFile_WithTraversalPath_DeletesNothingOutsideRoot();
[Fact] public async Task OversizedLengthPrefix_ThrowsInsteadOfAllocating();
```

### 13.3 — Failure paths

```csharp
[Fact] public async Task ChecksumMismatch_PreservesExistingDestination();
[Fact] public async Task PeerRejectedFile_IsNotMarkedSyncedInDatabase();
[Fact] public async Task InterruptedTransfer_LeavesNoPartialDestination();
[Fact] public async Task StoppedSync_DoesNotPersistStateAsComplete();
```

### 13.4 — Deletion safety

```csharp
[Fact] public async Task EmptyPeerFolder_TriggersDeleteThresholdAbort();
[Fact] public async Task ForceDelete_OverridesThreshold();
```

### 13.5 — Test-infrastructure fixes

Address the masking issues noted during review:

- Give every integration test a **unique** temp directory (`Path.Combine(Path.GetTempPath(), "rfs-test-" + Guid.NewGuid())`) and dispose it in a fixture.
- Replace fixed TCP ports with port 0 plus discovery of the bound port, so tests are parallel-safe.
- Remove `Thread.Sleep`-based synchronisation in favour of awaiting the relevant task or event.
- Delete or implement `tests/RemoteFileSync.Tests/UnitTest1.cs` (scaffold placeholder).
- Resolve the two analyzer warnings: `xUnit2029` (`SyncEngineTests.cs:56`) and `xUnit1031` (`BackupManagerTests.cs:83`).

### Commit

```bash
git add -A
git commit -m "test: add regression coverage for convergence, hostile peers, failure paths

Adds the tests the previous suite lacked: repeat-sync convergence, path
traversal rejection, checksum-mismatch preservation, peer-rejection handling,
and deletion-threshold enforcement. Also isolates integration temp dirs,
removes fixed test ports, and clears the two analyzer warnings.

Closes the gap that let 181 passing tests coexist with the defects fixed in
phases 1-8."
git push
```

---

## 14. Final integration

```bash
dotnet build RemoteFileSync.slnx -c Release
dotnet test  RemoteFileSync.slnx -c Release
git push
gh pr create --base main --head fix/security-and-sync-correctness \
  --title "Security and sync-correctness remediation" \
  --body-file Plans/2026-07-19-security-and-sync-correctness-remediation-plan.md
```

### Release checklist

- [ ] Two consecutive syncs of an unchanged folder produce zero transfers
- [ ] Three consecutive bidirectional syncs produce no ping-pong
- [ ] No backup directories appear inside the sync folder
- [ ] Traversal paths are rejected and logged, in both directions
- [ ] Server without `--bind` is unreachable from another host
- [ ] Deletion threshold aborts on an empty peer folder
- [ ] GUI progress bar advances; no orphaned processes after close
- [ ] `README.md` updated: new flags (`--bind`, `--max-delete-percent`, `--force-delete`), new exit code `4`, protocol v2 lockstep-upgrade requirement, and an explicit statement that the protocol is unauthenticated

---

## Appendix A — Full verified findings list

The complete 62-finding set, each with its failure scenario and both verifiers' reasoning, is preserved at:

```
C:\Users\heung\AppData\Local\Temp\claude\E--RemoteFileSync\d6003a95-622e-40d8-97eb-92e38213f7a8\tasks\details.md
```

Items not individually itemised in §3 are lower-severity variants that the phase changes above subsume — for example several DB-consistency findings (`sync_session_id=0` sentinel rows, `GetFileHistory` returning oldest-N, `GetDbPath` not canonicalising the folder) are addressed incidentally or deferred to Appendix C.

## Appendix B — Unverified backlog

51 additional candidate findings were produced by a second review pass whose verification stage could not run (spend limit reached mid-run). **They were not refuted — they are simply unconfirmed.** Twelve were hand-verified during plan authoring and are incorporated above (M2–M9, M11, H6, H11, H13). The remaining ~35 — predominantly Blazor cross-thread state mutation (`ClientPanel._activeTransfers` mutated on the stdout reader thread while the renderer enumerates it), event-subscription leaks, `LogViewer` async-void timer handlers, and test-quality issues — are listed at:

```
C:\Users\heung\AppData\Local\Temp\claude\E--RemoteFileSync\d6003a95-622e-40d8-97eb-92e38213f7a8\tasks\w2.md
```

Recommend triaging these after Phase 9, since several will be resolved incidentally by Phase 8.

## Appendix C — Follow-up work (not in this plan)

These are real gaps that need their own design review rather than a patch:

1. **Authentication.** The protocol has none. A pre-shared key exchanged at handshake is the minimum viable control; certificate-based mutual auth is better.
2. **Transport encryption.** All traffic, including file contents, is cleartext. `SslStream` over the existing `NetworkStream` is the natural fit.
3. **Decompression bounds.** `DecompressFile` has no output-size limit — a hostile peer can fill the disk. Bound the output against the declared `originalSize` from `FileStart`.
4. **`--max-threads` is a lie.** It is parsed, documented, and exposed in the UI (1–8), but transfers are strictly sequential. Either implement concurrent transfers or remove the flag.
5. **Resume is unimplemented.** The design promises mid-transfer reconnect with "resume from last chunk"; no such code exists.
6. **Server-side state DB.** `SyncServer` accepts a `SyncDatabase` parameter and never uses it.
7. **Empty directories** are never synced, and deletion propagation leaves empty directory husks behind.
8. **Session restore.** `_last-session.json` is written on close but `LoadLastSession()` is never called.

---

*Plan authored 2026-07-19. Line references correspond to `main` @ `6e2c106`.*
