# Deletion Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add opt-in deletion propagation (`--delete` flag) to RemoteFileSync so that files deleted on one side since the last sync are either deleted on the other side (if untouched) or restored (if modified).

**Architecture:** Client-side manifest snapshot stored at `%LOCALAPPDATA%\RemoteFileSync\{pairId}\sync-state.bin`. After each successful sync (exit 0), the client saves a merged manifest. On the next run with `--delete`, the engine compares current manifests against the snapshot to detect deletions. New `DeleteFile`/`DeleteConfirm` protocol messages handle remote deletion with mandatory backup.

**Tech Stack:** .NET 10, C# 13, xUnit for testing, zero NuGet dependencies in the main project.

**Design Spec:** `Plans/2026-03-28-deletion-sync-design.md`

---

## File Structure

```
src/RemoteFileSync/
├── Models/
│   ├── SyncAction.cs          # MODIFY: Add DeleteOnServer=5, DeleteOnClient=6
│   └── SyncOptions.cs         # MODIFY: Add DeleteEnabled property
├── State/
│   └── SyncStateManager.cs    # CREATE: State persistence (load/save/path)
├── Sync/
│   ├── ConflictResolver.cs    # MODIFY: Add ResolveDeleteConflict method
│   └── SyncEngine.cs          # MODIFY: New overload with deletion detection
├── Network/
│   ├── MessageType.cs         # MODIFY: Add DeleteFile=0x0A, DeleteConfirm=0x0B
│   ├── ProtocolHandler.cs     # MODIFY: Serialize/deserialize new messages + handshake + SyncComplete
│   ├── SyncServer.cs          # MODIFY: Handle deletion phases
│   └── SyncClient.cs          # MODIFY: Handle deletion phases + state save
└── Program.cs                 # MODIFY: Parse --delete/-d flag

tests/RemoteFileSync.Tests/
├── State/
│   └── SyncStateManagerTests.cs    # CREATE
├── Sync/
│   ├── ConflictResolverTests.cs    # MODIFY: Add deletion resolution tests
│   └── SyncEngineTests.cs         # MODIFY: Add deletion detection tests
├── Network/
│   └── ProtocolHandlerTests.cs     # MODIFY: Add new message tests
└── Integration/
    └── DeleteSyncTests.cs          # CREATE: E2E deletion tests
```

---

## Task 1: Models — Extend SyncActionType and SyncOptions

**Files:**
- Modify: `src/RemoteFileSync/Models/SyncAction.cs`
- Modify: `src/RemoteFileSync/Models/SyncOptions.cs`

- [ ] **Step 1: Add DeleteOnServer and DeleteOnClient to SyncActionType**

In `src/RemoteFileSync/Models/SyncAction.cs`, add two new enum values:

```csharp
namespace RemoteFileSync.Models;

public enum SyncActionType : byte
{
    SendToServer = 0,
    SendToClient = 1,
    ClientOnly = 2,
    ServerOnly = 3,
    Skip = 4,
    DeleteOnServer = 5,
    DeleteOnClient = 6
}

public sealed class SyncPlanEntry
{
    public SyncActionType Action { get; }
    public string RelativePath { get; }

    public SyncPlanEntry(SyncActionType action, string relativePath)
    {
        Action = action;
        RelativePath = relativePath;
    }

    public override string ToString() => $"{Action}: {RelativePath}";
}
```

- [ ] **Step 2: Add DeleteEnabled property to SyncOptions**

In `src/RemoteFileSync/Models/SyncOptions.cs`, add the `DeleteEnabled` property:

```csharp
namespace RemoteFileSync.Models;

public sealed class SyncOptions
{
    public bool IsServer { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 15782;
    public string Folder { get; set; } = string.Empty;
    public bool Bidirectional { get; set; }
    public bool DeleteEnabled { get; set; }
    public string? BackupFolder { get; set; }
    public List<string> IncludePatterns { get; set; } = new();
    public List<string> ExcludePatterns { get; set; } = new();
    public int BlockSize { get; set; } = 65536;
    public int MaxThreads { get; set; } = 1;
    public bool Verbose { get; set; }
    public string? LogFile { get; set; }

    public string EffectiveBackupFolder => BackupFolder ?? Folder;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Folder))
            throw new ArgumentException("--folder is required.");
        if (!Directory.Exists(Folder))
            throw new ArgumentException($"Folder does not exist: {Folder}");
        if (!IsServer && string.IsNullOrWhiteSpace(Host))
            throw new ArgumentException("--host is required in client mode.");
        if (Port < 1 || Port > 65535)
            throw new ArgumentException($"--port must be 1-65535, got {Port}.");

        const int minBlock = 4096;
        const int maxBlock = 4 * 1024 * 1024;
        if (BlockSize < minBlock)
        {
            Console.Error.WriteLine($"Warning: --block-size {BlockSize} clamped to minimum {minBlock}.");
            BlockSize = minBlock;
        }
        if (BlockSize > maxBlock)
        {
            Console.Error.WriteLine($"Warning: --block-size {BlockSize} clamped to maximum {maxBlock}.");
            BlockSize = maxBlock;
        }
        if (MaxThreads < 1) MaxThreads = 1;
    }
}
```

- [ ] **Step 3: Verify build succeeds**

Run: `dotnet build`

Expected: Build succeeded with 0 errors.

- [ ] **Step 4: Run existing tests to confirm no regressions**

Run: `dotnet test`

Expected: All existing tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteFileSync/Models/SyncAction.cs src/RemoteFileSync/Models/SyncOptions.cs
git commit -m "feat: add DeleteOnServer/DeleteOnClient actions and DeleteEnabled option"
```

---

## Task 2: State — SyncState Record and SyncStateManager

**Files:**
- Create: `src/RemoteFileSync/State/SyncStateManager.cs`
- Create: `tests/RemoteFileSync.Tests/State/SyncStateManagerTests.cs`

- [ ] **Step 1: Write the failing tests for SyncStateManager**

Create `tests/RemoteFileSync.Tests/State/SyncStateManagerTests.cs`:

```csharp
using RemoteFileSync.Models;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.State;

public class SyncStateManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SyncStateManager _manager;

    public SyncStateManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rfs_state_test_{Guid.NewGuid()}");
        _manager = new SyncStateManager(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void PairId_DeterministicAndCaseInsensitive()
    {
        var id1 = SyncStateManager.ComputePairId(@"C:\SyncFolder", "192.168.1.100", 15782);
        var id2 = SyncStateManager.ComputePairId(@"c:\syncfolder", "192.168.1.100", 15782);
        Assert.Equal(id1, id2);
        Assert.Equal(16, id1.Length);
    }

    [Fact]
    public void PairId_DifferentPairs_DifferentIds()
    {
        var id1 = SyncStateManager.ComputePairId(@"C:\FolderA", "host1", 1000);
        var id2 = SyncStateManager.ComputePairId(@"C:\FolderB", "host2", 2000);
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void MissingFile_ReturnsNull()
    {
        var result = _manager.LoadState(@"C:\NonExistent", "host", 9999);
        Assert.Null(result);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var manifest = new FileManifest();
        manifest.Add(new FileEntry("docs/report.docx", 1024, new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc)));
        manifest.Add(new FileEntry("data/export.csv", 2048, new DateTime(2026, 3, 27, 8, 0, 0, DateTimeKind.Utc)));
        var syncUtc = new DateTime(2026, 3, 28, 10, 0, 0, DateTimeKind.Utc);

        _manager.SaveState(@"C:\TestFolder", "localhost", 15782, manifest, syncUtc);
        var loaded = _manager.LoadState(@"C:\TestFolder", "localhost", 15782);

        Assert.NotNull(loaded);
        Assert.Equal(syncUtc, loaded.LastSyncUtc);
        Assert.Equal(2, loaded.Manifest.Count);
        var entry = loaded.Manifest.Get("docs/report.docx");
        Assert.NotNull(entry);
        Assert.Equal(1024, entry.FileSize);
        Assert.Equal(new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc), entry.LastModifiedUtc);
    }

    [Fact]
    public void CorruptedFile_ReturnsNull()
    {
        var statePath = _manager.GetStatePath(@"C:\TestFolder", "localhost", 15782);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllBytes(statePath, new byte[] { 0xFF, 0xFE, 0xFD });

        var result = _manager.LoadState(@"C:\TestFolder", "localhost", 15782);
        Assert.Null(result);
    }

    [Fact]
    public void AtomicWrite_TempFileCleanedUp()
    {
        var manifest = new FileManifest();
        manifest.Add(new FileEntry("file.txt", 100, DateTime.UtcNow));
        _manager.SaveState(@"C:\TestFolder", "localhost", 15782, manifest, DateTime.UtcNow);

        var statePath = _manager.GetStatePath(@"C:\TestFolder", "localhost", 15782);
        var tmpPath = statePath + ".tmp";
        Assert.False(File.Exists(tmpPath));
        Assert.True(File.Exists(statePath));
    }

    [Fact]
    public void GetStatePath_ReturnsExpectedStructure()
    {
        var path = _manager.GetStatePath(@"C:\SyncFolder", "192.168.1.100", 15782);
        Assert.EndsWith("sync-state.bin", path);
        Assert.Contains(_tempDir, path);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "SyncStateManagerTests"`

Expected: FAIL — `SyncStateManager` type does not exist.

- [ ] **Step 3: Implement SyncState and SyncStateManager**

Create `src/RemoteFileSync/State/SyncStateManager.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using RemoteFileSync.Models;

namespace RemoteFileSync.State;

public sealed record SyncState(FileManifest Manifest, DateTime LastSyncUtc);

public sealed class SyncStateManager
{
    private static readonly byte[] Magic = "RFS1"u8.ToArray();
    private readonly string _baseDir;

    /// <summary>
    /// Creates a SyncStateManager. Pass a custom baseDir for testing;
    /// use the static DefaultBaseDir property for production.
    /// </summary>
    public SyncStateManager(string baseDir)
    {
        _baseDir = baseDir;
    }

    public static string DefaultBaseDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteFileSync");

    public static string ComputePairId(string localFolder, string remoteHost, int port)
    {
        var input = $"{localFolder.TrimEnd('\\', '/')}|{remoteHost}:{port}".ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public string GetStatePath(string localFolder, string remoteHost, int port)
    {
        var pairId = ComputePairId(localFolder, remoteHost, port);
        return Path.Combine(_baseDir, pairId, "sync-state.bin");
    }

    public SyncState? LoadState(string localFolder, string remoteHost, int port)
    {
        var path = GetStatePath(localFolder, remoteHost, port);
        if (!File.Exists(path)) return null;

        try
        {
            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs, Encoding.UTF8);

            // Validate magic
            var magic = reader.ReadBytes(4);
            if (!magic.AsSpan().SequenceEqual(Magic)) return null;

            // Read LastSyncUtc
            var ticks = reader.ReadInt64();
            var lastSyncUtc = new DateTime(ticks, DateTimeKind.Utc);

            // Read manifest entries
            var manifest = new FileManifest();
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                short pathLen = reader.ReadInt16();
                var relPath = Encoding.UTF8.GetString(reader.ReadBytes(pathLen));
                long fileSize = reader.ReadInt64();
                long modTicks = reader.ReadInt64();
                manifest.Add(new FileEntry(relPath, fileSize, new DateTime(modTicks, DateTimeKind.Utc)));
            }

            return new SyncState(manifest, lastSyncUtc);
        }
        catch
        {
            return null;
        }
    }

    public void SaveState(string localFolder, string remoteHost, int port,
                          FileManifest mergedManifest, DateTime syncUtc)
    {
        var path = GetStatePath(localFolder, remoteHost, port);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var tmpPath = path + ".tmp";
        using (var fs = File.Create(tmpPath))
        using (var writer = new BinaryWriter(fs, Encoding.UTF8))
        {
            writer.Write(Magic);
            writer.Write(syncUtc.Ticks);
            writer.Write(mergedManifest.Count);
            foreach (var entry in mergedManifest.Entries)
            {
                var pathBytes = Encoding.UTF8.GetBytes(entry.RelativePath);
                writer.Write((short)pathBytes.Length);
                writer.Write(pathBytes);
                writer.Write(entry.FileSize);
                writer.Write(entry.LastModifiedUtc.Ticks);
            }
        }

        File.Move(tmpPath, path, overwrite: true);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "SyncStateManagerTests"`

Expected: All 7 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteFileSync/State/SyncStateManager.cs tests/RemoteFileSync.Tests/State/SyncStateManagerTests.cs
git commit -m "feat: add SyncStateManager for persistent sync state tracking"
```

---

## Task 3: Sync — ConflictResolver Deletion Resolution

**Files:**
- Modify: `src/RemoteFileSync/Sync/ConflictResolver.cs`
- Modify: `tests/RemoteFileSync.Tests/Sync/ConflictResolverTests.cs`

- [ ] **Step 1: Write the failing tests for ResolveDeleteConflict**

Add the following tests to `tests/RemoteFileSync.Tests/Sync/ConflictResolverTests.cs`:

```csharp
// === Add these tests to the existing ConflictResolverTests class ===

private static readonly DateTime LastSync = new(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
private static readonly DateTime BeforeSync = new(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
private static readonly DateTime AfterSync = new(2026, 3, 27, 8, 0, 0, DateTimeKind.Utc);

[Fact]
public void DeletedOnClient_UntouchedOnServer_ReturnsDeleteOnServer()
{
    var serverEntry = new FileEntry("file.txt", 100, BeforeSync);
    var result = ConflictResolver.ResolveDeleteConflict(
        deletedOnClient: true, survivingEntry: serverEntry, lastSyncUtc: LastSync);
    Assert.Equal(SyncActionType.DeleteOnServer, result);
}

[Fact]
public void DeletedOnClient_ModifiedOnServer_ReturnsSendToClient()
{
    var serverEntry = new FileEntry("file.txt", 200, AfterSync);
    var result = ConflictResolver.ResolveDeleteConflict(
        deletedOnClient: true, survivingEntry: serverEntry, lastSyncUtc: LastSync);
    Assert.Equal(SyncActionType.SendToClient, result);
}

[Fact]
public void DeletedOnServer_UntouchedOnClient_ReturnsDeleteOnClient()
{
    var clientEntry = new FileEntry("file.txt", 100, BeforeSync);
    var result = ConflictResolver.ResolveDeleteConflict(
        deletedOnClient: false, survivingEntry: clientEntry, lastSyncUtc: LastSync);
    Assert.Equal(SyncActionType.DeleteOnClient, result);
}

[Fact]
public void DeletedOnServer_ModifiedOnClient_ReturnsSendToServer()
{
    var clientEntry = new FileEntry("file.txt", 200, AfterSync);
    var result = ConflictResolver.ResolveDeleteConflict(
        deletedOnClient: false, survivingEntry: clientEntry, lastSyncUtc: LastSync);
    Assert.Equal(SyncActionType.SendToServer, result);
}

[Fact]
public void DeleteConflict_TimestampWithinTolerance_TreatedAsUntouched()
{
    // File mod time is 1 second after lastSync — within ±2s tolerance
    var withinTolerance = LastSync.AddSeconds(1);
    var serverEntry = new FileEntry("file.txt", 100, withinTolerance);
    var result = ConflictResolver.ResolveDeleteConflict(
        deletedOnClient: true, survivingEntry: serverEntry, lastSyncUtc: LastSync);
    Assert.Equal(SyncActionType.DeleteOnServer, result);
}

[Fact]
public void DeleteConflict_TimestampExactlyAtTolerance_TreatedAsUntouched()
{
    // File mod time is exactly 2 seconds after lastSync — boundary, treated as untouched
    var atTolerance = LastSync.AddSeconds(2);
    var serverEntry = new FileEntry("file.txt", 100, atTolerance);
    var result = ConflictResolver.ResolveDeleteConflict(
        deletedOnClient: true, survivingEntry: serverEntry, lastSyncUtc: LastSync);
    Assert.Equal(SyncActionType.DeleteOnServer, result);
}

[Fact]
public void DeleteConflict_TimestampJustBeyondTolerance_TreatedAsModified()
{
    // File mod time is 3 seconds after lastSync — beyond tolerance
    var beyondTolerance = LastSync.AddSeconds(3);
    var serverEntry = new FileEntry("file.txt", 100, beyondTolerance);
    var result = ConflictResolver.ResolveDeleteConflict(
        deletedOnClient: true, survivingEntry: serverEntry, lastSyncUtc: LastSync);
    Assert.Equal(SyncActionType.SendToClient, result);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "ConflictResolverTests"`

Expected: 7 new tests FAIL — `ResolveDeleteConflict` method does not exist.

- [ ] **Step 3: Implement ResolveDeleteConflict**

Update `src/RemoteFileSync/Sync/ConflictResolver.cs`:

```csharp
using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

public static class ConflictResolver
{
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    public static SyncActionType Resolve(FileEntry clientEntry, FileEntry serverEntry)
    {
        var timeDiff = clientEntry.LastModifiedUtc - serverEntry.LastModifiedUtc;

        if (Math.Abs(timeDiff.TotalSeconds) <= TimestampTolerance.TotalSeconds
            && clientEntry.FileSize == serverEntry.FileSize)
            return SyncActionType.Skip;

        if (Math.Abs(timeDiff.TotalSeconds) > TimestampTolerance.TotalSeconds)
            return timeDiff.TotalSeconds > 0 ? SyncActionType.SendToServer : SyncActionType.SendToClient;

        if (clientEntry.FileSize > serverEntry.FileSize) return SyncActionType.SendToServer;
        if (serverEntry.FileSize > clientEntry.FileSize) return SyncActionType.SendToClient;

        return SyncActionType.Skip;
    }

    /// <summary>
    /// Resolves the action when a file was deleted on one side and still exists on the other.
    /// Case 1: Surviving file untouched (modTime ≤ lastSyncUtc + tolerance) → propagate deletion.
    /// Case 2: Surviving file modified (modTime > lastSyncUtc + tolerance) → restore (copy to deleting side).
    /// </summary>
    public static SyncActionType ResolveDeleteConflict(bool deletedOnClient, FileEntry survivingEntry, DateTime lastSyncUtc)
    {
        bool untouched = survivingEntry.LastModifiedUtc <= lastSyncUtc + TimestampTolerance;

        if (deletedOnClient)
            return untouched ? SyncActionType.DeleteOnServer : SyncActionType.SendToClient;
        else
            return untouched ? SyncActionType.DeleteOnClient : SyncActionType.SendToServer;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "ConflictResolverTests"`

Expected: All tests pass (existing 7 + new 7 = 14 total).

- [ ] **Step 5: Commit**

```bash
git add src/RemoteFileSync/Sync/ConflictResolver.cs tests/RemoteFileSync.Tests/Sync/ConflictResolverTests.cs
git commit -m "feat: add ResolveDeleteConflict for delete-vs-modify decisions"
```

---

## Task 4: Sync — SyncEngine Deletion Detection

**Files:**
- Modify: `src/RemoteFileSync/Sync/SyncEngine.cs`
- Modify: `tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs`

- [ ] **Step 1: Write the failing tests for deletion-aware ComputePlan**

Add the following tests to `tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs`:

```csharp
// === Add these below the existing tests in SyncEngineTests ===

// State-aware deletion tests
private static readonly DateTime LastSync = new(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
private static readonly DateTime BeforeSync = new(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
private static readonly DateTime AfterSync = new(2026, 3, 27, 8, 0, 0, DateTimeKind.Utc);

[Fact]
public void DeletedOnClient_UntouchedOnServer_ProducesDeleteOnServer()
{
    // File was in previous state, now missing from client, server still has it (untouched)
    var snapshot = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));
    var state = new RemoteFileSync.State.SyncState(snapshot, LastSync);

    var client = new FileManifest(); // file deleted on client
    var server = MakeManifest(new FileEntry("file.txt", 100, BeforeSync)); // untouched

    var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, previousState: state, deleteEnabled: true);
    Assert.Single(plan);
    Assert.Equal(SyncActionType.DeleteOnServer, plan[0].Action);
    Assert.Equal("file.txt", plan[0].RelativePath);
}

[Fact]
public void DeletedOnClient_ModifiedOnServer_ProducesSendToClient()
{
    var snapshot = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));
    var state = new RemoteFileSync.State.SyncState(snapshot, LastSync);

    var client = new FileManifest();
    var server = MakeManifest(new FileEntry("file.txt", 200, AfterSync)); // modified after sync

    var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, previousState: state, deleteEnabled: true);
    Assert.Single(plan);
    Assert.Equal(SyncActionType.SendToClient, plan[0].Action);
}

[Fact]
public void DeletedOnServer_UntouchedOnClient_ProducesDeleteOnClient()
{
    var snapshot = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));
    var state = new RemoteFileSync.State.SyncState(snapshot, LastSync);

    var client = MakeManifest(new FileEntry("file.txt", 100, BeforeSync)); // untouched
    var server = new FileManifest(); // file deleted on server

    var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, previousState: state, deleteEnabled: true);
    Assert.Single(plan);
    Assert.Equal(SyncActionType.DeleteOnClient, plan[0].Action);
}

[Fact]
public void DeletedOnServer_ModifiedOnClient_ProducesSendToServer()
{
    var snapshot = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));
    var state = new RemoteFileSync.State.SyncState(snapshot, LastSync);

    var client = MakeManifest(new FileEntry("file.txt", 200, AfterSync)); // modified
    var server = new FileManifest();

    var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, previousState: state, deleteEnabled: true);
    Assert.Single(plan);
    Assert.Equal(SyncActionType.SendToServer, plan[0].Action);
}

[Fact]
public void BothDeleted_NoAction()
{
    var snapshot = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));
    var state = new RemoteFileSync.State.SyncState(snapshot, LastSync);

    var client = new FileManifest();
    var server = new FileManifest();

    var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, previousState: state, deleteEnabled: true);
    Assert.Empty(plan);
}

[Fact]
public void NoState_FullyAdditive()
{
    // No previous state = first run: file on server only should copy, not delete
    var client = new FileManifest();
    var server = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));

    var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, previousState: null, deleteEnabled: true);
    Assert.Single(plan);
    Assert.Equal(SyncActionType.ServerOnly, plan[0].Action);
}

[Fact]
public void UniDirectional_OnlyClientDeletionsPropagate()
{
    var snapshot = MakeManifest(
        new FileEntry("client-deleted.txt", 100, BeforeSync),
        new FileEntry("server-deleted.txt", 100, BeforeSync));
    var state = new RemoteFileSync.State.SyncState(snapshot, LastSync);

    var client = MakeManifest(new FileEntry("server-deleted.txt", 100, BeforeSync));
    var server = MakeManifest(new FileEntry("client-deleted.txt", 100, BeforeSync));

    var plan = SyncEngine.ComputePlan(client, server, bidirectional: false, previousState: state, deleteEnabled: true);
    // client-deleted.txt: client deleted → DeleteOnServer (propagate)
    // server-deleted.txt: server deleted → ignored (uni mode, server not authoritative)
    Assert.Single(plan);
    Assert.Equal(SyncActionType.DeleteOnServer, plan[0].Action);
    Assert.Equal("client-deleted.txt", plan[0].RelativePath);
}

[Fact]
public void NewFileNotInSnapshot_NormalCopyBehavior()
{
    // A file not in the snapshot is genuinely new — should use normal copy logic
    var snapshot = MakeManifest(new FileEntry("existing.txt", 100, BeforeSync));
    var state = new RemoteFileSync.State.SyncState(snapshot, LastSync);

    var client = MakeManifest(
        new FileEntry("existing.txt", 100, BeforeSync),
        new FileEntry("brand-new.txt", 50, AfterSync));
    var server = MakeManifest(new FileEntry("existing.txt", 100, BeforeSync));

    var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, previousState: state, deleteEnabled: true);
    var actions = plan.ToDictionary(p => p.RelativePath, p => p.Action);
    Assert.Equal(SyncActionType.Skip, actions["existing.txt"]);
    Assert.Equal(SyncActionType.ClientOnly, actions["brand-new.txt"]);
}

[Fact]
public void DeleteEnabled_False_IgnoresDeletions()
{
    var snapshot = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));
    var state = new RemoteFileSync.State.SyncState(snapshot, LastSync);

    var client = new FileManifest();
    var server = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));

    // deleteEnabled=false: should fall through to normal logic (ServerOnly in bidi)
    var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, previousState: state, deleteEnabled: false);
    Assert.Single(plan);
    Assert.Equal(SyncActionType.ServerOnly, plan[0].Action);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "SyncEngineTests"`

Expected: New tests FAIL — overload does not exist.

- [ ] **Step 3: Implement deletion-aware ComputePlan overload**

Replace `src/RemoteFileSync/Sync/SyncEngine.cs` with:

```csharp
using RemoteFileSync.Models;
using RemoteFileSync.State;

namespace RemoteFileSync.Sync;

public static class SyncEngine
{
    public static List<SyncPlanEntry> ComputePlan(FileManifest clientManifest, FileManifest serverManifest, bool bidirectional)
    {
        return ComputePlan(clientManifest, serverManifest, bidirectional, previousState: null, deleteEnabled: false);
    }

    public static List<SyncPlanEntry> ComputePlan(
        FileManifest clientManifest,
        FileManifest serverManifest,
        bool bidirectional,
        SyncState? previousState,
        bool deleteEnabled)
    {
        var plan = new List<SyncPlanEntry>();

        // Collect paths that are handled by deletion logic so we don't double-process them
        var deletionHandled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Phase 1: Deletion detection (only when deleteEnabled AND we have previous state)
        if (deleteEnabled && previousState != null)
        {
            foreach (var path in previousState.Manifest.AllPaths)
            {
                var clientHas = clientManifest.Contains(path);
                var serverHas = serverManifest.Contains(path);

                if (clientHas && serverHas)
                    continue; // Both still have it — not a deletion, handle in Phase 2

                if (!clientHas && !serverHas)
                {
                    deletionHandled.Add(path); // Both deleted — no action, but mark as handled
                    continue;
                }

                bool deletedOnClient = !clientHas && serverHas;
                bool deletedOnServer = clientHas && !serverHas;

                if (deletedOnClient)
                {
                    var serverEntry = serverManifest.Get(path)!;
                    var action = ConflictResolver.ResolveDeleteConflict(
                        deletedOnClient: true, survivingEntry: serverEntry, lastSyncUtc: previousState.LastSyncUtc);
                    plan.Add(new SyncPlanEntry(action, path));
                    deletionHandled.Add(path);
                }
                else if (deletedOnServer)
                {
                    if (bidirectional)
                    {
                        var clientEntry = clientManifest.Get(path)!;
                        var action = ConflictResolver.ResolveDeleteConflict(
                            deletedOnClient: false, survivingEntry: clientEntry, lastSyncUtc: previousState.LastSyncUtc);
                        plan.Add(new SyncPlanEntry(action, path));
                    }
                    // In uni-directional: server deletions are ignored (server not authoritative)
                    deletionHandled.Add(path);
                }
            }
        }

        // Phase 2: Standard comparison for all paths not handled by deletion logic
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in clientManifest.AllPaths) allPaths.Add(path);
        foreach (var path in serverManifest.AllPaths) allPaths.Add(path);

        foreach (var path in allPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (deletionHandled.Contains(path)) continue;

            var clientEntry = clientManifest.Get(path);
            var serverEntry = serverManifest.Get(path);

            if (clientEntry != null && serverEntry != null)
            {
                var action = ConflictResolver.Resolve(clientEntry, serverEntry);
                plan.Add(new SyncPlanEntry(action, path));
            }
            else if (clientEntry != null && serverEntry == null)
            {
                plan.Add(new SyncPlanEntry(SyncActionType.ClientOnly, path));
            }
            else if (clientEntry == null && serverEntry != null)
            {
                if (bidirectional)
                    plan.Add(new SyncPlanEntry(SyncActionType.ServerOnly, path));
            }
        }

        return plan;
    }

    /// <summary>
    /// Builds a merged manifest representing the post-sync state of both sides.
    /// Used by the client to save the state file after a successful sync.
    /// </summary>
    public static FileManifest BuildMergedManifest(
        FileManifest clientManifest,
        FileManifest serverManifest,
        List<SyncPlanEntry> syncPlan)
    {
        var merged = new FileManifest();
        foreach (var entry in syncPlan)
        {
            switch (entry.Action)
            {
                case SyncActionType.Skip:
                case SyncActionType.SendToServer:
                case SyncActionType.ClientOnly:
                    var clientEntry = clientManifest.Get(entry.RelativePath);
                    if (clientEntry != null) merged.Add(clientEntry);
                    break;
                case SyncActionType.SendToClient:
                case SyncActionType.ServerOnly:
                    var serverEntry = serverManifest.Get(entry.RelativePath);
                    if (serverEntry != null) merged.Add(serverEntry);
                    break;
                case SyncActionType.DeleteOnServer:
                case SyncActionType.DeleteOnClient:
                    // Deleted — do not add to merged manifest
                    break;
            }
        }
        return merged;
    }
}
```

- [ ] **Step 4: Run all tests to verify they pass**

Run: `dotnet test`

Expected: All tests pass (existing + new deletion tests).

- [ ] **Step 5: Commit**

```bash
git add src/RemoteFileSync/Sync/SyncEngine.cs tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs
git commit -m "feat: add deletion detection to SyncEngine with state-aware plan computation"
```

---

## Task 5: Protocol — New Messages, Handshake Extension, SyncComplete Update

**Files:**
- Modify: `src/RemoteFileSync/Network/MessageType.cs`
- Modify: `src/RemoteFileSync/Network/ProtocolHandler.cs`
- Modify: `tests/RemoteFileSync.Tests/Network/ProtocolHandlerTests.cs`

- [ ] **Step 1: Write the failing tests for new protocol methods**

Add the following tests to `tests/RemoteFileSync.Tests/Network/ProtocolHandlerTests.cs`:

```csharp
// === Add these tests to the existing ProtocolHandlerTests class ===

[Fact]
public void Handshake_SyncMode_RoundTrips()
{
    // syncMode 3 = bidi + delete
    var data = ProtocolHandler.SerializeHandshake(1, 3);
    var (version, syncMode) = ProtocolHandler.DeserializeHandshake(data);
    Assert.Equal(1, version);
    Assert.Equal(3, syncMode);
}

[Fact]
public void DeleteFile_SerializeDeserialize_RoundTrips()
{
    var data = ProtocolHandler.SerializeDeleteFile("docs/old-report.docx", backupFirst: true);
    var (path, backupFirst) = ProtocolHandler.DeserializeDeleteFile(data);
    Assert.Equal("docs/old-report.docx", path);
    Assert.True(backupFirst);
}

[Fact]
public void DeleteFile_NoBackup_RoundTrips()
{
    var data = ProtocolHandler.SerializeDeleteFile("temp/cache.bin", backupFirst: false);
    var (path, backupFirst) = ProtocolHandler.DeserializeDeleteFile(data);
    Assert.Equal("temp/cache.bin", path);
    Assert.False(backupFirst);
}

[Fact]
public void DeleteConfirm_Success_RoundTrips()
{
    var data = ProtocolHandler.SerializeDeleteConfirm("docs/old-report.docx", success: true);
    var (path, success) = ProtocolHandler.DeserializeDeleteConfirm(data);
    Assert.Equal("docs/old-report.docx", path);
    Assert.True(success);
}

[Fact]
public void DeleteConfirm_Failure_RoundTrips()
{
    var data = ProtocolHandler.SerializeDeleteConfirm("locked/file.txt", success: false);
    var (path, success) = ProtocolHandler.DeserializeDeleteConfirm(data);
    Assert.Equal("locked/file.txt", path);
    Assert.False(success);
}

[Fact]
public void SyncPlan_WithDeleteActions_RoundTrips()
{
    var plan = new List<SyncPlanEntry>
    {
        new(SyncActionType.SendToServer, "update.txt"),
        new(SyncActionType.DeleteOnServer, "old.txt"),
        new(SyncActionType.DeleteOnClient, "removed.txt"),
        new(SyncActionType.Skip, "same.txt")
    };
    var data = ProtocolHandler.SerializeSyncPlan(plan);
    var result = ProtocolHandler.DeserializeSyncPlan(data);
    Assert.Equal(4, result.Count);
    Assert.Equal(SyncActionType.DeleteOnServer, result[1].Action);
    Assert.Equal("old.txt", result[1].RelativePath);
    Assert.Equal(SyncActionType.DeleteOnClient, result[2].Action);
    Assert.Equal("removed.txt", result[2].RelativePath);
}

[Fact]
public void SyncComplete_WithFilesDeleted_RoundTrips()
{
    var data = ProtocolHandler.SerializeSyncComplete(10, 1024000, 3, 5000);
    var (transferred, bytes, deleted, elapsed) = ProtocolHandler.DeserializeSyncComplete(data);
    Assert.Equal(10, transferred);
    Assert.Equal(1024000, bytes);
    Assert.Equal(3, deleted);
    Assert.Equal(5000, elapsed);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "ProtocolHandlerTests"`

Expected: New tests FAIL — new methods don't exist, signatures don't match.

- [ ] **Step 3: Update MessageType enum**

Replace `src/RemoteFileSync/Network/MessageType.cs`:

```csharp
namespace RemoteFileSync.Network;

public enum MessageType : byte
{
    Handshake = 0x01,
    HandshakeAck = 0x02,
    Manifest = 0x03,
    SyncPlan = 0x04,
    FileStart = 0x05,
    FileChunk = 0x06,
    FileEnd = 0x07,
    BackupConfirm = 0x08,
    SyncComplete = 0x09,
    DeleteFile = 0x0A,
    DeleteConfirm = 0x0B,
    Error = 0xFF
}
```

- [ ] **Step 4: Update ProtocolHandler with new methods and modified signatures**

In `src/RemoteFileSync/Network/ProtocolHandler.cs`, apply these changes:

**Replace the Handshake methods (change `bool bidirectional` to `byte syncMode`):**

```csharp
public static byte[] SerializeHandshake(byte version, byte syncMode) =>
    new[] { version, syncMode };

public static (byte version, byte syncMode) DeserializeHandshake(byte[] data) =>
    (data[0], data[1]);
```

**Replace the SyncComplete methods (add `int filesDeleted` parameter):**

```csharp
public static byte[] SerializeSyncComplete(int filesTransferred, long bytesTransferred, int filesDeleted, long elapsedMs)
{
    var result = new byte[24];
    BitConverter.TryWriteBytes(result.AsSpan(0), filesTransferred);
    BitConverter.TryWriteBytes(result.AsSpan(4), bytesTransferred);
    BitConverter.TryWriteBytes(result.AsSpan(12), filesDeleted);
    BitConverter.TryWriteBytes(result.AsSpan(16), elapsedMs);
    return result;
}

public static (int filesTransferred, long bytesTransferred, int filesDeleted, long elapsedMs) DeserializeSyncComplete(byte[] data) =>
    (BitConverter.ToInt32(data, 0), BitConverter.ToInt64(data, 4), BitConverter.ToInt32(data, 12), BitConverter.ToInt64(data, 16));
```

**Add new DeleteFile and DeleteConfirm methods (at end of class, before the closing brace):**

```csharp
public static byte[] SerializeDeleteFile(string relativePath, bool backupFirst)
{
    var pathBytes = Encoding.UTF8.GetBytes(relativePath);
    using var ms = new MemoryStream();
    using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
    writer.Write((short)pathBytes.Length);
    writer.Write(pathBytes);
    writer.Write((byte)(backupFirst ? 1 : 0));
    writer.Flush();
    return ms.ToArray();
}

public static (string relativePath, bool backupFirst) DeserializeDeleteFile(byte[] data)
{
    using var ms = new MemoryStream(data);
    using var reader = new BinaryReader(ms, Encoding.UTF8);
    short pathLen = reader.ReadInt16();
    var path = Encoding.UTF8.GetString(reader.ReadBytes(pathLen));
    bool backupFirst = reader.ReadByte() == 1;
    return (path, backupFirst);
}

public static byte[] SerializeDeleteConfirm(string relativePath, bool success)
{
    var pathBytes = Encoding.UTF8.GetBytes(relativePath);
    using var ms = new MemoryStream();
    using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
    writer.Write((short)pathBytes.Length);
    writer.Write(pathBytes);
    writer.Write((byte)(success ? 1 : 0));
    writer.Flush();
    return ms.ToArray();
}

public static (string relativePath, bool success) DeserializeDeleteConfirm(byte[] data)
{
    using var ms = new MemoryStream(data);
    using var reader = new BinaryReader(ms, Encoding.UTF8);
    short pathLen = reader.ReadInt16();
    var path = Encoding.UTF8.GetString(reader.ReadBytes(pathLen));
    bool success = reader.ReadByte() == 1;
    return (path, success);
}
```

- [ ] **Step 5: Fix existing tests that use old Handshake/SyncComplete signatures**

In existing tests, update any calls to `SerializeHandshake(1, true)` → `SerializeHandshake(1, 1)` and `SerializeHandshake(1, false)` → `SerializeHandshake(1, 0)`.

Similarly, update `SerializeSyncComplete(files, bytes, elapsed)` → `SerializeSyncComplete(files, bytes, 0, elapsed)`.

Search and fix in test files. The main callers are in `SyncServer.cs` and `SyncClient.cs` — those are updated in Tasks 6 and 7.

- [ ] **Step 6: Run all tests to verify they pass**

Run: `dotnet test`

Expected: All tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/RemoteFileSync/Network/MessageType.cs src/RemoteFileSync/Network/ProtocolHandler.cs tests/RemoteFileSync.Tests/Network/ProtocolHandlerTests.cs
git commit -m "feat: add DeleteFile/DeleteConfirm protocol messages and extend Handshake/SyncComplete"
```

---

## Task 6: Network — SyncServer Deletion Handling

**Files:**
- Modify: `src/RemoteFileSync/Network/SyncServer.cs`

- [ ] **Step 1: Update SyncServer to handle delete messages**

Replace `src/RemoteFileSync/Network/SyncServer.cs` with:

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Backup;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Sync;
using RemoteFileSync.Transfer;

namespace RemoteFileSync.Network;

public sealed class SyncServer
{
    private readonly SyncOptions _options;
    private readonly SyncLogger _logger;

    public SyncServer(SyncOptions options, SyncLogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, _options.Port);
        listener.Start();
        _logger.Summary($"Listening on port {_options.Port}...");

        try
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            _logger.Summary("Client connected.");
            using var stream = client.GetStream();
            return await HandleConnectionAsync(stream, ct);
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<int> HandleConnectionAsync(NetworkStream stream, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int skippedFiles = 0;

        // 1. Receive handshake
        var (hsType, hsData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        if (hsType != MessageType.Handshake)
        {
            _logger.Error($"Expected Handshake, got {hsType}");
            return 3;
        }
        var (version, syncMode) = ProtocolHandler.DeserializeHandshake(hsData);
        bool bidirectional = (syncMode & 1) != 0;
        bool deleteEnabled = (syncMode & 2) != 0;
        _logger.Info($"Handshake: v{version}, {(bidirectional ? "bidirectional" : "unidirectional")}{(deleteEnabled ? " + delete" : "")}");

        // 2. Send HandshakeAck
        var ackPayload = ProtocolHandler.SerializeHandshakeAck(1, accepted: true);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.HandshakeAck, ackPayload, ct);

        // 3. Receive client manifest
        var (mType, mData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var clientManifest = ProtocolHandler.DeserializeManifest(mData);
        _logger.Info($"Client manifest: {clientManifest.Count} files");

        // 4. Scan local folder and send server manifest
        var scanner = new FileScanner(_options.Folder, _options.IncludePatterns, _options.ExcludePatterns);
        var serverManifest = scanner.Scan();
        _logger.Info($"Local manifest: {serverManifest.Count} files");
        var serverManifestBytes = ProtocolHandler.SerializeManifest(serverManifest);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Manifest, serverManifestBytes, ct);

        // 5. Receive sync plan
        var (pType, pData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var syncPlan = ProtocolHandler.DeserializeSyncPlan(pData);
        _logger.Info($"Sync plan: {syncPlan.Count} actions");

        var backup = new BackupManager(_options.Folder, _options.EffectiveBackupFolder);
        var receiver = new FileTransferReceiver(_options.Folder);
        var sender = new FileTransferSender(_options.Folder, _options.BlockSize);
        int filesTransferred = 0;
        long bytesTransferred = 0;
        int filesDeleted = 0;

        // 6. Receive files from client (SendToServer + ClientOnly)
        var toReceive = syncPlan.Where(p =>
            p.Action == SyncActionType.SendToServer || p.Action == SyncActionType.ClientOnly).ToList();

        foreach (var action in toReceive)
        {
            if (action.Action == SyncActionType.SendToServer)
            {
                if (!backup.BackupFile(action.RelativePath))
                    _logger.Debug($"No existing file to backup: {action.RelativePath}");
            }

            var result = await receiver.ReceiveFileAsync(stream, ct);
            if (result.Success)
            {
                _logger.Info($"[←] {result.RelativePath}");
                filesTransferred++;
                var fi = new FileInfo(Path.Combine(_options.Folder, result.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                bytesTransferred += fi.Length;
            }
            else
            {
                _logger.Error($"Failed to receive {action.RelativePath}: {result.ErrorMessage}");
                skippedFiles++;
            }

            // Send BackupConfirm
            var confirmPayload = System.Text.Encoding.UTF8.GetBytes(action.RelativePath);
            var confirm = new byte[confirmPayload.Length + 1];
            confirmPayload.CopyTo(confirm, 0);
            confirm[^1] = (byte)(result.Success ? 1 : 0);
            await ProtocolHandler.WriteMessageAsync(stream, MessageType.BackupConfirm, confirm, ct);
        }

        // 7. Deletion Phase (Server): Receive DeleteFile from client for DeleteOnServer actions
        if (deleteEnabled)
        {
            var serverDeletes = syncPlan.Where(p => p.Action == SyncActionType.DeleteOnServer).ToList();
            foreach (var del in serverDeletes)
            {
                var (delType, delData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
                if (delType != MessageType.DeleteFile)
                {
                    _logger.Warning($"Expected DeleteFile, got {delType}");
                    skippedFiles++;
                    continue;
                }

                var (path, backupFirst) = ProtocolHandler.DeserializeDeleteFile(delData);
                bool success = false;
                try
                {
                    if (backupFirst)
                    {
                        backup.BackupFile(path); // Moves file to backup (effectively deletes from sync folder)
                    }
                    else
                    {
                        var fullPath = Path.Combine(_options.Folder, path.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(fullPath)) File.Delete(fullPath);
                    }
                    success = true;
                    filesDeleted++;
                    _logger.Info($"[DEL] {path}");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to delete {path}: {ex.Message}");
                    skippedFiles++;
                }

                var confirmPayload = ProtocolHandler.SerializeDeleteConfirm(path, success);
                await ProtocolHandler.WriteMessageAsync(stream, MessageType.DeleteConfirm, confirmPayload, ct);
            }
        }

        // 8. Send files to client (SendToClient + ServerOnly) if bidirectional
        if (bidirectional)
        {
            var toSend = syncPlan.Where(p =>
                p.Action == SyncActionType.SendToClient || p.Action == SyncActionType.ServerOnly).ToList();

            foreach (var action in toSend)
            {
                try
                {
                    short fileId = (short)(filesTransferred % short.MaxValue);
                    await sender.SendFileAsync(stream, fileId, action.RelativePath, ct);
                    _logger.Info($"[→] {action.RelativePath}");
                    filesTransferred++;
                    var (cType, _) = await ProtocolHandler.ReadMessageAsync(stream, ct);
                    if (cType != MessageType.BackupConfirm)
                        _logger.Warning($"Expected BackupConfirm, got {cType}");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to send {action.RelativePath}: {ex.Message}");
                    skippedFiles++;
                }
            }
        }

        // 9. Deletion Phase (Client): Send DeleteFile for DeleteOnClient actions
        if (deleteEnabled && bidirectional)
        {
            var clientDeletes = syncPlan.Where(p => p.Action == SyncActionType.DeleteOnClient).ToList();
            foreach (var del in clientDeletes)
            {
                var payload = ProtocolHandler.SerializeDeleteFile(del.RelativePath, backupFirst: true);
                await ProtocolHandler.WriteMessageAsync(stream, MessageType.DeleteFile, payload, ct);

                var (confType, confData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
                if (confType == MessageType.DeleteConfirm)
                {
                    var (_, success) = ProtocolHandler.DeserializeDeleteConfirm(confData);
                    if (success)
                    {
                        filesDeleted++;
                        _logger.Info($"[DEL→] Client deleted {del.RelativePath}");
                    }
                    else
                    {
                        _logger.Warning($"Client failed to delete {del.RelativePath}");
                        skippedFiles++;
                    }
                }
            }
        }

        // 10. Exchange SyncComplete
        sw.Stop();
        var completePayload = ProtocolHandler.SerializeSyncComplete(filesTransferred, bytesTransferred, filesDeleted, sw.ElapsedMilliseconds);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncComplete, completePayload, ct);
        var (scType, scData) = await ProtocolHandler.ReadMessageAsync(stream, ct);

        var deletedSummary = filesDeleted > 0 ? $", {filesDeleted} deleted" : "";
        _logger.Summary($"Sync complete: {filesTransferred} files transferred{deletedSummary}, {bytesTransferred / (1024.0 * 1024.0):F1} MB, {sw.ElapsedMilliseconds}ms");
        return skippedFiles > 0 ? 1 : 0;
    }
}
```

- [ ] **Step 2: Verify build succeeds**

Run: `dotnet build`

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/RemoteFileSync/Network/SyncServer.cs
git commit -m "feat: add deletion handling to SyncServer (receive DeleteFile, send DeleteFile for client)"
```

---

## Task 7: Network — SyncClient Deletion Handling and State Save

**Files:**
- Modify: `src/RemoteFileSync/Network/SyncClient.cs`

- [ ] **Step 1: Update SyncClient with deletion handling and state persistence**

Replace `src/RemoteFileSync/Network/SyncClient.cs` with:

```csharp
using System.Diagnostics;
using System.Net.Sockets;
using RemoteFileSync.Backup;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.State;
using RemoteFileSync.Sync;
using RemoteFileSync.Transfer;

namespace RemoteFileSync.Network;

public sealed class SyncClient
{
    private readonly SyncOptions _options;
    private readonly SyncLogger _logger;
    private readonly SyncStateManager? _stateManager;

    public SyncClient(SyncOptions options, SyncLogger logger, SyncStateManager? stateManager = null)
    {
        _options = options;
        _logger = logger;
        _stateManager = stateManager;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        using var tcp = new TcpClient();
        int retries = 3;

        for (int attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
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
                _logger.Error($"Connection failed after {retries} attempts: {ex.Message}");
                return 2;
            }
        }

        var modeLabel = _options.Bidirectional ? "Bi-directional" : "Uni-directional";
        var deleteLabel = _options.DeleteEnabled ? " + delete" : "";
        _logger.Summary($"Connected. {modeLabel} sync{deleteLabel}." +
            (_options.Verbose ? $" Block: {_options.BlockSize / 1024}KB, Threads: {_options.MaxThreads}" : ""));

        using var stream = tcp.GetStream();
        return await HandleConnectionAsync(stream, ct);
    }

    private async Task<int> HandleConnectionAsync(NetworkStream stream, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int skippedFiles = 0;

        // 1. Send handshake with syncMode
        byte syncMode = (byte)((_options.Bidirectional ? 1 : 0) | (_options.DeleteEnabled ? 2 : 0));
        var hsPayload = ProtocolHandler.SerializeHandshake(1, syncMode);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Handshake, hsPayload, ct);

        // 2. Receive HandshakeAck
        var (ackType, ackData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        if (ackType != MessageType.HandshakeAck)
        {
            _logger.Error($"Expected HandshakeAck, got {ackType}");
            return 3;
        }
        var (_, accepted) = ProtocolHandler.DeserializeHandshakeAck(ackData);
        if (!accepted)
        {
            _logger.Error("Server rejected the connection.");
            return 2;
        }

        // 3. Load previous state (if delete enabled)
        SyncState? previousState = null;
        if (_options.DeleteEnabled && _stateManager != null)
        {
            previousState = _stateManager.LoadState(_options.Folder, _options.Host!, _options.Port);
            if (previousState == null)
                _logger.Info("No previous sync state found. First run with --delete: fully additive.");
            else
                _logger.Info($"Loaded sync state: {previousState.Manifest.Count} files from {previousState.LastSyncUtc:u}");
        }

        // 4. Scan local folder and send client manifest
        var scanner = new FileScanner(_options.Folder, _options.IncludePatterns, _options.ExcludePatterns);
        var clientManifest = scanner.Scan();
        _logger.Info($"Local manifest: {clientManifest.Count} files");
        var clientManifestBytes = ProtocolHandler.SerializeManifest(clientManifest);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Manifest, clientManifestBytes, ct);

        // 5. Receive server manifest
        var (mType, mData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var serverManifest = ProtocolHandler.DeserializeManifest(mData);
        _logger.Info($"Remote manifest: {serverManifest.Count} files");

        // 6. Compute sync plan (with deletion detection if enabled)
        var syncPlan = SyncEngine.ComputePlan(
            clientManifest, serverManifest, _options.Bidirectional,
            previousState, _options.DeleteEnabled);

        var transferCount = syncPlan.Count(p => p.Action != SyncActionType.Skip
            && p.Action != SyncActionType.DeleteOnServer && p.Action != SyncActionType.DeleteOnClient);
        var deleteCount = syncPlan.Count(p => p.Action == SyncActionType.DeleteOnServer || p.Action == SyncActionType.DeleteOnClient);
        var skipCount = syncPlan.Count(p => p.Action == SyncActionType.Skip);
        var deleteSummary = deleteCount > 0 ? $", {deleteCount} delete" : "";
        _logger.Info($"Sync plan: {transferCount} transfers{deleteSummary}, {skipCount} skipped");

        var planBytes = ProtocolHandler.SerializeSyncPlan(syncPlan);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncPlan, planBytes, ct);

        var backup = new BackupManager(_options.Folder, _options.EffectiveBackupFolder);
        var sender = new FileTransferSender(_options.Folder, _options.BlockSize);
        var receiver = new FileTransferReceiver(_options.Folder);
        int filesTransferred = 0;
        long bytesTransferred = 0;
        int filesDeleted = 0;

        // 7. Send files to server (SendToServer + ClientOnly)
        var toSend = syncPlan.Where(p =>
            p.Action == SyncActionType.SendToServer || p.Action == SyncActionType.ClientOnly).ToList();

        foreach (var action in toSend)
        {
            try
            {
                short fileId = (short)(filesTransferred % short.MaxValue);
                await sender.SendFileAsync(stream, fileId, action.RelativePath, ct);
                _logger.Info($"[→] {action.RelativePath}");
                filesTransferred++;
                var fi = new FileInfo(Path.Combine(_options.Folder, action.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                bytesTransferred += fi.Length;
                var (cType, _) = await ProtocolHandler.ReadMessageAsync(stream, ct);
                if (cType != MessageType.BackupConfirm)
                    _logger.Warning($"Expected BackupConfirm, got {cType}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to send {action.RelativePath}: {ex.Message}");
                skippedFiles++;
            }
        }

        // 8. Deletion Phase (Server): Send DeleteFile for DeleteOnServer actions
        if (_options.DeleteEnabled)
        {
            var serverDeletes = syncPlan.Where(p => p.Action == SyncActionType.DeleteOnServer).ToList();
            foreach (var del in serverDeletes)
            {
                var payload = ProtocolHandler.SerializeDeleteFile(del.RelativePath, backupFirst: true);
                await ProtocolHandler.WriteMessageAsync(stream, MessageType.DeleteFile, payload, ct);

                var (confType, confData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
                if (confType == MessageType.DeleteConfirm)
                {
                    var (_, success) = ProtocolHandler.DeserializeDeleteConfirm(confData);
                    if (success)
                    {
                        filesDeleted++;
                        _logger.Info($"[DEL→] {del.RelativePath} (deleted on server)");
                    }
                    else
                    {
                        _logger.Warning($"Server failed to delete {del.RelativePath}");
                        skippedFiles++;
                    }
                }
            }
        }

        // 9. Receive files from server (SendToClient + ServerOnly) if bidirectional
        if (_options.Bidirectional)
        {
            var toReceive = syncPlan.Where(p =>
                p.Action == SyncActionType.SendToClient || p.Action == SyncActionType.ServerOnly).ToList();

            foreach (var action in toReceive)
            {
                if (action.Action == SyncActionType.SendToClient)
                {
                    if (!backup.BackupFile(action.RelativePath))
                        _logger.Debug($"No existing file to backup: {action.RelativePath}");
                }

                var result = await receiver.ReceiveFileAsync(stream, ct);
                if (result.Success)
                {
                    _logger.Info($"[←] {result.RelativePath}");
                    filesTransferred++;
                    var fi = new FileInfo(Path.Combine(_options.Folder, result.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                    bytesTransferred += fi.Length;
                }
                else
                {
                    _logger.Error($"Failed to receive {action.RelativePath}: {result.ErrorMessage}");
                    skippedFiles++;
                }

                var confirmPayload = System.Text.Encoding.UTF8.GetBytes(action.RelativePath);
                var confirm = new byte[confirmPayload.Length + 1];
                confirmPayload.CopyTo(confirm, 0);
                confirm[^1] = (byte)(result.Success ? 1 : 0);
                await ProtocolHandler.WriteMessageAsync(stream, MessageType.BackupConfirm, confirm, ct);
            }
        }

        // 10. Deletion Phase (Client): Receive DeleteFile for DeleteOnClient actions
        if (_options.DeleteEnabled && _options.Bidirectional)
        {
            var clientDeletes = syncPlan.Where(p => p.Action == SyncActionType.DeleteOnClient).ToList();
            foreach (var del in clientDeletes)
            {
                var (delType, delData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
                if (delType != MessageType.DeleteFile)
                {
                    _logger.Warning($"Expected DeleteFile, got {delType}");
                    skippedFiles++;
                    continue;
                }

                var (path, backupFirst) = ProtocolHandler.DeserializeDeleteFile(delData);
                bool success = false;
                try
                {
                    if (backupFirst)
                    {
                        backup.BackupFile(path);
                    }
                    else
                    {
                        var fullPath = Path.Combine(_options.Folder, path.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(fullPath)) File.Delete(fullPath);
                    }
                    success = true;
                    filesDeleted++;
                    _logger.Info($"[DEL] {path} (deleted locally)");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to delete {path}: {ex.Message}");
                    skippedFiles++;
                }

                var confirmPayload = ProtocolHandler.SerializeDeleteConfirm(path, success);
                await ProtocolHandler.WriteMessageAsync(stream, MessageType.DeleteConfirm, confirmPayload, ct);
            }
        }

        // 11. Save state (only on full success)
        int exitCode = skippedFiles > 0 ? 1 : 0;
        if (exitCode == 0 && _options.DeleteEnabled && _stateManager != null)
        {
            var mergedManifest = SyncEngine.BuildMergedManifest(clientManifest, serverManifest, syncPlan);
            _stateManager.SaveState(_options.Folder, _options.Host!, _options.Port, mergedManifest, DateTime.UtcNow);
            _logger.Debug($"Sync state saved: {mergedManifest.Count} files");
        }

        // 12. Exchange SyncComplete
        sw.Stop();
        var completePayload = ProtocolHandler.SerializeSyncComplete(filesTransferred, bytesTransferred, filesDeleted, sw.ElapsedMilliseconds);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncComplete, completePayload, ct);
        var (scType, scData) = await ProtocolHandler.ReadMessageAsync(stream, ct);

        var deletedLabel = filesDeleted > 0 ? $", {filesDeleted} deleted" : "";
        _logger.Summary($"Sync complete: {filesTransferred} files transferred{deletedLabel}, {bytesTransferred / (1024.0 * 1024.0):F1} MB, {sw.ElapsedMilliseconds}ms");
        return exitCode;
    }
}
```

- [ ] **Step 2: Verify build succeeds**

Run: `dotnet build`

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/RemoteFileSync/Network/SyncClient.cs
git commit -m "feat: add deletion handling and state persistence to SyncClient"
```

---

## Task 8: CLI — Parse --delete/-d Flag

**Files:**
- Modify: `src/RemoteFileSync/Program.cs`
- Modify: `tests/RemoteFileSync.Tests/CliParserTests.cs`

- [ ] **Step 1: Write the failing test for --delete flag parsing**

Add to `tests/RemoteFileSync.Tests/CliParserTests.cs`:

```csharp
// === Add these tests to the existing CliParserTests class ===

[Fact]
public void ParseArgs_DeleteLongFlag_SetsDeleteEnabled()
{
    var args = new[] { "client", "--host", "localhost", "--folder", TestFolder, "--delete" };
    var opts = Program.ParseArgs(args);
    Assert.True(opts.DeleteEnabled);
}

[Fact]
public void ParseArgs_DeleteShortFlag_SetsDeleteEnabled()
{
    var args = new[] { "client", "--host", "localhost", "--folder", TestFolder, "-d" };
    var opts = Program.ParseArgs(args);
    Assert.True(opts.DeleteEnabled);
}

[Fact]
public void ParseArgs_NoDeleteFlag_DefaultsFalse()
{
    var args = new[] { "client", "--host", "localhost", "--folder", TestFolder };
    var opts = Program.ParseArgs(args);
    Assert.False(opts.DeleteEnabled);
}
```

Note: `TestFolder` should reference whatever test folder path the existing tests use. If the existing CliParserTests create a temp directory, use the same approach.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "CliParserTests"`

Expected: Tests for `--delete` and `-d` FAIL — unknown option.

- [ ] **Step 3: Update Program.cs to parse --delete/-d and wire up SyncStateManager**

In `src/RemoteFileSync/Program.cs`, add the `--delete` / `-d` case to the switch statement in `ParseArgs`:

```csharp
case "--delete" or "-d":
    options.DeleteEnabled = true;
    break;
```

Insert this case before the `default:` case in the switch block.

Also update the `PrintUsage` method to add the new flag:

Add this line after the `--bidirectional` line:

```csharp
Console.Error.WriteLine("  --delete, -d            Enable deletion propagation (opt-in)");
```

Finally, update the `Main` method to pass `SyncStateManager` to `SyncClient` when `--delete` is enabled. Replace the client instantiation block:

```csharp
else
{
    SyncStateManager? stateManager = null;
    if (options.DeleteEnabled)
        stateManager = new SyncStateManager(SyncStateManager.DefaultBaseDir);
    var client = new Network.SyncClient(options, logger, stateManager);
    return await client.RunAsync(cts.Token);
}
```

Add the using directive at the top of Program.cs:

```csharp
using RemoteFileSync.State;
```

- [ ] **Step 4: Run all tests to verify they pass**

Run: `dotnet test`

Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteFileSync/Program.cs tests/RemoteFileSync.Tests/CliParserTests.cs
git commit -m "feat: add --delete/-d CLI flag and wire up SyncStateManager"
```

---

## Task 9: Integration Tests — E2E Deletion Sync

**Files:**
- Create: `tests/RemoteFileSync.Tests/Integration/DeleteSyncTests.cs`

- [ ] **Step 1: Write the E2E deletion tests**

Create `tests/RemoteFileSync.Tests/Integration/DeleteSyncTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.Integration;

public class DeleteSyncTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _serverDir;
    private readonly string _clientDir;
    private readonly string _stateDir;

    public DeleteSyncTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"rfs_del_e2e_{Guid.NewGuid()}");
        _serverDir = Path.Combine(_testRoot, "server");
        _clientDir = Path.Combine(_testRoot, "client");
        _stateDir = Path.Combine(_testRoot, "state");
        Directory.CreateDirectory(_serverDir);
        Directory.CreateDirectory(_clientDir);
        Directory.CreateDirectory(_stateDir);
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

    private async Task<(int clientResult, int serverResult)> RunSyncAsync(int port, bool bidirectional, bool deleteEnabled, SyncStateManager? stateManager = null)
    {
        var serverOpts = new SyncOptions { IsServer = true, Port = port, Folder = _serverDir, DeleteEnabled = deleteEnabled };
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Bidirectional = bidirectional, DeleteEnabled = deleteEnabled };

        using var serverLogger = new SyncLogger(false, null);
        using var clientLogger = new SyncLogger(false, null);

        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger, stateManager);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(500);
        var clientResult = await client.RunAsync(cts.Token);
        var serverResult = await serverTask;
        return (clientResult, serverResult);
    }

    [Fact]
    public async Task DeleteSync_FirstRun_NoState_AdditiveOnly()
    {
        // First sync with --delete: both sides have files, should copy, not delete
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "client-file.txt", "from client", ts);
        CreateFileWithTimestamp(_serverDir, "server-file.txt", "from server", ts);

        int port = GetFreePort();
        var stateManager = new SyncStateManager(_stateDir);

        var (clientResult, serverResult) = await RunSyncAsync(port, bidirectional: true, deleteEnabled: true, stateManager);

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        // Both files should exist on both sides (additive, no deletions)
        Assert.True(File.Exists(Path.Combine(_serverDir, "client-file.txt")));
        Assert.True(File.Exists(Path.Combine(_clientDir, "server-file.txt")));
        // State file should have been saved
        var statePath = stateManager.GetStatePath(_clientDir, "127.0.0.1", port);
        Assert.True(File.Exists(statePath));
    }

    [Fact]
    public async Task DeleteSync_Case1_PropagatesDeletion()
    {
        var beforeSync = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var lastSync = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);

        // Setup: Both sides have file, saved in state
        CreateFileWithTimestamp(_serverDir, "to-delete.txt", "will be deleted", beforeSync);
        // Client does NOT have the file (simulates deletion after last sync)

        // Seed the state file: file existed on both sides at last sync
        int port = GetFreePort();
        var stateManager = new SyncStateManager(_stateDir);
        var stateManifest = new FileManifest();
        stateManifest.Add(new FileEntry("to-delete.txt", 15, beforeSync));
        stateManager.SaveState(_clientDir, "127.0.0.1", port, stateManifest, lastSync);

        var (clientResult, serverResult) = await RunSyncAsync(port, bidirectional: true, deleteEnabled: true, stateManager);

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        // File should be deleted from server
        Assert.False(File.Exists(Path.Combine(_serverDir, "to-delete.txt")));
        // File should be backed up on server
        var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
        Assert.True(File.Exists(Path.Combine(_serverDir, dateStr, "to-delete.txt")));
    }

    [Fact]
    public async Task DeleteSync_Case2_RestoresModifiedFile()
    {
        var beforeSync = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var lastSync = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
        var afterSync = new DateTime(2026, 3, 27, 8, 0, 0, DateTimeKind.Utc);

        // Server has a MODIFIED version (after last sync)
        CreateFileWithTimestamp(_serverDir, "modified.txt", "modified content", afterSync);
        // Client does NOT have the file (deleted it)

        // Seed state
        int port = GetFreePort();
        var stateManager = new SyncStateManager(_stateDir);
        var stateManifest = new FileManifest();
        stateManifest.Add(new FileEntry("modified.txt", 16, beforeSync));
        stateManager.SaveState(_clientDir, "127.0.0.1", port, stateManifest, lastSync);

        var (clientResult, serverResult) = await RunSyncAsync(port, bidirectional: true, deleteEnabled: true, stateManager);

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        // Server file should still exist (not deleted)
        Assert.True(File.Exists(Path.Combine(_serverDir, "modified.txt")));
        // Client should have received the restored file
        Assert.True(File.Exists(Path.Combine(_clientDir, "modified.txt")));
        Assert.Equal("modified content", File.ReadAllText(Path.Combine(_clientDir, "modified.txt")));
    }

    [Fact]
    public async Task DeleteSync_BidiSymmetric()
    {
        var beforeSync = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var lastSync = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);

        // client-deleted.txt: exists on server (untouched), deleted on client → delete on server
        CreateFileWithTimestamp(_serverDir, "client-deleted.txt", "from server", beforeSync);
        // server-deleted.txt: exists on client (untouched), deleted on server → delete on client
        CreateFileWithTimestamp(_clientDir, "server-deleted.txt", "from client", beforeSync);

        int port = GetFreePort();
        var stateManager = new SyncStateManager(_stateDir);
        var stateManifest = new FileManifest();
        stateManifest.Add(new FileEntry("client-deleted.txt", 11, beforeSync));
        stateManifest.Add(new FileEntry("server-deleted.txt", 11, beforeSync));
        stateManager.SaveState(_clientDir, "127.0.0.1", port, stateManifest, lastSync);

        var (clientResult, serverResult) = await RunSyncAsync(port, bidirectional: true, deleteEnabled: true, stateManager);

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        // Both files should be deleted from their respective sides
        Assert.False(File.Exists(Path.Combine(_serverDir, "client-deleted.txt")));
        Assert.False(File.Exists(Path.Combine(_clientDir, "server-deleted.txt")));
    }

    [Fact]
    public async Task DeleteSync_UniDirectional_ServerDeletionIgnored()
    {
        var beforeSync = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var lastSync = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);

        // Server deleted a file, client still has it (untouched)
        CreateFileWithTimestamp(_clientDir, "file.txt", "still here", beforeSync);
        // Server does NOT have the file

        int port = GetFreePort();
        var stateManager = new SyncStateManager(_stateDir);
        var stateManifest = new FileManifest();
        stateManifest.Add(new FileEntry("file.txt", 10, beforeSync));
        stateManager.SaveState(_clientDir, "127.0.0.1", port, stateManifest, lastSync);

        // Uni-directional: server is NOT authoritative, its deletions should be ignored
        var (clientResult, serverResult) = await RunSyncAsync(port, bidirectional: false, deleteEnabled: true, stateManager);

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        // Client file should still exist (not deleted)
        Assert.True(File.Exists(Path.Combine(_clientDir, "file.txt")));
        // File should have been pushed to server (ClientOnly action)
        Assert.True(File.Exists(Path.Combine(_serverDir, "file.txt")));
    }

    [Fact]
    public async Task DeleteSync_SecondRun_DetectsDeletions()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);

        // --- Run 1: Establish state ---
        CreateFileWithTimestamp(_clientDir, "keep.txt", "keep this", ts);
        CreateFileWithTimestamp(_serverDir, "keep.txt", "keep this", ts);
        CreateFileWithTimestamp(_clientDir, "will-delete.txt", "will be deleted", ts);
        CreateFileWithTimestamp(_serverDir, "will-delete.txt", "will be deleted", ts);

        int port = GetFreePort();
        var stateManager = new SyncStateManager(_stateDir);

        var (r1c, r1s) = await RunSyncAsync(port, bidirectional: true, deleteEnabled: true, stateManager);
        Assert.Equal(0, r1c);
        Assert.Equal(0, r1s);

        // --- Between syncs: client deletes a file ---
        File.Delete(Path.Combine(_clientDir, "will-delete.txt"));

        // --- Run 2: Should detect deletion and propagate ---
        int port2 = GetFreePort();
        var (r2c, r2s) = await RunSyncAsync(port2, bidirectional: true, deleteEnabled: true, stateManager);
        Assert.Equal(0, r2c);
        Assert.Equal(0, r2s);

        // will-delete.txt should be gone from server
        Assert.False(File.Exists(Path.Combine(_serverDir, "will-delete.txt")));
        // keep.txt should still exist on both
        Assert.True(File.Exists(Path.Combine(_clientDir, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "keep.txt")));
    }
}
```

- [ ] **Step 2: Run integration tests**

Run: `dotnet test --filter "DeleteSyncTests"`

Expected: All 7 tests pass.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test`

Expected: All tests pass (existing + new).

- [ ] **Step 4: Commit**

```bash
git add tests/RemoteFileSync.Tests/Integration/DeleteSyncTests.cs
git commit -m "test: add E2E integration tests for deletion sync scenarios"
```

---

## Task 10: Final Verification and Push

- [ ] **Step 1: Run full build and test suite**

```bash
cd E:\RemoteFileSync
dotnet build
dotnet test
```

Expected: Build succeeded, all tests pass.

- [ ] **Step 2: Update the design and usage documents**

Update `Plans/2026-03-26-remote-file-sync-design.md` section 5.1 to add the new action types, and section 4.3 to add the new message types. Update section 3.3 to reference the `--delete` option.

Update `Plans/2026-03-26-remote-file-sync-usage-guide.md` to add a new usage scenario for deletion sync.

- [ ] **Step 3: Commit documentation updates**

```bash
git add Plans/
git commit -m "docs: update design spec and usage guide with deletion sync feature"
```

- [ ] **Step 4: Push the branch**

```bash
git push -u origin feature/deletion-sync
```

---

## Self-Review Checklist

| Check | Result |
|-------|--------|
| All spec sections covered by tasks? | Yes — State (T2), Algorithm (T3-4), Protocol (T5), Network (T6-7), CLI (T8), Tests (T9) |
| No TBD/TODO/placeholders? | Yes — all code is complete |
| Type names consistent across tasks? | `SyncState`, `SyncStateManager`, `SyncActionType.DeleteOnServer/DeleteOnClient`, `ResolveDeleteConflict`, `BuildMergedManifest` — consistent |
| Method signatures match? | `ComputePlan(client, server, bidi, state, delete)` — used identically in T4, T7 |
| Protocol wire format matches spec? | `0x0A`/`0x0B`, Handshake syncMode byte, SyncComplete 24-byte layout — matches design doc |
| Existing tests won't break? | Handshake signature change requires updating callers in T5-T7; SyncComplete signature change similar |
| Each task independently testable? | Yes — TDD per task with commits |
