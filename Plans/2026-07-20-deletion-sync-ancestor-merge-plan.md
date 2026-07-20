# Deletion Synchronisation Redesign — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Date:** 2026-07-20
**Branch:** `feat/deletion-sync-ancestor-merge`
**Base:** `main` @ `bf8a1fb` (security & sync-correctness remediation)

**Goal:** Replace timestamp-guessing deletion sync with an ancestor-based three-way merge, so that a file's disappearance can be told apart from a file that was never there — and so neither side's edits are ever silently discarded.

**Architecture:** The client-side SQLite `files` table becomes a true *common ancestor*: it records, per path, what **both** sides looked like at the last successful sync. Every decision is then "did this side change relative to the ancestor?" rather than "which side has the newer clock?". That single change makes deletion detection correct, makes concurrent-edit conflicts detectable for the first time, and makes the whole engine immune to clock skew between the two machines. Deletions, overwrites and conflict losers are preserved in a per-session archive tree that can be restored one sync run at a time.

**Tech Stack:** C# / .NET 10, xUnit, `Microsoft.Data.Sqlite`, custom length-prefixed TCP protocol.

---

## Why this plan exists

The current engine decides deletions with this predicate (`src/RemoteFileSync/Sync/ConflictResolver.cs:33`):

```csharp
bool untouched = survivingEntry.LastModifiedUtc <= lastSyncUtc + TimestampTolerance;
```

`lastSyncUtc` is the wall-clock time the previous sync *ran*. So "untouched" really means "this file's mtime predates our last conversation" — which is true of almost every file, including ones that have nothing to do with the sync. A file extracted from an archive or restored from backup carries an old mtime; if the peer deleted that path, this predicate declares the file untouched and deletes it. That is silent data loss, and it is reachable today.

The fix is to compare each side's current state against **what we recorded for that side**, not against a clock reading. That is what the rest of this plan builds.

### The four defects being closed

| # | Defect | Evidence | Closed by |
|---|---|---|---|
| D1 | "Unmodified" is tested against the last-sync wall clock, not the recorded file state | `ConflictResolver.cs:33`, called from `SyncEngine.cs:144,181` | Phase 4 |
| D2 | One `last_modified` column cannot represent two filesystems, so a `Skip`ped file records only one side; the other side then looks modified forever | `SyncDatabase.cs:77`, `MarkSynced` hardcodes `side='both'` at `:255` | Phase 3 |
| D3 | Cross-machine mtime subtraction makes two-way sync collapse into one-way under clock skew | `ConflictResolver.cs:11-18` | Phase 2 + Phase 4 |
| D4 | Concurrent edits on both sides silently discard one of them | `ConflictResolver.Resolve` always picks a winner | Phase 5 |

### Design decisions already settled

These were decided during brainstorming and are **not** open for reinterpretation during implementation:

| Decision | Choice | Rationale |
|---|---|---|
| Database topology | **Client-only.** Server stays stateless. | The client already holds both manifests and computes the plan, so it is the only party that needs the ancestor. Two independently-written tables with no consensus protocol diverge on any crash, and then disagree about what to delete. |
| "Unmodified" predicate | **`size` equal AND `mtime` within the existing 2s tolerance** | Exact tick equality makes every file on FAT/exFAT/SMB look modified, which silently stops deletions from ever propagating. The size check costs nothing and catches in-place writes that preserve mtime. |
| Both sides changed | **Keep both; rename the loser** | No timestamp rule can resolve a genuine concurrent edit. Preserving both and reporting it is the only answer that never loses work. |
| Missing / corrupt DB | **Additive only; `--mirror` required to delete** | A corrupt DB currently reads as an empty DB, and the blast-radius guard is inert at zero tracked rows — so the "no state" path is exactly where an unbounded delete would land. |
| Archive layout | **`<root>/<session>/<reason>/<path>`** | One directory per sync run answers "what did this run do to my files", and makes a single run restorable in isolation. |
| Retention | **Age + size cap, pruned at session start** | Pruning before anything is written means a full disk never blocks the sync that is trying to preserve files. |
| Mode 1 (Pull) | **In scope** | The mode set only makes sense complete, and a half-implemented Pull that plans deletions then silently drops them (`SyncClient.cs:405`) is worse than none. |

---

## Global Constraints

Every task's requirements implicitly include this section.

- **Target framework:** `net10.0` (`net10.0-windows` for ExecRFS). Do not change target frameworks.
- **Branch:** all work lands on `feat/deletion-sync-ancestor-merge`. Commit **and push** at the end of every phase.
- **Green gate:** `dotnet build -c Release` must report **0 errors** and `dotnet test -c Release` must be fully green before every commit. The baseline at branch point is **260 passing, 0 failing**.
- **No new external dependencies.** `Microsoft.Data.Sqlite` is already referenced; nothing else may be added.
- **Wire protocol is v3.** Both peers must run the same build. A version mismatch is rejected at handshake — never silently tolerated.
- **Ancestor comparisons never cross machines.** Client state is only ever compared to `client_*` columns, server state only to `server_*` columns. Any code that subtracts one machine's mtime from the other's belongs solely to the no-ancestor fallback path.
- **Deletions are opt-in and bounded.** `--delete` gates them at all; the percentage guard bounds them; `--mirror` is required when there is no ancestor. Never weaken a guard to make a test pass — if a guard fires in a test, either the test scenario or the guard's threshold logic is wrong.
- **Nothing is destroyed without a copy.** Every delete, overwrite and conflict-loser is archived first. Copy-then-delete ordering, never move.
- **Path containment.** Any path that arrives from the network passes `PathGuard.TryResolveWithinRoot` before it reaches the filesystem. This includes archive destinations.
- **Comment style:** this codebase explains *why* a guard exists, usually naming the failure it prevents. Match that. Do not add narration comments.
- **`[Obsolete]` `SyncStateManager`** stays in the tree for binary-state migration. Its 18 build warnings are expected and must not grow.

---

## Phase overview

| Phase | Deliverable | Risk |
|---|---|---|
| 1 | `SyncMode` enum, `SyncOptions`, CLI flags | Medium — removing the `Bidirectional` setter breaks every assignment |
| 2 | Protocol v3 handshake + `ClockSkew` | Medium — wire format change, existing tests break |
| 3 | Schema v2, migration, `PairMarker` | **High** — a bad migration corrupts real user state |
| 4 | `ChangeDetector` + ancestor merge engine | **High** — this is the correctness core |
| 5 | Conflict keep-both execution | **High** — a wrong wire encoding desyncs the stream |
| 6 | `ArchiveManager` + retention | Medium — `Prune` deletes directories |
| 7 | Mode dispatch, Pull, reworked guards | High — touches every safety gate |
| 8 | End-of-sync review report | Low |
| 9 | E2E tests + documentation | Low |

Phases 3, 4 and 5 are the ones to slow down on. Phase 3 because it rewrites state users already have on disk; Phase 4 because every deletion decision flows through it; Phase 5 because an asymmetric plan interpretation between the two peers misaligns the frame stream, and the resulting corruption is silent.

---

# Implementation phases

## Phase 1: SyncMode, SyncOptions archive settings, and CLI parsing

**Goal:** Replace the boolean `Bidirectional` switch with a three-valued `SyncMode`, add the archive/mirror/skew settings to `SyncOptions`, and wire the new CLI flags — leaving `Bidirectional` as a read-only compatibility shim.

**Files:**
- Create: `src/RemoteFileSync/Models/SyncMode.cs`
- Modify: `src/RemoteFileSync/Models/SyncOptions.cs:9`, `src/RemoteFileSync/Models/SyncOptions.cs:60-81`, `src/RemoteFileSync/Models/SyncOptions.cs:113-121`
- Modify: `src/RemoteFileSync/Program.cs:103-109`, `src/RemoteFileSync/Program.cs:136-138`, `src/RemoteFileSync/Program.cs:197-199`, `src/RemoteFileSync/Program.cs:205-206`
- Modify (compile fix only): `tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs:52`, `:86`, `:126`, `:158`
- Modify (compile fix only): `tests/RemoteFileSync.Tests/Integration/DeleteSyncTests.cs:53`
- Modify (compile fix only): `tests/RemoteFileSync.Tests/Integration/DatabaseDeleteSyncTests.cs:56`
- Modify (compile fix only): `tests/RemoteFileSync.Tests/Integration/DeleteThresholdTests.cs:53`
- Test: `tests/RemoteFileSync.Tests/Models/SyncOptionsTests.cs`
- Test: `tests/RemoteFileSync.Tests/CliParserTests.cs`

**Interfaces:**
- Consumes: nothing from earlier phases (this is the first phase).
- Produces:
  - `public enum SyncMode : byte { Push = 1, Pull = 2, TwoWay = 3 }`
  - `SyncOptions.Mode { get; set; }` — `public SyncMode Mode { get; set; } = SyncMode.Push;`
  - `SyncOptions.Bidirectional` — `public bool Bidirectional => Mode == SyncMode.TwoWay;` (read-only)
  - `SyncOptions.MirrorDeletes { get; set; }` — `public bool MirrorDeletes { get; set; }`
  - `SyncOptions.ArchiveFolder { get; set; }` — `public string? ArchiveFolder { get; set; }`
  - `SyncOptions.EffectiveArchiveFolder { get; }` — `public string EffectiveArchiveFolder { get; }`
  - `SyncOptions.ArchiveKeepDays { get; set; }` — `public int ArchiveKeepDays { get; set; } = 30;`
  - `SyncOptions.ArchiveMaxBytes { get; set; }` — `public long ArchiveMaxBytes { get; set; }`
  - `SyncOptions.SuspiciousSkewSeconds` — `public const int SuspiciousSkewSeconds = 60;`

---

### Task 1.1: `SyncMode` enum, `Mode` property, and the read-only `Bidirectional` shim

This task is atomic by necessity: the moment the `Bidirectional` setter is removed, every assignment to it stops compiling. All call sites are fixed in Step 3 of this task.

- [ ] **Step 1: Write the failing test**

Append to `tests/RemoteFileSync.Tests/Models/SyncOptionsTests.cs`, before the closing brace of the class (currently at line 89):

```csharp
    [Fact]
    public void Mode_DefaultsToPush()
    {
        var options = new SyncOptions { IsServer = true, Folder = _syncDir };

        Assert.Equal(SyncMode.Push, options.Mode);
    }

    [Theory]
    [InlineData(SyncMode.Push, false)]
    [InlineData(SyncMode.Pull, false)]
    [InlineData(SyncMode.TwoWay, true)]
    public void Bidirectional_TracksMode(SyncMode mode, bool expected)
    {
        // Bidirectional is a read-only shim over Mode. A settable copy would let the two
        // drift apart, which is how a Pull sync could silently start writing to the server.
        var options = new SyncOptions { IsServer = true, Folder = _syncDir, Mode = mode };

        Assert.Equal(expected, options.Bidirectional);
    }

    [Fact]
    public void SyncMode_ValuesAreStableWireNumbers()
    {
        // These numbers travel in the handshake's low 2 bits; renumbering them silently
        // repoints an existing peer's sync direction.
        Assert.Equal(1, (byte)SyncMode.Push);
        Assert.Equal(2, (byte)SyncMode.Pull);
        Assert.Equal(3, (byte)SyncMode.TwoWay);
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Bidirectional_TracksMode"`
Expected: FAIL — build error `CS0246: The type or namespace name 'SyncMode' could not be found (are you missing a using directive or an assembly reference?)` in `SyncOptionsTests.cs`.

- [ ] **Step 3: Implement**

**3a. Create `src/RemoteFileSync/Models/SyncMode.cs`:**

```csharp
namespace RemoteFileSync.Models;

/// <summary>
/// Which side is authoritative for a sync. The numeric values travel in the low 2 bits of the
/// protocol handshake's syncMode byte, so they are wire format — do not renumber them.
/// </summary>
public enum SyncMode : byte
{
    /// <summary>Client -> server. The server is made to match the client; the client is never written to.</summary>
    Push = 1,

    /// <summary>Server -> client. The client is made to match the server; the server is never written to.</summary>
    Pull = 2,

    /// <summary>Both directions, with ancestor-based conflict resolution.</summary>
    TwoWay = 3,
}
```

**3b. `src/RemoteFileSync/Models/SyncOptions.cs:9` — replace:**

```csharp
    public bool Bidirectional { get; set; }
```

with:

```csharp
    public SyncMode Mode { get; set; } = SyncMode.Push;

    /// <summary>
    /// Compatibility shim for the pre-<see cref="SyncMode"/> callers. Read-only on purpose: a
    /// settable copy would let it disagree with <see cref="Mode"/>, so a Pull sync could still
    /// take the bidirectional write-to-server branches.
    /// </summary>
    public bool Bidirectional => Mode == SyncMode.TwoWay;
```

**3c. `src/RemoteFileSync/Program.cs:136-138` — replace:**

```csharp
                case "--bidirectional" or "-b":
                    options.Bidirectional = true;
                    break;
```

with:

```csharp
                case "--bidirectional" or "-b":
                    // Deprecated alias kept so existing scripts and ExecRFS profiles keep working.
                    options.Mode = SyncMode.TwoWay;
                    break;
```

**3d–3i. Every remaining assignment to `SyncOptions.Bidirectional`.** These are the complete set, found with `rg 'Bidirectional\s*=[^=]' src/ tests/` and hand-filtered to exclude `ExecRFS.Models.SyncProfile.Bidirectional`, which is a different type and is **not** changed:

| # | File:line | Current | Replacement |
|---|---|---|---|
| 3c | `src/RemoteFileSync/Program.cs:137` | `options.Bidirectional = true;` | `options.Mode = SyncMode.TwoWay;` (shown above) |
| 3d | `tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs:52` | `Bidirectional = false` | `Mode = SyncMode.Push` |
| 3e | `tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs:86` | `Bidirectional = true` | `Mode = SyncMode.TwoWay` |
| 3f | `tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs:126` | `Bidirectional = true` | `Mode = SyncMode.TwoWay` |
| 3g | `tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs:158` | `Bidirectional = bidirectional` | `Mode = bidirectional ? SyncMode.TwoWay : SyncMode.Push` |
| 3h | `tests/RemoteFileSync.Tests/Integration/DeleteSyncTests.cs:53` | `Bidirectional = bidirectional` | `Mode = bidirectional ? SyncMode.TwoWay : SyncMode.Push` |
| 3i | `tests/RemoteFileSync.Tests/Integration/DatabaseDeleteSyncTests.cs:56` | `Bidirectional = bidirectional` | `Mode = bidirectional ? SyncMode.TwoWay : SyncMode.Push` |
| 3j | `tests/RemoteFileSync.Tests/Integration/DeleteThresholdTests.cs:53` | `Bidirectional = true` | `Mode = SyncMode.TwoWay` |

**Deliberately NOT changed** (verified reads, not writes — they compile unchanged against the shim):
- `src/RemoteFileSync/Network/SyncClient.cs:73, 90, 119, 151, 152, 357, 405` — all reads of `_options.Bidirectional`. Phase 3 replaces them with `Mode`; they must keep compiling until then.
- `tests/RemoteFileSync.Tests/CliParserTests.cs:97, 116` — `Assert.True(result.Bidirectional)` reads.
- `tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs:60` — method *name* `ServerOnly_Bidirectional_ProducesServerOnlyAction`, no member access.
- `src/ExecRFS/Models/SyncProfile.cs:21`, `src/ExecRFS/Services/CommandBuilder.cs:21`, `src/ExecRFS/Components/Panels/ClientPanel.razor:44-45`, `tests/ExecRFS.Tests/Services/ProfileServiceTests.cs:28, 35`, `tests/ExecRFS.Tests/Services/CommandBuilderTests.cs:44` — all `ExecRFS.Models.SyncProfile.Bidirectional`, an unrelated settable bool on a different class. ExecRFS shells out to the CLI and keeps emitting `--bidirectional`, which still works.
- `Plans/**/*.md`, `.superpowers/**` — historical documents, not compiled.

**Exact edits for 3d–3j.**

`tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs:52` — replace:

```csharp
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Bidirectional = false };
```

with:

```csharp
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Mode = SyncMode.Push };
```

`tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs:86` and `:126` — both currently read:

```csharp
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Bidirectional = true };
```

Replace both occurrences with:

```csharp
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Mode = SyncMode.TwoWay };
```

`tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs:155-159` — replace:

```csharp
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port,
            Folder = _clientDir, Bidirectional = bidirectional
        };
```

with:

```csharp
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port,
            Folder = _clientDir, Mode = bidirectional ? SyncMode.TwoWay : SyncMode.Push
        };
```

`tests/RemoteFileSync.Tests/Integration/DeleteSyncTests.cs:53` and `tests/RemoteFileSync.Tests/Integration/DatabaseDeleteSyncTests.cs:56` — both currently read (byte-identical):

```csharp
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Bidirectional = bidirectional, DeleteEnabled = deleteEnabled };
```

Replace each with:

```csharp
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Mode = bidirectional ? SyncMode.TwoWay : SyncMode.Push, DeleteEnabled = deleteEnabled };
```

`tests/RemoteFileSync.Tests/Integration/DeleteThresholdTests.cs:50-54` — replace:

```csharp
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir,
            Bidirectional = true, DeleteEnabled = true, ForceDelete = forceDelete,
        };
```

with:

```csharp
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir,
            Mode = SyncMode.TwoWay, DeleteEnabled = true, ForceDelete = forceDelete,
        };
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Bidirectional_TracksMode|FullyQualifiedName~Mode_DefaultsToPush|FullyQualifiedName~SyncMode_ValuesAreStableWireNumbers"`
Expected: PASS

Then confirm nothing else broke: `dotnet build -c Release` — 0 errors.

---

### Task 1.2: Archive settings and `EffectiveArchiveFolder`

- [ ] **Step 1: Write the failing test**

Append to `tests/RemoteFileSync.Tests/Models/SyncOptionsTests.cs`:

```csharp
    [Fact]
    public void EffectiveArchiveFolder_DefaultsBesideSyncFolder_NotInsideIt()
    {
        var options = new SyncOptions { IsServer = true, Folder = _syncDir };

        var archive = Path.GetFullPath(options.EffectiveArchiveFolder);

        Assert.Equal(Path.Combine(_testRoot, ".rfs-archive-data"), archive);
        Assert.False(archive.StartsWith(Path.GetFullPath(_syncDir) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EffectiveArchiveFolder_HonoursExplicitOverride()
    {
        var explicitPath = Path.Combine(_testRoot, "my-archive");
        var options = new SyncOptions { IsServer = true, Folder = _syncDir, ArchiveFolder = explicitPath };

        Assert.Equal(explicitPath, options.EffectiveArchiveFolder);
    }

    [Fact]
    public void EffectiveArchiveFolder_ThrowsForDriveRoot()
    {
        // Same reasoning as the backup folder: a drive root has no parent, and defaulting into
        // the sync folder would make archived deletions re-sync and resurrect themselves.
        var root = Path.GetPathRoot(Path.GetFullPath(_syncDir))!;
        var options = new SyncOptions { IsServer = true, Folder = root };

        var ex = Assert.Throws<ArgumentException>(() => options.EffectiveArchiveFolder);
        Assert.Contains("--archive-folder", ex.Message);
    }

    [Fact]
    public void ArchiveRetention_HasSafeDefaults()
    {
        var options = new SyncOptions { IsServer = true, Folder = _syncDir };

        Assert.Equal(30, options.ArchiveKeepDays);
        Assert.Equal(0L, options.ArchiveMaxBytes);   // 0 = no size cap
        Assert.False(options.MirrorDeletes);
        Assert.Equal(60, SyncOptions.SuspiciousSkewSeconds);
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~EffectiveArchiveFolder"`
Expected: FAIL — build error `CS0117: 'SyncOptions' does not contain a definition for 'EffectiveArchiveFolder'` (and `CS0117` for `ArchiveFolder`, `ArchiveKeepDays`, `ArchiveMaxBytes`, `MirrorDeletes`, `SuspiciousSkewSeconds`).

- [ ] **Step 3: Implement**

`src/RemoteFileSync/Models/SyncOptions.cs` — insert immediately after the `EffectiveBackupFolder` property's closing brace (currently line 81) and before `public void Validate()`:

```csharp
    /// <summary>
    /// Propagate deletions from the authoritative side even when the ancestor table has no
    /// evidence the file was ever synced. Off by default: without an ancestor row a missing
    /// file is indistinguishable from a file that was simply never sent, so mirroring would
    /// delete the peer's independent work.
    /// </summary>
    public bool MirrorDeletes { get; set; }

    /// <summary>Archive destination override. See <see cref="EffectiveArchiveFolder"/>.</summary>
    public string? ArchiveFolder { get; set; }

    /// <summary>
    /// Where deleted/overwritten/conflicting files are parked before removal. Defaults to a
    /// sibling ".rfs-archive-NAME" directory OUTSIDE the sync folder — archiving inside the
    /// synced tree makes the archived copy re-scan as a new file and propagate back to the
    /// peer, resurrecting exactly the file that was just deleted.
    /// Throws when the sync folder has no parent (a drive root or UNC share root); there is
    /// no safe default in that case and the user must pass --archive-folder explicitly.
    /// </summary>
    public string EffectiveArchiveFolder
    {
        get
        {
            if (ArchiveFolder != null) return ArchiveFolder;

            var full = Path.GetFullPath(Folder).TrimEnd(Path.DirectorySeparatorChar);
            var parent = Path.GetDirectoryName(full);
            var name = Path.GetFileName(full);

            // A drive root ("E:\") or UNC share root ("\\server\share") has no parent.
            // Falling back to the sync folder here would put the archive inside the synced
            // tree, which resurrects deletions on the next run.
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
                throw new ArgumentException(
                    $"--folder '{Folder}' is a drive or share root and has no parent directory, " +
                    "so there is no safe default archive location. Pass --archive-folder explicitly " +
                    "(it must be outside the sync folder).");

            return Path.Combine(parent, $".rfs-archive-{name}");
        }
    }

    /// <summary>Prune archived sessions older than this. 0 = keep forever.</summary>
    public int ArchiveKeepDays { get; set; } = 30;

    /// <summary>Prune oldest archived sessions once the archive exceeds this size. 0 = no cap.</summary>
    public long ArchiveMaxBytes { get; set; }

    /// <summary>
    /// Clock offsets above this are reported rather than silently trusted. Newest-wins
    /// comparisons are only meaningful within a small skew; a peer an hour off would make
    /// every one of its files look newer and overwrite the whole other side.
    /// </summary>
    public const int SuspiciousSkewSeconds = 60;
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncOptionsTests"`
Expected: PASS

---

### Task 1.3: `Validate()` rejects an archive folder inside the sync folder

- [ ] **Step 1: Write the failing test**

Append to `tests/RemoteFileSync.Tests/Models/SyncOptionsTests.cs`:

```csharp
    [Fact]
    public void Validate_RejectsArchiveFolderInsideSyncFolder()
    {
        // An archive inside the synced tree re-syncs to the peer, which recreates every file
        // the archive was holding — the deletion undoes itself on the next run.
        var options = new SyncOptions
        {
            IsServer = true,
            Folder = _syncDir,
            ArchiveFolder = Path.Combine(_syncDir, "archive"),
        };

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("--archive-folder", ex.Message);
        Assert.Contains("outside the sync folder", ex.Message);
    }

    [Fact]
    public void Validate_AcceptsTheDefaultArchiveFolder()
    {
        var options = new SyncOptions { IsServer = true, Folder = _syncDir };

        options.Validate();   // the default must not trip its own containment guard
    }

    [Fact]
    public void Validate_RejectsNegativeArchiveKeepDays()
    {
        // A negative keep-age makes every session older than "now + n", so the first prune
        // would empty the archive that is holding the user's only copy of deleted files.
        var options = new SyncOptions { IsServer = true, Folder = _syncDir, ArchiveKeepDays = -1 };

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("--archive-keep-days", ex.Message);
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Validate_RejectsArchiveFolderInsideSyncFolder"`
Expected: FAIL — `Assert.Throws() Failure: No exception was thrown` (Validate currently only checks the backup folder).

- [ ] **Step 3: Implement**

`src/RemoteFileSync/Models/SyncOptions.cs:113-121` — replace:

```csharp
        // Backups inside the sync folder are re-scanned as new files and propagated to the
        // peer, growing without bound. Reject that outright rather than discovering it later.
        var syncFull = Path.GetFullPath(Folder);
        if (!syncFull.EndsWith(Path.DirectorySeparatorChar)) syncFull += Path.DirectorySeparatorChar;
        var backupFull = Path.GetFullPath(EffectiveBackupFolder);
        if (backupFull.StartsWith(syncFull, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"--backup-folder must be outside the sync folder (got '{backupFull}' inside '{syncFull}'). " +
                "Backups inside the sync folder are re-synced to the peer and grow without bound.");
```

with:

```csharp
        if (ArchiveKeepDays < 0)
            throw new ArgumentException(
                $"--archive-keep-days must be >= 0 (0 = keep forever), got {ArchiveKeepDays}. " +
                "A negative age makes every archived session look expired and empties the archive " +
                "on its first prune.");
        if (ArchiveMaxBytes < 0)
            throw new ArgumentException(
                $"--archive-max-size must be >= 0 (0 = no cap), got {ArchiveMaxBytes}.");

        // Backups inside the sync folder are re-scanned as new files and propagated to the
        // peer, growing without bound. Reject that outright rather than discovering it later.
        var syncFull = Path.GetFullPath(Folder);
        if (!syncFull.EndsWith(Path.DirectorySeparatorChar)) syncFull += Path.DirectorySeparatorChar;
        var backupFull = Path.GetFullPath(EffectiveBackupFolder);
        if (backupFull.StartsWith(syncFull, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"--backup-folder must be outside the sync folder (got '{backupFull}' inside '{syncFull}'). " +
                "Backups inside the sync folder are re-synced to the peer and grow without bound.");

        // Same containment rule for the archive, but a worse failure: an archived deletion
        // sitting inside the synced tree propagates back to the peer and resurrects the file
        // that was just deleted, so the deletion silently undoes itself next run.
        var archiveFull = Path.GetFullPath(EffectiveArchiveFolder);
        if (archiveFull.StartsWith(syncFull, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"--archive-folder must be outside the sync folder (got '{archiveFull}' inside '{syncFull}'). " +
                "Archived deletions inside the sync folder are re-synced to the peer and resurrect " +
                "the files that were just deleted.");
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncOptionsTests"`
Expected: PASS

---

### Task 1.4: `--mode` parsing

- [ ] **Step 1: Write the failing test**

Append to `tests/RemoteFileSync.Tests/CliParserTests.cs`, before the closing brace of the class (currently at line 166):

```csharp
    [Theory]
    [InlineData("push", SyncMode.Push)]
    [InlineData("PUSH", SyncMode.Push)]
    [InlineData("pull", SyncMode.Pull)]
    [InlineData("Pull", SyncMode.Pull)]
    [InlineData("two-way", SyncMode.TwoWay)]
    [InlineData("TWO-WAY", SyncMode.TwoWay)]
    public void ParseArgs_ModeFlag_IsCaseInsensitive(string value, SyncMode expected)
    {
        var opts = Program.ParseArgs(new[] { "client", "--host", "h", "--folder", ".", "--mode", value });
        Assert.Equal(expected, opts.Mode);
    }

    [Theory]
    [InlineData("bidi")]
    [InlineData("twoway")]
    [InlineData("mirror")]
    [InlineData("")]
    public void ParseArgs_UnknownMode_ThrowsArgumentException(string value)
    {
        // Silently falling back to the Push default would send the user's files in the
        // opposite direction from the one they typed.
        var ex = Assert.Throws<ArgumentException>(
            () => Program.ParseArgs(new[] { "client", "--host", "h", "--folder", ".", "--mode", value }));
        Assert.Contains("--mode", ex.Message);
    }

    [Fact]
    public void ParseArgs_NoModeFlag_DefaultsToPush()
    {
        var opts = Program.ParseArgs(new[] { "client", "--host", "h", "--folder", "." });
        Assert.Equal(SyncMode.Push, opts.Mode);
        Assert.False(opts.Bidirectional);
    }

    [Theory]
    [InlineData("--bidirectional")]
    [InlineData("-b")]
    public void ParseArgs_BidirectionalAlias_SetsTwoWayMode(string flag)
    {
        // Deprecated but still accepted: existing scripts and ExecRFS profiles emit it.
        var opts = Program.ParseArgs(new[] { "client", "--host", "h", "--folder", ".", flag });
        Assert.Equal(SyncMode.TwoWay, opts.Mode);
        Assert.True(opts.Bidirectional);
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ParseArgs_ModeFlag_IsCaseInsensitive"`
Expected: FAIL — `System.ArgumentException : Unknown option: --mode` (thrown by the `default:` case at `Program.cs:179`).

- [ ] **Step 3: Implement**

**3a.** `src/RemoteFileSync/Program.cs` — insert this helper immediately after `NextInt` (which ends at line 109) and before `public static SyncOptions ParseArgs`:

```csharp
    private static SyncMode ParseMode(string[] args, ref int i)
    {
        var raw = NextValue(args, ref i, "--mode");
        return raw.ToLowerInvariant() switch
        {
            "push" => SyncMode.Push,
            "pull" => SyncMode.Pull,
            "two-way" => SyncMode.TwoWay,
            // No fallback to the default: guessing here would sync in the opposite direction
            // from the one the user asked for, which is a data-loss-shaped mistake.
            _ => throw new ArgumentException(
                $"--mode expects 'push', 'pull' or 'two-way', got '{raw}'."),
        };
    }
```

**3b.** `src/RemoteFileSync/Program.cs` — in the `ParseArgs` switch, replace the `--bidirectional` case (as rewritten in Task 1.1):

```csharp
                case "--bidirectional" or "-b":
                    // Deprecated alias kept so existing scripts and ExecRFS profiles keep working.
                    options.Mode = SyncMode.TwoWay;
                    break;
```

with:

```csharp
                case "--mode":
                    options.Mode = ParseMode(args, ref i);
                    break;
                case "--bidirectional" or "-b":
                    // Deprecated alias kept so existing scripts and ExecRFS profiles keep working.
                    options.Mode = SyncMode.TwoWay;
                    break;
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ParseArgs_ModeFlag_IsCaseInsensitive|FullyQualifiedName~ParseArgs_UnknownMode_ThrowsArgumentException|FullyQualifiedName~ParseArgs_NoModeFlag_DefaultsToPush|FullyQualifiedName~ParseArgs_BidirectionalAlias_SetsTwoWayMode"`
Expected: PASS

---

### Task 1.5: `--mirror` and the archive flags, with `ParseSize`

- [ ] **Step 1: Write the failing test**

Append to `tests/RemoteFileSync.Tests/CliParserTests.cs`:

```csharp
    [Fact]
    public void ParseArgs_MirrorFlag_SetsMirrorDeletes()
    {
        var opts = Program.ParseArgs(new[] { "client", "--host", "h", "--folder", ".", "--mirror" });
        Assert.True(opts.MirrorDeletes);
    }

    [Fact]
    public void ParseArgs_NoMirrorFlag_DefaultsFalse()
    {
        var opts = Program.ParseArgs(new[] { "client", "--host", "h", "--folder", "." });
        Assert.False(opts.MirrorDeletes);
    }

    [Fact]
    public void ParseArgs_ArchiveFolderAndRetentionFlags()
    {
        var args = new[]
        {
            "client", "--host", "h", "--folder", ".",
            "--archive-folder", @"C:\Archive",
            "--archive-keep-days", "7",
            "--archive-max-size", "512M"
        };
        var opts = Program.ParseArgs(args);

        Assert.Equal(@"C:\Archive", opts.ArchiveFolder);
        Assert.Equal(7, opts.ArchiveKeepDays);
        Assert.Equal(512L * 1024 * 1024, opts.ArchiveMaxBytes);
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("1024", 1024L)]
    [InlineData("4k", 4L * 1024)]
    [InlineData("4K", 4L * 1024)]
    [InlineData("4KB", 4L * 1024)]
    [InlineData("20m", 20L * 1024 * 1024)]
    [InlineData("20MB", 20L * 1024 * 1024)]
    [InlineData("2G", 2L * 1024 * 1024 * 1024)]
    [InlineData("2gb", 2L * 1024 * 1024 * 1024)]
    public void ParseArgs_ArchiveMaxSize_AcceptsSuffixes(string value, long expected)
    {
        var opts = Program.ParseArgs(
            new[] { "client", "--host", "h", "--folder", ".", "--archive-max-size", value });
        Assert.Equal(expected, opts.ArchiveMaxBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("M")]
    [InlineData("MB")]
    [InlineData("abc")]
    [InlineData("1.5G")]
    [InlineData("10T")]
    [InlineData("-1M")]
    [InlineData("9999999999999999999G")]
    public void ParseArgs_ArchiveMaxSize_RejectsGarbage(string value)
    {
        // A silently-zero cap reads as "no cap" and lets the archive grow until the disk fills.
        var ex = Assert.Throws<ArgumentException>(
            () => Program.ParseArgs(new[] { "client", "--host", "h", "--folder", ".", "--archive-max-size", value }));
        Assert.Contains("--archive-max-size", ex.Message);
    }

    [Theory]
    [InlineData("--mode")]
    [InlineData("--archive-folder")]
    [InlineData("--archive-keep-days")]
    [InlineData("--archive-max-size")]
    public void ParseArgs_MissingValueAfterNewFlag_ThrowsArgumentException(string flag)
    {
        Assert.Throws<ArgumentException>(() => Program.ParseArgs(new[] { "client", flag }));
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ParseArgs_ArchiveMaxSize_AcceptsSuffixes"`
Expected: FAIL — `System.ArgumentException : Unknown option: --archive-max-size` (thrown by the `default:` case at `Program.cs:179`).

- [ ] **Step 3: Implement**

**3a.** `src/RemoteFileSync/Program.cs` — insert `ParseSize` immediately after `ParseMode` (added in Task 1.4) and before `public static SyncOptions ParseArgs`:

```csharp
    /// <summary>
    /// Parses a byte count with an optional K/M/G(B) suffix, 1024-based. Sizes are typed by
    /// humans as "500M"; a bare long.Parse rejects the common case, and a lenient parse that
    /// fell back to 0 would read as "no cap" and let the archive fill the disk.
    /// </summary>
    private static long ParseSize(string[] args, ref int i, string flag)
    {
        var raw = NextValue(args, ref i, flag).Trim();
        var digits = raw;
        long multiplier = 1;

        if (digits.Length > 0 && char.ToUpperInvariant(digits[^1]) == 'B')
            digits = digits[..^1];   // accept "10MB" as well as "10M"

        if (digits.Length > 0)
        {
            switch (char.ToUpperInvariant(digits[^1]))
            {
                case 'K': multiplier = 1024L; digits = digits[..^1]; break;
                case 'M': multiplier = 1024L * 1024; digits = digits[..^1]; break;
                case 'G': multiplier = 1024L * 1024 * 1024; digits = digits[..^1]; break;
            }
        }

        if (!long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException(
                $"{flag} expects a size like 500, 4K, 20M or 2G, got '{raw}'.");
        if (value < 0)
            throw new ArgumentException($"{flag} must not be negative, got '{raw}'.");
        // Multiplying past long.MaxValue wraps negative, which Validate() then rejects with a
        // confusing message about a value the user never typed.
        if (value > long.MaxValue / multiplier)
            throw new ArgumentException($"{flag} value '{raw}' is too large for a 64-bit byte count.");

        return value * multiplier;
    }
```

**3b.** `src/RemoteFileSync/Program.cs` — in the `ParseArgs` switch, replace:

```csharp
                case "--backup-folder":
                    options.BackupFolder = NextValue(args, ref i, "--backup-folder");
                    break;
```

with:

```csharp
                case "--backup-folder":
                    options.BackupFolder = NextValue(args, ref i, "--backup-folder");
                    break;
                case "--mirror":
                    options.MirrorDeletes = true;
                    break;
                case "--archive-folder":
                    options.ArchiveFolder = NextValue(args, ref i, "--archive-folder");
                    break;
                case "--archive-keep-days":
                    options.ArchiveKeepDays = NextInt(args, ref i, "--archive-keep-days");
                    break;
                case "--archive-max-size":
                    options.ArchiveMaxBytes = ParseSize(args, ref i, "--archive-max-size");
                    break;
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~CliParserTests"`
Expected: PASS

---

### Task 1.6: `PrintUsage()` documents the new flags

`PrintUsage` writes to stderr and has no test coverage in this repo; it is verified by inspection. No test step.

- [ ] **Step 1: Implement**

`src/RemoteFileSync/Program.cs:197-199` — replace:

```csharp
        Console.Error.WriteLine("  --folder, -f <path>     Local sync folder (required)");
        Console.Error.WriteLine("  --bidirectional, -b     Enable bi-directional sync");
        Console.Error.WriteLine("  --delete, -d            Enable deletion propagation (opt-in)");
```

with:

```csharp
        Console.Error.WriteLine("  --folder, -f <path>     Local sync folder (required)");
        Console.Error.WriteLine("  --mode <m>              push | pull | two-way (default: push).");
        Console.Error.WriteLine("                          push: server is made to match the client;");
        Console.Error.WriteLine("                          pull: client is made to match the server.");
        Console.Error.WriteLine("  --bidirectional, -b     Deprecated alias for --mode two-way");
        Console.Error.WriteLine("  --delete, -d            Enable deletion propagation (opt-in)");
        Console.Error.WriteLine("  --mirror                Propagate deletions even without ancestor");
        Console.Error.WriteLine("                          evidence the file was ever synced. Makes the");
        Console.Error.WriteLine("                          peer an exact mirror — it can delete files the");
        Console.Error.WriteLine("                          peer created independently.");
```

`src/RemoteFileSync/Program.cs:205-206` (line numbers before the edit above; after it they shift by +7) — replace:

```csharp
        Console.Error.WriteLine("  --backup-folder <path>  Backup folder (default: .rfs-backups-NAME beside");
        Console.Error.WriteLine("                          the sync folder; must be outside it)");
```

with:

```csharp
        Console.Error.WriteLine("  --backup-folder <path>  Backup folder (default: .rfs-backups-NAME beside");
        Console.Error.WriteLine("                          the sync folder; must be outside it)");
        Console.Error.WriteLine("  --archive-folder <path> Archive for deleted/overwritten/conflicting files");
        Console.Error.WriteLine("                          (default: .rfs-archive-NAME beside the sync");
        Console.Error.WriteLine("                          folder; must be outside it)");
        Console.Error.WriteLine("  --archive-keep-days <n> Prune archived sessions older than n days");
        Console.Error.WriteLine("                          (default: 30; 0 = keep forever)");
        Console.Error.WriteLine("  --archive-max-size <n>  Prune oldest sessions above this total size.");
        Console.Error.WriteLine("                          Accepts a K/M/G suffix (default: 0 = no cap)");
```

- [ ] **Step 2: Verify by hand**

Run: `dotnet run -c Release --project src/RemoteFileSync -- client`
Expected: usage text lists `--mode`, `--mirror`, `--archive-folder`, `--archive-keep-days`, `--archive-max-size`; exit code 3.

---

### Phase 1 commit

```bash
git add src/RemoteFileSync/Models/SyncMode.cs \
        src/RemoteFileSync/Models/SyncOptions.cs \
        src/RemoteFileSync/Program.cs \
        tests/RemoteFileSync.Tests/Models/SyncOptionsTests.cs \
        tests/RemoteFileSync.Tests/CliParserTests.cs \
        tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs \
        tests/RemoteFileSync.Tests/Integration/DeleteSyncTests.cs \
        tests/RemoteFileSync.Tests/Integration/DatabaseDeleteSyncTests.cs \
        tests/RemoteFileSync.Tests/Integration/DeleteThresholdTests.cs
git commit -m "feat: replace Bidirectional bool with SyncMode and add archive options

Bidirectional could only express push-or-two-way, so a pull sync had no
representation at all. Mode carries push/pull/two-way; Bidirectional stays as a
read-only shim (Mode == TwoWay) so SyncClient and the CLI keep compiling until
they are migrated.

Adds MirrorDeletes, ArchiveFolder/EffectiveArchiveFolder, ArchiveKeepDays,
ArchiveMaxBytes and SuspiciousSkewSeconds, plus --mode, --mirror,
--archive-folder, --archive-keep-days and --archive-max-size. The archive folder
gets the same drive-root and containment guards as the backup folder: an archive
inside the synced tree re-syncs to the peer and resurrects the files it holds.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
git push -u origin feat/deletion-sync-ancestor-merge
```

**Verification before commit:**
```bash
dotnet build -c Release
dotnet test -c Release
```
Expected: 0 errors.

Existing tests knowingly changed — all mechanical, none change assertions or behaviour:
- `EndToEndTests.cs:52, 86, 126, 158`, `DeleteSyncTests.cs:53`, `DatabaseDeleteSyncTests.cs:56`, `DeleteThresholdTests.cs:53` — object-initializer `Bidirectional = X` rewritten to `Mode = …`, forced by the removal of the setter. Each maps `true -> SyncMode.TwoWay` and `false -> SyncMode.Push`, so every one of these tests exercises the same sync direction it did before.
- `CliParserTests.cs:97` and `:116` are **unchanged** — they read `result.Bidirectional`, which the shim still answers, and they now additionally prove the `--bidirectional`/`-b` alias still routes through `Mode`.
- No ExecRFS file changes: `SyncProfile.Bidirectional` is a separate settable bool and `CommandBuilder` keeps emitting `--bidirectional`, which remains a supported alias.

---

## Phase 2: Protocol v3 handshake (mode + clock timestamps) and ClockSkew

**Goal:** Raise the wire protocol to v3 so the handshake carries the full `SyncMode` + delete/mirror flags and both sides' clock readings, and add `ClockSkew` to turn those readings into a measured offset.

**Files:**
- Modify: `src/RemoteFileSync/Network/ProtocolHandler.cs:8-13` (version constant + doc comment)
- Modify: `src/RemoteFileSync/Network/ProtocolHandler.cs:75-91` (handshake serialization)
- Create: `src/RemoteFileSync/Sync/ClockSkew.cs`
- Modify: `src/RemoteFileSync/Network/SyncClient.cs:89-113` (handshake block)
- Modify: `src/RemoteFileSync/Network/SyncServer.cs:132-152` (handshake block)
- Test: `tests/RemoteFileSync.Tests/Network/ProtocolHandlerTests.cs` (modify: lines 62-67, 103-128)
- Test: `tests/RemoteFileSync.Tests/Sync/ClockSkewTests.cs` (new)

**Interfaces:**
- Consumes (Phase 1): `RemoteFileSync.Models.SyncMode` (`Push = 1, Pull = 2, TwoWay = 3`), `SyncOptions.Mode`, `SyncOptions.MirrorDeletes`, `SyncOptions.Bidirectional` (read-only shim), `SyncOptions.SuspiciousSkewSeconds`
- Produces (Phases 3+ rely on these):
  - `public const byte ProtocolHandler.ProtocolVersion = 3;`
  - `public static byte[] SerializeHandshake(byte version, byte syncMode, long clientSentTicks);`
  - `public static (byte version, byte syncMode, long clientSentTicks) DeserializeHandshake(byte[] data);`
  - `public static byte[] SerializeHandshakeAck(byte version, bool accepted, long serverTicks);`
  - `public static (byte version, bool accepted, long serverTicks) DeserializeHandshakeAck(byte[] data);`
  - `public readonly record struct RemoteFileSync.Sync.ClockSkew(TimeSpan Offset)` with `static ClockSkew None { get; }`, `static ClockSkew Measure(long clientSentTicks, long serverTicks, long clientRecvTicks)`, `DateTime NormaliseServerTime(DateTime serverUtc)`, `bool IsSuspicious { get; }`

---

### Task 2.1: ClockSkew

- [ ] **Step 1: Write the failing test**

Create `tests/RemoteFileSync.Tests/Sync/ClockSkewTests.cs`:

```csharp
using RemoteFileSync.Models;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class ClockSkewTests
{
    [Fact]
    public void Measure_KnownOffset_RoundTrips()
    {
        // Server clock runs 5 minutes fast; the handshake round-trip takes 200ms and the
        // server stamps its reply at the midpoint, so the NTP estimate must recover exactly
        // the 5 minutes and none of the transit time.
        var expected = TimeSpan.FromMinutes(5);
        long clientSent = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc).Ticks;
        long clientRecv = clientSent + TimeSpan.FromMilliseconds(200).Ticks;
        long serverTicks = clientSent + TimeSpan.FromMilliseconds(100).Ticks + expected.Ticks;

        var skew = ClockSkew.Measure(clientSent, serverTicks, clientRecv);

        Assert.Equal(expected, skew.Offset);
    }

    [Fact]
    public void Measure_ServerBehind_ProducesNegativeOffset()
    {
        var behind = TimeSpan.FromSeconds(90);
        long clientSent = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc).Ticks;
        long clientRecv = clientSent + TimeSpan.FromMilliseconds(40).Ticks;
        long serverTicks = clientSent + TimeSpan.FromMilliseconds(20).Ticks - behind.Ticks;

        var skew = ClockSkew.Measure(clientSent, serverTicks, clientRecv);

        Assert.Equal(-behind, skew.Offset);
    }

    [Fact]
    public void NormaliseServerTime_SubtractsOffset()
    {
        var skew = new ClockSkew(TimeSpan.FromMinutes(5));
        var serverUtc = new DateTime(2026, 7, 20, 10, 5, 0, DateTimeKind.Utc);

        var normalised = skew.NormaliseServerTime(serverUtc);

        Assert.Equal(new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc), normalised);
        Assert.Equal(DateTimeKind.Utc, normalised.Kind);
    }

    [Fact]
    public void None_IsZeroAndNotSuspicious()
    {
        Assert.Equal(TimeSpan.Zero, ClockSkew.None.Offset);
        Assert.False(ClockSkew.None.IsSuspicious);
        var t = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(t, ClockSkew.None.NormaliseServerTime(t));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(SyncOptions.SuspiciousSkewSeconds, false)]
    [InlineData(SyncOptions.SuspiciousSkewSeconds + 1, true)]
    [InlineData(-(SyncOptions.SuspiciousSkewSeconds + 1), true)]
    public void IsSuspicious_TripsBothDirectionsAboveThreshold(int offsetSeconds, bool expected)
    {
        var skew = new ClockSkew(TimeSpan.FromSeconds(offsetSeconds));
        Assert.Equal(expected, skew.IsSuspicious);
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ClockSkewTests"`
Expected: FAIL — `CS0246: The type or namespace name 'ClockSkew' could not be found (are you missing a using directive or an assembly reference?)`

- [ ] **Step 3: Implement**

Create `src/RemoteFileSync/Sync/ClockSkew.cs`:

```csharp
using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

/// <summary>
/// Difference between the peer's wall clock and ours, measured over the handshake round-trip.
/// Newest-wins resolution compares an mtime stamped by the server against one stamped by the
/// client; on machines whose clocks disagree that comparison picks the wrong winner and the
/// loser's edit is silently overwritten. Every cross-side timestamp comparison must go through
/// <see cref="NormaliseServerTime"/> first.
/// </summary>
public readonly record struct ClockSkew(TimeSpan Offset)
{
    /// <summary>No correction. Use when the peer's clock reading is unavailable.</summary>
    public static ClockSkew None { get; } = new(TimeSpan.Zero);

    /// <summary>
    /// NTP-style single-sample estimate: assume the server stamped its reply at the midpoint of
    /// the round-trip, so offset = serverTicks - (clientSentTicks + rtt/2). Positive means the
    /// server clock is ahead of ours.
    /// </summary>
    public static ClockSkew Measure(long clientSentTicks, long serverTicks, long clientRecvTicks)
    {
        long rtt = clientRecvTicks - clientSentTicks;
        return new ClockSkew(TimeSpan.FromTicks(serverTicks - (clientSentTicks + rtt / 2)));
    }

    /// <summary>Converts a server-stamped UTC time into this machine's frame of reference.</summary>
    public DateTime NormaliseServerTime(DateTime serverUtc) => serverUtc - Offset;

    /// <summary>
    /// Beyond this the measurement is no longer plausible transit noise and mtime ordering
    /// between the two sides cannot be trusted, so the user must be told.
    /// </summary>
    public bool IsSuspicious =>
        Math.Abs(Offset.TotalSeconds) > SyncOptions.SuspiciousSkewSeconds;
}
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ClockSkewTests"`
Expected: PASS

---

### Task 2.2: Protocol v3 handshake frames

- [ ] **Step 1: Write the failing test**

Replace `tests/RemoteFileSync.Tests/Network/ProtocolHandlerTests.cs:103-128`.

Exact current code being replaced:

```csharp
    [Fact]
    public void SerializeHandshake_CorrectBytes()
    {
        var bytes = ProtocolHandler.SerializeHandshake(version: 1, syncMode: 1);
        Assert.Equal(2, bytes.Length);
        Assert.Equal(1, bytes[0]);
        Assert.Equal(1, bytes[1]);
    }

    [Fact]
    public void DeserializeHandshake_ParsesCorrectly()
    {
        var bytes = new byte[] { 1, 0 };
        var (version, syncMode) = ProtocolHandler.DeserializeHandshake(bytes);
        Assert.Equal(1, version);
        Assert.Equal(0, syncMode);
    }

    [Fact]
    public void Handshake_SyncMode_RoundTrips()
    {
        var data = ProtocolHandler.SerializeHandshake(1, 3);
        var (version, syncMode) = ProtocolHandler.DeserializeHandshake(data);
        Assert.Equal(1, version);
        Assert.Equal(3, syncMode);
    }
```

Exact replacement:

```csharp
    [Fact]
    public void SerializeHandshake_CorrectBytes()
    {
        long sent = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc).Ticks;
        var bytes = ProtocolHandler.SerializeHandshake(version: 3, syncMode: 1, clientSentTicks: sent);
        Assert.Equal(11, bytes.Length);
        Assert.Equal(3, bytes[0]);
        Assert.Equal(1, bytes[1]);
        Assert.Equal(sent, BitConverter.ToInt64(bytes, 2));
        Assert.Equal(0, bytes[10]);   // reserved byte must stay zero: v3 peers agree on frame length
    }

    [Fact]
    public void DeserializeHandshake_ParsesCorrectly()
    {
        long sent = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc).Ticks;
        var bytes = new byte[11];
        bytes[0] = 3;
        bytes[1] = 0;
        BitConverter.TryWriteBytes(bytes.AsSpan(2), sent);
        var (version, syncMode, clientSentTicks) = ProtocolHandler.DeserializeHandshake(bytes);
        Assert.Equal(3, version);
        Assert.Equal(0, syncMode);
        Assert.Equal(sent, clientSentTicks);
    }

    [Theory]
    [InlineData((byte)SyncMode.Push, false, false)]
    [InlineData((byte)SyncMode.Pull, true, false)]
    [InlineData((byte)SyncMode.TwoWay, true, true)]
    [InlineData((byte)SyncMode.TwoWay, false, true)]
    public void Handshake_SyncMode_RoundTrips(byte mode, bool deleteEnabled, bool mirrorDeletes)
    {
        byte packed = (byte)(mode | (deleteEnabled ? 4 : 0) | (mirrorDeletes ? 8 : 0));
        long sent = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc).Ticks;

        var data = ProtocolHandler.SerializeHandshake(3, packed, sent);
        var (version, syncMode, clientSentTicks) = ProtocolHandler.DeserializeHandshake(data);

        Assert.Equal(3, version);
        Assert.Equal(sent, clientSentTicks);
        Assert.Equal((SyncMode)mode, (SyncMode)(syncMode & 0b11));
        Assert.Equal(deleteEnabled, (syncMode & 4) != 0);
        Assert.Equal(mirrorDeletes, (syncMode & 8) != 0);
    }

    [Fact]
    public void HandshakeAck_RoundTripsServerTicks()
    {
        long serverTicks = new DateTime(2026, 7, 20, 10, 0, 5, DateTimeKind.Utc).Ticks;
        var data = ProtocolHandler.SerializeHandshakeAck(3, accepted: true, serverTicks);
        Assert.Equal(10, data.Length);

        var (version, accepted, ticks) = ProtocolHandler.DeserializeHandshakeAck(data);
        Assert.Equal(3, version);
        Assert.True(accepted);
        Assert.Equal(serverTicks, ticks);

        // Rejection keeps the existing polarity: byte 1 == 0 means accepted.
        var rejected = ProtocolHandler.SerializeHandshakeAck(3, accepted: false, serverTicks);
        Assert.Equal(1, rejected[1]);
        Assert.False(ProtocolHandler.DeserializeHandshakeAck(rejected).accepted);
    }
```

Then replace `tests/RemoteFileSync.Tests/Network/ProtocolHandlerTests.cs:62-67`.

Exact current code being replaced:

```csharp
    [Fact]
    public void DeserializeHandshake_RejectsTruncatedPayload()
    {
        Assert.Throws<InvalidDataException>(() => ProtocolHandler.DeserializeHandshake(new byte[] { 2 }));
        Assert.Throws<InvalidDataException>(() => ProtocolHandler.DeserializeHandshakeAck(Array.Empty<byte>()));
    }
```

Exact replacement:

```csharp
    [Fact]
    public void DeserializeHandshake_RejectsTruncatedPayload()
    {
        // One byte short of a v3 frame: reading past the end would fabricate a clock reading
        // and hand ClockSkew garbage. A v2 peer's 2-byte handshake lands here too.
        Assert.Throws<InvalidDataException>(() => ProtocolHandler.DeserializeHandshake(new byte[10]));
        Assert.Throws<InvalidDataException>(() => ProtocolHandler.DeserializeHandshake(new byte[] { 2, 1 }));
        Assert.Throws<InvalidDataException>(() => ProtocolHandler.DeserializeHandshakeAck(new byte[9]));
        Assert.Throws<InvalidDataException>(() => ProtocolHandler.DeserializeHandshakeAck(Array.Empty<byte>()));
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ProtocolHandlerTests"`
Expected: FAIL — `CS1501: No overload for method 'SerializeHandshake' takes 3 arguments` (and `CS1501` for `SerializeHandshakeAck` taking 3 arguments; `CS8132: Cannot deconstruct a tuple of '2' elements into '3' variables`)

- [ ] **Step 3: Implement**

Modify `src/RemoteFileSync/Network/ProtocolHandler.cs:8-13`.

Exact current code being replaced:

```csharp
    /// <summary>
    /// Wire protocol version. v2 added lastModifiedUtcTicks to the FileStart frame.
    /// Peers running different versions are rejected during handshake: a v1 peer silently
    /// ignores the trailing timestamp bytes, which makes sync never converge.
    /// </summary>
    public const byte ProtocolVersion = 2;
```

Exact replacement:

```csharp
    /// <summary>
    /// Wire protocol version. v2 added lastModifiedUtcTicks to the FileStart frame. v3 widened
    /// the handshake to carry the full SyncMode plus the delete and mirror flags (v2 had one
    /// bit for "bidirectional"), and made both sides stamp a UTC tick count so the client can
    /// measure clock skew — see <see cref="Sync.ClockSkew"/>.
    /// Peers running different versions are rejected during handshake: a v1 peer silently
    /// ignores the trailing timestamp bytes, which makes sync never converge.
    /// </summary>
    public const byte ProtocolVersion = 3;
```

Modify `src/RemoteFileSync/Network/ProtocolHandler.cs:75-91`.

Exact current code being replaced:

```csharp
    public static byte[] SerializeHandshake(byte version, byte syncMode) =>
        new[] { version, syncMode };

    public static (byte version, byte syncMode) DeserializeHandshake(byte[] data)
    {
        if (data.Length < 2) throw new InvalidDataException("Handshake payload truncated.");
        return (data[0], data[1]);
    }

    public static byte[] SerializeHandshakeAck(byte version, bool accepted) =>
        new[] { version, (byte)(accepted ? 0 : 1) };

    public static (byte version, bool accepted) DeserializeHandshakeAck(byte[] data)
    {
        if (data.Length < 2) throw new InvalidDataException("HandshakeAck payload truncated.");
        return (data[0], data[1] == 0);
    }
```

Exact replacement:

```csharp
    /// <summary>
    /// v3 handshake, 11 bytes: [0] version, [1] syncMode bits (low 2 = SyncMode, bit 2 =
    /// deleteEnabled, bit 3 = mirrorDeletes), [2..9] clientSentTicks, [10] reserved (0).
    /// </summary>
    public static byte[] SerializeHandshake(byte version, byte syncMode, long clientSentTicks)
    {
        var result = new byte[11];
        result[0] = version;
        result[1] = syncMode;
        BitConverter.TryWriteBytes(result.AsSpan(2), clientSentTicks);
        // result[10] left 0: reserved, but sent so both v3 peers agree on the frame length.
        return result;
    }

    public static (byte version, byte syncMode, long clientSentTicks) DeserializeHandshake(byte[] data)
    {
        if (data.Length < 11) throw new InvalidDataException("Handshake payload truncated.");
        return (data[0], data[1], BitConverter.ToInt64(data, 2));
    }

    /// <summary>
    /// v3 ack, 10 bytes: [0] version, [1] accepted (0 = accepted — same polarity as v2, so an
    /// older peer still reads the verdict correctly), [2..9] serverTicks.
    /// </summary>
    public static byte[] SerializeHandshakeAck(byte version, bool accepted, long serverTicks)
    {
        var result = new byte[10];
        result[0] = version;
        result[1] = (byte)(accepted ? 0 : 1);
        BitConverter.TryWriteBytes(result.AsSpan(2), serverTicks);
        return result;
    }

    public static (byte version, bool accepted, long serverTicks) DeserializeHandshakeAck(byte[] data)
    {
        if (data.Length < 10) throw new InvalidDataException("HandshakeAck payload truncated.");
        return (data[0], data[1] == 0, BitConverter.ToInt64(data, 2));
    }
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ProtocolHandlerTests"`
Expected: FAIL to build until Task 2.3 and 2.4 land — `SyncClient.cs` and `SyncServer.cs` still call the 2-argument overloads (`CS7036: There is no argument given that corresponds to the required parameter 'clientSentTicks'`). Complete 2.3 and 2.4, then this command is expected to PASS.

---

### Task 2.3: Client sends v3 handshake and measures skew

- [ ] **Step 1: Write the failing test**

The client handshake path is exercised end-to-end by the existing integration suite (`EndToEndTests`, `DeleteSyncTests`), which fails to build until the call sites are updated. No new unit test is added here — the client's handshake block has no seam that can be driven without a socket, and `ClockSkewTests` already pins the arithmetic. The failing signal for this task is the compile error below.

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~EndToEndTests"`
Expected: FAIL — `CS7036: There is no argument given that corresponds to the required formal parameter 'clientSentTicks' of 'ProtocolHandler.SerializeHandshake(byte, byte, long)'` at `SyncClient.cs:91`, and `CS8132: Cannot deconstruct a tuple of '3' elements into '2' variables` at `SyncClient.cs:101`

- [ ] **Step 3: Implement**

Modify `src/RemoteFileSync/Network/SyncClient.cs:89-113`.

Exact current code being replaced:

```csharp
        // 1. Send handshake
        byte syncMode = (byte)((_options.Bidirectional ? 1 : 0) | (_options.DeleteEnabled ? 2 : 0));
        var hsPayload = ProtocolHandler.SerializeHandshake(ProtocolHandler.ProtocolVersion, syncMode);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Handshake, hsPayload, ct);

        // 2. Receive HandshakeAck
        var (ackType, ackData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        if (ackType != MessageType.HandshakeAck)
        {
            _logger.Error($"Expected HandshakeAck, got {ackType}");
            return 3;
        }
        var (serverVersion, accepted) = ProtocolHandler.DeserializeHandshakeAck(ackData);
        if (serverVersion != ProtocolHandler.ProtocolVersion)
        {
            _logger.Error($"Protocol mismatch: server speaks v{serverVersion}, this build speaks " +
                          $"v{ProtocolHandler.ProtocolVersion}. Upgrade both sides to the same build. " +
                          "(A v1 server silently discards the timestamp field and sync will never converge.)");
            return 2;
        }
        if (!accepted)
        {
            _logger.Error("Server rejected the connection.");
            return 2;
        }
```

Exact replacement:

```csharp
        // 1. Send handshake
        byte syncMode = (byte)((byte)_options.Mode
                               | (_options.DeleteEnabled ? 4 : 0)
                               | (_options.MirrorDeletes ? 8 : 0));
        // Stamped immediately before the write so the round-trip we divide in half is the
        // network's, not ours.
        long clientSentTicks = DateTime.UtcNow.Ticks;
        var hsPayload = ProtocolHandler.SerializeHandshake(
            ProtocolHandler.ProtocolVersion, syncMode, clientSentTicks);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Handshake, hsPayload, ct);

        // 2. Receive HandshakeAck
        var (ackType, ackData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        long clientRecvTicks = DateTime.UtcNow.Ticks;
        if (ackType != MessageType.HandshakeAck)
        {
            _logger.Error($"Expected HandshakeAck, got {ackType}");
            return 3;
        }
        var (serverVersion, accepted, serverTicks) = ProtocolHandler.DeserializeHandshakeAck(ackData);
        if (serverVersion != ProtocolHandler.ProtocolVersion)
        {
            _logger.Error($"Protocol mismatch: server speaks v{serverVersion}, this build speaks " +
                          $"v{ProtocolHandler.ProtocolVersion}. Upgrade both sides to the same build. " +
                          "(A v1 server silently discards the timestamp field and sync will never converge.)");
            return 2;
        }
        if (!accepted)
        {
            _logger.Error("Server rejected the connection.");
            return 2;
        }

        var skew = ClockSkew.Measure(clientSentTicks, serverTicks, clientRecvTicks);
        if (skew.IsSuspicious)
        {
            _logger.Warning(
                $"Server clock differs from this machine by {skew.Offset.TotalSeconds:+0.0;-0.0} seconds " +
                $"(threshold {SyncOptions.SuspiciousSkewSeconds}s; positive means the server is ahead). " +
                "Two-way sync decides ties by comparing the two sides' modification times, so a skew " +
                "this large can pick the older edit as the winner and overwrite the newer one. " +
                "Fix NTP on both machines before relying on two-way sync.");
        }
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~EndToEndTests"`
Expected: FAIL still — `SyncServer.cs` has not been updated yet (`CS7036` at `SyncServer.cs:146`). Proceed to Task 2.4; this command is expected to PASS after that.

---

### Task 2.4: Server decodes v3 handshake and echoes its clock

- [ ] **Step 1: Write the failing test**

Covered by the existing integration suite, which cannot build against the mixed-version call sites. The failing signal is the compile error below.

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~EndToEndTests"`
Expected: FAIL — `CS8132: Cannot deconstruct a tuple of '3' elements into '2' variables` at `SyncServer.cs:139`, and `CS7036: There is no argument given that corresponds to the required formal parameter 'serverTicks' of 'ProtocolHandler.SerializeHandshakeAck(byte, bool, long)'` at `SyncServer.cs:146`

- [ ] **Step 3: Implement**

Modify `src/RemoteFileSync/Network/SyncServer.cs:132-152`.

Exact current code being replaced:

```csharp
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
        _logger.Info($"Handshake: v{version}, {(bidirectional ? "bidirectional" : "unidirectional")}");

        // 2. Send HandshakeAck — reject version mismatches rather than misparse frames.
        bool versionOk = version == ProtocolHandler.ProtocolVersion;
        var ackPayload = ProtocolHandler.SerializeHandshakeAck(ProtocolHandler.ProtocolVersion, accepted: versionOk);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.HandshakeAck, ackPayload, ct);
        if (!versionOk)
        {
            _logger.Error($"Rejected client: protocol v{version}, this build speaks v{ProtocolHandler.ProtocolVersion}.");
            return 3;
        }
```

Exact replacement:

```csharp
        // 1. Receive handshake
        var (hsType, hsData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        if (hsType != MessageType.Handshake)
        {
            _logger.Error($"Expected Handshake, got {hsType}");
            return 3;
        }

        byte version;
        byte syncMode;
        try
        {
            (version, syncMode, _) = ProtocolHandler.DeserializeHandshake(hsData);
        }
        catch (InvalidDataException)
        {
            // A v2 client sends a 2-byte handshake, which the v3 length guard rejects before we
            // can read its version byte. Answer with a well-formed ack anyway so the peer prints
            // "protocol mismatch" instead of an unexplained dropped connection.
            await ProtocolHandler.WriteMessageAsync(stream, MessageType.HandshakeAck,
                ProtocolHandler.SerializeHandshakeAck(
                    ProtocolHandler.ProtocolVersion, accepted: false, DateTime.UtcNow.Ticks), ct);
            _logger.Error("Rejected client: handshake shorter than protocol " +
                          $"v{ProtocolHandler.ProtocolVersion} requires — the peer is an older build.");
            return 3;
        }

        var clientMode = (SyncMode)(syncMode & 0b11);
        bool bidirectional = clientMode == SyncMode.TwoWay;
        bool deleteEnabled = (syncMode & 4) != 0;
        bool mirrorDeletes = (syncMode & 8) != 0;
        _logger.Info($"Handshake: v{version}, mode={clientMode}" +
                     (deleteEnabled ? " +delete" : "") + (mirrorDeletes ? " +mirror" : ""));

        // 2. Send HandshakeAck — reject version mismatches rather than misparse frames.
        // serverTicks is stamped at send time so the client's round-trip halving is honest.
        bool versionOk = version == ProtocolHandler.ProtocolVersion;
        var ackPayload = ProtocolHandler.SerializeHandshakeAck(
            ProtocolHandler.ProtocolVersion, accepted: versionOk, DateTime.UtcNow.Ticks);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.HandshakeAck, ackPayload, ct);
        if (!versionOk)
        {
            _logger.Error($"Rejected client: protocol v{version}, this build speaks v{ProtocolHandler.ProtocolVersion}.");
            return 3;
        }
```

Note: `bidirectional` and `deleteEnabled` keep their names and meanings, so the downstream uses at `SyncServer.cs:222`, `SyncServer.cs:305` and `SyncServer.cs:356` are untouched by this phase. `mirrorDeletes` is decoded and logged here; a later phase feeds it to `SyncEngine.ComputePlan`.

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~EndToEndTests"`
Expected: PASS

Then re-run the suites deferred in Tasks 2.2 and 2.3:

Run: `dotnet test -c Release --filter "FullyQualifiedName~ProtocolHandlerTests"`
Expected: PASS

Run: `dotnet test -c Release --filter "FullyQualifiedName~ClockSkewTests"`
Expected: PASS

---

### Phase 2 commit

```bash
git add src/RemoteFileSync/Network/ProtocolHandler.cs \
        src/RemoteFileSync/Network/SyncClient.cs \
        src/RemoteFileSync/Network/SyncServer.cs \
        src/RemoteFileSync/Sync/ClockSkew.cs \
        tests/RemoteFileSync.Tests/Network/ProtocolHandlerTests.cs \
        tests/RemoteFileSync.Tests/Sync/ClockSkewTests.cs
git commit -m "feat: protocol v3 handshake carries sync mode and clock readings

The v2 handshake had one bit for 'bidirectional', which cannot express
push/pull/two-way plus the delete and mirror flags. v3 widens the mode byte
and has both sides stamp DateTime.UtcNow.Ticks, so the client can measure
clock skew over the round-trip and warn before two-way sync resolves a tie
using timestamps from disagreeing clocks.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
git push -u origin feat/deletion-sync-ancestor-merge
```

**Verification before commit:**
```bash
dotnet build -c Release
dotnet test -c Release
```
Expected: 0 errors. Existing tests knowingly changed: `ProtocolHandlerTests.SerializeHandshake_CorrectBytes`, `DeserializeHandshake_ParsesCorrectly`, `Handshake_SyncMode_RoundTrips` and `DeserializeHandshake_RejectsTruncatedPayload` — all four assert the v2 two-byte frame layout, which v3 replaces. `Handshake_SyncMode_RoundTrips` becomes a `[Theory]` because the mode byte now has three independent fields to cover. No other existing test touches the handshake (verified: `ProtocolHandlerTests.cs` is the only test file mentioning it), and the integration suites are unaffected because both peers in-process speak v3.

---

## Phase 3: Schema v2 — per-side columns, tombstone retention, PairMarker, and the new SyncDatabase API

**Goal:** Replace the single-sided v1 `files` table with the schema v2 ancestor table (separate client/server size+mtime, tombstone retention timestamps), migrate existing v1 databases in place, and expose the `AncestorRow`-based `SyncDatabase` API the merge engine needs.

**Files:**
- Create: `src/RemoteFileSync/State/PairMarker.cs`
- Modify: `src/RemoteFileSync/State/SyncDatabase.cs:1-5`, `:7-13`, `:32`, `:37-39`, `:65-112`, `:180-242`, `:244-327`, `:341-383`
- Modify: `tests/RemoteFileSync.Tests/State/SyncDatabaseTests.cs:180-188`
- Test: `tests/RemoteFileSync.Tests/State/PairMarkerTests.cs` (new)
- Test: `tests/RemoteFileSync.Tests/State/SyncDatabaseSchemaV2Tests.cs` (new)
- Test: `tests/RemoteFileSync.Tests/State/SyncDatabaseSchemaMigrationTests.cs` (new)

**Interfaces:**

- Consumes (from Phase 2 — Phase 3 does not compile until `AncestorRow.cs` has landed):
```csharp
namespace RemoteFileSync.Sync;
public sealed record AncestorRow(
    string Path,
    long   ClientSize,
    long   ClientMtimeTicks,
    long   ServerSize,
    long   ServerMtimeTicks,
    string Status,
    long   LastSyncedTicks,
    long?  DeletedUtcTicks);
```

- Produces (relied on by the SyncEngine / SyncClient phases):
```csharp
namespace RemoteFileSync.State;

public record ConflictEntry(string Path, string Detail, DateTime Timestamp);

public static class PairMarker
{
    public static string PathFor(string dbPath);
    public static bool   Exists(string dbPath);
    public static void   Write(string dbPath);
}

public sealed class SyncDatabase : IDisposable
{
    public const int SchemaVersion = 2;
    public AncestorRow? GetRow(string path);
    public Dictionary<string, AncestorRow> LoadAll();
    public void UpsertSynced(string path,
                             long clientSize, long clientMtimeTicks,
                             long serverSize, long serverMtimeTicks,
                             long sessionId, string direction);
    public void Tombstone(string path, long sessionId, string? detail);
    public int  PurgeTombstonesOlderThan(TimeSpan age);
    public void LogConflict(string path, long sessionId, string detail);
    public void LogResurrection(string path, long sessionId, string detail);   // ← see contract gap
    public IReadOnlyList<ConflictEntry> GetSessionConflicts(long sessionId);
    public IReadOnlyList<ConflictEntry> GetSessionResurrections(long sessionId);
}
```

> **CONTRACT GAP — requires sign-off before Task 3.5.**
> CONTRACT.md specifies `GetSessionResurrections(long)` and states that `file_versions.action` gains the value `'resurrected'`, but it defines **no writer** for that value. `LogConflict` writes `action='conflict'` only, and its signature is frozen so it cannot take an action argument. `GetSessionResurrections` is therefore unimplementable as a meaningful query without one additional method.
> **Proposed minimal resolution (implemented below, flagged for review):** add `public void LogResurrection(string path, long sessionId, string detail);` — an exact mirror of `LogConflict` differing only in the stored action string. It adds no new type and changes no frozen signature. If the contract owner prefers a different resolution, only Task 3.5 changes.

### Existing-test disposition (complete inventory)

`tests/RemoteFileSync.Tests/State/SyncDatabaseTests.cs` — 17 facts, of which 13 touch `MarkSynced`/`MarkNew`/`MarkDeleted`/`GetAllTrackedFiles`/`GetDeletedFiles`/`GetFileState`/`FileState`:

| # | Test (line) | Legacy API used | Disposition |
|---|---|---|---|
| 1 | `CreateDatabase_InitializesSchema` (:28) | none | **unchanged** |
| 2 | `StartSession_ReturnsPositiveId` (:35) | none | **unchanged** |
| 3 | `CompleteSession_SetsCompletedUtcAndStats` (:44) | none | **unchanged** |
| 4 | `MarkSynced_CreatesFileAndVersion` (:61) | `MarkSynced`, `GetFileState`, `FileState.Side` | **unchanged** — `Assert.Equal("both", state.Side)` at :71 still passes; the read shim reports `"both"` for every row, which is exactly what v1 `MarkSynced` wrote |
| 5 | `MarkSynced_UpdatesExistingFile` (:80) | `MarkSynced`, `GetFileState` | **unchanged** |
| 6 | `MarkDeleted_SetsStatusDeleted` (:100) | `MarkSynced`, `MarkDeleted`, `GetFileState` | **unchanged** |
| 7 | `GetFileState_CaseInsensitive` (:116) | `MarkSynced`, `GetFileState` | **unchanged** — v2 keeps `COLLATE NOCASE` on the PK |
| 8 | `GetFileState_NotFound_ReturnsNull` (:128) | `GetFileState` | **unchanged** |
| 9 | `GetDeletedFiles_ReturnsOnlyDeleted` (:136) | `MarkSynced`, `MarkDeleted`, `GetDeletedFiles` | **unchanged** |
| 10 | `GetAllTrackedFiles_ReturnsAll` (:150) | `MarkSynced`, `MarkDeleted`, `GetAllTrackedFiles` | **unchanged** — tombstones stay in the table; only `PurgeTombstonesOlderThan` removes them |
| 11 | `MarkSkipped_CreatesVersionEntry` (:163) | `MarkSynced`, `MarkSkipped`, `GetFileState` | **unchanged** — `MarkSkipped` only writes `file_versions`, untouched by this phase |
| 12 | `MarkNew_SetsStatusNew` (:180) | `MarkNew`, `GetFileState`, `FileState.Side` | **CHANGED — one line deleted.** `Assert.Equal("remote", state.Side)` at :187 is the only assertion in the suite that cannot survive: schema v2 has no `side` column, so `"remote"` is unrecoverable. The `status == "new"` assertion is kept. |
| 13 | `PartialSync_PreservesPerFileState` (:192) | `MarkSynced`, `GetFileState` | **unchanged** |
| 14 | `GetDbPath_DeterministicAndCaseInsensitive` (:206) | none | **unchanged** |
| 15 | `MarkDeleted_NonexistentPath_NoPhantomHistory` (:215) | `MarkDeleted`, `GetFileHistory` | **unchanged** — `Tombstone` preserves the no-op-when-untracked guard verbatim |
| 16 | `MarkNew_CreatesVersionEntry` (:225) | `MarkNew`, `GetFileHistory` | **unchanged** |
| 17 | `PreviouslyDeleted_Reappeared_CanBeMarkedExists` (:235) | `MarkSynced`, `MarkDeleted`, `GetFileState` | **unchanged** — this is the test that pins `UpsertSynced` clearing `deleted_utc` back to NULL |

`tests/RemoteFileSync.Tests/State/SyncDatabaseMigrationTests.cs` — 3 facts, all **unchanged**: `Migration_ImportsBinaryState` (:22, uses `GetAllTrackedFiles` + `GetFileState`), `Migration_NoBinaryFile_DoesNothing` (:67), `Migration_DbAlreadyExists_SkipsMigration` (:78). `MigrateFromBinary` keeps calling `MarkSynced`, which is now a shim.

Two other suites also use the legacy API and are **unchanged**: `tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs:264,305,306,327,350,374,395` (`MarkSynced`/`MarkDeleted`) and `tests/RemoteFileSync.Tests/Integration/DeleteThresholdTests.cs:81` (`MarkSynced`).

**Decision: keep the old methods as thin shims over the new ones.** Justification: `SyncClient.cs` has 12 call sites (`:165,194,196,201,205,239,307,344,387,430,451`) and `SyncEngine.cs:105-113` builds a `Dictionary<string, FileState>` from `GetAllTrackedFiles()`. Both files are rewritten in later phases. Deleting the legacy surface here would force Phase 3 to drag the entire network and engine rewrite into one commit, and would discard the 20 existing tests that are the strongest available evidence the column rebuild did not lose or corrupt data. The shims cost ~30 lines and are deleted in the phase that removes their last caller.

---

### Task 3.1: PairMarker

- [ ] **Step 1: Write the failing test**

Create `tests/RemoteFileSync.Tests/State/PairMarkerTests.cs`:

```csharp
using System;
using System.IO;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.State;

public sealed class PairMarkerTests : IDisposable
{
    private readonly string _tempDir;

    public PairMarkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rfs_pairmarker_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void PathFor_PlacesMarkerBesideDatabase()
    {
        var dbPath = Path.Combine(_tempDir, "sync.db");
        Assert.Equal(Path.Combine(_tempDir, "pair.marker"), PairMarker.PathFor(dbPath));
    }

    [Fact]
    public void PathFor_BareFileName_ReturnsBareMarkerName()
    {
        Assert.Equal("pair.marker", PairMarker.PathFor("sync.db"));
    }

    [Fact]
    public void Exists_FalseBeforeWrite_TrueAfterWrite()
    {
        var dbPath = Path.Combine(_tempDir, "sync.db");
        Assert.False(PairMarker.Exists(dbPath));
        PairMarker.Write(dbPath);
        Assert.True(PairMarker.Exists(dbPath));
    }

    [Fact]
    public void Exists_IgnoresTheDatabaseItself()
    {
        // The safety gate keys on marker-present + db-absent, so a marker must never be
        // inferred from the presence of sync.db.
        var dbPath = Path.Combine(_tempDir, "sync.db");
        File.WriteAllText(dbPath, "not a real database");
        Assert.False(PairMarker.Exists(dbPath));
    }

    [Fact]
    public void Write_CreatesMissingDirectory()
    {
        var dbPath = Path.Combine(_tempDir, "nested", "pairid", "sync.db");
        PairMarker.Write(dbPath);
        Assert.True(File.Exists(Path.Combine(_tempDir, "nested", "pairid", "pair.marker")));
    }

    [Fact]
    public void Write_IsIdempotent()
    {
        var dbPath = Path.Combine(_tempDir, "sync.db");
        PairMarker.Write(dbPath);
        PairMarker.Write(dbPath);
        Assert.True(PairMarker.Exists(dbPath));
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~PairMarkerTests"`
Expected: FAIL — build error `CS0103: The name 'PairMarker' does not exist in the current context` at each of the 6 call sites.

- [ ] **Step 3: Implement**

Create `src/RemoteFileSync/State/PairMarker.cs`:

```csharp
namespace RemoteFileSync.State;

/// <summary>
/// A zero-content sentinel written beside sync.db after the first successful sync.
/// Its presence next to a MISSING or unreadable database is what distinguishes a genuine
/// first run from lost sync state — without it, a wiped database looks identical to a new
/// pair and the engine would treat every remote file as new, then mirror a full-tree delete.
/// </summary>
public static class PairMarker
{
    private const string MarkerFileName = "pair.marker";

    public static string PathFor(string dbPath)
    {
        var dir = System.IO.Path.GetDirectoryName(dbPath);
        return string.IsNullOrEmpty(dir)
            ? MarkerFileName
            : System.IO.Path.Combine(dir, MarkerFileName);
    }

    public static bool Exists(string dbPath) => File.Exists(PathFor(dbPath));

    public static void Write(string dbPath)
    {
        var markerPath = PathFor(dbPath);
        var dir = System.IO.Path.GetDirectoryName(markerPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Content is diagnostic only; the gate reads existence, never the bytes.
        File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O"));
    }
}
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~PairMarkerTests"`
Expected: PASS — 6 passed.

---

### Task 3.2: Schema v2 table, `SchemaVersion`, and the ancestor-row API

- [ ] **Step 1: Write the failing test**

Create `tests/RemoteFileSync.Tests/State/SyncDatabaseSchemaV2Tests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncDatabaseSchemaV2Tests"`
Expected: FAIL — build errors: `CS0117: 'SyncDatabase' does not contain a definition for 'SchemaVersion'`, `CS1061: 'SyncDatabase' does not contain a definition for 'UpsertSynced'`, `'GetRow'`, `'LoadAll'`, `'Tombstone'`.

- [ ] **Step 3: Implement**

**Edit 3.2a — `src/RemoteFileSync/State/SyncDatabase.cs:1-3`, add the `AncestorRow` namespace.**

Current:
```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
```

Replacement:
```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RemoteFileSync.Sync;
```

**Edit 3.2b — `src/RemoteFileSync/State/SyncDatabase.cs:7-13`, document the now-synthetic `Side`.**

Current:
```csharp
public record FileState(
    string Path,
    long FileSize,
    DateTime LastModified,
    string Status,
    DateTime LastSynced,
    string Side);
```

Replacement:
```csharp
/// <summary>
/// Legacy schema v1 projection of a row. Kept only for callers not yet migrated to
/// <see cref="AncestorRow"/>. Schema v2 has no `side` column, so <c>Side</c> is always
/// reported as "both" — the value v1 MarkSynced wrote for every synced row.
/// </summary>
public record FileState(
    string Path,
    long FileSize,
    DateTime LastModified,
    string Status,
    DateTime LastSynced,
    string Side);
```

**Edit 3.2c — `src/RemoteFileSync/State/SyncDatabase.cs:24-32`, add the `ConflictEntry` record after `SyncSessionEntry`.**

Current:
```csharp
public record SyncSessionEntry(
    long Id,
    DateTime StartedUtc,
    DateTime? CompletedUtc,
    string Mode,
    int FilesTransferred,
    int FilesDeleted,
    int FilesSkipped,
    int? ExitCode);
```

Replacement:
```csharp
public record SyncSessionEntry(
    long Id,
    DateTime StartedUtc,
    DateTime? CompletedUtc,
    string Mode,
    int FilesTransferred,
    int FilesDeleted,
    int FilesSkipped,
    int? ExitCode);

/// <summary>A conflict or resurrection recorded during one sync session.</summary>
public record ConflictEntry(string Path, string Detail, DateTime Timestamp);
```

**Edit 3.2d — `src/RemoteFileSync/State/SyncDatabase.cs:37-39`, add the `SchemaVersion` constant.**

Current:
```csharp
public sealed class SyncDatabase : IDisposable
{
    private readonly SqliteConnection _conn;
```

Replacement:
```csharp
public sealed class SyncDatabase : IDisposable
{
    /// <summary>Stamped into PRAGMA user_version. Bump only alongside a migration step in InitSchema.</summary>
    public const int SchemaVersion = 2;

    private readonly SqliteConnection _conn;
```

**Edit 3.2e — `src/RemoteFileSync/State/SyncDatabase.cs:65-112`, replace `InitSchema` wholesale.**

Current:
```csharp
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
```

Replacement:
```csharp
    /// <summary>
    /// Creates or upgrades the schema. Schema v1 never stamped PRAGMA user_version, so a
    /// user_version of 0 is ambiguous — it means either "brand new file" or "v1 database".
    /// The presence of the v1-only `file_size` column is what tells the two apart.
    /// </summary>
    private void InitSchema()
    {
        // journal_mode cannot be set inside a transaction, so pragmas run first and alone.
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

        if (ReadUserVersion() >= SchemaVersion) return;

        // Probed before BeginTransaction: Microsoft.Data.Sqlite rejects any command whose
        // Transaction property is unset while a transaction is open on the connection.
        bool isV1 = TableExists("files") && ColumnExists("files", "file_size");

        using var txn = _conn.BeginTransaction();
        try
        {
            if (isV1) MigrateV1ToV2(txn);
            else      CreateFilesV2(txn);

            using var stamp = _conn.CreateCommand();
            stamp.Transaction = txn;
            // user_version lives in the db header and is transactional, so the stamp commits
            // with the table rebuild or not at all. That atomicity is what makes reopening a
            // database interrupted mid-upgrade safe: it is still v1 and simply migrates again.
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
        // file_versions.action has no CHECK constraint, so the v2 values 'conflict' and
        // 'resurrected' need no DDL change.
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
        // SQLite before 3.35 has no DROP COLUMN and `side` must go, so the table is rebuilt:
        // create / copy / drop / rename, all inside the caller's transaction. v1 stored one
        // size+mtime for both sides, so both per-side columns seed from it — that is the
        // correct ancestor for a pair that has only ever been synced through v1's one-way model.
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
```

**Edit 3.2f — `src/RemoteFileSync/State/SyncDatabase.cs:180-242`, replace the whole "File state" section with the v2 ancestor readers plus legacy read shims.**

Current:
```csharp
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
```

Replacement:
```csharp
    // ── Ancestor rows (schema v2) ─────────────────────────────────────────────

    private const string AncestorSelect = @"
SELECT path, client_size, client_mtime, server_size, server_mtime, status, last_synced, deleted_utc
FROM files";

    private static AncestorRow ReadAncestorRow(SqliteDataReader reader) => new AncestorRow(
        Path: reader.GetString(0),
        ClientSize: reader.GetInt64(1),
        ClientMtimeTicks: reader.GetInt64(2),
        ServerSize: reader.GetInt64(3),
        ServerMtimeTicks: reader.GetInt64(4),
        Status: reader.GetString(5),
        LastSyncedTicks: reader.GetInt64(6),
        DeletedUtcTicks: reader.IsDBNull(7) ? null : reader.GetInt64(7));

    public AncestorRow? GetRow(string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = AncestorSelect + " WHERE path = $path COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$path", path);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadAncestorRow(reader) : null;
    }

    public Dictionary<string, AncestorRow> LoadAll()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = AncestorSelect + ";";
        using var reader = cmd.ExecuteReader();
        // OrdinalIgnoreCase matches the table's NOCASE primary key; an ordinal dictionary
        // would miss rows whose casing drifted between scans on Windows.
        var rows = new Dictionary<string, AncestorRow>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var row = ReadAncestorRow(reader);
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
```

**Edit 3.2g — `src/RemoteFileSync/State/SyncDatabase.cs:244-327`, replace the `MarkSynced` + `MarkDeleted` block with `UpsertSynced` / `Tombstone` and their shims.**

Current:
```csharp
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
```

Replacement:
```csharp
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
            // deleted_utc is cleared on every successful sync: a resurrected path that kept
            // its tombstone date would be silently dropped by PurgeTombstonesOlderThan,
            // losing the ancestor and re-opening the delete-loop this schema exists to close.
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

            // History records the client side; it is a human-facing audit log, not an ancestor.
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
                // observed, and the next run would read that phantom entry as evidence.
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

    // ── Legacy v1 write surface (thin shims over the v2 API) ──────────────────

    /// <summary>Legacy one-sided upsert: v1 had a single size+mtime, so both sides get it.</summary>
    public void MarkSynced(string path, long fileSize, DateTime lastModified, long sessionId, string direction)
    {
        var ticks = lastModified.ToUniversalTime().Ticks;
        UpsertSynced(path, fileSize, ticks, fileSize, ticks, sessionId, direction);
    }

    public void MarkDeleted(string path, long sessionId, string? detail) =>
        Tombstone(path, sessionId, detail);
```

**Edit 3.2h — `src/RemoteFileSync/State/SyncDatabase.cs:341-383`, retarget `MarkNew` onto the v2 columns.**

Current:
```csharp
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
```

Replacement:
```csharp
    /// <summary>
    /// Legacy discovery marker. No production caller remains; kept for the v1 test suite.
    /// The <paramref name="side"/> argument is accepted but not stored — v2 dropped the column.
    /// Rows land with status='new', which every v2 decision table treats as "no usable
    /// ancestor" and routes down the newest-wins path, never down the delete path.
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
```

**Edit 3.2i — `tests/RemoteFileSync.Tests/State/SyncDatabaseTests.cs:180-188`, drop the one assertion schema v2 cannot honour.**

Current:
```csharp
    [Fact]
    public void MarkNew_SetsStatusNew()
    {
        _db.MarkNew("incoming/newfile.txt", fileSize: 512, lastModified: DateTime.UtcNow, side: "remote");

        var state = _db.GetFileState("incoming/newfile.txt");
        Assert.NotNull(state);
        Assert.Equal("new", state!.Status);
        Assert.Equal("remote", state.Side);
    }
```

Replacement:
```csharp
    [Fact]
    public void MarkNew_SetsStatusNew()
    {
        _db.MarkNew("incoming/newfile.txt", fileSize: 512, lastModified: DateTime.UtcNow, side: "remote");

        var state = _db.GetFileState("incoming/newfile.txt");
        Assert.NotNull(state);
        Assert.Equal("new", state!.Status);
        // No Side assertion: schema v2 dropped the `side` column, so "remote" is unrecoverable.
    }
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncDatabaseSchemaV2Tests"`
Expected: PASS — 7 passed.

Then confirm the shims held the old suite:

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncDatabaseTests|FullyQualifiedName~SyncDatabaseMigrationTests|FullyQualifiedName~SyncEngineTests"`
Expected: PASS — all previously-green tests still green.

---

### Task 3.3: PurgeTombstonesOlderThan

- [ ] **Step 1: Write the failing test**

Append to `tests/RemoteFileSync.Tests/State/SyncDatabaseSchemaV2Tests.cs`, inside the class:

```csharp
    /// <summary>Ages a tombstone without going through the public API, which always stamps "now".</summary>
    private void BackdateDeletedUtc(string path, long ticks)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE files SET deleted_utc = $ticks WHERE path = $path COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$ticks", ticks);
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
        BackdateDeletedUtc("old-tombstone.txt", DateTime.UtcNow.AddDays(-90).Ticks);

        var removed = db.PurgeTombstonesOlderThan(TimeSpan.FromDays(30));

        Assert.Equal(1, removed);
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
        // gate. Purging a live ancestor would make the next run see the file as brand new.
        BackdateDeletedUtc("alive.txt", DateTime.UtcNow.AddYears(-5).Ticks);

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
        BackdateDeletedUtcToNull("unknown-age.txt");

        Assert.Equal(0, db.PurgeTombstonesOlderThan(TimeSpan.FromDays(30)));
        Assert.Equal("deleted", db.GetRow("unknown-age.txt")!.Status);
    }

    private void BackdateDeletedUtcToNull(string path)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE files SET deleted_utc = NULL WHERE path = $path COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$path", path);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void PurgeTombstonesOlderThan_NegativeAge_Throws()
    {
        using var db = new SyncDatabase(_dbPath);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => db.PurgeTombstonesOlderThan(TimeSpan.FromDays(-1)));
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~PurgeTombstonesOlderThan"`
Expected: FAIL — build error `CS1061: 'SyncDatabase' does not contain a definition for 'PurgeTombstonesOlderThan'`.

- [ ] **Step 3: Implement**

Insert into `src/RemoteFileSync/State/SyncDatabase.cs` immediately after the `Tombstone` method added in Edit 3.2g, before the `// ── Legacy v1 write surface` banner:

```csharp
    public int PurgeTombstonesOlderThan(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(age),
                "Negative retention puts the cutoff in the future and would purge every tombstone.");

        using var cmd = _conn.CreateCommand();
        // status is the gate, not deleted_utc alone: an 'exists' row must never be purged
        // regardless of a stale deleted_utc, and a tombstone with a NULL deleted_utc is kept
        // because its age is unknowable — dropping it would silently discard an ancestor.
        cmd.CommandText = @"
DELETE FROM files
WHERE status = 'deleted' AND deleted_utc IS NOT NULL AND deleted_utc < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.Ticks - age.Ticks);
        return cmd.ExecuteNonQuery();
    }
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~PurgeTombstonesOlderThan"`
Expected: PASS — 4 passed.

---

### Task 3.4: v1 → v2 migration from a real v1 database

- [ ] **Step 1: Write the failing test**

Create `tests/RemoteFileSync.Tests/State/SyncDatabaseSchemaMigrationTests.cs`:

```csharp
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

    private static readonly DateTime Mtime    = new(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SyncedAt = new(2026, 3, 28, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DeletedAt = new(2026, 4, 2, 8, 0, 0, DateTimeKind.Utc);

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

        using (var db = new SyncDatabase(_dbPath)) { }
        SqliteConnection.ClearAllPools();

        Assert.Equal(2, UserVersion());
        Assert.Equal(
            new[] { "path", "client_size", "client_mtime", "server_size", "server_mtime",
                    "status", "last_synced", "deleted_utc" },
            ColumnsOf("files"));
        // The create/copy/drop/rename scratch table must not survive the transaction.
        Assert.False(TableExists("files_v2"));
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
            // A second open sees user_version=2 and must skip the rebuild; re-running it
            // against a v2 table would find no file_size column and throw.
            Assert.Equal(2, db.LoadAll().Count);
            Assert.NotNull(db.GetRow("docs/report.docx"));
            Assert.NotNull(db.GetRow("data/export.csv"));
        }
        SqliteConnection.ClearAllPools();

        Assert.Equal(2, UserVersion());
        Assert.False(TableExists("files_v2"));
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
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncDatabaseSchemaMigrationTests"`
Expected: PASS if Edit 3.2e already landed (the migration ships with `InitSchema`). If Task 3.2 is being reordered so this task lands first, expected: FAIL — `Assert.Equal() Failure: Expected: 0, Actual: ["path","file_size","last_modified","status","last_synced","side"]` in `OpeningV1Database_RebuildsTableInV2Shape`, and `SqliteException: no such column: client_size` in `OpeningV1Database_CopiesSizeAndMtimeToBothSides`.

- [ ] **Step 3: Implement**

No new production code — the migration is `MigrateV1ToV2` from Edit 3.2e. This task exists to prove that edit against a genuine v1 file rather than a synthetic one. If any assertion fails, fix `MigrateV1ToV2` only; do not weaken the test.

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncDatabaseSchemaMigrationTests"`
Expected: PASS — 7 passed.

---

### Task 3.5: Conflict and resurrection logging

- [ ] **Step 1: Write the failing test**

Append to `tests/RemoteFileSync.Tests/State/SyncDatabaseSchemaV2Tests.cs`, inside the class:

```csharp
    [Fact]
    public void LogConflictAndLogResurrection_AreReadBackPerSessionAndPerKind()
    {
        using var db = new SyncDatabase(_dbPath);
        var s1 = db.StartSession("two-way", "/folder", "host", 8765);
        var s2 = db.StartSession("two-way", "/folder", "host", 8765);

        db.LogConflict("docs/report.docx", s1, "both sides changed since last sync");
        db.LogResurrection("docs/notes.txt", s1, "client changed a file the server deleted");
        db.LogConflict("other/file.txt", s2, "both sides changed since last sync");

        var conflicts = db.GetSessionConflicts(s1);
        Assert.Single(conflicts);
        Assert.Equal("docs/report.docx", conflicts[0].Path);
        Assert.Equal("both sides changed since last sync", conflicts[0].Detail);
        Assert.Equal(DateTimeKind.Utc, conflicts[0].Timestamp.Kind);

        var resurrections = db.GetSessionResurrections(s1);
        Assert.Single(resurrections);
        Assert.Equal("docs/notes.txt", resurrections[0].Path);

        // Neither kind may leak into the other's report, nor across session boundaries.
        Assert.Empty(db.GetSessionResurrections(s2));
        Assert.Single(db.GetSessionConflicts(s2));
    }

    [Fact]
    public void GetSessionConflicts_NoneLogged_ReturnsEmpty()
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
        db.LogConflict("docs/report.docx", s, "both sides changed");

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
    public void LogConflict_UntrackedPath_IsStillRecorded()
    {
        // Unlike Tombstone, a conflict is an observation about live files on both sides and
        // does not require a pre-existing ancestor row.
        using var db = new SyncDatabase(_dbPath);
        var s = db.StartSession("two-way", "/folder", "host", 8765);
        db.LogConflict("never-synced.txt", s, "both sides appeared at once");

        Assert.Single(db.GetSessionConflicts(s));
        Assert.Null(db.GetRow("never-synced.txt"));
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~LogConflict|FullyQualifiedName~GetSessionConflicts"`
Expected: FAIL — build errors `CS1061: 'SyncDatabase' does not contain a definition for 'LogConflict'`, `'LogResurrection'`, `'GetSessionConflicts'`, `'GetSessionResurrections'`.

- [ ] **Step 3: Implement**

Insert into `src/RemoteFileSync/State/SyncDatabase.cs` immediately before the `// ── History ──` banner (currently `:385`):

```csharp
    // ── Conflict / resurrection log ───────────────────────────────────────────

    public void LogConflict(string path, long sessionId, string detail) =>
        LogVersionAction(path, "conflict", sessionId, detail);

    /// <summary>
    /// CONTRACT ADDITION (pending sign-off): CONTRACT.md defines GetSessionResurrections and
    /// the file_versions action 'resurrected' but no writer for it, and LogConflict's frozen
    /// signature cannot carry an action. This is the minimal mirror that closes the gap.
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

    private IReadOnlyList<ConflictEntry> GetSessionEntries(long sessionId, string action)
    {
        using var cmd = _conn.CreateCommand();
        // id breaks ties: two entries logged in the same tick must still report in write order.
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
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncDatabaseSchemaV2Tests"`
Expected: PASS — 15 passed (7 from Task 3.2 + 4 from Task 3.3 + 4 from Task 3.5).

---

### Phase 3 commit

```bash
git add src/RemoteFileSync/State/PairMarker.cs \
        src/RemoteFileSync/State/SyncDatabase.cs \
        tests/RemoteFileSync.Tests/State/PairMarkerTests.cs \
        tests/RemoteFileSync.Tests/State/SyncDatabaseSchemaV2Tests.cs \
        tests/RemoteFileSync.Tests/State/SyncDatabaseSchemaMigrationTests.cs \
        tests/RemoteFileSync.Tests/State/SyncDatabaseTests.cs
git commit -m "feat(state): schema v2 with per-side ancestor columns, tombstone retention and PairMarker

Rebuild the files table with separate client/server size+mtime so a two-way
merge can tell which side moved, add deleted_utc so tombstones can age out,
and drop the meaningless side column. Migration is create/copy/drop/rename
inside one transaction, gated and stamped by PRAGMA user_version (v1 never
stamped it, so a 0 is disambiguated by the presence of file_size).

Adds GetRow/LoadAll/UpsertSynced/Tombstone/PurgeTombstonesOlderThan and the
conflict + resurrection log. MarkSynced/MarkDeleted/MarkNew/GetFileState/
GetAllTrackedFiles/GetDeletedFiles stay as thin shims so SyncClient and
SyncEngine keep compiling until their own phases land.

PairMarker records that a pair has synced at least once, so a later phase can
tell a genuine first run from lost state instead of mirroring a full delete.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
git push -u origin feat/deletion-sync-ancestor-merge
```

**Verification before commit:**
```bash
dotnet build -c Release
dotnet test -c Release
```
Expected: 0 errors. One existing test changes knowingly: `SyncDatabaseTests.MarkNew_SetsStatusNew` loses its `Assert.Equal("remote", state.Side)` assertion, because schema v2 drops the `side` column per CONTRACT.md and the value is unrecoverable; the `status == "new"` assertion is retained. Every other test in `SyncDatabaseTests.cs` (16 facts), `SyncDatabaseMigrationTests.cs` (3 facts), `SyncEngineTests.cs` and `DeleteThresholdTests.cs` passes unmodified through the shims — that is the intended regression evidence for the column rebuild.

---

## Phase 4: ChangeDetector and the ancestor-based SyncEngine

**Goal:** Replace every timestamp-vs-LastSynced heuristic in the planner with a three-way merge against a per-file ancestor row, so that "which side changed" is a recorded fact on the paths where we know it and an explicitly additive fallback everywhere else.

**Files:**
- Create: `src/RemoteFileSync/Sync/AncestorRow.cs`
- Create: `src/RemoteFileSync/Sync/ChangeDetector.cs`
- Modify: `src/RemoteFileSync/Sync/SyncEngine.cs:1-234` (delete both legacy overloads, add the new primary overload, extend `BuildMergedManifest`)
- Modify: `src/RemoteFileSync/Sync/ConflictResolver.cs:26-39` (delete `ResolveDeleteConflict`)
- Modify: `src/RemoteFileSync/Network/SyncClient.cs:149-152` (only call site outside tests)
- Test: `tests/RemoteFileSync.Tests/Sync/ChangeDetectorTests.cs` (new)
- Test: `tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs:1-406` (full replacement)
- Test: `tests/RemoteFileSync.Tests/Sync/ConflictResolverTests.cs:9-11,69-133` (delete the `ResolveDeleteConflict` block)

**Interfaces:**
- Consumes (Phase 1): `public enum SyncMode : byte { Push = 1, Pull = 2, TwoWay = 3 }`; `SyncActionType.ConflictKeepBoth = 7`; `SyncOptions.Mode`, `SyncOptions.MirrorDeletes`
- Consumes (Phase 2): `public readonly record struct ClockSkew(TimeSpan Offset)` with `ClockSkew.None` and `DateTime NormaliseServerTime(DateTime serverUtc)`
- Consumes (Phase 3): `Dictionary<string, AncestorRow> SyncDatabase.LoadAll()`
- Produces:
  - `public sealed record AncestorRow(string Path, long ClientSize, long ClientMtimeTicks, long ServerSize, long ServerMtimeTicks, string Status, long LastSyncedTicks, long? DeletedUtcTicks)`
  - `public static bool ChangeDetector.Unchanged(FileEntry current, long rowSize, long rowMtimeTicks)` and `public static readonly TimeSpan ChangeDetector.Tolerance`
  - `public static List<SyncPlanEntry> SyncEngine.ComputePlan(FileManifest clientManifest, FileManifest serverManifest, SyncMode mode, IReadOnlyDictionary<string, AncestorRow>? ancestor, bool deleteEnabled, bool mirrorDeletes, ClockSkew skew)`
- Removed (nothing later may call these): `SyncEngine.ComputePlan(FileManifest, FileManifest, bool)`, `SyncEngine.ComputePlan(FileManifest, FileManifest, bool, SyncState?, bool)`, `SyncEngine.ComputePlan(FileManifest, FileManifest, bool, SyncDatabase?, bool)`, `ConflictResolver.ResolveDeleteConflict(bool, FileEntry, DateTime)`

**Decision — the legacy overloads are DELETED, not kept as shims.** Justification: they cannot be faithfully delegated. `SyncState` stores a *single* manifest and a *single* `LastSyncUtc`, and `FileState` (from `GetAllTrackedFiles`) stores a single size/mtime pair. Neither can distinguish "the client changed" from "the server changed" — that missing distinction *is* the bug. A delegating shim would have to fabricate `ClientSize == ServerSize` and `ClientMtimeTicks == ServerMtimeTicks`, which returns `Skip` for the both-changed case and silently loses an edit. Keeping them compiling would keep the defect reachable from a two-argument typo. The `bool bidirectional` parameter is independently unsalvageable: `false` now means Push *or* Pull, and those tables are mirror images. Every call site is shown updated in Task 4.3.

**Known-inert for one phase:** `ConflictKeepBoth` is emitted by the planner here but not yet executed — `SyncClient` selects transfers by explicit action lists (`SyncClient.cs:261`, `SyncClient.cs:360`), so a `ConflictKeepBoth` entry is currently a no-op rather than a crash. The conflict-rename executor lands in a later phase. This is stated so it is not mistaken for an oversight.

### Task 4.1: ChangeDetector and AncestorRow

`AncestorRow` is a pure data record with no behaviour and gets no dedicated test; it is exercised by the `Row`/`Tombstone` helpers in Task 4.2.

- [ ] **Step 1: Write the failing test**

Create `tests/RemoteFileSync.Tests/Sync/ChangeDetectorTests.cs`:

```csharp
using RemoteFileSync.Models;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class ChangeDetectorTests
{
    private static readonly DateTime RowTime = new(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SameSizeSameMtime_Unchanged()
    {
        var current = new FileEntry("f.txt", 100, RowTime);
        Assert.True(ChangeDetector.Unchanged(current, 100, RowTime.Ticks));
    }

    [Fact]
    public void SizeChanged_MtimeIdentical_ReportsChanged()
    {
        // In-place rewrites that land in the same mtime slot are invisible to a timestamp-only
        // check; the size comparison is the only thing that catches them.
        var current = new FileEntry("f.txt", 250, RowTime);
        Assert.False(ChangeDetector.Unchanged(current, 100, RowTime.Ticks));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.5)]
    [InlineData(-1.5)]
    [InlineData(2)]
    [InlineData(-2)]
    public void MtimeDriftWithinTolerance_Unchanged(double seconds)
    {
        var current = new FileEntry("f.txt", 100, RowTime.AddSeconds(seconds));
        Assert.True(ChangeDetector.Unchanged(current, 100, RowTime.Ticks));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-3)]
    public void MtimeDriftBeyondTolerance_ReportsChanged(double seconds)
    {
        var current = new FileEntry("f.txt", 100, RowTime.AddSeconds(seconds));
        Assert.False(ChangeDetector.Unchanged(current, 100, RowTime.Ticks));
    }

    [Fact]
    public void ToleranceIsTwoSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), ChangeDetector.Tolerance);
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ChangeDetectorTests"`
Expected: FAIL — build error `CS0103: The name 'ChangeDetector' does not exist in the current context` at every call site in the new file.

- [ ] **Step 3: Implement**

Create `src/RemoteFileSync/Sync/AncestorRow.cs`:

```csharp
namespace RemoteFileSync.Sync;

/// <summary>
/// What the two sides looked like the last time they were known to agree. Storing BOTH sides
/// separately is the whole point: a single snapshot cannot tell an edited client copy from an
/// edited server copy, which is how a one-sided deletion used to be mistaken for consensus.
/// </summary>
/// <param name="Status">"exists" while both sides hold the file; "deleted" once tombstoned.</param>
public sealed record AncestorRow(
    string Path,
    long   ClientSize,
    long   ClientMtimeTicks,
    long   ServerSize,
    long   ServerMtimeTicks,
    string Status,
    long   LastSyncedTicks,
    long?  DeletedUtcTicks);
```

Create `src/RemoteFileSync/Sync/ChangeDetector.cs`:

```csharp
using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

public static class ChangeDetector
{
    /// <summary>
    /// Filesystems round mtimes (FAT to 2s, some SMB shares to 1s), so a byte-identical file can
    /// come back with a slightly different stamp after a round trip. Sizes never drift, so they
    /// are compared exactly.
    /// </summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// True when <paramref name="current"/> still matches what the ancestor row recorded for that
    /// side. Size is checked first and without tolerance: a rewrite that changes length but lands
    /// inside the mtime tolerance window would otherwise read as unchanged and be deleted.
    /// </summary>
    public static bool Unchanged(FileEntry current, long rowSize, long rowMtimeTicks)
    {
        if (current.FileSize != rowSize) return false;
        return Math.Abs(current.LastModifiedUtc.Ticks - rowMtimeTicks) <= Tolerance.Ticks;
    }
}
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ChangeDetectorTests"`
Expected: PASS (11 tests)

### Task 4.2: The new ComputePlan overload

- [ ] **Step 1: Write the failing test**

Full replacement for `tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs` (replaces lines 1-406 in their entirety; the per-test disposition of the old file is enumerated in Task 4.4):

```csharp
using RemoteFileSync.Models;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class SyncEngineTests
{
    private static readonly DateTime T1 = new(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);

    private static FileManifest MakeManifest(params FileEntry[] entries)
    {
        var m = new FileManifest();
        foreach (var e in entries) m.Add(e);
        return m;
    }

    /// <summary>Row for a file both sides agreed on at <paramref name="mtime"/>.</summary>
    private static AncestorRow Row(string path, long size, DateTime mtime) =>
        new(path, size, mtime.Ticks, size, mtime.Ticks, "exists", T1.Ticks, null);

    /// <summary>Row for a file already tombstoned — must behave exactly like "no row".</summary>
    private static AncestorRow Tombstoned(string path, long size, DateTime mtime) =>
        new(path, size, mtime.Ticks, size, mtime.Ticks, "deleted", T1.Ticks, T1.Ticks);

    private static Dictionary<string, AncestorRow> Ancestor(params AncestorRow[] rows) =>
        rows.ToDictionary(r => r.Path, r => r, StringComparer.OrdinalIgnoreCase);

    private static List<SyncPlanEntry> Plan(
        FileManifest client,
        FileManifest server,
        SyncMode mode,
        IReadOnlyDictionary<string, AncestorRow>? ancestor,
        bool deleteEnabled = true,
        bool mirrorDeletes = false,
        ClockSkew? skew = null) =>
        SyncEngine.ComputePlan(client, server, mode, ancestor, deleteEnabled, mirrorDeletes,
                               skew ?? ClockSkew.None);

    private static Dictionary<string, SyncActionType> Actions(List<SyncPlanEntry> plan) =>
        plan.ToDictionary(p => p.RelativePath, p => p.Action, StringComparer.OrdinalIgnoreCase);

    // ── TwoWay, row present, Status == "exists" ───────────────────────────────

    [Fact]
    public void TwoWay_UnchangedBothSides_Skip()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var plan = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(plan);
        Assert.Equal(SyncActionType.Skip, plan[0].Action);
    }

    [Fact]
    public void TwoWay_ClientChangedOnly_SendToServer()
    {
        var client = MakeManifest(new FileEntry("f.txt", 150, T2));
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var plan = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(plan);
        Assert.Equal(SyncActionType.SendToServer, plan[0].Action);
    }

    [Fact]
    public void TwoWay_ServerChangedOnly_SendToClient()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = MakeManifest(new FileEntry("f.txt", 150, T2));
        var plan = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(plan);
        Assert.Equal(SyncActionType.SendToClient, plan[0].Action);
    }

    [Fact]
    public void TwoWay_BothChanged_ConflictKeepBoth()
    {
        // Both sides edited since the ancestor. Neither edit may be silently discarded, so the
        // plan keeps both and the executor renames the loser.
        var client = MakeManifest(new FileEntry("f.txt", 150, T2));
        var server = MakeManifest(new FileEntry("f.txt", 220, T2.AddMinutes(5)));
        var plan = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(plan);
        Assert.Equal(SyncActionType.ConflictKeepBoth, plan[0].Action);
    }

    [Fact]
    public void TwoWay_ClientAbsent_ServerUnchanged_DeleteOnServer()
    {
        // Rule [1]: the client deleted it and nobody touched the server copy, so the deletion is
        // the only edit in play and it propagates.
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var plan = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(plan);
        Assert.Equal(SyncActionType.DeleteOnServer, plan[0].Action);
        Assert.Equal("f.txt", plan[0].RelativePath);
    }

    [Fact]
    public void TwoWay_ClientAbsent_ServerChanged_SendToClient()
    {
        // Rule [2]: a delete on one side loses to a real edit on the other. Losing the edit is
        // unrecoverable; an unwanted resurrection costs one more delete.
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("f.txt", 220, T2));
        var plan = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(plan);
        Assert.Equal(SyncActionType.SendToClient, plan[0].Action);
    }

    [Fact]
    public void TwoWay_ServerAbsent_ClientUnchanged_DeleteOnClient()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = new FileManifest();
        var plan = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(plan);
        Assert.Equal(SyncActionType.DeleteOnClient, plan[0].Action);
    }

    [Fact]
    public void TwoWay_ServerAbsent_ClientChanged_SendToServer()
    {
        var client = MakeManifest(new FileEntry("f.txt", 220, T2));
        var server = new FileManifest();
        var plan = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(plan);
        Assert.Equal(SyncActionType.SendToServer, plan[0].Action);
    }

    [Fact]
    public void TwoWay_AbsentBothSides_NoPlanEntry()
    {
        // Both sides already removed it. There is nothing to transfer and nothing to delete;
        // the caller tombstones the row, ComputePlan stays free of database writes.
        var plan = Plan(new FileManifest(), new FileManifest(), SyncMode.TwoWay,
                        Ancestor(Row("f.txt", 100, T1)));
        Assert.Empty(plan);
    }

    [Fact]
    public void TwoWay_SizeChangedMtimeIdentical_CountsAsChanged()
    {
        // An in-place rewrite keeps the mtime inside tolerance. Comparing mtimes alone returns
        // Skip here and the larger client copy is never pushed.
        var client = MakeManifest(new FileEntry("f.txt", 250, T1));
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var plan = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(plan);
        Assert.Equal(SyncActionType.SendToServer, plan[0].Action);
    }

    [Fact]
    public void TwoWay_DeleteDisabled_ReCopiesInsteadOfDeleting()
    {
        // Without --delete the deletion must not propagate, but dropping the path from the plan
        // would leave the two sides permanently divergent with no record of why.
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var plan = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)),
                        deleteEnabled: false);
        Assert.Single(plan);
        Assert.Equal(SyncActionType.SendToClient, plan[0].Action);
    }

    [Fact]
    public void TwoWay_NewFileWithNoRow_TakesAdditivePath()
    {
        var ancestor = Ancestor(Row("existing.txt", 100, T1));
        var client = MakeManifest(
            new FileEntry("existing.txt", 100, T1),
            new FileEntry("brand-new.txt", 50, T2));
        var server = MakeManifest(new FileEntry("existing.txt", 100, T1));
        var actions = Actions(Plan(client, server, SyncMode.TwoWay, ancestor));
        Assert.Equal(SyncActionType.Skip, actions["existing.txt"]);
        Assert.Equal(SyncActionType.SendToServer, actions["brand-new.txt"]);
    }

    // ── No ancestor (null table, missing row, or tombstoned row) ──────────────

    [Fact]
    public void NoAncestor_AdditiveOnly_NeverEmitsDelete()
    {
        var client = MakeManifest(new FileEntry("c-only.txt", 50, T1));
        var server = MakeManifest(new FileEntry("s-only.txt", 50, T1));
        var plan = Plan(client, server, SyncMode.TwoWay, ancestor: null, deleteEnabled: true);
        var actions = Actions(plan);
        Assert.Equal(SyncActionType.SendToServer, actions["c-only.txt"]);
        Assert.Equal(SyncActionType.SendToClient, actions["s-only.txt"]);
        Assert.DoesNotContain(plan, p => p.Action == SyncActionType.DeleteOnServer);
        Assert.DoesNotContain(plan, p => p.Action == SyncActionType.DeleteOnClient);
    }

    [Fact]
    public void NoAncestor_BothPresent_NewestWins()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T2));
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var plan = Plan(client, server, SyncMode.TwoWay, ancestor: null);
        Assert.Single(plan);
        Assert.Equal(SyncActionType.SendToServer, plan[0].Action);
    }

    [Fact]
    public void NoAncestor_SameMtime_LargerWins()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = MakeManifest(new FileEntry("f.txt", 200, T1));
        var plan = Plan(client, server, SyncMode.TwoWay, ancestor: null);
        Assert.Single(plan);
        Assert.Equal(SyncActionType.SendToClient, plan[0].Action);
    }

    [Fact]
    public void TombstonedRow_TreatedAsNoAncestor_NeverDeletes()
    {
        // A "deleted" row is settled history. Reading it as an ancestor would turn a file the
        // user deliberately re-created into an immediate re-deletion.
        var client = MakeManifest(new FileEntry("f.txt", 100, T2));
        var server = new FileManifest();
        var plan = Plan(client, server, SyncMode.TwoWay, Ancestor(Tombstoned("f.txt", 100, T1)));
        Assert.Single(plan);
        Assert.Equal(SyncActionType.SendToServer, plan[0].Action);
    }

    [Fact]
    public void BothEmpty_EmptyPlan()
    {
        Assert.Empty(Plan(new FileManifest(), new FileManifest(), SyncMode.TwoWay, ancestor: null));
    }

    // ── Clock skew ───────────────────────────────────────────────────────────

    [Fact]
    public void ClockSkew_ServerOneHourFast_DoesNotWin()
    {
        // The server's clock is +1h. Its file is byte-identical and was written at the same real
        // instant, but its raw mtime is an hour "newer" and wins newest-wins forever, so every
        // run pulls it down again. THIS TEST FAILS WITHOUT THE SKEW NORMALISATION: with
        // ClockSkew.None the engine returns SendToClient, asserted below to prove the test bites.
        var client = MakeManifest(new FileEntry("f.txt", 100, T2));
        var server = MakeManifest(new FileEntry("f.txt", 100, T2.AddHours(1)));

        var withoutSkew = Plan(client, server, SyncMode.TwoWay, ancestor: null,
                               skew: ClockSkew.None);
        Assert.Equal(SyncActionType.SendToClient, withoutSkew[0].Action);

        var withSkew = Plan(client, server, SyncMode.TwoWay, ancestor: null,
                            skew: new ClockSkew(TimeSpan.FromHours(1)));
        Assert.Single(withSkew);
        Assert.Equal(SyncActionType.Skip, withSkew[0].Action);
    }

    // ── Push (client authoritative) ──────────────────────────────────────────

    [Fact]
    public void Push_NeverEmitsClientSideActions()
    {
        var ancestor = Ancestor(Row("keep.txt", 100, T1), Row("gone.txt", 100, T1));
        var client = MakeManifest(
            new FileEntry("keep.txt", 100, T1),
            new FileEntry("push-me.txt", 50, T2));
        var server = MakeManifest(
            new FileEntry("keep.txt", 100, T1),
            new FileEntry("gone.txt", 100, T1),
            new FileEntry("server-extra.txt", 100, T2));

        var plan = Plan(client, server, SyncMode.Push, ancestor);
        var actions = Actions(plan);

        Assert.Equal(SyncActionType.Skip, actions["keep.txt"]);
        Assert.Equal(SyncActionType.SendToServer, actions["push-me.txt"]);
        Assert.Equal(SyncActionType.DeleteOnServer, actions["gone.txt"]);
        // No row proves the client ever had it, so it is left alone rather than wiped.
        Assert.Equal(SyncActionType.Skip, actions["server-extra.txt"]);

        Assert.DoesNotContain(plan, p => p.Action == SyncActionType.SendToClient);
        Assert.DoesNotContain(plan, p => p.Action == SyncActionType.DeleteOnClient);
    }

    [Fact]
    public void Push_ServerChangedUnderneath_StillSendToServer()
    {
        // Push means the server does not get a vote, even when its copy is the newer one.
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = MakeManifest(new FileEntry("f.txt", 220, T2));
        var plan = Plan(client, server, SyncMode.Push, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(plan);
        Assert.Equal(SyncActionType.SendToServer, plan[0].Action);
    }

    [Fact]
    public void Push_ServerLostFile_RePushed()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = new FileManifest();
        var plan = Plan(client, server, SyncMode.Push, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(plan);
        Assert.Equal(SyncActionType.SendToServer, plan[0].Action);
    }

    [Fact]
    public void Push_UnknownServerFile_WithMirror_DeleteOnServer()
    {
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("stray.txt", 100, T1));
        var plan = Plan(client, server, SyncMode.Push, ancestor: null, mirrorDeletes: true);
        Assert.Single(plan);
        Assert.Equal(SyncActionType.DeleteOnServer, plan[0].Action);
    }

    [Fact]
    public void Push_DeleteDisabled_KeepsServerFile()
    {
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var plan = Plan(client, server, SyncMode.Push, Ancestor(Row("f.txt", 100, T1)),
                        deleteEnabled: false);
        Assert.Single(plan);
        Assert.Equal(SyncActionType.Skip, plan[0].Action);
    }

    // ── Pull (server authoritative) ──────────────────────────────────────────

    [Fact]
    public void Pull_NeverEmitsServerSideActions()
    {
        var ancestor = Ancestor(Row("keep.txt", 100, T1), Row("gone.txt", 100, T1));
        var client = MakeManifest(
            new FileEntry("keep.txt", 100, T1),
            new FileEntry("gone.txt", 100, T1),
            new FileEntry("client-extra.txt", 100, T2));
        var server = MakeManifest(
            new FileEntry("keep.txt", 100, T1),
            new FileEntry("pull-me.txt", 50, T2));

        var plan = Plan(client, server, SyncMode.Pull, ancestor);
        var actions = Actions(plan);

        Assert.Equal(SyncActionType.Skip, actions["keep.txt"]);
        Assert.Equal(SyncActionType.SendToClient, actions["pull-me.txt"]);
        Assert.Equal(SyncActionType.DeleteOnClient, actions["gone.txt"]);
        Assert.Equal(SyncActionType.Skip, actions["client-extra.txt"]);

        Assert.DoesNotContain(plan, p => p.Action == SyncActionType.SendToServer);
        Assert.DoesNotContain(plan, p => p.Action == SyncActionType.DeleteOnServer);
    }

    [Fact]
    public void Pull_ClientChangedUnderneath_StillSendToClient()
    {
        var client = MakeManifest(new FileEntry("f.txt", 220, T2));
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var plan = Plan(client, server, SyncMode.Pull, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(plan);
        Assert.Equal(SyncActionType.SendToClient, plan[0].Action);
    }

    [Fact]
    public void Pull_UnknownClientFile_WithoutMirror_Skip()
    {
        var client = MakeManifest(new FileEntry("stray.txt", 100, T1));
        var server = new FileManifest();
        var plan = Plan(client, server, SyncMode.Pull, ancestor: null, mirrorDeletes: false);
        Assert.Single(plan);
        Assert.Equal(SyncActionType.Skip, plan[0].Action);
    }

    // ── BuildMergedManifest ──────────────────────────────────────────────────

    [Fact]
    public void BuildMergedManifest_ConflictKeepBoth_KeepsClientEntry()
    {
        // The renamed loser is written into the sync folder and picked up by the next scan; the
        // merged manifest records the winner so the path is not dropped from tracking entirely.
        var client = MakeManifest(new FileEntry("f.txt", 150, T2));
        var server = MakeManifest(new FileEntry("f.txt", 220, T2.AddMinutes(5)));
        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, "f.txt") };
        var merged = SyncEngine.BuildMergedManifest(client, server, plan);
        Assert.Equal(150, merged.Get("f.txt")!.FileSize);
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncEngineTests"`
Expected: FAIL — build error `CS1501: No overload for method 'ComputePlan' takes 7 arguments` at the `Plan` helper, plus `CS0117: 'SyncEngine' does not contain a definition for` nothing else. (`AncestorRow` resolves from Task 4.1; `SyncMode`, `ClockSkew` and `SyncActionType.ConflictKeepBoth` resolve from Phases 1-2.)

- [ ] **Step 3: Implement**

Replace `src/RemoteFileSync/Sync/SyncEngine.cs` lines 1-205 in full. The exact current text being replaced begins:

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
```

…and ends at line 205 with the closing brace of the `SyncDatabase` overload:

```csharp
        return plan;
    }
```

The replacement (lines 1-… of the new file, with `BuildMergedManifest` following unchanged apart from the edit shown after it):

```csharp
using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

public static class SyncEngine
{
    /// <summary>
    /// Builds the sync plan by three-way merge against <paramref name="ancestor"/>.
    /// A null table, a missing row, or a tombstoned row all mean the same thing — we do not know
    /// which side changed — and route to the strictly additive fallback, which can never emit a
    /// deletion. Heuristics live only in that fallback and must not leak into the paths where the
    /// ancestor gives us the answer outright.
    /// </summary>
    public static List<SyncPlanEntry> ComputePlan(
        FileManifest clientManifest,
        FileManifest serverManifest,
        SyncMode mode,
        IReadOnlyDictionary<string, AncestorRow>? ancestor,
        bool deleteEnabled,
        bool mirrorDeletes,
        ClockSkew skew)
    {
        var plan = new List<SyncPlanEntry>();

        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in clientManifest.AllPaths) allPaths.Add(path);
        foreach (var path in serverManifest.AllPaths) allPaths.Add(path);

        // A path present in neither manifest is invisible unless the ancestor names it — that
        // absence is the only evidence a deletion ever happened. Tombstoned rows are deliberately
        // left out: they are settled, and re-adding them would replan deletions every run.
        if (ancestor != null)
        {
            foreach (var row in ancestor.Values)
                if (row.Status == "exists") allPaths.Add(row.Path);
        }

        foreach (var path in allPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var client = clientManifest.Get(path);
            var server = serverManifest.Get(path);

            AncestorRow? row = null;
            if (ancestor != null && ancestor.TryGetValue(path, out var found)) row = found;

            SyncActionType? action = mode switch
            {
                SyncMode.Push => PlanPush(client, server, row, deleteEnabled, mirrorDeletes, skew),
                SyncMode.Pull => PlanPull(client, server, row, deleteEnabled, mirrorDeletes, skew),
                SyncMode.TwoWay => row is { Status: "exists" }
                    ? PlanTwoWayWithAncestor(client, server, row, deleteEnabled)
                    : PlanNoAncestor(client, server, skew),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown sync mode."),
            };

            if (action.HasValue) plan.Add(new SyncPlanEntry(action.Value, path));
        }

        return plan;
    }

    /// <summary>
    /// The only path where we KNOW what happened: the row records what each side looked like when
    /// they last agreed, so "changed" is a recorded fact rather than a timestamp guess. No
    /// newest-wins comparison may appear in this method — that is the bug being fixed.
    /// Clock skew is irrelevant here: the current server mtime and the stored server mtime both
    /// come from the server's clock, so any constant offset cancels.
    /// </summary>
    private static SyncActionType? PlanTwoWayWithAncestor(
        FileEntry? client, FileEntry? server, AncestorRow row, bool deleteEnabled)
    {
        bool clientChanged = client != null
            && !ChangeDetector.Unchanged(client, row.ClientSize, row.ClientMtimeTicks);
        bool serverChanged = server != null
            && !ChangeDetector.Unchanged(server, row.ServerSize, row.ServerMtimeTicks);

        if (client != null && server != null)
        {
            if (!clientChanged && !serverChanged) return SyncActionType.Skip;
            if (clientChanged && !serverChanged) return SyncActionType.SendToServer;
            if (!clientChanged && serverChanged) return SyncActionType.SendToClient;
            return SyncActionType.ConflictKeepBoth;
        }

        if (client != null && server == null)
        {
            // A delete loses to an edit: losing the edit is unrecoverable, an unwanted
            // resurrection costs one more delete.
            if (clientChanged) return SyncActionType.SendToServer;
            // Without --delete the deletion does not propagate. Re-push rather than emit nothing,
            // which would leave the sides divergent with no record of why.
            return deleteEnabled ? SyncActionType.DeleteOnClient : SyncActionType.SendToServer;
        }

        if (client == null && server != null)
        {
            if (serverChanged) return SyncActionType.SendToClient;
            return deleteEnabled ? SyncActionType.DeleteOnServer : SyncActionType.SendToClient;
        }

        // Gone from both sides. Nothing to transfer and nothing to delete; the caller tombstones
        // the row. ComputePlan has no sessionId in scope and must not write to the database.
        return null;
    }

    /// <summary>
    /// No usable row: we cannot tell an edit from a deletion, so this path is strictly additive
    /// and must never return DeleteOnServer or DeleteOnClient.
    /// </summary>
    private static SyncActionType? PlanNoAncestor(FileEntry? client, FileEntry? server, ClockSkew skew)
    {
        if (client != null && server != null) return ResolveNoAncestor(client, server, skew);
        if (client != null) return SyncActionType.SendToServer;
        if (server != null) return SyncActionType.SendToClient;
        return null;
    }

    /// <summary>
    /// Newest wins, tie broken by size — but only after the server's mtime is pulled back into
    /// the client's clock domain. A server running an hour fast otherwise wins every comparison
    /// forever and the same bytes are re-downloaded on every run.
    /// </summary>
    private static SyncActionType ResolveNoAncestor(FileEntry client, FileEntry server, ClockSkew skew)
    {
        var normalised = new FileEntry(server.RelativePath, server.FileSize,
                                       skew.NormaliseServerTime(server.LastModifiedUtc));
        return ConflictResolver.Resolve(client, normalised);
    }

    /// <summary>
    /// Client authoritative: the server is made to match and nothing is ever written to the
    /// client, so this method may only return SendToServer, DeleteOnServer or Skip.
    /// </summary>
    private static SyncActionType? PlanPush(
        FileEntry? client, FileEntry? server, AncestorRow? row,
        bool deleteEnabled, bool mirrorDeletes, ClockSkew skew)
    {
        if (client != null && server == null) return SyncActionType.SendToServer;

        if (client != null && server != null)
        {
            // Deliberately not "newest wins": in Push the server does not get a vote, so a newer
            // server copy is still overwritten. Same-content is compared skew-normalised so a
            // clock offset alone does not re-upload every file forever.
            return SameContent(client, server, skew)
                ? SyncActionType.Skip
                : SyncActionType.SendToServer;
        }

        if (client == null && server != null)
        {
            if (!deleteEnabled) return SyncActionType.Skip;
            // Without a row proving the client once held this path, an absent client file is
            // indistinguishable from a file the client never had — deleting it would wipe the
            // server on the first run against an unrelated or repointed folder. --mirror is the
            // explicit opt-in to that risk.
            bool clientHadIt = row is { Status: "exists" };
            return (clientHadIt || mirrorDeletes) ? SyncActionType.DeleteOnServer : SyncActionType.Skip;
        }

        return null;
    }

    /// <summary>
    /// Server authoritative: the exact mirror of <see cref="PlanPush"/>. May only return
    /// SendToClient, DeleteOnClient or Skip.
    /// </summary>
    private static SyncActionType? PlanPull(
        FileEntry? client, FileEntry? server, AncestorRow? row,
        bool deleteEnabled, bool mirrorDeletes, ClockSkew skew)
    {
        if (server != null && client == null) return SyncActionType.SendToClient;

        if (server != null && client != null)
        {
            return SameContent(client, server, skew)
                ? SyncActionType.Skip
                : SyncActionType.SendToClient;
        }

        if (server == null && client != null)
        {
            if (!deleteEnabled) return SyncActionType.Skip;
            bool serverHadIt = row is { Status: "exists" };
            return (serverHadIt || mirrorDeletes) ? SyncActionType.DeleteOnClient : SyncActionType.Skip;
        }

        return null;
    }

    /// <summary>
    /// Same bytes as far as metadata can tell, with the server mtime normalised into the client's
    /// clock domain first.
    /// </summary>
    private static bool SameContent(FileEntry client, FileEntry server, ClockSkew skew)
    {
        if (client.FileSize != server.FileSize) return false;
        var normalised = skew.NormaliseServerTime(server.LastModifiedUtc);
        return Math.Abs((client.LastModifiedUtc - normalised).TotalSeconds)
               <= ChangeDetector.Tolerance.TotalSeconds;
    }
```

Then extend `BuildMergedManifest`. Exact current text at `SyncEngine.cs:223-231`:

```csharp
                case SyncActionType.SendToClient:
                case SyncActionType.ServerOnly:
                    var serverEntry = serverManifest.Get(entry.RelativePath);
                    if (serverEntry != null) merged.Add(serverEntry);
                    break;
                case SyncActionType.DeleteOnServer:
                case SyncActionType.DeleteOnClient:
                    break;
            }
```

Replacement:

```csharp
                case SyncActionType.SendToClient:
                case SyncActionType.ServerOnly:
                    var serverEntry = serverManifest.Get(entry.RelativePath);
                    if (serverEntry != null) merged.Add(serverEntry);
                    break;
                case SyncActionType.ConflictKeepBoth:
                    // Both copies survive; the loser is renamed and reappears on the next scan.
                    // Record the client copy so the path is not silently dropped from tracking.
                    var conflictEntry = clientManifest.Get(entry.RelativePath);
                    if (conflictEntry != null) merged.Add(conflictEntry);
                    break;
                case SyncActionType.DeleteOnServer:
                case SyncActionType.DeleteOnClient:
                    break;
            }
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncEngineTests"`
Expected: PASS (25 tests)

### Task 4.3: Delete ResolveDeleteConflict and update every caller

`ResolveDeleteConflict` compared the surviving file's mtime against `LastSynced` to guess whether it had been edited. That guess is exactly the defect: a file whose mtime merely *looked* older than the last sync was deleted on the peer. With `AncestorRow` the same question is answered from the recorded per-side size and mtime, so the method has no remaining caller and no defensible use. `ConflictResolver.Resolve` survives — it is the newest-wins tie-breaker used only by `ResolveNoAncestor`.

- [ ] **Step 1: Write the failing test**

The failing "test" here is the compiler plus the trimmed `ConflictResolverTests`. Delete lines 9-11 of `tests/RemoteFileSync.Tests/Sync/ConflictResolverTests.cs`, exact current text:

```csharp
    private static readonly DateTime LastSync = new(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BeforeSync = new(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AfterSync = new(2026, 3, 27, 8, 0, 0, DateTimeKind.Utc);
```

(replaced by nothing), and delete lines 69-133, exact current text from `DeletedOnClient_UntouchedOnServer_ReturnsDeleteOnServer` through the closing brace of `DeleteConflict_TimestampJustBeyondTolerance_TreatedAsModified`:

```csharp
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
        var withinTolerance = LastSync.AddSeconds(1);
        var serverEntry = new FileEntry("file.txt", 100, withinTolerance);
        var result = ConflictResolver.ResolveDeleteConflict(
            deletedOnClient: true, survivingEntry: serverEntry, lastSyncUtc: LastSync);
        Assert.Equal(SyncActionType.DeleteOnServer, result);
    }

    [Fact]
    public void DeleteConflict_TimestampExactlyAtTolerance_TreatedAsUntouched()
    {
        var atTolerance = LastSync.AddSeconds(2);
        var serverEntry = new FileEntry("file.txt", 100, atTolerance);
        var result = ConflictResolver.ResolveDeleteConflict(
            deletedOnClient: true, survivingEntry: serverEntry, lastSyncUtc: LastSync);
        Assert.Equal(SyncActionType.DeleteOnServer, result);
    }

    [Fact]
    public void DeleteConflict_TimestampJustBeyondTolerance_TreatedAsModified()
    {
        var beyondTolerance = LastSync.AddSeconds(3);
        var serverEntry = new FileEntry("file.txt", 100, beyondTolerance);
        var result = ConflictResolver.ResolveDeleteConflict(
            deletedOnClient: true, survivingEntry: serverEntry, lastSyncUtc: LastSync);
        Assert.Equal(SyncActionType.SendToClient, result);
    }
```

(replaced by nothing; the file now ends after `Tolerance_JustOver2Seconds_NotSkipped` and its class-closing brace).

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ConflictResolverTests"`
Expected: FAIL — `CS0103: The name 'LastSync' does not exist in the current context` does **not** appear (those tests are gone); instead the whole solution fails to build with `CS1501: No overload for method 'ComputePlan' takes 5 arguments` at `src/RemoteFileSync/Network/SyncClient.cs:151` and `:152`, because Task 4.2 removed the legacy overloads. That is the failure this task fixes.

- [ ] **Step 3: Implement**

Delete `ConflictResolver.ResolveDeleteConflict`. Exact current text at `src/RemoteFileSync/Sync/ConflictResolver.cs:26-39`:

```csharp
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
```

Replacement:

```csharp
    // ResolveDeleteConflict was removed: it decided whether a surviving file had been edited by
    // comparing its mtime against the session-wide LastSynced, which deleted any file whose
    // stamp merely looked older. SyncEngine now answers that from the per-side AncestorRow.
```

The complete resulting `src/RemoteFileSync/Sync/ConflictResolver.cs`:

```csharp
using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

public static class ConflictResolver
{
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Newest wins, ties broken by size. Only valid on the no-ancestor path: with no record of
    /// what the sides last agreed on, the timestamp is the only signal available. Callers must
    /// normalise the server entry for clock skew before calling.
    /// </summary>
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

    // ResolveDeleteConflict was removed: it decided whether a surviving file had been edited by
    // comparing its mtime against the session-wide LastSynced, which deleted any file whose
    // stamp merely looked older. SyncEngine now answers that from the per-side AncestorRow.
}
```

Update the sole production call site. Exact current text at `src/RemoteFileSync/Network/SyncClient.cs:149-152`:

```csharp
        // 6. Compute sync plan and send
        var syncPlan = (_db != null)
            ? SyncEngine.ComputePlan(clientManifest, serverManifest, _options.Bidirectional, _db, _options.DeleteEnabled)
            : SyncEngine.ComputePlan(clientManifest, serverManifest, _options.Bidirectional, previousState, _options.DeleteEnabled);
```

Replacement:

```csharp
        // 6. Compute sync plan and send
        // A null ancestor table is the honest signal for "we do not know what changed", and the
        // engine refuses to emit any deletion on that path. Skew stays None until the v3
        // handshake carries the server clock.
        IReadOnlyDictionary<string, AncestorRow>? ancestor = _db?.LoadAll();
        var syncPlan = SyncEngine.ComputePlan(
            clientManifest, serverManifest, _options.Mode, ancestor,
            _options.DeleteEnabled, _options.MirrorDeletes, ClockSkew.None);
```

`previousState` remains live — it is still read at `SyncClient.cs:129-132` and `SyncClient.cs:240` — so no unused-local warning results.

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ConflictResolverTests|FullyQualifiedName~SyncEngineTests|FullyQualifiedName~ChangeDetectorTests"`
Expected: PASS (7 + 25 + 11 tests)

### Task 4.4: Disposition of every pre-existing test

No step-by-step TDD here — this is the audit trail for the two rewritten test files. Every method is accounted for by name and original line.

**`tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs`** (all 27 members; the file is replaced wholesale in Task 4.2):

| # | Original test (line) | Disposition | New form / justification |
|---|---|---|---|
| 1 | `BothEmpty_EmptyPlan` (25) | UPDATED | Same name; now `Plan(..., SyncMode.TwoWay, ancestor: null)`. |
| 2 | `IdenticalFiles_AllSkipped` (32) | UPDATED, renamed | `TwoWay_UnchangedBothSides_Skip` with a matching ancestor row. The old form asserted `Assert.All` over a possibly-empty plan, which passes vacuously; the new form asserts `Single` first. |
| 3 | `ClientOnly_Unidirectional_ProducesClientOnlyAction` (41) | UPDATED, renamed | `Push_NeverEmitsClientSideActions` (`push-me.txt` leg). `ClientOnly` is no longer emitted — the new tables produce `SendToServer`. `SyncClient.cs:261` already treats the two identically, so no executor change is needed. |
| 4 | `ServerOnly_Unidirectional_Ignored` (51) | DELETED | Subsumed by `Push_NeverEmitsClientSideActions`, which asserts the stronger invariant (no `SendToClient` **and** no `DeleteOnClient` anywhere in the plan) rather than "nothing but Skip". |
| 5 | `ServerOnly_Bidirectional_ProducesServerOnlyAction` (60) | UPDATED, renamed | `NoAncestor_AdditiveOnly_NeverEmitsDelete` (`s-only.txt` leg); `ServerOnly` → `SendToClient`. |
| 6 | `ClientNewer_SendToServer` (70) | UPDATED, renamed | `NoAncestor_BothPresent_NewestWins`. Semantics unchanged; only reachable on the no-ancestor path now. |
| 7 | `ServerNewer_SendToClient` (80) | UPDATED, renamed | `NoAncestor_SameMtime_LargerWins` covers the size tie-break; the mtime direction is covered by #6 and by `ConflictResolverTests.ServerNewer_ReturnsSendToClient`, which survives untouched. |
| 8 | `MixedScenario_CorrectPlan` (90) | UPDATED, renamed | `TwoWay_NewFileWithNoRow_TakesAdditivePath` plus `NoAncestor_AdditiveOnly_NeverEmitsDelete`. `ClientOnly`→`SendToServer`, `ServerOnly`→`SendToClient`. |
| 9 | `DeletedOnClient_UntouchedOnServer_ProducesDeleteOnServer` (109) | DELETED | Replaced by `TwoWay_ClientAbsent_ServerUnchanged_DeleteOnServer`. The original drove the deleted `SyncState` overload and asserted the LastSynced heuristic. |
| 10 | `DeletedOnClient_ModifiedOnServer_ProducesSendToClient` (122) | DELETED | Replaced by `TwoWay_ClientAbsent_ServerChanged_SendToClient` (rule [2]). |
| 11 | `DeletedOnServer_UntouchedOnClient_ProducesDeleteOnClient` (134) | DELETED | Replaced by `TwoWay_ServerAbsent_ClientUnchanged_DeleteOnClient`. |
| 12 | `DeletedOnServer_ModifiedOnClient_ProducesSendToServer` (146) | DELETED | Replaced by `TwoWay_ServerAbsent_ClientChanged_SendToServer`. |
| 13 | `BothDeleted_NoAction` (158) | DELETED | Replaced by `TwoWay_AbsentBothSides_NoPlanEntry`. |
| 14 | `NoState_FullyAdditive` (169) | DELETED | Replaced by `NoAncestor_AdditiveOnly_NeverEmitsDelete`, which additionally asserts no `Delete*` action appears. |
| 15 | `UniDirectional_OnlyClientDeletionsPropagate` (179) | DELETED | Replaced by `Push_NeverEmitsClientSideActions` (`gone.txt` leg). |
| 16 | `NewFileNotInSnapshot_NormalCopyBehavior` (194) | UPDATED, renamed | `TwoWay_NewFileWithNoRow_TakesAdditivePath`; `brand-new.txt` has no row so it takes `PlanNoAncestor` → `SendToServer` (was `ClientOnly`). |
| 17 | `TimestampTolerance_WithinTwoSeconds_TreatedAsUntouched` (209) | DELETED | It tested mtime-vs-`LastSync` tolerance, which is precisely the heuristic being removed. Tolerance is now specified against the ancestor row in `ChangeDetectorTests.MtimeDriftWithinTolerance_Unchanged`. |
| 18 | `UniDirectional_ServerDeletionsIgnored` (223) | DELETED | Subsumed by `Push_NeverEmitsClientSideActions` and `Push_ServerLostFile_RePushed`. Note the behaviour deliberately changed: the old test asserted an empty plan (file silently dropped); Push now re-pushes it, which is the correct client-authoritative answer. |
| 19 | `DeleteEnabled_False_IgnoresDeletions` (236) | UPDATED, renamed | `TwoWay_DeleteDisabled_ReCopiesInsteadOfDeleting`; expectation changes `ServerOnly` → `SendToClient` (same effect, canonical action). |
| 20 | `CreateTestDb` helper (249) | DELETED | The engine now takes a plain `IReadOnlyDictionary`, so the tests no longer need SQLite. This also removes `using Microsoft.Data.Sqlite;` (line 1) and `using RemoteFileSync.State;` (line 3), and eliminates seven temp-directory fixtures and their `SqliteConnection.ClearAllPools()` teardown. |
| 21 | `Db_DeletedFile_InDb_ProducesDeleteAction` (257) | DELETED | Replaced by `TwoWay_ClientAbsent_ServerUnchanged_DeleteOnServer`. |
| 22 | `Db_NewFile_NotInDb_ProducesCopyAction` (279) | DELETED | Replaced by `NoAncestor_AdditiveOnly_NeverEmitsDelete`. |
| 23 | `Db_PreviouslyDeleted_Reappeared_CopiesAgain` (298) | UPDATED, renamed | `TombstonedRow_TreatedAsNoAncestor_NeverDeletes` — same intent, now expressed with a `Status == "deleted"` `AncestorRow` instead of `MarkDeleted`. |
| 24 | `Db_UniDirectional_ServerLostFile_RePushed` (320) | UPDATED, renamed | `Push_ServerLostFile_RePushed`; `ClientOnly` → `SendToServer`. |
| 25 | `Db_PerFileTimestamp_UsedForDeletion` (342) | DELETED | This test *codified* the bug: it asserted that a server mtime later than `LastSynced` means "modified". Its intent is preserved correctly by `TwoWay_ClientAbsent_ServerChanged_SendToClient`, which compares against the recorded server size/mtime instead. It also depended on `DateTime.UtcNow.AddDays(1)`, making it wall-clock dependent. |
| 26 | `Db_DeleteEnabled_False_NormalBehavior` (367) | DELETED | Duplicate of #19 with a DB fixture; subsumed by `TwoWay_DeleteDisabled_ReCopiesInsteadOfDeleting`. |
| 27 | `Db_BothDeletedFromDb_NoAction` (388) | DELETED | Replaced by `TwoWay_AbsentBothSides_NoPlanEntry`. |

**`tests/RemoteFileSync.Tests/Sync/ConflictResolverTests.cs`** (all 14 tests):

| Original test (line) | Disposition |
|---|---|
| `SameTimestampAndSize_ReturnsSkip` (14) | SURVIVES unchanged — `Resolve` is still the no-ancestor tie-breaker. |
| `TimestampWithin2Seconds_SameSize_ReturnsSkip` (22) | SURVIVES unchanged. |
| `ClientNewer_ReturnsSendToServer` (30) | SURVIVES unchanged. |
| `ServerNewer_ReturnsSendToClient` (38) | SURVIVES unchanged. |
| `SameTimestamp_LargerClient_ReturnsSendToServer` (46) | SURVIVES unchanged. |
| `SameTimestamp_LargerServer_ReturnsSendToClient` (54) | SURVIVES unchanged. |
| `Tolerance_JustOver2Seconds_NotSkipped` (62) | SURVIVES unchanged. |
| `DeletedOnClient_UntouchedOnServer_ReturnsDeleteOnServer` (70) | DELETED — tests the removed method. |
| `DeletedOnClient_ModifiedOnServer_ReturnsSendToClient` (79) | DELETED — tests the removed method. |
| `DeletedOnServer_UntouchedOnClient_ReturnsDeleteOnClient` (88) | DELETED — tests the removed method. |
| `DeletedOnServer_ModifiedOnClient_ReturnsSendToServer` (97) | DELETED — tests the removed method. |
| `DeleteConflict_TimestampWithinTolerance_TreatedAsUntouched` (106) | DELETED — replaced by `ChangeDetectorTests.MtimeDriftWithinTolerance_Unchanged`. |
| `DeleteConflict_TimestampExactlyAtTolerance_TreatedAsUntouched` (116) | DELETED — replaced by the same theory's `InlineData(2)` case. |
| `DeleteConflict_TimestampJustBeyondTolerance_TreatedAsModified` (126) | DELETED — replaced by `ChangeDetectorTests.MtimeDriftBeyondTolerance_ReportsChanged`. |

Fields `LastSync` (9), `BeforeSync` (10) and `AfterSync` (11) are deleted with their only consumers; `BaseTime` (8) stays.

### Phase 4 commit

```bash
git add src/RemoteFileSync/Sync/AncestorRow.cs \
        src/RemoteFileSync/Sync/ChangeDetector.cs \
        src/RemoteFileSync/Sync/SyncEngine.cs \
        src/RemoteFileSync/Sync/ConflictResolver.cs \
        src/RemoteFileSync/Network/SyncClient.cs \
        tests/RemoteFileSync.Tests/Sync/ChangeDetectorTests.cs \
        tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs \
        tests/RemoteFileSync.Tests/Sync/ConflictResolverTests.cs
git commit -m "feat(sync): plan deletions from a per-file ancestor row instead of LastSynced

ResolveDeleteConflict decided whether a surviving file had been edited by
comparing its mtime against the session-wide LastSynced, so any file whose
stamp merely looked older than the last sync was deleted on the peer.

ComputePlan now takes an AncestorRow table recording what each side looked
like when they last agreed. The with-ancestor and no-ancestor paths are
separate methods: the former decides from recorded facts and may delete, the
latter is strictly additive and can never emit a delete. Push and Pull are
explicit mirror tables rather than a bidirectional bool.

Server mtimes are normalised through ClockSkew before any newest-wins
comparison, and ChangeDetector compares size as well as mtime so an in-place
rewrite is not mistaken for an untouched file.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
git push -u origin feat/deletion-sync-ancestor-merge
```

**Verification before commit:**
```bash
dotnet build -c Release
dotnet test -c Release
```
Expected: 0 errors.

Existing tests knowingly changed:
- `tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs` — replaced in full. 10 tests updated/renamed, 17 deleted, 15 added (net 25). Per-test justification in Task 4.4. The deletions are all tests that asserted the LastSynced heuristic or drove a deleted overload; none covers behaviour that survives uncovered.
- `tests/RemoteFileSync.Tests/Sync/ConflictResolverTests.cs` — 7 tests deleted (all `ResolveDeleteConflict`), 7 survive byte-identical.
- One deliberate behaviour change beyond the redesign proper: `UniDirectional_ServerDeletionsIgnored` asserted that a client file missing from the server yields an empty plan in uni-directional mode. Push now re-pushes it (`Push_ServerLostFile_RePushed`), which is what client-authoritative means.

Integration tests under `tests/RemoteFileSync.Tests/Integration/` do not call `ComputePlan` directly (verified: `grep -n "ComputePlan\|ResolveDeleteConflict" tests/` matches only the two files above), so they compile unchanged. They exercise the engine through `SyncClient`, which now routes to the new overload — any behavioural fallout there surfaces in the full `dotnet test` run and belongs to this phase to fix.

---

## Phase 5: ConflictKeepBoth execution — preserve both copies, rename the loser

**Goal:** Execute `SyncActionType.ConflictKeepBoth` by renaming the losing copy to the contract's conflict name, archiving it with `ArchiveReason.Conflict`, and moving both copies across the wire using only the existing transfer actions so neither peer's frame sequence can desync.

**Files:**
- Create: `src/RemoteFileSync/Sync/ConflictNamer.cs`
- Create: `src/RemoteFileSync/Sync/ConflictKeepBothExecutor.cs`
- Modify: `src/RemoteFileSync/Network/SyncClient.cs:169-183`
- Modify: `src/RemoteFileSync/Network/SyncClient.cs:257-259`
- Modify: `src/RemoteFileSync/Network/SyncServer.cs:178-180`
- Test: `tests/RemoteFileSync.Tests/Sync/ConflictNamerTests.cs`
- Test: `tests/RemoteFileSync.Tests/Sync/ConflictKeepBothExecutorTests.cs`
- Test: `tests/RemoteFileSync.Tests/Integration/ConflictKeepBothSyncTests.cs`

**Interfaces:**

- Consumes (from CONTRACT.md, delivered by earlier phases):
  - `SyncActionType.ConflictKeepBoth = 7` (Phase 1)
  - `SyncMode.TwoWay`, `SyncOptions.Mode`, `SyncOptions.EffectiveArchiveFolder` (Phase 1)
  - `public readonly record struct ClockSkew(TimeSpan Offset)` with `ClockSkew.None` and `DateTime NormaliseServerTime(DateTime serverUtc)` (Phase 3)
  - `public void LogConflict(string path, long sessionId, string detail)` (Phase 2)
  - `public enum ArchiveReason { Deleted, Overwritten, Conflict }` and
    `ArchiveManager(string syncFolder, string archiveRoot, DateTime sessionStartUtc)` /
    `bool Archive(string relativePath, ArchiveReason reason, bool removeOriginal)` (Phase 6)
  - Existing, unchanged: `FileTransferSender.SendFileAsync(Stream, short, string, CancellationToken, Action<long>?)`, `FileTransferReceiver.ReceiveFileAsync(Stream, CancellationToken, Func<string,bool>?)`, `record FileReceiveResult(bool Success, string RelativePath, string? ErrorMessage = null)`, `PathGuard.TryResolveWithinRoot(string root, string relativePath, out string fullPath)`.

- **Ordering caveat, stated explicitly:** this phase *consumes* `ArchiveManager`, which Phase 6 *delivers*. `src/RemoteFileSync/Backup/ArchiveManager.cs` must already be in the tree for Phase 5's implementation steps to compile. Land Phase 6 before Phase 5, or cherry-pick `ArchiveManager.cs` ahead of it. This phase does **not** redefine it and does **not** touch the existing `BackupManager` call sites at `SyncClient.cs:209` / `SyncServer.cs:173` — Phase 6 owns that swap.

- **Also consumed, name-sensitive:** Phase 3 leaves a local `ClockSkew skew` inside `SyncClient.HandleConnectionAsync`, computed from the v3 handshake. Step 5.4 reads that local. If Phase 3 named it differently, rename at the call site only — do not recompute skew here.

- Produces (later phases rely on these; **neither is in CONTRACT.md — this phase adds them, declared here rather than silently invented**):

```csharp
// src/RemoteFileSync/Sync/ConflictNamer.cs
namespace RemoteFileSync.Sync;
public static class ConflictNamer
{
    public const string Infix = ".conflict-";
    public const string ClientSide = "client";
    public const string ServerSide = "server";
    public const int MaxOrdinal = 1000;
    public static string Compose(string relativePath, DateTime sessionStartUtc, string losingSide, int ordinal = 1);
    public static string MakeUnique(string syncFolder, string relativePath, DateTime sessionStartUtc, string losingSide);
    public static bool TryParse(string conflictRelativePath, out string originalPath, out string losingSide);
}

// src/RemoteFileSync/Sync/ConflictKeepBothExecutor.cs
namespace RemoteFileSync.Sync;
public readonly record struct ConflictRenameOutcome(int Renamed, IReadOnlyList<string> Failures);
public static class ConflictKeepBothExecutor
{
    public static List<SyncPlanEntry> Expand(
        IReadOnlyList<SyncPlanEntry> plan, FileManifest clientManifest, FileManifest serverManifest,
        ClockSkew skew, DateTime sessionStartUtc, string clientFolder);
    public static ConflictRenameOutcome ApplyLocalRenames(
        IReadOnlyList<SyncPlanEntry> plan, string side, string syncFolder, ArchiveManager archive);
}
```

---

### Wire design — decision and justification

**Question posed by the brief:** should the client expand `ConflictKeepBoth` into `SendToServer` + `SendToClient` before serialising, so the wire never sees action 7?

**Answer: expand, but not *fully*.** Pure expansion cannot express both loser sides, and here is the proof:

- Let `P` be the conflicted path and `N` the conflict name.
- **Case A — server copy wins, client copy loses** (`N` ends in `-client`). The client renames its own `P → N` locally, then `SendToServer(N)` + `SendToClient(P)`. Fully expressible with existing actions; the server needs zero new behaviour.
- **Case B — client copy wins, server copy loses** (`N` ends in `-server`). `N` must contain the *server's* old bytes and must exist under that name in both sync folders. The only holder of those bytes is the server. Getting them to the client under the name `N` requires the server's sender to emit `FileStart` with path `N` — and `FileTransferSender.SendFileAsync` derives the wire path from the plan entry and opens that exact file (`FileTransfer.cs:24`), so `N` must exist on the server's disk first. No reordering helps: the server's receive phase (`SyncServer.cs:184`) runs *before* its send phase (`SyncServer.cs:311`), so `P` on the server is already overwritten by the winner before it could be sent. **Case B is unrepresentable without a server-side rename.**

So the recommendation is a **hybrid: client-side expansion into three entries, one of which is a frame-free rename instruction.**

For each `ConflictKeepBoth(P)` the client emits, in order:

| # | entry | who acts | frames exchanged |
|---|---|---|---|
| 1 | `ConflictKeepBoth(N)` | **only** the peer named in `N`'s `losingSide` | **none** |
| 2 | `SendToServer(...)` | client sends, server receives | `FileStart`, `FileChunk`×n, `FileEnd`, `BackupConfirm` |
| 3 | `SendToClient(...)` | server sends, client receives | `FileStart`, `FileChunk`×n, `FileEnd`, `BackupConfirm` |

Entry 2 carries `N` and entry 3 carries `P` in Case A; entry 2 carries `P` and entry 3 carries `N` in Case B. Either way **exactly one file moves client→server and exactly one moves server→client.**

Full MessageType ordering for one conflict, in phase order:

1. `Handshake` → `HandshakeAck` → `Manifest` (c→s) → `Manifest` (s→c) → `SyncPlan` (c→s) — unchanged.
2. **Conflict rename pass.** Client step 7a, server step 5a. Both iterate the plan; only the losing peer touches disk. **Zero messages.**
3. **Transfer phase 1** (`SyncClient.cs:259` / `SyncServer.cs:180`): client → `FileStart`, `FileChunk`…, `FileEnd`; server → `BackupConfirm`.
4. **Deletion phase server** (`SyncClient.cs:325` / `SyncServer.cs:221`): unaffected, `ConflictKeepBoth` matches none of its filters.
5. **Transfer phase 2** (`SyncServer.cs:304` / `SyncClient.cs:356`): server → `FileStart`, `FileChunk`…, `FileEnd`; client → `BackupConfirm`.
6. **Deletion phase client**, then `SyncComplete` ↔ `SyncComplete`: unaffected.

**Why this cannot desync.** The rename pass is the only step where the two peers do different work, and it exchanges no frames — so it cannot shift either side's frame position. Every message-bearing step still derives its work list from `syncPlan.Where(p => p.Action == …)` over an identical `List<SyncPlanEntry>`, and `ConflictKeepBoth` matches none of those predicates (`ProtocolHandler.DeserializeSyncPlan` at `ProtocolHandler.cs:146` casts the action byte without validation, so value 7 round-trips unchanged). The residual risk is a *failed* rename leaving the promised source file missing — `SendFileAsync` throws at `sourceInfo.Length` before writing `FileStart` (`FileTransfer.cs:50`), which would hang the peer on a frame that never arrives. Step 5.4/5.5 therefore make a failed conflict rename **fatal (exit 4) before any frame is sent**, rather than skippable.

**Does the renamed loser get re-synced by the next scan?** No, and that is intended. `N` is transferred to the peer inside the same session, `File.Move` preserves the loser's mtime, and `FileTransferReceiver` restores the sender's mtime from `FileStart` (`FileTransfer.cs:160-161`), so both sides hold `N` with identical size and mtime. The existing send/receive loops write its ancestor row (`SyncClient.cs:307` and `:387`), so the next `ComputePlan` resolves `N` to `Skip`. **No exclusion is needed and none is added.** If `N` fails the user's `--include` filters, the existing `filteredOut` guard at `SyncClient.cs:157-167` retires the row instead of deleting the file — the exact bug that guard exists for.

---

### Task 5.1: ConflictNamer.Compose — the frozen name format

- [ ] **Step 1: Write the failing test**

```csharp
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class ConflictNamerTests
{
    private static readonly DateTime Stamp = new(2026, 7, 20, 14, 30, 52, DateTimeKind.Utc);

    [Theory]
    // Plain name with an extension: the contract's worked example.
    [InlineData("report.docx", "server", "report.conflict-20260720-143052-server.docx")]
    [InlineData("report.docx", "client", "report.conflict-20260720-143052-client.docx")]
    // No extension at all.
    [InlineData("README", "server", "README.conflict-20260720-143052-server")]
    // Multiple dots: Path.GetExtension takes only the last, so ".tar" stays in the stem.
    [InlineData("archive.tar.gz", "client", "archive.tar.conflict-20260720-143052-client.gz")]
    // Subdirectory: the loser must land beside the winner, not at the root.
    [InlineData("docs/q3/report.docx", "server", "docs/q3/report.conflict-20260720-143052-server.docx")]
    // Dotfile: GetExtension returns the whole name, leaving an empty stem. Still round-trips.
    [InlineData(".gitignore", "client", ".conflict-20260720-143052-client.gitignore")]
    public void Compose_MatchesContractFormat(string relativePath, string losingSide, string expected)
    {
        Assert.Equal(expected, ConflictNamer.Compose(relativePath, Stamp, losingSide));
    }

    [Fact]
    public void Compose_OrdinalTwoAppendsSuffixBeforeExtension()
    {
        Assert.Equal("report.conflict-20260720-143052-server-2.docx",
            ConflictNamer.Compose("report.docx", Stamp, ConflictNamer.ServerSide, ordinal: 2));
    }

    [Fact]
    public void Compose_RejectsUnknownLosingSide()
    {
        Assert.Throws<ArgumentException>(() => ConflictNamer.Compose("a.txt", Stamp, "peer"));
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ConflictNamerTests"`
Expected: FAIL — `error CS0246: The type or namespace name 'ConflictNamer' could not be found (are you missing a using directive or an assembly reference?)`

- [ ] **Step 3: Implement**

Create `src/RemoteFileSync/Sync/ConflictNamer.cs`:

```csharp
namespace RemoteFileSync.Sync;

/// <summary>
/// Builds the name a losing copy is renamed to when a ConflictKeepBoth entry is executed:
/// {nameWithoutExtension}.conflict-{yyyyMMdd-HHmmss}-{losingSide}{extension}
///
/// The name is chosen once, by the client, and travels inside the sync plan. Both peers must
/// land the loser on the byte-identical path: if they disagree, the next scan sees two
/// unrelated files and copies each one to the other side, forever.
/// </summary>
public static class ConflictNamer
{
    public const string Infix = ".conflict-";
    public const string ClientSide = "client";
    public const string ServerSide = "server";

    /// <summary>Upper bound on collision retries, so a directory the process cannot write to
    /// fails loudly instead of spinning.</summary>
    public const int MaxOrdinal = 1000;

    private const string StampFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    /// <paramref name="ordinal"/> 1 produces the bare name; 2 and above append "-{ordinal}" so a
    /// second conflict on the same path within the same second does not overwrite the first.
    /// </summary>
    public static string Compose(string relativePath, DateTime sessionStartUtc, string losingSide, int ordinal = 1)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("relativePath must not be empty", nameof(relativePath));
        if (losingSide != ClientSide && losingSide != ServerSide)
            throw new ArgumentException($"losingSide must be '{ClientSide}' or '{ServerSide}'", nameof(losingSide));
        if (ordinal < 1)
            throw new ArgumentOutOfRangeException(nameof(ordinal), "ordinal starts at 1");

        // Wire paths are always '/'-separated. Split the directory off by hand rather than via
        // Path.GetDirectoryName, which would rewrite the separator on Windows and produce a name
        // the peer cannot match against its own manifest.
        var normalized = relativePath.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        var dir = slash >= 0 ? normalized[..(slash + 1)] : string.Empty;
        var fileName = slash >= 0 ? normalized[(slash + 1)..] : normalized;

        // GetExtension takes the LAST dot, so "archive.tar.gz" keeps ".tar" in the stem and only
        // ".gz" is re-appended — which is what the user sees in Explorer and what double-click
        // still opens.
        var extension = Path.GetExtension(fileName);
        var stem = extension.Length > 0 ? fileName[..^extension.Length] : fileName;

        var suffix = ordinal > 1 ? $"-{ordinal}" : string.Empty;
        return $"{dir}{stem}{Infix}{sessionStartUtc.ToString(StampFormat)}-{losingSide}{suffix}{extension}";
    }

    /// <summary>
    /// Compose, walked forward until nothing occupies the name inside <paramref name="syncFolder"/>.
    /// Only the client calls this: the chosen name goes into the plan and the server renames to
    /// exactly that name, so the two folders cannot diverge on a collision.
    /// </summary>
    public static string MakeUnique(string syncFolder, string relativePath, DateTime sessionStartUtc, string losingSide)
    {
        for (int ordinal = 1; ordinal <= MaxOrdinal; ordinal++)
        {
            var candidate = Compose(relativePath, sessionStartUtc, losingSide, ordinal);
            var full = Path.Combine(syncFolder, candidate.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full) && !Directory.Exists(full)) return candidate;
        }
        throw new IOException(
            $"Could not find a free conflict name for '{relativePath}' after {MaxOrdinal} attempts.");
    }

    /// <summary>
    /// Recovers the path a conflict copy was renamed from, and which side lost. Returns false for
    /// any name this class did not produce, so an ordinary file that happens to contain
    /// ".conflict-" is never mistaken for a rename instruction from the peer.
    /// </summary>
    public static bool TryParse(string conflictRelativePath, out string originalPath, out string losingSide)
    {
        originalPath = string.Empty;
        losingSide = string.Empty;
        if (string.IsNullOrWhiteSpace(conflictRelativePath)) return false;

        var normalized = conflictRelativePath.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        var dir = slash >= 0 ? normalized[..(slash + 1)] : string.Empty;
        var fileName = slash >= 0 ? normalized[(slash + 1)..] : normalized;

        // LastIndexOf, not IndexOf: a file already named "a.conflict-...-client.txt" that
        // conflicts a second time must parse back to that name, not to "a.txt" — otherwise the
        // rename would clobber the first conflict copy.
        int idx = fileName.LastIndexOf(Infix, StringComparison.Ordinal);
        if (idx < 0) return false;

        var stem = fileName[..idx];
        var rest = fileName[(idx + Infix.Length)..];
        var extension = Path.GetExtension(rest);
        var token = extension.Length > 0 ? rest[..^extension.Length] : rest;

        var parts = token.Split('-');
        if (parts.Length is not (3 or 4)) return false;
        if (parts[0].Length != 8 || !parts[0].All(char.IsAsciiDigit)) return false;
        if (parts[1].Length != 6 || !parts[1].All(char.IsAsciiDigit)) return false;
        if (parts[2] != ClientSide && parts[2] != ServerSide) return false;
        if (parts.Length == 4 && (parts[3].Length == 0 || !parts[3].All(char.IsAsciiDigit))) return false;

        losingSide = parts[2];
        originalPath = dir + stem + extension;
        return originalPath.Length > 0;
    }
}
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ConflictNamerTests"`
Expected: PASS

---

### Task 5.2: MakeUnique — collision handling

- [ ] **Step 1: Write the failing test**

Append to `tests/RemoteFileSync.Tests/Sync/ConflictNamerTests.cs`, and make the class implement `IDisposable` (replace the class declaration line `public class ConflictNamerTests` with `public class ConflictNamerTests : IDisposable` and add the field, constructor and Dispose below):

```csharp
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"rfs_cname_{Guid.NewGuid()}");

    public ConflictNamerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void MakeUnique_ReturnsBareNameWhenNothingOccupiesIt()
    {
        Assert.Equal("report.conflict-20260720-143052-client.txt",
            ConflictNamer.MakeUnique(_dir, "report.txt", Stamp, ConflictNamer.ClientSide));
    }

    [Fact]
    public void MakeUnique_WalksOrdinalPastExistingFiles()
    {
        File.WriteAllText(Path.Combine(_dir, "report.conflict-20260720-143052-client.txt"), "first");
        Assert.Equal("report.conflict-20260720-143052-client-2.txt",
            ConflictNamer.MakeUnique(_dir, "report.txt", Stamp, ConflictNamer.ClientSide));

        File.WriteAllText(Path.Combine(_dir, "report.conflict-20260720-143052-client-2.txt"), "second");
        Assert.Equal("report.conflict-20260720-143052-client-3.txt",
            ConflictNamer.MakeUnique(_dir, "report.txt", Stamp, ConflictNamer.ClientSide));
    }

    [Fact]
    public void MakeUnique_PreservesSubdirectoryAndCreatesNoFile()
    {
        var name = ConflictNamer.MakeUnique(_dir, "docs/report.txt", Stamp, ConflictNamer.ServerSide);
        Assert.Equal("docs/report.conflict-20260720-143052-server.txt", name);
        Assert.False(File.Exists(Path.Combine(_dir, "docs", "report.conflict-20260720-143052-server.txt")));
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~MakeUnique_WalksOrdinalPastExistingFiles"`
Expected: FAIL — `Assert.Equal() Failure: Expected: report.conflict-20260720-143052-client-2.txt, Actual: report.conflict-20260720-143052-client.txt` (before Task 5.1's `MakeUnique` exists this is instead `CS0117`; run 5.1 first)

- [ ] **Step 3: Implement**

Already implemented by `ConflictNamer.MakeUnique` in Task 5.1 Step 3. No further code.

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ConflictNamerTests"`
Expected: PASS

---

### Task 5.3: TryParse — round-trip and rejection

- [ ] **Step 1: Write the failing test**

Append to `tests/RemoteFileSync.Tests/Sync/ConflictNamerTests.cs`:

```csharp
    [Theory]
    [InlineData("report.docx", "server")]
    [InlineData("README", "client")]
    [InlineData("archive.tar.gz", "server")]
    [InlineData("docs/q3/report.docx", "client")]
    [InlineData(".gitignore", "server")]
    public void TryParse_RoundTripsCompose(string relativePath, string losingSide)
    {
        var name = ConflictNamer.Compose(relativePath, Stamp, losingSide);
        Assert.True(ConflictNamer.TryParse(name, out var original, out var side));
        Assert.Equal(relativePath, original);
        Assert.Equal(losingSide, side);
    }

    [Fact]
    public void TryParse_RoundTripsOrdinalNames()
    {
        var name = ConflictNamer.Compose("report.docx", Stamp, ConflictNamer.ServerSide, ordinal: 7);
        Assert.True(ConflictNamer.TryParse(name, out var original, out var side));
        Assert.Equal("report.docx", original);
        Assert.Equal(ConflictNamer.ServerSide, side);
    }

    [Fact]
    public void TryParse_NestedConflictUnwrapsOnlyTheOuterLayer()
    {
        // A conflict copy that conflicts again must resolve to the conflict copy, not to the
        // original — unwrapping both layers would rename over the first conflict copy.
        var inner = ConflictNamer.Compose("report.docx", Stamp, ConflictNamer.ClientSide);
        var outer = ConflictNamer.Compose(inner, Stamp, ConflictNamer.ServerSide);
        Assert.True(ConflictNamer.TryParse(outer, out var original, out var side));
        Assert.Equal(inner, original);
        Assert.Equal(ConflictNamer.ServerSide, side);
    }

    [Theory]
    [InlineData("report.docx")]                                   // no infix at all
    [InlineData("my.conflict-notes.txt")]                         // infix present, no stamp
    [InlineData("report.conflict-20260720-143052-peer.docx")]     // unknown side
    [InlineData("report.conflict-2026072-143052-server.docx")]    // 7-digit date
    [InlineData("report.conflict-20260720-14305-server.docx")]    // 5-digit time
    [InlineData("report.conflict-20260720-143052-server-x.docx")] // non-numeric ordinal
    [InlineData("")]
    public void TryParse_RejectsNamesItDidNotProduce(string candidate)
    {
        Assert.False(ConflictNamer.TryParse(candidate, out _, out _));
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~TryParse"`
Expected: FAIL — `error CS0117: 'ConflictNamer' does not contain a definition for 'TryParse'` (if Task 5.1 was landed first this task's tests pass immediately; run them anyway to confirm the parse rules)

- [ ] **Step 3: Implement**

Already implemented by `ConflictNamer.TryParse` in Task 5.1 Step 3. No further code.

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ConflictNamerTests"`
Expected: PASS

---

### Task 5.4: ConflictKeepBothExecutor — plan expansion and the local rename pass

- [ ] **Step 1: Write the failing test**

Create `tests/RemoteFileSync.Tests/Sync/ConflictKeepBothExecutorTests.cs`:

```csharp
using RemoteFileSync.Backup;
using RemoteFileSync.Models;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class ConflictKeepBothExecutorTests : IDisposable
{
    private static readonly DateTime Stamp = new(2026, 7, 20, 14, 30, 52, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rfs_ckb_{Guid.NewGuid()}");
    private readonly string _sync;
    private readonly string _archiveRoot;

    public ConflictKeepBothExecutorTests()
    {
        _sync = Path.Combine(_root, "sync");
        _archiveRoot = Path.Combine(_root, "archive");
        Directory.CreateDirectory(_sync);
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

        var expanded = ConflictKeepBothExecutor.Expand(
            plan, client, server, ClockSkew.None, Stamp, _sync);

        var expectedName = "report.conflict-20260720-143052-client.txt";
        Assert.Equal(3, expanded.Count);
        Assert.Equal(SyncActionType.ConflictKeepBoth, expanded[0].Action);
        Assert.Equal(expectedName, expanded[0].RelativePath);
        Assert.Equal(SyncActionType.SendToServer, expanded[1].Action);
        Assert.Equal(expectedName, expanded[1].RelativePath);
        Assert.Equal(SyncActionType.SendToClient, expanded[2].Action);
        Assert.Equal("report.txt", expanded[2].RelativePath);
    }

    [Fact]
    public void Expand_ClientNewer_RenamesServerCopyAndMovesOneFileEachWay()
    {
        var client = Manifest("report.txt", 20, Stamp.AddHours(1));
        var server = Manifest("report.txt", 10, Stamp);
        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, "report.txt") };

        var expanded = ConflictKeepBothExecutor.Expand(
            plan, client, server, ClockSkew.None, Stamp, _sync);

        var expectedName = "report.conflict-20260720-143052-server.txt";
        Assert.Equal(3, expanded.Count);
        Assert.Equal(SyncActionType.ConflictKeepBoth, expanded[0].Action);
        Assert.Equal(expectedName, expanded[0].RelativePath);
        Assert.Equal(SyncActionType.SendToServer, expanded[1].Action);
        Assert.Equal("report.txt", expanded[1].RelativePath);
        Assert.Equal(SyncActionType.SendToClient, expanded[2].Action);
        Assert.Equal(expectedName, expanded[2].RelativePath);
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

        var expanded = ConflictKeepBothExecutor.Expand(
            plan, client, server, ClockSkew.None, Stamp, _sync);

        Assert.Equal(2, expanded.Count(e => e.Action == SyncActionType.SendToServer));
        Assert.Equal(2, expanded.Count(e => e.Action == SyncActionType.SendToClient));
        Assert.Equal(2, expanded.Count(e => e.Action == SyncActionType.ConflictKeepBoth));
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

        var expanded = ConflictKeepBothExecutor.Expand(
            plan, new FileManifest(), new FileManifest(), ClockSkew.None, Stamp, _sync);

        Assert.Equal(3, expanded.Count);
        Assert.Equal(SyncActionType.SendToServer, expanded[0].Action);
        Assert.Equal(SyncActionType.Skip, expanded[1].Action);
        Assert.Equal(SyncActionType.DeleteOnClient, expanded[2].Action);
    }

    [Fact]
    public void ApplyLocalRenames_LosingSideRenamesArchivesAndPreservesMtime()
    {
        var mtime = Stamp.AddHours(-3);
        Write("report.txt", "client edit", mtime);
        var name = "report.conflict-20260720-143052-client.txt";
        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, name) };
        var archive = new ArchiveManager(_sync, _archiveRoot, Stamp);

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ClientSide, _sync, archive);

        Assert.Equal(1, outcome.Renamed);
        Assert.Empty(outcome.Failures);
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
        var archive = new ArchiveManager(_sync, _archiveRoot, Stamp);

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ServerSide, _sync, archive);

        Assert.Equal(0, outcome.Renamed);
        Assert.Empty(outcome.Failures);
        Assert.Equal("server edit", File.ReadAllText(Path.Combine(_sync, "report.txt")));
    }

    [Fact]
    public void ApplyLocalRenames_MissingOriginalIsAFailureNotASilentSkip()
    {
        // The plan already promises the peer a transfer under this name; a sender that cannot
        // open its source never writes FileStart and the peer blocks forever. Fail loudly.
        var plan = new List<SyncPlanEntry>
        {
            new(SyncActionType.ConflictKeepBoth, "gone.conflict-20260720-143052-client.txt"),
        };
        var archive = new ArchiveManager(_sync, _archiveRoot, Stamp);

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ClientSide, _sync, archive);

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
        var archive = new ArchiveManager(_sync, _archiveRoot, Stamp);

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ClientSide, _sync, archive);

        Assert.Equal(0, outcome.Renamed);
        Assert.Single(outcome.Failures);
    }

    [Fact]
    public void ApplyLocalRenames_MalformedEntryIsAFailure()
    {
        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, "not-a-conflict.txt") };
        var archive = new ArchiveManager(_sync, _archiveRoot, Stamp);

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ClientSide, _sync, archive);

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
        var archive = new ArchiveManager(_sync, _archiveRoot, Stamp);

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ClientSide, _sync, archive);

        Assert.Equal(1, outcome.Renamed);
        Assert.Empty(outcome.Failures);
        Assert.Equal("loser", File.ReadAllText(Path.Combine(_sync, name)));
        Assert.True(File.Exists(Path.Combine(archive.SessionRoot, "conflict", name)));
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ConflictKeepBothExecutorTests"`
Expected: FAIL — `error CS0246: The type or namespace name 'ConflictKeepBothExecutor' could not be found (are you missing a using directive or an assembly reference?)`

- [ ] **Step 3: Implement**

Create `src/RemoteFileSync/Sync/ConflictKeepBothExecutor.cs`:

```csharp
using RemoteFileSync.Backup;
using RemoteFileSync.Models;
using RemoteFileSync.Security;

namespace RemoteFileSync.Sync;

/// <summary>Result of one peer's conflict rename pass. A non-empty <see cref="Failures"/> list is
/// fatal, not skippable — see <see cref="ConflictKeepBothExecutor"/>.</summary>
public readonly record struct ConflictRenameOutcome(int Renamed, IReadOnlyList<string> Failures);

/// <summary>
/// Executes SyncActionType.ConflictKeepBoth.
///
/// The plan is serialised once and BOTH peers iterate the identical list, so a conflict must not
/// require a peer-specific decision during any message-bearing phase. The client therefore
/// rewrites every ConflictKeepBoth entry into three entries before the plan goes on the wire:
///
///   1. ConflictKeepBoth(conflictName) — a purely LOCAL rename, performed only by the peer named
///      in the conflict name. It exchanges no frames.
///   2. SendToServer(...) and 3. SendToClient(...) — the loser copy in one direction and the
///      winner in the other, in whichever order the two sides happen to own them.
///
/// Because step 1 moves no bytes, a conflict adds exactly one file to each existing transfer
/// phase and cannot shift either peer's frame sequence.
///
/// Pure client-side expansion (never putting action 7 on the wire) was rejected: it can only
/// express the case where the CLIENT loses. When the server loses, the conflict-named file must
/// exist on the server's disk before FileTransferSender opens it, and the server's receive phase
/// overwrites the original before its send phase runs — so no reordering of existing actions can
/// produce it.
/// </summary>
public static class ConflictKeepBothExecutor
{
    /// <summary>
    /// Rewrites ConflictKeepBoth entries into the three-entry form. Runs on the client only,
    /// before the plan is serialised.
    /// </summary>
    public static List<SyncPlanEntry> Expand(
        IReadOnlyList<SyncPlanEntry> plan,
        FileManifest clientManifest,
        FileManifest serverManifest,
        ClockSkew skew,
        DateTime sessionStartUtc,
        string clientFolder)
    {
        var expanded = new List<SyncPlanEntry>(plan.Count);
        foreach (var entry in plan)
        {
            if (entry.Action != SyncActionType.ConflictKeepBoth)
            {
                expanded.Add(entry);
                continue;
            }

            var client = clientManifest.Get(entry.RelativePath);
            var server = serverManifest.Get(entry.RelativePath);
            if (client is null || server is null)
            {
                // ComputePlan only emits ConflictKeepBoth when both sides hold the file. If the
                // manifests disagree the state is not what the planner believed, and the safe
                // action is to touch nothing rather than rename on a guess.
                expanded.Add(new SyncPlanEntry(SyncActionType.Skip, entry.RelativePath));
                continue;
            }

            // Compare in client time: an unsynchronised server clock would otherwise decide the
            // winner by how wrong it is rather than by which copy is actually newer.
            // Tie goes to the server, so the choice is deterministic on repeat runs.
            var serverTime = skew.NormaliseServerTime(server.LastModifiedUtc);
            bool clientWins = serverTime < client.LastModifiedUtc;

            var losingSide = clientWins ? ConflictNamer.ServerSide : ConflictNamer.ClientSide;
            var conflictName = ConflictNamer.MakeUnique(
                clientFolder, entry.RelativePath, sessionStartUtc, losingSide);

            expanded.Add(new SyncPlanEntry(SyncActionType.ConflictKeepBoth, conflictName));
            if (clientWins)
            {
                expanded.Add(new SyncPlanEntry(SyncActionType.SendToServer, entry.RelativePath));
                expanded.Add(new SyncPlanEntry(SyncActionType.SendToClient, conflictName));
            }
            else
            {
                expanded.Add(new SyncPlanEntry(SyncActionType.SendToServer, conflictName));
                expanded.Add(new SyncPlanEntry(SyncActionType.SendToClient, entry.RelativePath));
            }
        }
        return expanded;
    }

    /// <summary>
    /// Runs on both peers before their transfer phases. <paramref name="side"/> is this peer's
    /// identity; only the peer the conflict name blames touches its disk, so the two sides never
    /// both rename and the per-direction file counts stay symmetric.
    ///
    /// Every entry this peer owns but cannot complete lands in Failures. Callers MUST abort the
    /// session on a non-empty list: the plan already promises the peer a transfer under the
    /// conflict name, and FileTransferSender throws while sizing a missing source — before it
    /// writes FileStart — leaving the peer blocked on a frame that never arrives.
    /// </summary>
    public static ConflictRenameOutcome ApplyLocalRenames(
        IReadOnlyList<SyncPlanEntry> plan, string side, string syncFolder, ArchiveManager archive)
    {
        int renamed = 0;
        var failures = new List<string>();

        foreach (var entry in plan)
        {
            if (entry.Action != SyncActionType.ConflictKeepBoth) continue;

            if (!ConflictNamer.TryParse(entry.RelativePath, out var originalPath, out var losingSide))
            {
                // Cannot tell whose entry this is, so both peers fail it and both abort — which
                // is still symmetric, and better than one side silently ignoring it.
                failures.Add($"{entry.RelativePath}: malformed conflict name");
                continue;
            }
            if (losingSide != side) continue;

            // The plan arrives from a peer we do not authenticate. Both names must be proven
            // inside the root before either reaches the filesystem.
            if (!PathGuard.TryResolveWithinRoot(syncFolder, originalPath, out var originalFull)
                || !PathGuard.TryResolveWithinRoot(syncFolder, entry.RelativePath, out var conflictFull))
            {
                failures.Add($"{entry.RelativePath}: resolves outside the sync root");
                continue;
            }

            if (!File.Exists(originalFull))
            {
                failures.Add($"{originalPath}: no longer exists");
                continue;
            }

            try
            {
                // The name is authoritative: it came from the plan and the peer will use exactly
                // it. A local file squatting on the name is archived, never overwritten.
                if (File.Exists(conflictFull))
                {
                    archive.Archive(entry.RelativePath, ArchiveReason.Conflict, removeOriginal: true);
                    if (File.Exists(conflictFull)) File.Delete(conflictFull);
                }

                archive.Archive(originalPath, ArchiveReason.Conflict, removeOriginal: false);

                // Move, not copy-then-delete: the mtime must survive so the copy the peer
                // receives compares equal on the next scan instead of transferring forever.
                File.Move(originalFull, conflictFull);
                renamed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{originalPath}: {ex.Message}");
            }
        }

        return new ConflictRenameOutcome(renamed, failures);
    }
}
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ConflictKeepBothExecutorTests"`
Expected: PASS

---

### Task 5.5: Wire the client — expand the plan, then rename before any frame moves

- [ ] **Step 1: Write the failing test**

Create `tests/RemoteFileSync.Tests/Integration/ConflictKeepBothSyncTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.Integration;

public class ConflictKeepBothSyncTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _serverDir;
    private readonly string _clientDir;
    private readonly string _dbDir;

    public ConflictKeepBothSyncTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"rfs_conflict_e2e_{Guid.NewGuid()}");
        _serverDir = Path.Combine(_testRoot, "server");
        _clientDir = Path.Combine(_testRoot, "client");
        _dbDir = Path.Combine(_testRoot, "db");
        Directory.CreateDirectory(_serverDir);
        Directory.CreateDirectory(_clientDir);
        Directory.CreateDirectory(_dbDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
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

    private async Task<(int clientResult, int serverResult)> RunTwoWaySyncAsync(SyncDatabase db)
    {
        int port = GetFreePort();
        var serverOpts = new SyncOptions
        {
            IsServer = true, Once = true, Port = port, Folder = _serverDir,
            Mode = SyncMode.TwoWay, DeleteEnabled = true,
        };
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir,
            Mode = SyncMode.TwoWay, DeleteEnabled = true,
        };

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
    public async Task TwoWayConflict_BothContentsSurviveOnBothSides()
    {
        var baseTs = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var dbPath = Path.Combine(_dbDir, "conflict.db");

        // Run 1: establish an ancestor row so run 2 can see that BOTH sides changed.
        CreateFileWithTimestamp(_clientDir, "report.txt", "original", baseTs);
        CreateFileWithTimestamp(_serverDir, "report.txt", "original", baseTs);
        using (var db = new SyncDatabase(dbPath))
            await RunTwoWaySyncAsync(db);

        // Both sides edit the same path. The server copy is newer, so the client copy loses.
        CreateFileWithTimestamp(_clientDir, "report.txt", "client edit", baseTs.AddHours(1));
        CreateFileWithTimestamp(_serverDir, "report.txt", "server edit", baseTs.AddHours(2));

        using (var db = new SyncDatabase(dbPath))
        {
            var (clientResult, serverResult) = await RunTwoWaySyncAsync(db);
            Assert.Equal(0, clientResult);
            Assert.Equal(0, serverResult);
        }

        // The winner keeps the canonical name on both peers.
        Assert.Equal("server edit", File.ReadAllText(Path.Combine(_clientDir, "report.txt")));
        Assert.Equal("server edit", File.ReadAllText(Path.Combine(_serverDir, "report.txt")));

        // The loser survives under the conflict name on both peers, under the SAME name.
        var clientLosers = Directory.GetFiles(_clientDir, "report.conflict-*-client.txt");
        var serverLosers = Directory.GetFiles(_serverDir, "report.conflict-*-client.txt");
        Assert.Single(clientLosers);
        Assert.Single(serverLosers);
        Assert.Equal(Path.GetFileName(clientLosers[0]), Path.GetFileName(serverLosers[0]));
        Assert.Equal("client edit", File.ReadAllText(clientLosers[0]));
        Assert.Equal("client edit", File.ReadAllText(serverLosers[0]));
    }

    [Fact]
    public async Task TwoWayConflict_ServerCopyLosesWhenClientCopyIsNewer()
    {
        var baseTs = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var dbPath = Path.Combine(_dbDir, "conflict-serverloses.db");

        CreateFileWithTimestamp(_clientDir, "notes.md", "original", baseTs);
        CreateFileWithTimestamp(_serverDir, "notes.md", "original", baseTs);
        using (var db = new SyncDatabase(dbPath))
            await RunTwoWaySyncAsync(db);

        // Client copy is newer this time, so the SERVER copy is the loser — the case that pure
        // client-side expansion cannot express.
        CreateFileWithTimestamp(_clientDir, "notes.md", "client edit", baseTs.AddHours(2));
        CreateFileWithTimestamp(_serverDir, "notes.md", "server edit", baseTs.AddHours(1));

        using (var db = new SyncDatabase(dbPath))
        {
            var (clientResult, serverResult) = await RunTwoWaySyncAsync(db);
            Assert.Equal(0, clientResult);
            Assert.Equal(0, serverResult);
        }

        Assert.Equal("client edit", File.ReadAllText(Path.Combine(_clientDir, "notes.md")));
        Assert.Equal("client edit", File.ReadAllText(Path.Combine(_serverDir, "notes.md")));

        var clientLosers = Directory.GetFiles(_clientDir, "notes.conflict-*-server.md");
        var serverLosers = Directory.GetFiles(_serverDir, "notes.conflict-*-server.md");
        Assert.Single(clientLosers);
        Assert.Single(serverLosers);
        Assert.Equal(Path.GetFileName(clientLosers[0]), Path.GetFileName(serverLosers[0]));
        Assert.Equal("server edit", File.ReadAllText(clientLosers[0]));
        Assert.Equal("server edit", File.ReadAllText(serverLosers[0]));
    }

    [Fact]
    public async Task ConflictCopy_IsNotResyncedByTheNextScan()
    {
        var baseTs = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var dbPath = Path.Combine(_dbDir, "conflict-stable.db");

        CreateFileWithTimestamp(_clientDir, "report.txt", "original", baseTs);
        CreateFileWithTimestamp(_serverDir, "report.txt", "original", baseTs);
        using (var db = new SyncDatabase(dbPath))
            await RunTwoWaySyncAsync(db);

        CreateFileWithTimestamp(_clientDir, "report.txt", "client edit", baseTs.AddHours(1));
        CreateFileWithTimestamp(_serverDir, "report.txt", "server edit", baseTs.AddHours(2));
        using (var db = new SyncDatabase(dbPath))
            await RunTwoWaySyncAsync(db);

        var loser = Path.GetFileName(Directory.GetFiles(_clientDir, "report.conflict-*-client.txt").Single());

        // Run 3 must be a no-op: the conflict copy is byte- and mtime-identical on both peers,
        // so it converges instead of ping-ponging as a "new" file forever.
        using (var db = new SyncDatabase(dbPath))
        {
            var (clientResult, serverResult) = await RunTwoWaySyncAsync(db);
            Assert.Equal(0, clientResult);
            Assert.Equal(0, serverResult);
        }

        Assert.Single(Directory.GetFiles(_clientDir, "report.conflict-*"));
        Assert.Single(Directory.GetFiles(_serverDir, "report.conflict-*"));
        Assert.Equal("client edit", File.ReadAllText(Path.Combine(_clientDir, loser)));
        Assert.Equal("client edit", File.ReadAllText(Path.Combine(_serverDir, loser)));
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ConflictKeepBothSyncTests"`
Expected: FAIL — the plan reaches the wire carrying a raw `ConflictKeepBoth` entry that neither peer acts on, so the loser is silently overwritten by the winner: `Assert.Single() Failure: The collection was empty` on `Directory.GetFiles(_clientDir, "report.conflict-*-client.txt")`.

- [ ] **Step 3: Implement**

**Edit 1 — `src/RemoteFileSync/Network/SyncClient.cs:169-183`.** Exact current code:

```csharp
        var transferCount = syncPlan.Count(p => p.Action != SyncActionType.Skip
            && p.Action != SyncActionType.DeleteOnServer && p.Action != SyncActionType.DeleteOnClient);
        var deleteCount = syncPlan.Count(p => p.Action == SyncActionType.DeleteOnServer || p.Action == SyncActionType.DeleteOnClient);
        var skipCount = syncPlan.Count(p => p.Action == SyncActionType.Skip);
        var deleteSummary = deleteCount > 0 ? $", {deleteCount} delete" : "";
        _logger.Info($"Sync plan: {transferCount} transfers{deleteSummary}, {skipCount} skipped");

        // Total bytes the client will push, so the GUI can show real progress rather than
        // guessing from file counts.
        long plannedBytes = syncPlan
            .Where(p => p.Action is SyncActionType.SendToServer or SyncActionType.ClientOnly)
            .Sum(p => clientManifest.Get(p.RelativePath)?.FileSize ?? 0);
        _progress.WritePlan(transferCount, deleteCount, skipCount, plannedBytes);
        var planBytes = ProtocolHandler.SerializeSyncPlan(syncPlan);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncPlan, planBytes, ct);
```

Exact replacement:

```csharp
        // Every ConflictKeepBoth becomes a local rename plus one transfer in each direction, and
        // this MUST happen before the plan is serialised: both peers execute the list they are
        // handed, so a conflict the server has to interpret for itself is a desync waiting to
        // happen. One stamp for the whole session so a folder full of conflicts reads as one
        // event and the names are stable if the expansion is repeated.
        var conflictStamp = DateTime.UtcNow;
        syncPlan = ConflictKeepBothExecutor.Expand(
            syncPlan, clientManifest, serverManifest, skew, conflictStamp, _options.Folder);

        // ConflictKeepBoth entries move no bytes, so they are not transfers: counting them would
        // make the GUI's progress bar overshoot and never reach 100%.
        var transferCount = syncPlan.Count(p => p.Action != SyncActionType.Skip
            && p.Action != SyncActionType.DeleteOnServer && p.Action != SyncActionType.DeleteOnClient
            && p.Action != SyncActionType.ConflictKeepBoth);
        var deleteCount = syncPlan.Count(p => p.Action == SyncActionType.DeleteOnServer || p.Action == SyncActionType.DeleteOnClient);
        var skipCount = syncPlan.Count(p => p.Action == SyncActionType.Skip);
        var deleteSummary = deleteCount > 0 ? $", {deleteCount} delete" : "";
        _logger.Info($"Sync plan: {transferCount} transfers{deleteSummary}, {skipCount} skipped");

        // Total bytes the client will push, so the GUI can show real progress rather than
        // guessing from file counts. A conflict copy is not in the manifest yet — it is named
        // after a file that is — so fall back to the original's size rather than counting zero.
        long plannedBytes = syncPlan
            .Where(p => p.Action is SyncActionType.SendToServer or SyncActionType.ClientOnly)
            .Sum(p => clientManifest.Get(p.RelativePath)?.FileSize
                   ?? (ConflictNamer.TryParse(p.RelativePath, out var origin, out _)
                        ? clientManifest.Get(origin)?.FileSize ?? 0
                        : 0));
        _progress.WritePlan(transferCount, deleteCount, skipCount, plannedBytes);
        var planBytes = ProtocolHandler.SerializeSyncPlan(syncPlan);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncPlan, planBytes, ct);
```

**Edit 2 — `src/RemoteFileSync/Network/SyncClient.cs:257-259`.** Exact current code (the close of the delete-threshold gate, then the transfer phase header):

```csharp
            }
        }

        // 7. Send files to server (SendToServer + ClientOnly)
```

Exact replacement:

```csharp
            }
        }

        // 7a. Conflict renames. Frame-free, and BEFORE any transfer: this is the only step where
        // the two peers do different work, so it must finish on both sides before a single file
        // frame moves or their transfer sets stop lining up.
        var conflictEntries = syncPlan.Where(p => p.Action == SyncActionType.ConflictKeepBoth).ToList();
        if (conflictEntries.Count > 0)
        {
            // Local instance rather than the shared BackupManager above: this phase must archive
            // under ArchiveReason.Conflict. Phase 6 collapses the two into one ArchiveManager.
            var archive = new ArchiveManager(_options.Folder, _options.EffectiveArchiveFolder, conflictStamp);
            var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
                syncPlan, ConflictNamer.ClientSide, _options.Folder, archive);

            // Fatal, not skippable: the plan already promised the peer a transfer under the
            // conflict name, and a sender that cannot size its source throws before it writes
            // FileStart — leaving the peer blocked on a frame that never arrives.
            if (outcome.Failures.Count > 0)
            {
                var msg = $"Refusing to sync: conflict rename failed for {outcome.Failures.Count} " +
                          $"path(s): {string.Join("; ", outcome.Failures)}";
                _logger.Error(msg);
                _progress.WriteError(msg, fatal: true);
                return 4;
            }

            foreach (var entry in conflictEntries)
            {
                if (!ConflictNamer.TryParse(entry.RelativePath, out var original, out var losingSide)) continue;
                _logger.Info($"[!] Conflict on {original}: {losingSide} copy kept as {entry.RelativePath}");
                _db?.LogConflict(original, sessionId,
                    $"both sides changed; {losingSide} copy renamed to {entry.RelativePath}");
            }
        }

        // 7. Send files to server (SendToServer + ClientOnly)
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ConflictKeepBothSyncTests"`
Expected: FAIL still — the client renames and sends, but the server never renames its own losing copy, so `TwoWayConflict_ServerCopyLosesWhenClientCopyIsNewer` fails with `Assert.Single() Failure: The collection was empty` on `Directory.GetFiles(_serverDir, "notes.conflict-*-server.md")`. Task 5.6 closes this.

---

### Task 5.6: Wire the server — mirror the rename pass

- [ ] **Step 1: Write the failing test**

Covered by `TwoWayConflict_ServerCopyLosesWhenClientCopyIsNewer` from Task 5.5, which is red at the end of Task 5.5 Step 4. No new test.

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~TwoWayConflict_ServerCopyLosesWhenClientCopyIsNewer"`
Expected: FAIL — `Assert.Single() Failure: The collection was empty` (the server's losing copy was overwritten by the incoming winner and never renamed)

- [ ] **Step 3: Implement**

**Edit 3 — `src/RemoteFileSync/Network/SyncServer.cs:178-180`.** Exact current code:

```csharp
        int filesDeleted = 0;

        // 6. Receive files from client (SendToServer + ClientOnly)
```

Exact replacement:

```csharp
        int filesDeleted = 0;

        // 5a. Conflict renames. Mirror of the client's step 7a: frame-free, and completed before
        // the first file frame so both peers' transfer sets stay aligned.
        var conflictEntries = syncPlan.Where(p => p.Action == SyncActionType.ConflictKeepBoth).ToList();
        if (conflictEntries.Count > 0)
        {
            // The server only ever sends in the bidirectional branch below, so a conflict from a
            // unidirectional peer would strand the renamed loser here with no way back to it.
            if (!bidirectional)
            {
                var msg = $"Rejecting sync plan: {conflictEntries.Count} conflict action(s) from a " +
                          "unidirectional peer, which has no phase to receive the renamed copy.";
                _logger.Error(msg);
                _progress.WriteError(msg, fatal: true);
                return 4;
            }

            // Local instance rather than the shared BackupManager above: this phase must archive
            // under ArchiveReason.Conflict. Phase 6 collapses the two into one ArchiveManager.
            var archive = new ArchiveManager(_options.Folder, _options.EffectiveArchiveFolder, DateTime.UtcNow);
            var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
                syncPlan, ConflictNamer.ServerSide, _options.Folder, archive);

            // See SyncClient step 7a: the plan already promised a transfer under the conflict
            // name, so a half-applied rename hangs the peer rather than merely skipping a file.
            if (outcome.Failures.Count > 0)
            {
                var msg = $"Refusing to sync: conflict rename failed for {outcome.Failures.Count} " +
                          $"path(s): {string.Join("; ", outcome.Failures)}";
                _logger.Error(msg);
                _progress.WriteError(msg, fatal: true);
                return 4;
            }

            if (outcome.Renamed > 0)
                _logger.Info($"Conflict: {outcome.Renamed} losing copy/copies renamed and kept.");
        }

        // 6. Receive files from client (SendToServer + ClientOnly)
```

`SyncServer.cs` already has `using RemoteFileSync.Backup;` (line 4) and `using RemoteFileSync.Sync;` (line 10), so no using changes are needed. Same for `SyncClient.cs` (lines 3 and 9).

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ConflictKeepBothSyncTests"`
Expected: PASS (all three facts)

---

### Phase 5 commit

```bash
git add src/RemoteFileSync/Sync/ConflictNamer.cs \
        src/RemoteFileSync/Sync/ConflictKeepBothExecutor.cs \
        src/RemoteFileSync/Network/SyncClient.cs \
        src/RemoteFileSync/Network/SyncServer.cs \
        tests/RemoteFileSync.Tests/Sync/ConflictNamerTests.cs \
        tests/RemoteFileSync.Tests/Sync/ConflictKeepBothExecutorTests.cs \
        tests/RemoteFileSync.Tests/Integration/ConflictKeepBothSyncTests.cs
git commit -m "feat: execute ConflictKeepBoth by renaming and keeping the losing copy

Expand each ConflictKeepBoth entry into a frame-free local rename plus one
transfer in each direction, so both peers execute the same plan without a
peer-specific decision during any message-bearing phase. Loser is archived
under ArchiveReason.Conflict and renamed to the contract format before the
first file frame moves; a failed rename aborts with exit 4 rather than
leaving the peer blocked on a FileStart that never arrives.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
git push -u origin feat/deletion-sync-ancestor-merge
```

**Verification before commit:**
```bash
dotnet build -c Release
dotnet test -c Release
```
Expected: 0 errors. No existing tests change. `SyncClient.cs:169-183` and `SyncServer.cs:178-180` are modified in place, but `SyncEngineTests`, `DeleteSyncTests`, `DatabaseDeleteSyncTests`, `DeleteThresholdTests` and `EndToEndTests` exercise plans containing no `ConflictKeepBoth` entries, for which `Expand` is an identity transform, the new `conflictEntries` lists are empty, and both new blocks are skipped entirely. `BackupManagerTests` is untouched — this phase adds an `ArchiveManager` alongside `BackupManager` and does not replace it; that swap belongs to Phase 6.

---

## Phase 6: ArchiveManager — per-session folders, reason partitioning, and retention

**Goal:** Replace `BackupManager` with an `ArchiveManager` whose session timestamp is captured once per run by the caller, partitions archived copies by reason, and prunes whole session folders by age and size cap.

**Files:**
- Create: `src/RemoteFileSync/Backup/ArchiveManager.cs`
- Delete: `src/RemoteFileSync/Backup/BackupManager.cs`
- Delete: `tests/RemoteFileSync.Tests/Backup/BackupManagerTests.cs`
- Modify: `src/RemoteFileSync/Network/SyncClient.cs:209`, `src/RemoteFileSync/Network/SyncClient.cs:370-371`, `src/RemoteFileSync/Network/SyncClient.cs:425`
- Modify: `src/RemoteFileSync/Network/SyncServer.cs:173`, `src/RemoteFileSync/Network/SyncServer.cs:192-193`, `src/RemoteFileSync/Network/SyncServer.cs:260`
- Test: `tests/RemoteFileSync.Tests/Backup/ArchiveManagerTests.cs`

**Interfaces:**
- Consumes (from the earlier `SyncOptions` phase): `public string EffectiveArchiveFolder { get; }`, `public int ArchiveKeepDays { get; set; }`, `public long ArchiveMaxBytes { get; set; }`
- Consumes (existing): `PathGuard.TryResolveWithinRoot(string root, string relativePath, out string fullPath)`
- Produces:
  - `public enum ArchiveReason { Deleted, Overwritten, Conflict }`
  - `public ArchiveManager(string syncFolder, string archiveRoot, DateTime sessionStartUtc)`
  - `public string SessionFolderName { get; }`
  - `public string SessionRoot { get; }`
  - `public bool Archive(string relativePath, ArchiveReason reason, bool removeOriginal)`
  - `public static PruneResult Prune(string archiveRoot, TimeSpan keepAge, long maxBytes)`
  - `public readonly record struct PruneResult(int SessionsRemoved, long BytesFreed)`

**Decision: delete `BackupManager`, do not keep a delegating shim.**

A shim cannot delegate honestly. `BackupManager`'s constructor takes no timestamp, so the shim would have to synthesise a `sessionStartUtc` — either at construction (which is the fix, but then the type name lies about its `yyyyMMdd` layout and its five existing tests break anyway) or per call (which reintroduces the exact midnight-split bug this phase exists to remove). Worse, a surviving `BackupManager` keeps writing `yyyyMMdd` folders into the same archive root, and `Prune` deliberately refuses to parse those names, so every folder it writes leaks forever. There are six call sites total and they are all migrated below. `SyncOptions.EffectiveBackupFolder` and its `Validate()` containment check at `SyncOptions.cs:117` are left untouched — that is the earlier `SyncOptions` phase's surface, and `EffectiveArchiveFolder` is specified to reuse its fallback rules.

**Where `Prune` is called:** once per session, at the top of `HandleConnectionAsync` immediately before the `ArchiveManager` is constructed — in `SyncClient` before the send loop and in `SyncServer` before the receive loop. Pruning before construction means this run's session folder does not exist yet and therefore can never be a prune candidate, even under a tiny `--archive-max-size`.

---

### Task 6.1: ArchiveManager session folders, reason partitioning, and copy-before-delete

- [ ] **Step 1: Write the failing test**

Create `tests/RemoteFileSync.Tests/Backup/ArchiveManagerTests.cs`:

```csharp
using System.Globalization;
using RemoteFileSync.Backup;

namespace RemoteFileSync.Tests.Backup;

public class ArchiveManagerTests : IDisposable
{
    private readonly string _syncDir;
    private readonly string _archiveDir;

    public ArchiveManagerTests()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rfs_arc_{Guid.NewGuid()}");
        _syncDir = Path.Combine(root, "sync");
        _archiveDir = Path.Combine(root, "archive");
        Directory.CreateDirectory(_syncDir);
        Directory.CreateDirectory(_archiveDir);
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_syncDir)!;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private void CreateSyncFile(string relativePath, string content = "original")
    {
        var full = Path.Combine(_syncDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private ArchiveManager NewManager(DateTime sessionStartUtc) =>
        new(_syncDir, _archiveDir, sessionStartUtc);

    [Fact]
    public void SessionFolderName_IsSessionStartStamp_AndSessionRootHangsOffArchiveRoot()
    {
        var start = new DateTime(2026, 7, 19, 14, 30, 52, DateTimeKind.Utc);
        var mgr = NewManager(start);

        Assert.Equal("20260719-143052", mgr.SessionFolderName);
        Assert.Equal(Path.Combine(Path.GetFullPath(_archiveDir), "20260719-143052"), mgr.SessionRoot);
    }

    [Theory]
    [InlineData(ArchiveReason.Deleted, "deleted")]
    [InlineData(ArchiveReason.Overwritten, "overwritten")]
    [InlineData(ArchiveReason.Conflict, "conflict")]
    public void Archive_PartitionsByReason(ArchiveReason reason, string expectedFolder)
    {
        CreateSyncFile("report.docx");
        var mgr = NewManager(new DateTime(2026, 7, 19, 14, 30, 52, DateTimeKind.Utc));

        Assert.True(mgr.Archive("report.docx", reason, removeOriginal: false));
        Assert.True(File.Exists(Path.Combine(_archiveDir, "20260719-143052", expectedFolder, "report.docx")));
    }

    [Fact]
    public void Archive_PreservesNestedStructureUnderTheReasonFolder()
    {
        CreateSyncFile("docs/sub/file.txt");
        var mgr = NewManager(new DateTime(2026, 7, 19, 14, 30, 52, DateTimeKind.Utc));

        Assert.True(mgr.Archive("docs/sub/file.txt", ArchiveReason.Overwritten, removeOriginal: false));
        Assert.True(File.Exists(Path.Combine(
            _archiveDir, "20260719-143052", "overwritten", "docs", "sub", "file.txt")));
    }

    [Fact]
    public void Archive_RemoveOriginalFalse_LeavesOriginalInPlace()
    {
        CreateSyncFile("report.docx");
        var mgr = NewManager(new DateTime(2026, 7, 19, 14, 30, 52, DateTimeKind.Utc));

        Assert.True(mgr.Archive("report.docx", ArchiveReason.Overwritten, removeOriginal: false));
        // Copy, not move: a failed transfer must not leave the sync folder without the file.
        Assert.True(File.Exists(Path.Combine(_syncDir, "report.docx")));
        Assert.Equal("original", File.ReadAllText(
            Path.Combine(_archiveDir, "20260719-143052", "overwritten", "report.docx")));
    }

    [Fact]
    public void Archive_RemoveOriginalTrue_CopiesThenDeletesOriginal()
    {
        CreateSyncFile("report.docx");
        var mgr = NewManager(new DateTime(2026, 7, 19, 14, 30, 52, DateTimeKind.Utc));

        Assert.True(mgr.Archive("report.docx", ArchiveReason.Deleted, removeOriginal: true));
        // Deletion propagation: the original goes away, but only after the copy succeeded.
        Assert.False(File.Exists(Path.Combine(_syncDir, "report.docx")));
        Assert.Equal("original", File.ReadAllText(
            Path.Combine(_archiveDir, "20260719-143052", "deleted", "report.docx")));
    }

    [Fact]
    public void Archive_SamePathTwiceInOneSession_AppendsNumericSuffix()
    {
        var mgr = NewManager(new DateTime(2026, 7, 19, 14, 30, 52, DateTimeKind.Utc));
        CreateSyncFile("report.docx", "version1");
        mgr.Archive("report.docx", ArchiveReason.Overwritten, removeOriginal: false);
        CreateSyncFile("report.docx", "version2");
        mgr.Archive("report.docx", ArchiveReason.Overwritten, removeOriginal: false);

        var dir = Path.Combine(_archiveDir, "20260719-143052", "overwritten");
        Assert.Equal("version1", File.ReadAllText(Path.Combine(dir, "report.docx")));
        Assert.Equal("version2", File.ReadAllText(Path.Combine(dir, "report_1.docx")));
    }

    [Fact]
    public void Archive_RejectsPathEscapingTheSyncRoot()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_syncDir)!, "outside.txt");
        File.WriteAllText(outside, "secret");
        var mgr = NewManager(new DateTime(2026, 7, 19, 14, 30, 52, DateTimeKind.Utc));

        // relativePath arrives from the network on deletion propagation, so containment must
        // hold before the path reaches the filesystem.
        Assert.False(mgr.Archive("../outside.txt", ArchiveReason.Deleted, removeOriginal: true));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void Archive_MissingFile_ReturnsFalse()
    {
        var mgr = NewManager(new DateTime(2026, 7, 19, 14, 30, 52, DateTimeKind.Utc));
        Assert.False(mgr.Archive("nonexistent.txt", ArchiveReason.Deleted, removeOriginal: true));
    }

    [Fact]
    public async Task Archive_ConcurrentCalls_AllSucceed()
    {
        for (int i = 0; i < 10; i++) CreateSyncFile($"file{i}.txt", $"content{i}");
        var mgr = NewManager(new DateTime(2026, 7, 19, 14, 30, 52, DateTimeKind.Utc));

        var tasks = Enumerable.Range(0, 10)
            .Select(i => Task.Run(() => mgr.Archive($"file{i}.txt", ArchiveReason.Deleted, removeOriginal: false)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, Assert.True);
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ArchiveManagerTests"`
Expected: FAIL — `CS0246: The type or namespace name 'ArchiveManager' could not be found` and `CS0246: The type or namespace name 'ArchiveReason' could not be found`.

- [ ] **Step 3: Implement**

Create `src/RemoteFileSync/Backup/ArchiveManager.cs`:

```csharp
using System.Globalization;
using RemoteFileSync.Security;

namespace RemoteFileSync.Backup;

public enum ArchiveReason { Deleted, Overwritten, Conflict }

public readonly record struct PruneResult(int SessionsRemoved, long BytesFreed);

/// <summary>
/// Archives files into one folder per sync session:
/// <c>&lt;archiveRoot&gt;/&lt;yyyyMMdd-HHmmss&gt;/&lt;reason&gt;/&lt;original relative path&gt;</c>.
/// The session stamp is supplied by the caller and captured ONCE per run; the superseded
/// BackupManager read DateTime.UtcNow per file, so a run crossing midnight UTC scattered one
/// logical session across two dated folders that could not be restored together.
/// </summary>
public sealed class ArchiveManager
{
    /// <summary>Session folder format. Prune parses folder names back with this exact format.</summary>
    public const string SessionFolderFormat = "yyyyMMdd-HHmmss";

    private readonly string _syncFolder;
    private readonly object _lock = new();

    public ArchiveManager(string syncFolder, string archiveRoot, DateTime sessionStartUtc)
    {
        _syncFolder = Path.GetFullPath(syncFolder);
        SessionFolderName = sessionStartUtc.ToString(SessionFolderFormat, CultureInfo.InvariantCulture);
        SessionRoot = Path.Combine(Path.GetFullPath(archiveRoot), SessionFolderName);
    }

    public string SessionFolderName { get; }

    public string SessionRoot { get; }

    /// <summary>
    /// Copies the file into this session's archive under <paramref name="reason"/>, optionally
    /// deleting the original afterwards. Returns false if the path is not contained by the sync
    /// root or the file does not exist.
    /// </summary>
    public bool Archive(string relativePath, ArchiveReason reason, bool removeOriginal)
    {
        // relativePath can arrive from the network (deletion propagation), so it must be
        // contained before it reaches the filesystem.
        if (!PathGuard.TryResolveWithinRoot(_syncFolder, relativePath, out var sourcePath)) return false;
        if (!File.Exists(sourcePath)) return false;

        lock (_lock)
        {
            var relDir = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? "";
            var destDir = Path.Combine(SessionRoot, ReasonFolder(reason), relDir);
            Directory.CreateDirectory(destDir);

            var fileName = Path.GetFileNameWithoutExtension(relativePath);
            var ext = Path.GetExtension(relativePath);
            var destPath = Path.Combine(destDir, Path.GetFileName(relativePath));

            // One path can be archived twice in a session (overwritten, then deleted), and a
            // clobbering copy would destroy the earlier version.
            int suffix = 1;
            while (File.Exists(destPath))
            {
                destPath = Path.Combine(destDir, $"{fileName}_{suffix}{ext}");
                suffix++;
            }

            // Copy first: if the copy fails we must not have destroyed the original.
            File.Copy(sourcePath, destPath, overwrite: false);
            if (removeOriginal) File.Delete(sourcePath);
            return true;
        }
    }

    private static string ReasonFolder(ArchiveReason reason) => reason switch
    {
        ArchiveReason.Deleted => "deleted",
        ArchiveReason.Overwritten => "overwritten",
        ArchiveReason.Conflict => "conflict",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unmapped ArchiveReason."),
    };
}
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ArchiveManagerTests"`
Expected: PASS

---

### Task 6.2: A run spanning midnight UTC lands in ONE session folder

- [ ] **Step 1: Write the failing test**

Append to `tests/RemoteFileSync.Tests/Backup/ArchiveManagerTests.cs`, inside the class:

```csharp
    [Fact]
    public void Archive_RunSpanningMidnightUtc_LandsInExactlyOneSessionFolder()
    {
        // Regression: BackupManager derived its folder from DateTime.UtcNow on EVERY call, so a
        // run that started at 23:59:59 and finished at 00:00:01 split into two dated folders and
        // neither half was a complete restore point. The stamp is now fixed at construction, so
        // the folder is a function of the session start alone and not of the wall clock.
        var sessionStart = new DateTime(2026, 7, 19, 23, 59, 59, DateTimeKind.Utc);
        var mgr = NewManager(sessionStart);

        CreateSyncFile("before-midnight.txt", "before");
        Assert.True(mgr.Archive("before-midnight.txt", ArchiveReason.Deleted, removeOriginal: true));

        CreateSyncFile("after-midnight.txt", "after");
        Assert.True(mgr.Archive("after-midnight.txt", ArchiveReason.Deleted, removeOriginal: true));

        var sessionFolders = Directory.GetDirectories(_archiveDir);
        Assert.Single(sessionFolders);
        Assert.Equal("20260719-235959", Path.GetFileName(sessionFolders[0]));

        // sessionStart is a fixed instant in the past, so the wall clock cannot coincide with
        // it: this asserts the folder did not come from DateTime.UtcNow.
        Assert.NotEqual(DateTime.UtcNow.ToString(ArchiveManager.SessionFolderFormat, CultureInfo.InvariantCulture),
                        Path.GetFileName(sessionFolders[0]));

        var deletedDir = Path.Combine(_archiveDir, "20260719-235959", "deleted");
        Assert.Equal("before", File.ReadAllText(Path.Combine(deletedDir, "before-midnight.txt")));
        Assert.Equal("after", File.ReadAllText(Path.Combine(deletedDir, "after-midnight.txt")));
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Archive_RunSpanningMidnightUtc_LandsInExactlyOneSessionFolder"`
Expected: PASS immediately — Task 6.1 already made the stamp a constructor argument, so this test is a regression lock, not a driver. If it FAILS, the implementation from 6.1 is reading the clock somewhere and must be corrected before proceeding.

- [ ] **Step 3: Implement**

No production change. The behaviour is supplied by `ArchiveManager`'s constructor capturing `sessionStartUtc` into `SessionFolderName` (see Task 6.1). This task exists to pin the bug shut; deleting the test would silently re-open it.

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Archive_RunSpanningMidnightUtc_LandsInExactlyOneSessionFolder"`
Expected: PASS

---

### Task 6.3: Prune whole session folders by age, then by size cap

- [ ] **Step 1: Write the failing test**

Append to `tests/RemoteFileSync.Tests/Backup/ArchiveManagerTests.cs`, inside the class:

```csharp
    /// <summary>Fabricates an already-archived session folder of a known size.</summary>
    private string CreateArchivedSession(DateTime startUtc, string fileName, int sizeBytes)
    {
        var sessionRoot = Path.Combine(
            _archiveDir, startUtc.ToString(ArchiveManager.SessionFolderFormat, CultureInfo.InvariantCulture));
        var reasonDir = Path.Combine(sessionRoot, "deleted");
        Directory.CreateDirectory(reasonDir);
        File.WriteAllBytes(Path.Combine(reasonDir, fileName), new byte[sizeBytes]);
        return sessionRoot;
    }

    [Fact]
    public void Prune_RemovesSessionsOlderThanKeepAge_AndKeepsNewerOnes()
    {
        var stale = CreateArchivedSession(DateTime.UtcNow.AddDays(-40), "a.txt", 16);
        var fresh = CreateArchivedSession(DateTime.UtcNow.AddDays(-2), "b.txt", 16);

        var result = ArchiveManager.Prune(_archiveDir, TimeSpan.FromDays(30), maxBytes: 0);

        Assert.Equal(1, result.SessionsRemoved);
        Assert.Equal(16L, result.BytesFreed);
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void Prune_ZeroKeepAge_KeepsEverythingForever()
    {
        var ancient = CreateArchivedSession(DateTime.UtcNow.AddDays(-4000), "a.txt", 16);

        // --archive-keep-days 0 means keep forever, not delete everything.
        var result = ArchiveManager.Prune(_archiveDir, TimeSpan.Zero, maxBytes: 0);

        Assert.Equal(0, result.SessionsRemoved);
        Assert.True(Directory.Exists(ancient));
    }

    [Fact]
    public void Prune_EnforcesSizeCap_DeletingWholeSessionsOldestFirst()
    {
        var oldest = CreateArchivedSession(DateTime.UtcNow.AddHours(-3), "a.txt", 1000);
        var middle = CreateArchivedSession(DateTime.UtcNow.AddHours(-2), "b.txt", 1000);
        var newest = CreateArchivedSession(DateTime.UtcNow.AddHours(-1), "c.txt", 1000);

        var result = ArchiveManager.Prune(_archiveDir, TimeSpan.Zero, maxBytes: 2000);

        Assert.Equal(1, result.SessionsRemoved);
        Assert.Equal(1000L, result.BytesFreed);
        Assert.False(Directory.Exists(oldest));
        // Whole folders only: a partially-emptied session is not a restore point.
        Assert.True(File.Exists(Path.Combine(middle, "deleted", "b.txt")));
        Assert.True(File.Exists(Path.Combine(newest, "deleted", "c.txt")));
    }

    [Fact]
    public void Prune_IgnoresDirectoriesWhoseNameDoesNotParseAsASessionStamp()
    {
        var legacy = Path.Combine(_archiveDir, "20260101");            // pre-ArchiveManager dated backup
        var foreign = Path.Combine(_archiveDir, "my-important-stuff"); // user dropped it here
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(foreign);
        File.WriteAllBytes(Path.Combine(legacy, "a.txt"), new byte[64]);
        File.WriteAllBytes(Path.Combine(foreign, "b.txt"), new byte[64]);

        // Maximally aggressive retention: everything we created would go. Nothing here was.
        var result = ArchiveManager.Prune(_archiveDir, TimeSpan.FromTicks(1), maxBytes: 1);

        Assert.Equal(0, result.SessionsRemoved);
        Assert.Equal(0L, result.BytesFreed);
        Assert.True(File.Exists(Path.Combine(legacy, "a.txt")));
        Assert.True(File.Exists(Path.Combine(foreign, "b.txt")));
    }

    [Fact]
    public void Prune_MissingArchiveRoot_IsANoOp()
    {
        // First run: retention executes before anything has ever been archived.
        var never = Path.Combine(Path.GetDirectoryName(_syncDir)!, "no-such-archive");

        var result = ArchiveManager.Prune(never, TimeSpan.FromDays(30), maxBytes: 0);

        Assert.Equal(0, result.SessionsRemoved);
        Assert.Equal(0L, result.BytesFreed);
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ArchiveManagerTests.Prune"`
Expected: FAIL — `CS0117: 'ArchiveManager' does not contain a definition for 'Prune'`.

- [ ] **Step 3: Implement**

Add to `src/RemoteFileSync/Backup/ArchiveManager.cs`, immediately after the `ReasonFolder` method and before the closing brace of the class:

```csharp
    /// <summary>
    /// Applies retention to <paramref name="archiveRoot"/>: first drops sessions older than
    /// <paramref name="keepAge"/>, then drops the oldest survivors until the total falls to
    /// <paramref name="maxBytes"/>. keepAge &lt;= zero disables the age rule; maxBytes &lt;= 0
    /// disables the size cap. Whole session folders only — a half-emptied session is not a
    /// restore point.
    /// </summary>
    public static PruneResult Prune(string archiveRoot, TimeSpan keepAge, long maxBytes)
    {
        var rootFull = Path.GetFullPath(archiveRoot);
        if (!Directory.Exists(rootFull)) return new PruneResult(0, 0);

        var sessions = new List<(DateTime Start, string Path, long Bytes)>();
        foreach (var dir in Directory.GetDirectories(rootFull))
        {
            // Only folders we created are eligible. A legacy yyyyMMdd backup tree, or anything a
            // user dropped into the archive root, fails the parse and is left strictly alone —
            // retention must never delete something this code did not write.
            if (!DateTime.TryParseExact(Path.GetFileName(dir), SessionFolderFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
                continue;

            sessions.Add((start, dir, DirectorySize(dir)));
        }

        // Oldest first: retention consumes history from the far end, never the recent end.
        sessions.Sort((a, b) => a.Start.CompareTo(b.Start));

        int removed = 0;
        long freed = 0;

        var survivors = new List<(DateTime Start, string Path, long Bytes)>();
        var cutoff = DateTime.UtcNow - keepAge;
        foreach (var s in sessions)
        {
            if (keepAge > TimeSpan.Zero && s.Start < cutoff && TryDeleteSession(s.Path))
            {
                removed++;
                freed += s.Bytes;
                continue;
            }
            survivors.Add(s);
        }

        if (maxBytes > 0)
        {
            long total = 0;
            foreach (var s in survivors) total += s.Bytes;

            foreach (var s in survivors)
            {
                if (total <= maxBytes) break;
                if (!TryDeleteSession(s.Path)) continue;
                removed++;
                freed += s.Bytes;
                total -= s.Bytes;
            }
        }

        return new PruneResult(removed, freed);
    }

    /// <summary>
    /// Retention is best-effort: a locked or unreadable archive folder must not fail the sync
    /// that is about to run, since Prune executes before any transfer.
    /// </summary>
    private static bool TryDeleteSession(string sessionPath)
    {
        try
        {
            Directory.Delete(sessionPath, recursive: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static long DirectorySize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                // A file vanishing mid-walk must not abort retention for the whole archive.
                try { total += new FileInfo(file).Length; }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return total;
    }
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ArchiveManagerTests"`
Expected: PASS (all 16 tests)

---

### Task 6.4: Migrate every call site and delete BackupManager

- [ ] **Step 1: Write the failing test**

Delete `tests/RemoteFileSync.Tests/Backup/BackupManagerTests.cs` — every behaviour it asserted (copy semantics, copy-then-delete, missing file, nested structure, numeric suffix, thread safety) is covered by the `ArchiveManagerTests` written in Task 6.1.

```bash
git rm tests/RemoteFileSync.Tests/Backup/BackupManagerTests.cs
git rm src/RemoteFileSync/Backup/BackupManager.cs
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ArchiveManagerTests"`
Expected: FAIL to build — `SyncClient.cs(209,26): error CS0246: The type or namespace name 'BackupManager' could not be found` and the same at `SyncServer.cs(173,26)`. The whole test assembly fails to compile until the six call sites are migrated.

- [ ] **Step 3: Implement**

**3a. `src/RemoteFileSync/Network/SyncClient.cs:209`** — current line:

```csharp
        var backup = new BackupManager(_options.Folder, _options.EffectiveBackupFolder);
```

Replace with:

```csharp
        // Retention runs here, before the first archive write and before the first transfer, so
        // the session folder this run is about to create can never be a prune candidate.
        var pruned = ArchiveManager.Prune(_options.EffectiveArchiveFolder,
                                          TimeSpan.FromDays(_options.ArchiveKeepDays),
                                          _options.ArchiveMaxBytes);
        if (pruned.SessionsRemoved > 0)
            _logger.Info($"Archive retention: removed {pruned.SessionsRemoved} session(s), " +
                         $"freed {pruned.BytesFreed / 1024} KB.");

        // Captured ONCE for the whole run: reading the clock per file split a sync that crossed
        // midnight UTC across two archive folders, leaving neither half a complete restore point.
        var archive = new ArchiveManager(_options.Folder, _options.EffectiveArchiveFolder, DateTime.UtcNow);
```

**3b. `src/RemoteFileSync/Network/SyncClient.cs:370-371`** — current lines:

```csharp
                    result = await receiver.ReceiveFileAsync(stream, ct,
                        onBeforeCommit: p => action.Action == SyncActionType.SendToClient && backup.BackupFile(p));
```

Replace with:

```csharp
                    result = await receiver.ReceiveFileAsync(stream, ct,
                        onBeforeCommit: p => action.Action == SyncActionType.SendToClient
                            && archive.Archive(p, ArchiveReason.Overwritten, removeOriginal: false));
```

**3c. `src/RemoteFileSync/Network/SyncClient.cs:425`** — current line:

```csharp
                        if (backup.BackupAndRemove(path))
```

Replace with:

```csharp
                        if (archive.Archive(path, ArchiveReason.Deleted, removeOriginal: true))
```

**3d. `src/RemoteFileSync/Network/SyncServer.cs:173`** — current line:

```csharp
        var backup = new BackupManager(_options.Folder, _options.EffectiveBackupFolder);
```

Replace with:

```csharp
        // Retention runs here, before the first archive write and before the first transfer, so
        // the session folder this run is about to create can never be a prune candidate.
        var pruned = ArchiveManager.Prune(_options.EffectiveArchiveFolder,
                                          TimeSpan.FromDays(_options.ArchiveKeepDays),
                                          _options.ArchiveMaxBytes);
        if (pruned.SessionsRemoved > 0)
            _logger.Info($"Archive retention: removed {pruned.SessionsRemoved} session(s), " +
                         $"freed {pruned.BytesFreed / 1024} KB.");

        // Captured ONCE for the whole run: reading the clock per file split a sync that crossed
        // midnight UTC across two archive folders, leaving neither half a complete restore point.
        var archive = new ArchiveManager(_options.Folder, _options.EffectiveArchiveFolder, DateTime.UtcNow);
```

**3e. `src/RemoteFileSync/Network/SyncServer.cs:192-193`** — current lines:

```csharp
                result = await receiver.ReceiveFileAsync(stream, ct,
                    onBeforeCommit: p => action.Action == SyncActionType.SendToServer && backup.BackupFile(p));
```

Replace with:

```csharp
                result = await receiver.ReceiveFileAsync(stream, ct,
                    onBeforeCommit: p => action.Action == SyncActionType.SendToServer
                        && archive.Archive(p, ArchiveReason.Overwritten, removeOriginal: false));
```

**3f. `src/RemoteFileSync/Network/SyncServer.cs:260`** — current line:

```csharp
                        if (backup.BackupAndRemove(path))
```

Replace with:

```csharp
                        if (archive.Archive(path, ArchiveReason.Deleted, removeOriginal: true))
```

No `using` changes: `using RemoteFileSync.Backup;` is already present at `SyncClient.cs:3` and `SyncServer.cs:4`.

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ArchiveManagerTests"`
Expected: PASS, and the solution builds with `BackupManager` gone.

---

### Phase 6 commit

```bash
git add src/RemoteFileSync/Backup/ArchiveManager.cs \
        src/RemoteFileSync/Backup/BackupManager.cs \
        src/RemoteFileSync/Network/SyncClient.cs \
        src/RemoteFileSync/Network/SyncServer.cs \
        tests/RemoteFileSync.Tests/Backup/ArchiveManagerTests.cs \
        tests/RemoteFileSync.Tests/Backup/BackupManagerTests.cs
git commit -m "feat: replace BackupManager with ArchiveManager (session folders, reasons, retention)

The session stamp is now captured once by the caller and passed to the
constructor. BackupManager read DateTime.UtcNow on every call, so a run
that crossed midnight UTC scattered one logical session across two dated
folders and neither half was a complete restore point.

Layout is <archiveRoot>/<yyyyMMdd-HHmmss>/<reason>/<relative path>, with
reason in {deleted, overwritten, conflict}. Prune removes whole session
folders, oldest first, by age then by size cap, and skips any directory
whose name does not parse as a session stamp so it can never delete
something it did not create. It runs at sync start, before the first
archive write, so the folder the run is about to create is not a
candidate.

BackupManager is deleted rather than kept as a shim: a shim has no
session stamp to delegate, so it would either lie about its layout or
reintroduce the per-file clock read, and its yyyyMMdd folders would be
unparseable to Prune and leak forever."
git push -u origin feat/deletion-sync-ancestor-merge
```

**Verification before commit:**
```bash
dotnet build -c Release
dotnet test -c Release
```
Expected: 0 errors. Existing tests knowingly changed: `tests/RemoteFileSync.Tests/Backup/BackupManagerTests.cs` is deleted in full (7 tests) because the type it covers is deleted; every property it asserted — copy semantics, copy-then-delete ordering, missing-file false, nested structure preservation, numeric collision suffixing, thread safety — is re-asserted against `ArchiveManager` in `ArchiveManagerTests`, plus new PathGuard-containment coverage the old suite lacked. No other existing test changes; `SyncOptionsTests.EffectiveBackupFolder_*` remain valid because `SyncOptions` is untouched by this phase.

---

## Phase 7: Mode dispatch, Pull-mode execution, and the reworked safety gates

**Goal:** Make `SyncMode` actually drive the client and server session loops — so Pull-mode deletes execute instead of being planned and dropped — and replace the two delete guards that go inert exactly when they matter.

**Files:**
- Modify: `src/RemoteFileSync/Network/SyncClient.cs:72-77`, `:89-113`, `:119`, `:149-152`, `:209`, `:233-256`, `:259-261`, `:356-357`, `:371`, `:404-405`, `:425`
- Modify: `src/RemoteFileSync/Network/SyncServer.cs:139-146`, `:171`, `:173`, `:193`, `:221-241`, `:260`, `:305`, `:356`
- Modify: `src/RemoteFileSync/Program.cs:55-78` (+ new static helper after `Main`)
- Modify: `tests/RemoteFileSync.Tests/Integration/DeleteThresholdTests.cs:73-85`
- Test: `tests/RemoteFileSync.Tests/Integration/SyncModeTests.cs` (create)

All line numbers below are **pre-edit** positions in the files as they stand at the start of this phase. Apply the edits within a file in descending line order, or re-read after each edit.

**Interfaces:**
- Consumes (Phase 1): `SyncMode { Push=1, Pull=2, TwoWay=3 }`; `SyncOptions.Mode`, `SyncOptions.Bidirectional => Mode == SyncMode.TwoWay` (getter only), `SyncOptions.MirrorDeletes`, `SyncOptions.EffectiveArchiveFolder`, `SyncOptions.ArchiveKeepDays`, `SyncOptions.ArchiveMaxBytes`. **Assumes Phase 1 already migrated every `Bidirectional = true` initializer** (`Program.ParseArgs`, `DeleteThresholdTests:53`, `DatabaseDeleteSyncTests:56`) to `Mode = SyncMode.TwoWay`, since the setter is removed.
- Consumes (Phase 2): `ClockSkew.Measure(long clientSentTicks, long serverTicks, long clientRecvTicks)`, `ClockSkew.Offset`, `ClockSkew.IsSuspicious`.
- Consumes (Phase 3): `SyncDatabase.LoadAll()`, `SyncDatabase.UpsertSynced(string, long, long, long, long, long, string)`, `PairMarker.PathFor/Exists/Write`. **Assumes `MarkSynced`/`MarkDeleted`/`MarkSkipped`/`GetAllTrackedFiles` survive Phase 3** — this phase does not rewrite those call sites (SyncClient.cs:165, :194, :196, :201-206, :307, :387, :430, :451).
- Consumes (Phase 4): `SyncEngine.ComputePlan(FileManifest, FileManifest, SyncMode, IReadOnlyDictionary<string,AncestorRow>?, bool, bool, ClockSkew)`.
- Consumes (Phase 5): `ProtocolHandler.SerializeHandshake(byte, byte, long)`, `DeserializeHandshake(byte[]) -> (byte, byte, long)`, `SerializeHandshakeAck(byte, bool, long)`, `DeserializeHandshakeAck(byte[]) -> (byte, bool, long)`, `ProtocolVersion = 3`.
- Consumes (Phase 6): `ArchiveManager(string, string, DateTime)`, `Archive(string, ArchiveReason, bool)`, `ArchiveManager.Prune(string, TimeSpan, long)`, `PruneResult`, `ArchiveReason`.
- Produces — **CONTRACT EXTENSION, not in CONTRACT.md, declared here rather than invented silently:**
  - `public static bool RemoteFileSync.Program.PairStateLost(string dbPath)` — the no-ancestor gate needs a testable seam; testing it through `Main` would require a live socket.
  - `private bool SyncClient.WithinDeleteBudget(int deletes, int destinationCount, string destinationLabel)` and the identical private member on `SyncServer` — private, so no public surface is added.

---

### Task 7.1: Mode dispatch — v3 handshake, clock skew, and the transfer-loop gates

- [ ] **Step 1: Write the failing test**

Create `tests/RemoteFileSync.Tests/Integration/SyncModeTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.Integration;

/// <summary>
/// Push and Pull must be genuinely one-directional. Before mode dispatch existed both were
/// flattened to "not bidirectional", so a Pull run happily uploaded the client's stale copies
/// over the authoritative server.
/// </summary>
public class SyncModeTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _serverDir;
    private readonly string _clientDir;
    private readonly string _dbDir;

    public SyncModeTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"rfs_mode_{Guid.NewGuid()}");
        _serverDir = Path.Combine(_testRoot, "server");
        _clientDir = Path.Combine(_testRoot, "client");
        _dbDir = Path.Combine(_testRoot, "db");
        Directory.CreateDirectory(_serverDir);
        Directory.CreateDirectory(_clientDir);
        Directory.CreateDirectory(_dbDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
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

    private string ClientArchiveRoot => Path.Combine(_testRoot, "client-archive");

    private async Task<int> RunSyncAsync(SyncMode mode, bool deleteEnabled, bool mirrorDeletes, SyncDatabase? db)
    {
        int port = GetFreePort();
        // The server no longer reads DeleteEnabled/Mode from its own options — it decodes both
        // from the v3 handshake — so deliberately leave them at their defaults here.
        var serverOpts = new SyncOptions
        {
            IsServer = true, Once = true, Port = port, Folder = _serverDir,
            BackupFolder = Path.Combine(_testRoot, "server-backup"),
            ArchiveFolder = Path.Combine(_testRoot, "server-archive"),
        };
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir,
            Mode = mode, DeleteEnabled = deleteEnabled, MirrorDeletes = mirrorDeletes,
            BackupFolder = Path.Combine(_testRoot, "client-backup"),
            ArchiveFolder = ClientArchiveRoot,
        };

        using var serverLogger = new SyncLogger(false, null);
        using var clientLogger = new SyncLogger(false, null);
        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger, db: db);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(300);
        var result = await client.RunAsync(cts.Token);
        // When a guard fires the client drops the connection, so the server session faults too.
        try { await serverTask; } catch { /* client exit code is the assertion subject */ }
        return result;
    }

    /// <summary>
    /// Records a path as previously synced with identical content on both sides, using the
    /// client file's real size/mtime so ChangeDetector reports it unchanged.
    /// </summary>
    private void SeedAncestor(SyncDatabase db, long sessionId, string relativePath)
    {
        var fi = new FileInfo(Path.Combine(_clientDir, relativePath));
        db.UpsertSynced(relativePath, fi.Length, fi.LastWriteTimeUtc.Ticks,
                        fi.Length, fi.LastWriteTimeUtc.Ticks, sessionId, "to_client");
    }

    [Fact]
    public async Task Push_EmitsNoClientSideWrites()
    {
        var ts = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "from-client.txt", "client content", ts);
        CreateFileWithTimestamp(_serverDir, "server-only.txt", "server content", ts);

        var exit = await RunSyncAsync(SyncMode.Push, deleteEnabled: false, mirrorDeletes: false, db: null);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(_serverDir, "from-client.txt")));
        // Push is client-authoritative one way only: nothing lands on the client, and without
        // --delete the server keeps its own file rather than being mirrored empty.
        Assert.False(File.Exists(Path.Combine(_clientDir, "server-only.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "server-only.txt")));
        Assert.Single(Directory.GetFiles(_clientDir));
    }

    [Fact]
    public async Task Pull_DoesNotUploadClientOnlyFileToServer()
    {
        var ts = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "client-only.txt", "stale local copy", ts);
        CreateFileWithTimestamp(_serverDir, "server-file.txt", "authoritative", ts);

        var exit = await RunSyncAsync(SyncMode.Pull, deleteEnabled: false, mirrorDeletes: false, db: null);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(_clientDir, "server-file.txt")));
        // The server is authoritative in Pull: an unknown client file is left alone locally
        // (no --delete) but must never be pushed up.
        Assert.False(File.Exists(Path.Combine(_serverDir, "client-only.txt")));
        Assert.Single(Directory.GetFiles(_serverDir));
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncModeTests"`
Expected: FAIL — the solution does not compile. Phase 5 changed the handshake signatures, leaving:
`src/RemoteFileSync/Network/SyncClient.cs(91,35): error CS1501: No overload for method 'SerializeHandshake' takes 2 arguments`
`src/RemoteFileSync/Network/SyncServer.cs(139,13): error CS8132: Cannot deconstruct a tuple of '3' elements into '2' variables`
`src/RemoteFileSync/Network/SyncServer.cs(146,28): error CS7036: There is no argument given that corresponds to the required parameter 'serverTicks'`

- [ ] **Step 3: Implement**

**3a — `SyncClient.cs:73`.** Replace exactly:

```csharp
        var modeLabel = _options.Bidirectional ? "Bi-directional" : "Uni-directional";
```

with:

```csharp
        var modeLabel = _options.Mode switch
        {
            SyncMode.Push => "Push",
            SyncMode.Pull => "Pull",
            _ => "Two-way",
        };
```

**3b — `SyncClient.cs:89-113`.** Replace exactly:

```csharp
        // 1. Send handshake
        byte syncMode = (byte)((_options.Bidirectional ? 1 : 0) | (_options.DeleteEnabled ? 2 : 0));
        var hsPayload = ProtocolHandler.SerializeHandshake(ProtocolHandler.ProtocolVersion, syncMode);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Handshake, hsPayload, ct);

        // 2. Receive HandshakeAck
        var (ackType, ackData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        if (ackType != MessageType.HandshakeAck)
        {
            _logger.Error($"Expected HandshakeAck, got {ackType}");
            return 3;
        }
        var (serverVersion, accepted) = ProtocolHandler.DeserializeHandshakeAck(ackData);
        if (serverVersion != ProtocolHandler.ProtocolVersion)
        {
            _logger.Error($"Protocol mismatch: server speaks v{serverVersion}, this build speaks " +
                          $"v{ProtocolHandler.ProtocolVersion}. Upgrade both sides to the same build. " +
                          "(A v1 server silently discards the timestamp field and sync will never converge.)");
            return 2;
        }
        if (!accepted)
        {
            _logger.Error("Server rejected the connection.");
            return 2;
        }
```

with:

```csharp
        // 1. Send handshake. Low 2 bits carry SyncMode; bit 2 deleteEnabled, bit 3 mirrorDeletes.
        byte syncMode = (byte)((byte)_options.Mode
                             | (_options.DeleteEnabled ? 4 : 0)
                             | (_options.MirrorDeletes ? 8 : 0));
        long clientSentTicks = DateTime.UtcNow.Ticks;
        var hsPayload = ProtocolHandler.SerializeHandshake(
            ProtocolHandler.ProtocolVersion, syncMode, clientSentTicks);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Handshake, hsPayload, ct);

        // 2. Receive HandshakeAck
        var (ackType, ackData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        // Stamped before any validation so the round-trip measurement excludes our own parsing.
        long clientRecvTicks = DateTime.UtcNow.Ticks;
        if (ackType != MessageType.HandshakeAck)
        {
            _logger.Error($"Expected HandshakeAck, got {ackType}");
            return 3;
        }
        var (serverVersion, accepted, serverTicks) = ProtocolHandler.DeserializeHandshakeAck(ackData);
        if (serverVersion != ProtocolHandler.ProtocolVersion)
        {
            _logger.Error($"Protocol mismatch: server speaks v{serverVersion}, this build speaks " +
                          $"v{ProtocolHandler.ProtocolVersion}. Upgrade both sides to the same build. " +
                          "(A v1 server silently discards the timestamp field and sync will never converge.)");
            return 2;
        }
        if (!accepted)
        {
            _logger.Error("Server rejected the connection.");
            return 2;
        }

        // Peer clock offset, measured once per session. Without it a server running fast makes
        // every server-side file look newer, and newest-wins drags the whole tree backwards.
        var skew = ClockSkew.Measure(clientSentTicks, serverTicks, clientRecvTicks);
        if (skew.IsSuspicious)
            _logger.Warning($"Peer clock differs by {skew.Offset.TotalSeconds:F0}s. Newest-wins " +
                            "comparisons are corrected for this, but check the clock on both machines.");
```

**3c — `SyncClient.cs:119`.** Replace exactly:

```csharp
            var mode = $"{(_options.Bidirectional ? "bidi" : "uni")}+delete";
```

with:

```csharp
            var mode = $"{modeLabel.ToLowerInvariant()}+delete" + (_options.MirrorDeletes ? "+mirror" : "");
```

`modeLabel` is a local of `RunAsync`, not `HandleConnectionAsync`, so recompute it here instead:

```csharp
            var mode = $"{_options.Mode.ToString().ToLowerInvariant()}+delete"
                     + (_options.MirrorDeletes ? "+mirror" : "");
```

Use the second form; delete the first.

**3d — `SyncClient.cs:149-152`.** Replace exactly:

```csharp
        // 6. Compute sync plan and send
        var syncPlan = (_db != null)
            ? SyncEngine.ComputePlan(clientManifest, serverManifest, _options.Bidirectional, _db, _options.DeleteEnabled)
            : SyncEngine.ComputePlan(clientManifest, serverManifest, _options.Bidirectional, previousState, _options.DeleteEnabled);
```

with:

```csharp
        // 6. Compute sync plan and send. A null ancestor table selects the no-ancestor fallback
        // rules; passing an empty dictionary instead would read as "nothing was ever synced"
        // and resolve every peer-only file to a deletion.
        var ancestor = _db?.LoadAll();
        var syncPlan = SyncEngine.ComputePlan(
            clientManifest, serverManifest, _options.Mode, ancestor,
            _options.DeleteEnabled, _options.MirrorDeletes, skew);
```

**3e — `SyncClient.cs:259-261`.** Replace exactly:

```csharp
        // 7. Send files to server (SendToServer + ClientOnly)
        var toSend = syncPlan.Where(p =>
            p.Action == SyncActionType.SendToServer || p.Action == SyncActionType.ClientOnly).ToList();
```

with:

```csharp
        // 7. Send files to server (SendToServer + ClientOnly). Pull never uploads: the server
        // is authoritative, so a stale client copy must not be pushed back over it. Gated here
        // as well as in the planner because the plan also travels to the peer, which sizes its
        // receive loop from it — the two loops must agree exactly or the stream desyncs.
        var toSend = _options.Mode == SyncMode.Pull
            ? new List<SyncPlanEntry>()
            : syncPlan.Where(p =>
                p.Action == SyncActionType.SendToServer || p.Action == SyncActionType.ClientOnly).ToList();
```

**3f — `SyncClient.cs:356-357`.** Replace exactly:

```csharp
        // 9. Receive files from server (SendToClient + ServerOnly) if bidirectional
        if (_options.Bidirectional)
```

with:

```csharp
        // 9. Receive files from server (SendToClient + ServerOnly). Push never writes to the
        // client; the peer sends nothing in that mode, so entering this loop would block on a
        // frame that never arrives until the session timeout.
        if (_options.Mode != SyncMode.Push)
```

**3g — `SyncServer.cs:139-142`.** Replace exactly:

```csharp
        var (version, syncMode) = ProtocolHandler.DeserializeHandshake(hsData);
        bool bidirectional = (syncMode & 1) != 0;
        bool deleteEnabled = (syncMode & 2) != 0;
        _logger.Info($"Handshake: v{version}, {(bidirectional ? "bidirectional" : "unidirectional")}");
```

with:

```csharp
        var (version, syncMode, _) = ProtocolHandler.DeserializeHandshake(hsData);
        // The peer is unauthenticated, so an out-of-range mode must not become an unrecognised
        // enum value that later comparisons treat as "not Push" and admit writes on.
        var mode = (syncMode & 0b11) switch
        {
            2 => SyncMode.Pull,
            3 => SyncMode.TwoWay,
            _ => SyncMode.Push,
        };
        bool deleteEnabled = (syncMode & 4) != 0;
        bool mirrorDeletes = (syncMode & 8) != 0;
        _logger.Info($"Handshake: v{version}, mode={mode}" +
                     (deleteEnabled ? (mirrorDeletes ? ", delete+mirror" : ", delete") : ""));
```

**3h — `SyncServer.cs:146`.** Replace exactly:

```csharp
        var ackPayload = ProtocolHandler.SerializeHandshakeAck(ProtocolHandler.ProtocolVersion, accepted: versionOk);
```

with:

```csharp
        var ackPayload = ProtocolHandler.SerializeHandshakeAck(
            ProtocolHandler.ProtocolVersion, accepted: versionOk, serverTicks: DateTime.UtcNow.Ticks);
```

**3i — `SyncServer.cs:304-305`.** Replace exactly:

```csharp
        // 8. Send files to client (SendToClient + ServerOnly) if bidirectional
        if (bidirectional)
```

with:

```csharp
        // 8. Send files to client (SendToClient + ServerOnly). Mirrors the client's receive
        // gate: both sides must derive the loop from `mode` or the frame counts diverge.
        if (mode != SyncMode.Push)
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncModeTests"`
Expected: PASS

---

### Task 7.2: Pull-mode client deletions actually execute

- [ ] **Step 1: Write the failing test**

Append to `tests/RemoteFileSync.Tests/Integration/SyncModeTests.cs`:

```csharp
    [Fact]
    public async Task Pull_DeletesFileOnClientWhenServerNoLongerHasIt()
    {
        var ts = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "keep.txt", "keep", ts);
        CreateFileWithTimestamp(_serverDir, "keep.txt", "keep", ts);
        // Pulled from the server on an earlier run, then deleted there.
        CreateFileWithTimestamp(_clientDir, "gone.txt", "was pulled once", ts);

        var dbPath = Path.Combine(_dbDir, "pull.db");
        int exit;
        using (var db = new SyncDatabase(dbPath))
        {
            var session = db.StartSession("pull+delete", _clientDir, "127.0.0.1", 1234);
            SeedAncestor(db, session, "keep.txt");
            SeedAncestor(db, session, "gone.txt");
            db.CompleteSession(session, 2, 0, 0, 0);

            exit = await RunSyncAsync(SyncMode.Pull, deleteEnabled: true, mirrorDeletes: false, db);
        }

        Assert.Equal(0, exit);
        // The DeleteOnClient phase used to be gated on Bidirectional, so in Pull mode the
        // action was planned, sent by the server, and silently never applied.
        Assert.False(File.Exists(Path.Combine(_clientDir, "gone.txt")));
        Assert.True(File.Exists(Path.Combine(_clientDir, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "keep.txt")));
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Pull_DeletesFileOnClientWhenServerNoLongerHasIt"`
Expected: FAIL — `Assert.False() Failure` / `Expected: False` / `Actual: True` at the `File.Exists(gone.txt)` assertion. (`SyncOptions.Bidirectional` is false in Pull mode, so both delete gates are closed and the server's `DeleteFile` frame is never read.)

- [ ] **Step 3: Implement**

**3a — `SyncClient.cs:404-405`.** Replace exactly:

```csharp
        // 10. Deletion Phase (Client): Receive DeleteFile for DeleteOnClient actions
        if (_options.DeleteEnabled && _options.Bidirectional)
```

with:

```csharp
        // 10. Deletion Phase (Client): Receive DeleteFile for DeleteOnClient actions.
        // Gated on mode rather than Bidirectional: Pull plans DeleteOnClient too, and the old
        // gate dropped every one of them while the server sat in its send loop waiting for a
        // DeleteConfirm that never came.
        if (_options.DeleteEnabled && _options.Mode != SyncMode.Push)
```

**3b — `SyncServer.cs:355-356`.** Replace exactly:

```csharp
        // 9. Deletion Phase (Client): Send DeleteFile for DeleteOnClient actions
        if (deleteEnabled && bidirectional)
```

with:

```csharp
        // 9. Deletion Phase (Client): Send DeleteFile for DeleteOnClient actions. Must use the
        // identical predicate to the client's receive gate, or one side blocks on the other.
        if (deleteEnabled && mode != SyncMode.Push)
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Pull_DeletesFileOnClientWhenServerNoLongerHasIt"`
Expected: PASS

---

### Task 7.3: ArchiveManager replaces BackupManager, with Prune at session start

- [ ] **Step 1: Write the failing test**

Append to `tests/RemoteFileSync.Tests/Integration/SyncModeTests.cs`:

```csharp
    [Fact]
    public async Task Pull_ArchivesTheDeletedClientFileUnderTheSessionFolder()
    {
        var ts = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "keep.txt", "keep", ts);
        CreateFileWithTimestamp(_serverDir, "keep.txt", "keep", ts);
        CreateFileWithTimestamp(_clientDir, "gone.txt", "was pulled once", ts);

        var dbPath = Path.Combine(_dbDir, "archive.db");
        using (var db = new SyncDatabase(dbPath))
        {
            var session = db.StartSession("pull+delete", _clientDir, "127.0.0.1", 1234);
            SeedAncestor(db, session, "keep.txt");
            SeedAncestor(db, session, "gone.txt");
            db.CompleteSession(session, 2, 0, 0, 0);

            await RunSyncAsync(SyncMode.Pull, deleteEnabled: true, mirrorDeletes: false, db);
        }

        // A propagated deletion is recoverable: the original is copied into
        // <archiveRoot>/<session>/deleted/<relative path> before it is removed.
        var archived = Directory.GetFiles(ClientArchiveRoot, "gone.txt", SearchOption.AllDirectories);
        Assert.Single(archived);
        Assert.Contains($"{Path.DirectorySeparatorChar}deleted{Path.DirectorySeparatorChar}", archived[0]);
        Assert.Equal("was pulled once", File.ReadAllText(archived[0]));
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Pull_ArchivesTheDeletedClientFileUnderTheSessionFolder"`
Expected: FAIL — `System.IO.DirectoryNotFoundException: Could not find a part of the path '...\client-archive'` from `Directory.GetFiles`. The delete path still calls `BackupManager.BackupAndRemove`, which writes under `EffectiveBackupFolder`, so the archive root is never created.

- [ ] **Step 3: Implement**

**3a — `SyncClient.cs:209`.** Replace exactly:

```csharp
        var backup = new BackupManager(_options.Folder, _options.EffectiveBackupFolder);
```

with:

```csharp
        // Prune BEFORE archiving anything this session: pruning afterwards can evict the very
        // snapshots just taken whenever ArchiveMaxBytes is smaller than one session's output.
        if (_options.ArchiveKeepDays > 0 || _options.ArchiveMaxBytes > 0)
        {
            var keepAge = _options.ArchiveKeepDays > 0
                ? TimeSpan.FromDays(_options.ArchiveKeepDays)
                : TimeSpan.MaxValue;
            var pruned = ArchiveManager.Prune(_options.EffectiveArchiveFolder, keepAge, _options.ArchiveMaxBytes);
            if (pruned.SessionsRemoved > 0)
                _logger.Info($"Archive prune: {pruned.SessionsRemoved} session(s) removed, " +
                             $"{pruned.BytesFreed / (1024.0 * 1024.0):F1} MB freed");
        }
        var archive = new ArchiveManager(_options.Folder, _options.EffectiveArchiveFolder, DateTime.UtcNow);
```

**3b — `SyncClient.cs:370-371`.** Replace exactly:

```csharp
                    result = await receiver.ReceiveFileAsync(stream, ct,
                        onBeforeCommit: p => action.Action == SyncActionType.SendToClient && backup.BackupFile(p));
```

with:

```csharp
                    result = await receiver.ReceiveFileAsync(stream, ct,
                        onBeforeCommit: p => action.Action == SyncActionType.SendToClient
                            && archive.Archive(p, ArchiveReason.Overwritten, removeOriginal: false));
```

**3c — `SyncClient.cs:425`.** Replace exactly:

```csharp
                        if (backup.BackupAndRemove(path))
```

with:

```csharp
                        if (archive.Archive(path, ArchiveReason.Deleted, removeOriginal: true))
```

**3d — `SyncServer.cs:173`.** Replace exactly:

```csharp
        var backup = new BackupManager(_options.Folder, _options.EffectiveBackupFolder);
```

with:

```csharp
        // Same ordering rule as the client: prune first, so this session's snapshots survive.
        if (_options.ArchiveKeepDays > 0 || _options.ArchiveMaxBytes > 0)
        {
            var keepAge = _options.ArchiveKeepDays > 0
                ? TimeSpan.FromDays(_options.ArchiveKeepDays)
                : TimeSpan.MaxValue;
            var pruned = ArchiveManager.Prune(_options.EffectiveArchiveFolder, keepAge, _options.ArchiveMaxBytes);
            if (pruned.SessionsRemoved > 0)
                _logger.Info($"Archive prune: {pruned.SessionsRemoved} session(s) removed, " +
                             $"{pruned.BytesFreed / (1024.0 * 1024.0):F1} MB freed");
        }
        var archive = new ArchiveManager(_options.Folder, _options.EffectiveArchiveFolder, DateTime.UtcNow);
```

**3e — `SyncServer.cs:192-193`.** Replace exactly:

```csharp
                result = await receiver.ReceiveFileAsync(stream, ct,
                    onBeforeCommit: p => action.Action == SyncActionType.SendToServer && backup.BackupFile(p));
```

with:

```csharp
                result = await receiver.ReceiveFileAsync(stream, ct,
                    onBeforeCommit: p => action.Action == SyncActionType.SendToServer
                        && archive.Archive(p, ArchiveReason.Overwritten, removeOriginal: false));
```

**3f — `SyncServer.cs:260`.** Replace exactly:

```csharp
                        if (backup.BackupAndRemove(path))
```

with:

```csharp
                        if (archive.Archive(path, ArchiveReason.Deleted, removeOriginal: true))
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Pull_ArchivesTheDeletedClientFileUnderTheSessionFolder"`
Expected: PASS

---

### Task 7.4: Destination-side delete guards on both client and server

- [ ] **Step 1: Write the failing test**

Append to `tests/RemoteFileSync.Tests/Integration/SyncModeTests.cs`:

```csharp
    [Fact]
    public async Task EmptyDatabase_DoesNotDisarmTheDeleteGuard()
    {
        var ts = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 20; i++)
            CreateFileWithTimestamp(_clientDir, $"file{i:D3}.txt", $"content {i}", ts);

        // A wiped or freshly created database has zero tracked rows. The old guard divided by
        // that count, so it went inert in exactly the situation it exists for: state lost, and
        // every client file now indistinguishable from one the server deleted.
        var dbPath = Path.Combine(_dbDir, "wiped.db");
        int exit;
        using (var db = new SyncDatabase(dbPath))
            exit = await RunSyncAsync(SyncMode.Pull, deleteEnabled: true, mirrorDeletes: true, db);

        Assert.Equal(4, exit);
        Assert.Equal(20, Directory.GetFiles(_clientDir).Length);
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~EmptyDatabase_DoesNotDisarmTheDeleteGuard"`
Expected: FAIL — `Assert.Equal() Failure: Values differ` / `Expected: 4` / `Actual: 0`, followed by `Assert.Equal() Failure` / `Expected: 20` / `Actual: 0` on the surviving-file count.

- [ ] **Step 3: Implement**

**3a — `SyncClient.cs:233-256`.** Replace exactly:

```csharp
            if (!_options.ForceDelete)
            {
                // Denominator is the tracked-file population, NOT a manifest count: with
                // max(client, server) a peer repointed at a larger unrelated folder yields a
                // small percentage and every tracked file gets deleted anyway.
                int tracked = _db != null
                    ? _db.GetAllTrackedFiles().Count(f => f.Status == "exists")
                    : previousState?.Manifest.Count ?? 0;

                if (tracked >= SyncOptions.MinTrackedFilesForDeleteGuard)
                {
                    double pct = deleteCount * 100.0 / tracked;
                    if (pct > _options.MaxDeletePercent)
                    {
                        var msg = $"Refusing to sync: {deleteCount} of {tracked} tracked files " +
                                  $"({pct:F0}%) would be deleted, exceeding --max-delete-percent " +
                                  $"{_options.MaxDeletePercent}. Check that --folder on both sides " +
                                  "points where you expect. If this is intentional, re-run with --force-delete.";
                        _logger.Error(msg);
                        _progress.WriteError(msg, fatal: true);
                        return 4;
                    }
                }
            }
```

with:

```csharp
            if (!_options.ForceDelete)
            {
                // Bound each direction against the manifest of the side being deleted FROM —
                // the population actually at risk. The old denominator was the tracked-row
                // count, which is 0 on a wiped or never-built database, so the guard divided
                // into nothing and went inert precisely when state loss made every peer-only
                // file look like a deletion.
                int serverDeletes = syncPlan.Count(p => p.Action == SyncActionType.DeleteOnServer);
                int clientDeletes = syncPlan.Count(p => p.Action == SyncActionType.DeleteOnClient);

                if (!WithinDeleteBudget(serverDeletes, serverManifest.Count, "server")) return 4;
                if (!WithinDeleteBudget(clientDeletes, clientManifest.Count, "client")) return 4;
            }
```

**3b — `SyncClient.cs`, new private method inserted immediately before the closing brace of the class (after `HandleConnectionAsync`, i.e. after line 503):**

```csharp
    /// <summary>
    /// Percentage guard for one direction. <paramref name="destinationCount"/> is the live file
    /// count on the side being deleted from, so a lost database cannot zero the denominator.
    /// </summary>
    private bool WithinDeleteBudget(int deletes, int destinationCount, string destinationLabel)
    {
        if (deletes == 0) return true;

        // Below the floor the percentage is noise: 1 of 2 files is 50% but entirely ordinary,
        // and a guard that fires on ordinary edits trains users into --force-delete by reflex.
        if (destinationCount < SyncOptions.MinTrackedFilesForDeleteGuard) return true;

        double pct = deletes * 100.0 / destinationCount;
        if (pct <= _options.MaxDeletePercent) return true;

        var msg = $"Refusing to sync: {deletes} of {destinationCount} files on the " +
                  $"{destinationLabel} ({pct:F0}%) would be deleted, exceeding " +
                  $"--max-delete-percent {_options.MaxDeletePercent}. Check that --folder on both " +
                  "sides points where you expect, and that the sync database was not moved or " +
                  "deleted. If this is intentional, re-run with --force-delete.";
        _logger.Error(msg);
        _progress.WriteError(msg, fatal: true);
        return false;
    }
```

**3c — `SyncServer.cs:221-241`.** Replace exactly:

```csharp
        // 7. Deletion Phase (Server): Receive DeleteFile from client for DeleteOnServer actions
        if (deleteEnabled)
        {
            // The plan arrives over the wire from a peer we do not authenticate, so the server
            // enforces its own bound rather than trusting the client's guard.
            int requested = syncPlan.Count(p => p.Action == SyncActionType.DeleteOnServer);
            if (requested > 0 && serverManifest.Count >= SyncOptions.MinTrackedFilesForDeleteGuard
                && !_options.ForceDelete)
            {
                double pct = requested * 100.0 / serverManifest.Count;
                if (pct > _options.MaxDeletePercent)
                {
                    var msg = $"Rejecting sync plan: peer requested deletion of {requested} of " +
                              $"{serverManifest.Count} local files ({pct:F0}%), exceeding " +
                              $"--max-delete-percent {_options.MaxDeletePercent}.";
                    _logger.Error(msg);
                    _progress.WriteError(msg, fatal: true);
                    return 4;
                }
            }

```

with:

```csharp
        // 7. Deletion Phase (Server): Receive DeleteFile from client for DeleteOnServer actions
        if (deleteEnabled)
        {
```

**3d — `SyncServer.cs:171`.** The guard moves ahead of the transfer phase so a refusal happens before anything is written. Replace exactly:

```csharp
        _logger.Info($"Sync plan: {syncPlan.Count} actions");
```

with:

```csharp
        _logger.Info($"Sync plan: {syncPlan.Count} actions");

        // The plan arrives over the wire from a peer we do not authenticate, so the server
        // enforces its own bound rather than trusting the client's guard. BOTH directions are
        // bounded: in Pull mode every delete is a DeleteOnClient the server itself originates,
        // and the previous guard only counted DeleteOnServer, so nothing checked them at all.
        // Checked here, before the receive loop, so a refusal costs no writes on either side.
        if (deleteEnabled && !_options.ForceDelete)
        {
            int plannedServerDeletes = syncPlan.Count(p => p.Action == SyncActionType.DeleteOnServer);
            int plannedClientDeletes = syncPlan.Count(p => p.Action == SyncActionType.DeleteOnClient);
            if (!WithinDeleteBudget(plannedServerDeletes, serverManifest.Count, "server")) return 4;
            if (!WithinDeleteBudget(plannedClientDeletes, clientManifest.Count, "client")) return 4;
        }
```

**3e — `SyncServer.cs`, new private method inserted immediately before the closing brace of the class (after `HandleConnectionAsync`, i.e. after line 393):**

```csharp
    /// <summary>
    /// Percentage guard for one direction. <paramref name="destinationCount"/> is the file count
    /// on the side being deleted from; for the client that is the manifest it just sent us, which
    /// is the only view of the client's population the server has.
    /// </summary>
    private bool WithinDeleteBudget(int deletes, int destinationCount, string destinationLabel)
    {
        if (deletes == 0) return true;
        if (destinationCount < SyncOptions.MinTrackedFilesForDeleteGuard) return true;

        double pct = deletes * 100.0 / destinationCount;
        if (pct <= _options.MaxDeletePercent) return true;

        var msg = $"Rejecting sync plan: it would delete {deletes} of {destinationCount} files " +
                  $"on the {destinationLabel} ({pct:F0}%), exceeding this server's " +
                  $"--max-delete-percent {_options.MaxDeletePercent}.";
        _logger.Error(msg);
        _progress.WriteError(msg, fatal: true);
        return false;
    }
```

**3f — `tests/RemoteFileSync.Tests/Integration/DeleteThresholdTests.cs:73-85`.** The seeded ancestor rows must match the files on disk or `ChangeDetector` reports the client side changed and the engine resurrects instead of deleting, so the guard never gets the chance to fire. Replace exactly:

```csharp
    private SyncDatabase SeedTrackedFiles(int count)
    {
        var db = new SyncDatabase(Path.Combine(_stateDir, "state.db"));
        var session = db.StartSession("bidi+delete", _clientDir, "127.0.0.1", 1234);
        for (int i = 0; i < count; i++)
        {
            var name = $"file{i:D3}.txt";
            File.WriteAllText(Path.Combine(_clientDir, name), $"content {i}");
            db.MarkSynced(name, 9, DateTime.UtcNow.AddDays(-1), session, "to_server");
        }
        db.CompleteSession(session, count, 0, 0, 0);
        return db;
    }
```

with:

```csharp
    private SyncDatabase SeedTrackedFiles(int count)
    {
        var db = new SyncDatabase(Path.Combine(_stateDir, "state.db"));
        var session = db.StartSession("two-way+delete", _clientDir, "127.0.0.1", 1234);
        for (int i = 0; i < count; i++)
        {
            var name = $"file{i:D3}.txt";
            var full = Path.Combine(_clientDir, name);
            File.WriteAllText(full, $"content {i}");
            // The ancestor row must carry the file's real size and mtime: ChangeDetector
            // compares against it, and a row a day stale reads as "client changed since the
            // last sync", which resurrects the file instead of planning the deletion this
            // test exists to bound.
            var fi = new FileInfo(full);
            db.UpsertSynced(name, fi.Length, fi.LastWriteTimeUtc.Ticks,
                            fi.Length, fi.LastWriteTimeUtc.Ticks, session, "to_server");
        }
        db.CompleteSession(session, count, 0, 0, 0);
        return db;
    }
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~EmptyDatabase_DoesNotDisarmTheDeleteGuard"`
Expected: PASS

Then confirm the pre-existing threshold tests still hold:

Run: `dotnet test -c Release --filter "FullyQualifiedName~DeleteThresholdTests"`
Expected: PASS (3 tests)

---

### Task 7.5: No-ancestor safety gate via PairMarker

- [ ] **Step 1: Write the failing test**

Append to `tests/RemoteFileSync.Tests/Integration/SyncModeTests.cs`:

```csharp
    [Fact]
    public void MarkerWithoutDatabase_IsRefusedAsLostState()
    {
        var dbPath = Path.Combine(_dbDir, "marker.db");
        using (var db = new SyncDatabase(dbPath))
            db.CompleteSession(db.StartSession("pull+delete", _clientDir, "127.0.0.1", 1234), 0, 0, 0, 0);
        PairMarker.Write(dbPath);
        SqliteConnection.ClearAllPools();

        // db + marker: the normal steady state, nothing to refuse.
        Assert.False(Program.PairStateLost(dbPath));

        // marker without db: this pair has synced before, so an absent ancestor table is state
        // loss, not a first run — every one-sided file would resolve to a deletion.
        File.Delete(dbPath);
        Assert.True(Program.PairStateLost(dbPath));

        // Unreadable counts the same as absent: a truncated file opens no ancestor rows.
        File.WriteAllText(dbPath, "not a sqlite database");
        Assert.True(Program.PairStateLost(dbPath));

        // Neither: a genuine first run, which is additive and safe.
        File.Delete(dbPath);
        File.Delete(PairMarker.PathFor(dbPath));
        Assert.False(Program.PairStateLost(dbPath));
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~MarkerWithoutDatabase_IsRefusedAsLostState"`
Expected: FAIL — `error CS0117: 'Program' does not contain a definition for 'PairStateLost'`

- [ ] **Step 3: Implement**

**3a — `Program.cs:55-78`.** Replace exactly:

```csharp
            else
            {
                SyncDatabase? db = null;
                if (options.DeleteEnabled)
                {
                    var dbPath = SyncDatabase.GetDbPath(SyncDatabase.DefaultBaseDir, options.Folder, options.Host!, options.Port);

                    // Auto-migrate from old binary state if needed
                    var binPath = Path.Combine(Path.GetDirectoryName(dbPath)!, "sync-state.bin");
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

with:

```csharp
            else
            {
                SyncDatabase? db = null;
                string? dbPath = null;
                if (options.DeleteEnabled)
                {
                    dbPath = SyncDatabase.GetDbPath(SyncDatabase.DefaultBaseDir, options.Folder, options.Host!, options.Port);

                    // Checked BEFORE the socket is opened. A refusal must not reach the peer,
                    // exchange manifests, or leave a half-open session row behind — the whole
                    // point is that nothing happens.
                    if (PairStateLost(dbPath) && !options.MirrorDeletes)
                    {
                        var msg = "Sync state lost: this pair has synced before (pair.marker is " +
                                  $"present) but its database at '{dbPath}' is missing or unreadable. " +
                                  "Without it, every file present on only one side is " +
                                  "indistinguishable from one the peer deleted. Restore the database " +
                                  "from backup, or re-run with --mirror to accept the destination " +
                                  "being overwritten to match the source.";
                        logger.Error(msg);
                        progressWriter.WriteError(msg, fatal: true);
                        return 4;
                    }

                    // Auto-migrate from old binary state if needed
                    var binPath = Path.Combine(Path.GetDirectoryName(dbPath)!, "sync-state.bin");
                    SyncDatabase.MigrateFromBinary(binPath, dbPath);

                    db = new SyncDatabase(dbPath);
                }

                try
                {
                    var client = new Network.SyncClient(options, logger, db: db,
                        progressWriter: progressWriter, stdinReader: stdinReader);
                    var exit = await client.RunAsync(cts.Token);

                    // Written only after a clean session. Arming the gate on a partial run would
                    // point it at a database that never finished being built, turning the next
                    // ordinary run into a hard refusal.
                    if (exit == 0 && dbPath != null)
                        PairMarker.Write(dbPath);

                    return exit;
                }
                finally
                {
                    db?.Dispose();
                }
            }
```

**3b — `Program.cs`, new static method inserted immediately after `Main` (after line 94, before `NextValue`):**

```csharp
    /// <summary>
    /// True when this pair has synced before but its ancestor database is gone or corrupt.
    /// An absent database on its own is indistinguishable from one deleted after a hundred
    /// successful syncs, so the marker file is the only thing separating a safe additive first
    /// run from a destructive one.
    /// </summary>
    public static bool PairStateLost(string dbPath)
    {
        if (!PairMarker.Exists(dbPath)) return false;
        if (!File.Exists(dbPath)) return true;

        // Opening it is the only reliable readability test: a truncated or foreign file passes
        // every cheaper check and then throws mid-session, after deletions are already planned.
        try
        {
            using (new SyncDatabase(dbPath)) { }
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~MarkerWithoutDatabase_IsRefusedAsLostState"`
Expected: PASS

---

### Phase 7 commit

```bash
git add src/RemoteFileSync/Network/SyncClient.cs \
        src/RemoteFileSync/Network/SyncServer.cs \
        src/RemoteFileSync/Program.cs \
        tests/RemoteFileSync.Tests/Integration/SyncModeTests.cs \
        tests/RemoteFileSync.Tests/Integration/DeleteThresholdTests.cs
git commit -m "feat: dispatch on SyncMode and rework the deletion safety gates

Push and Pull were both flattened to 'not bidirectional', so Pull-mode
DeleteOnClient actions were planned, sent by the server, and silently
dropped by a client gate keyed on Bidirectional. Gate the transfer and
deletion loops on Mode on both sides instead, decode the mode from the
v3 handshake, and wire ClockSkew and the ancestor table into ComputePlan.

Both delete guards used denominators that vanish when it matters: the
client divided by the tracked-row count (0 on a wiped DB) and the server
bounded only DeleteOnServer (0 in Pull mode). Both now bound each
direction against the destination-side manifest count.

Add the no-ancestor gate: refuse to run when pair.marker survives but the
database does not, unless --mirror. Swap BackupManager for ArchiveManager
with a prune at session start.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
git push -u origin feat/deletion-sync-ancestor-merge
```

**Verification before commit:**
```bash
dotnet build -c Release
dotnet test -c Release
```
Expected: 0 errors.

Existing tests knowingly changed in this phase: **`DeleteThresholdTests.SeedTrackedFiles`** only — its ancestor rows were seeded with a literal size of 9 and an mtime a day in the past, which under the Phase 4 decision tables reads as "client changed since last sync" and produces `SendToServer` + a resurrection log instead of the `DeleteOnClient` actions both `EmptyPeerFolder_AbortsInsteadOfMassDeleting` and `ForceDelete_OverridesTheThreshold` exist to bound. Seeding via `UpsertSynced` with the real `FileInfo` restores the intended scenario; both assertions are unchanged. No other existing test is modified.

---

## Phase 8: End-of-Sync Review Report for Conflicts and Resurrections

**Goal:** After the SyncComplete summary, print a review section listing every `ConflictKeepBoth` and every resurrection with both sides' size and mtime, persisted through `SyncDatabase.LogConflict` and mirrored as a `review` JSON progress event for the ExecRFS GUI.

**Files:**
- Create: `src/RemoteFileSync/Sync/ConflictDetail.cs`
- Create: `src/RemoteFileSync/Sync/ReviewReport.cs`
- Modify: `src/RemoteFileSync/Progress/JsonProgressWriter.cs:60-68`
- Modify: `src/ExecRFS/Models/ProgressEvent.cs:34-36`
- Modify: `src/RemoteFileSync/Network/SyncClient.cs:478-482`
- Test: `tests/RemoteFileSync.Tests/Sync/ConflictDetailTests.cs`
- Test: `tests/RemoteFileSync.Tests/Sync/ReviewReportTests.cs`
- Test: `tests/RemoteFileSync.Tests/Sync/ReviewReportEmitTests.cs`
- Test: `tests/RemoteFileSync.Tests/Progress/JsonProgressWriterTests.cs:105-111`
- Test: `tests/ExecRFS.Tests/Models/ProgressEventTests.cs:73-80`

**Interfaces:**

- Consumes (from the SyncDatabase phase, exactly as frozen in CONTRACT.md):
  - `public void LogConflict(string path, long sessionId, string detail);`
  - `public IReadOnlyList<ConflictEntry> GetSessionConflicts(long sessionId);`
  - `public IReadOnlyList<ConflictEntry> GetSessionResurrections(long sessionId);`
  - `public record ConflictEntry(string Path, string Detail, DateTime Timestamp);`
  - `public long StartSession(string mode, string clientFolder, string serverHost, int serverPort);` (existing, `SyncDatabase.cs:116`)
- Consumes (existing): `SyncLogger.Summary(string)` (`SyncLogger.cs:41`), `JsonProgressWriter` (`JsonProgressWriter.cs:5`).

- Produces:
  - `public readonly record struct ConflictDetail(long ClientSize, DateTime ClientMtimeUtc, long ServerSize, DateTime ServerMtimeUtc, bool Resurrected)` with `string Encode()`, `static bool TryParse(string?, out ConflictDetail)`, `const string ResurrectedPrefix = "resurrected:"`.
  - `public static class ReviewReport` with `static IReadOnlyList<string> BuildLines(IReadOnlyList<ConflictEntry>, IReadOnlyList<ConflictEntry>)` and `static void Emit(SyncDatabase? db, long sessionId, SyncLogger logger, JsonProgressWriter progress)`.
  - `JsonProgressWriter.WriteReview(string kind, string path, long client_size, string client_mtime, long server_size, string server_mtime)` — emits `{"event":"review", ...}`.
  - `ProgressEvent.Kind`, `.ClientSize`, `.ClientMtime`, `.ServerSize`, `.ServerMtime`.

- **Two gaps in CONTRACT.md this phase must fill — stated, not silently invented:**
  1. CONTRACT.md gives one writer (`LogConflict`) but two readers (`GetSessionConflicts` / `GetSessionResurrections`), and says `file_versions.action` gains **both** `'conflict'` and `'resurrected'`. Nothing in the frozen signature carries the discriminator, so it must live in `detail`. This phase fixes the convention: **`detail` beginning with `"resurrected:"` is stored with `action='resurrected'`; everything else with `action='conflict'`.** Task 8.5's `Emit_ReadsBackConflictAndResurrectionSeparately` pins this against the SyncDatabase phase's implementation and fails loudly if it diverges.
  2. CONTRACT.md does not specify the `detail` payload format. `ConflictDetail` (above) defines it so the report can render both sides. Earlier phases that call `LogConflict` must pass `new ConflictDetail(...).Encode()`. The report degrades gracefully — an unparsable detail is still listed, printed verbatim — because dropping a row would hide the exact case rule [2] exists to surface.

### Task 8.1: ConflictDetail encode/parse

- [ ] **Step 1: Write the failing test**

`tests/RemoteFileSync.Tests/Sync/ConflictDetailTests.cs`

```csharp
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class ConflictDetailTests
{
    [Fact]
    public void Encode_ThenTryParse_RoundTripsBothSides()
    {
        var original = new ConflictDetail(
            ClientSize: 2100000,
            ClientMtimeUtc: new DateTime(2026, 7, 20, 14, 30, 52, DateTimeKind.Utc),
            ServerSize: 2050112,
            ServerMtimeUtc: new DateTime(2026, 7, 20, 14, 31, 10, DateTimeKind.Utc),
            Resurrected: false);

        Assert.True(ConflictDetail.TryParse(original.Encode(), out var parsed));
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void Encode_Resurrection_CarriesThePrefixSoTheDbCanRouteIt()
    {
        // SyncDatabase routes to action='resurrected' on this prefix — it is the only
        // discriminator LogConflict's frozen signature leaves room for.
        var detail = new ConflictDetail(
            ClientSize: 1024,
            ClientMtimeUtc: new DateTime(2026, 7, 20, 9, 15, 0, DateTimeKind.Utc),
            ServerSize: 900,
            ServerMtimeUtc: new DateTime(2026, 7, 19, 17, 0, 0, DateTimeKind.Utc),
            Resurrected: true).Encode();

        Assert.StartsWith(ConflictDetail.ResurrectedPrefix, detail);
        Assert.True(ConflictDetail.TryParse(detail, out var parsed));
        Assert.True(parsed.Resurrected);
        Assert.Equal(1024, parsed.ClientSize);
        Assert.Equal(900, parsed.ServerSize);
    }

    [Fact]
    public void TryParse_PreservesUtcKind()
    {
        // A DateTime that came back as Kind.Unspecified would shift by the local offset
        // when formatted, printing a review time that never happened.
        var detail = new ConflictDetail(
            1, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            2, new DateTime(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc),
            false).Encode();

        Assert.True(ConflictDetail.TryParse(detail, out var parsed));
        Assert.Equal(DateTimeKind.Utc, parsed.ClientMtimeUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, parsed.ServerMtimeUtc.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("deleted on server, propagated to client")]
    [InlineData("client=12@2026-07-20T14:30:52.0000000Z")]
    [InlineData("client=abc@2026-07-20T14:30:52.0000000Z|server=1@2026-07-20T14:30:52.0000000Z")]
    [InlineData("client=1@not-a-date|server=1@2026-07-20T14:30:52.0000000Z")]
    public void TryParse_FreeFormOrMalformedDetail_ReturnsFalse(string detail)
    {
        Assert.False(ConflictDetail.TryParse(detail, out _));
    }

    [Fact]
    public void TryParse_Null_ReturnsFalse()
    {
        Assert.False(ConflictDetail.TryParse(null, out _));
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ConflictDetailTests"`
Expected: FAIL — `error CS0246: The type or namespace name 'ConflictDetail' could not be found (are you missing a using directive or an assembly reference?)`

- [ ] **Step 3: Implement**

`src/RemoteFileSync/Sync/ConflictDetail.cs`

```csharp
using System.Globalization;

namespace RemoteFileSync.Sync;

/// <summary>
/// The payload stored in SyncDatabase.LogConflict's <c>detail</c> column. CONTRACT.md fixes the
/// method signature but not the string, and the end-of-sync review has to print both sides'
/// size and mtime — so the string carries them, plus the resurrection flag that is the only
/// discriminator LogConflict leaves room for between action='conflict' and action='resurrected'.
/// </summary>
public readonly record struct ConflictDetail(
    long ClientSize,
    DateTime ClientMtimeUtc,
    long ServerSize,
    DateTime ServerMtimeUtc,
    bool Resurrected)
{
    public const string ResurrectedPrefix = "resurrected:";

    public string Encode()
    {
        var prefix = Resurrected ? ResurrectedPrefix : string.Empty;
        return prefix
             + "client=" + ClientSize.ToString(CultureInfo.InvariantCulture)
             + "@" + ClientMtimeUtc.ToString("O", CultureInfo.InvariantCulture)
             + "|server=" + ServerSize.ToString(CultureInfo.InvariantCulture)
             + "@" + ServerMtimeUtc.ToString("O", CultureInfo.InvariantCulture);
    }

    public static bool TryParse(string? detail, out ConflictDetail parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(detail)) return false;

        var body = detail;
        var resurrected = false;
        if (body.StartsWith(ResurrectedPrefix, StringComparison.Ordinal))
        {
            resurrected = true;
            body = body[ResurrectedPrefix.Length..];
        }

        var sides = body.Split('|');
        if (sides.Length != 2) return false;
        if (!TryParseSide(sides[0], "client=", out var clientSize, out var clientMtime)) return false;
        if (!TryParseSide(sides[1], "server=", out var serverSize, out var serverMtime)) return false;

        parsed = new ConflictDetail(clientSize, clientMtime, serverSize, serverMtime, resurrected);
        return true;
    }

    private static bool TryParseSide(string side, string prefix, out long size, out DateTime mtimeUtc)
    {
        size = 0;
        mtimeUtc = default;
        if (!side.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var rest = side[prefix.Length..];
        var at = rest.IndexOf('@');
        if (at < 0) return false;

        if (!long.TryParse(rest[..at], NumberStyles.Integer, CultureInfo.InvariantCulture, out size))
            return false;

        // RoundtripKind keeps the trailing Z as Kind.Utc; without it the review would print
        // the mtime shifted by the local offset.
        return DateTime.TryParseExact(rest[(at + 1)..], "O", CultureInfo.InvariantCulture,
                                      DateTimeStyles.RoundtripKind, out mtimeUtc);
    }
}
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ConflictDetailTests"`
Expected: PASS

### Task 8.2: ReviewReport.BuildLines

- [ ] **Step 1: Write the failing test**

`tests/RemoteFileSync.Tests/Sync/ReviewReportTests.cs`

```csharp
using RemoteFileSync.State;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class ReviewReportTests
{
    private static ConflictEntry Conflict(string path) => new(
        path,
        new ConflictDetail(
            ClientSize: 2100000,
            ClientMtimeUtc: new DateTime(2026, 7, 20, 14, 30, 52, DateTimeKind.Utc),
            ServerSize: 2050112,
            ServerMtimeUtc: new DateTime(2026, 7, 20, 14, 31, 10, DateTimeKind.Utc),
            Resurrected: false).Encode(),
        new DateTime(2026, 7, 20, 14, 31, 11, DateTimeKind.Utc));

    private static ConflictEntry Resurrection(string path) => new(
        path,
        new ConflictDetail(
            ClientSize: 1024,
            ClientMtimeUtc: new DateTime(2026, 7, 20, 9, 15, 0, DateTimeKind.Utc),
            ServerSize: 900,
            ServerMtimeUtc: new DateTime(2026, 7, 19, 17, 0, 0, DateTimeKind.Utc),
            Resurrected: true).Encode(),
        new DateTime(2026, 7, 20, 9, 16, 0, DateTimeKind.Utc));

    [Fact]
    public void BuildLines_NothingToReview_ReturnsEmpty()
    {
        var lines = ReviewReport.BuildLines(Array.Empty<ConflictEntry>(), Array.Empty<ConflictEntry>());
        Assert.Empty(lines);
    }

    [Fact]
    public void BuildLines_Conflict_ShowsBothSidesSizeAndMtime()
    {
        var lines = ReviewReport.BuildLines(
            new[] { Conflict("docs/report.docx") },
            Array.Empty<ConflictEntry>());
        var text = string.Join("\n", lines);

        Assert.Contains("[CONFLICT] docs/report.docx", text);
        Assert.Contains("client: 2100000 bytes  2026-07-20 14:30:52Z", text);
        Assert.Contains("server: 2050112 bytes  2026-07-20 14:31:10Z", text);
        Assert.Contains("both copies kept", text);
    }

    [Fact]
    public void BuildLines_Resurrection_ShowsBothSidesAndWhyItSurvived()
    {
        var lines = ReviewReport.BuildLines(
            Array.Empty<ConflictEntry>(),
            new[] { Resurrection("notes/todo.txt") });
        var text = string.Join("\n", lines);

        Assert.Contains("[RESURRECTED] notes/todo.txt", text);
        Assert.Contains("client: 1024 bytes  2026-07-20 09:15:00Z", text);
        Assert.Contains("server: 900 bytes  2026-07-19 17:00:00Z", text);
        Assert.Contains("kept — modified after the peer deleted it", text);
    }

    [Fact]
    public void BuildLines_HeaderCountsBothKinds()
    {
        var lines = ReviewReport.BuildLines(
            new[] { Conflict("a.docx"), Conflict("b.docx") },
            new[] { Resurrection("c.txt") });

        Assert.Contains("3", lines[0]);
        Assert.Contains("Review", lines[0]);
    }

    [Fact]
    public void BuildLines_UnparsableDetail_StillListsTheFile()
    {
        // A detail written by an older build must not vanish from the review — a silently
        // dropped row hides the exact case the review exists to surface.
        var entry = new ConflictEntry("legacy.docx", "kept both copies",
            new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc));

        var text = string.Join("\n", ReviewReport.BuildLines(new[] { entry }, Array.Empty<ConflictEntry>()));

        Assert.Contains("[CONFLICT] legacy.docx", text);
        Assert.Contains("kept both copies", text);
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ReviewReportTests"`
Expected: FAIL — `error CS0103: The name 'ReviewReport' does not exist in the current context`

- [ ] **Step 3: Implement**

`src/RemoteFileSync/Sync/ReviewReport.cs`

```csharp
using System.Globalization;
using RemoteFileSync.Logging;
using RemoteFileSync.Progress;
using RemoteFileSync.State;

namespace RemoteFileSync.Sync;

/// <summary>
/// The end-of-sync review. Anything the sync could not decide on its own — a two-sided
/// conflict where both copies were kept, or a file that survived deletion because the other
/// side edited it — is listed here after the summary so the operator sees it last.
/// </summary>
public static class ReviewReport
{
    private const string ConflictNote     = "both copies kept";
    private const string ResurrectionNote = "kept — modified after the peer deleted it";

    public static IReadOnlyList<string> BuildLines(
        IReadOnlyList<ConflictEntry> conflicts,
        IReadOnlyList<ConflictEntry> resurrections)
    {
        var lines = new List<string>();
        var total = conflicts.Count + resurrections.Count;
        if (total == 0) return lines;

        lines.Add($"Review — {total} item(s) need your attention:");
        foreach (var entry in conflicts)
            AppendItem(lines, "CONFLICT", entry, ConflictNote);
        foreach (var entry in resurrections)
            AppendItem(lines, "RESURRECTED", entry, ResurrectionNote);
        return lines;
    }

    public static void Emit(SyncDatabase? db, long sessionId, SyncLogger logger, JsonProgressWriter progress)
    {
        if (db == null || sessionId <= 0) return;

        var conflicts = db.GetSessionConflicts(sessionId);
        var resurrections = db.GetSessionResurrections(sessionId);
        if (conflicts.Count == 0 && resurrections.Count == 0) return;

        foreach (var line in BuildLines(conflicts, resurrections))
            logger.Summary(line);

        foreach (var entry in conflicts)
            WriteEvent(progress, "conflict", entry);
        foreach (var entry in resurrections)
            WriteEvent(progress, "resurrection", entry);
    }

    private static void AppendItem(List<string> lines, string tag, ConflictEntry entry, string note)
    {
        lines.Add($"  [{tag}] {entry.Path}");
        if (ConflictDetail.TryParse(entry.Detail, out var detail))
        {
            lines.Add($"      client: {detail.ClientSize} bytes  {Stamp(detail.ClientMtimeUtc)}");
            lines.Add($"      server: {detail.ServerSize} bytes  {Stamp(detail.ServerMtimeUtc)}");
        }
        else
        {
            // Detail from a build that predates ConflictDetail: print it raw rather than
            // dropping the row, which would hide the case entirely.
            lines.Add($"      {entry.Detail}");
        }
        lines.Add($"      {note}");
    }

    private static void WriteEvent(JsonProgressWriter progress, string kind, ConflictEntry entry)
    {
        if (!ConflictDetail.TryParse(entry.Detail, out var detail))
        {
            // -1 / empty means "unknown", so the GUI can render the path without inventing a size.
            progress.WriteReview(kind, entry.Path, -1, string.Empty, -1, string.Empty);
            return;
        }

        progress.WriteReview(kind, entry.Path,
            detail.ClientSize, detail.ClientMtimeUtc.ToString("O", CultureInfo.InvariantCulture),
            detail.ServerSize, detail.ServerMtimeUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    // InvariantCulture because ':' is the culture's time separator in a custom format string —
    // a de-DE console would otherwise print 14.30.52.
    private static string Stamp(DateTime utc) =>
        utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
}
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ReviewReportTests"`
Expected: PASS

### Task 8.3: JsonProgressWriter.WriteReview

- [ ] **Step 1: Write the failing test**

Current `tests/RemoteFileSync.Tests/Progress/JsonProgressWriterTests.cs:105-111`:

```csharp
    [Fact]
    public void NullWriter_NoOutput()
    {
        var writer = JsonProgressWriter.Null;
        writer.WriteStatus("connecting");
        writer.WriteComplete(0, 0, 0, 0, 0);
    }
```

Replace with:

```csharp
    [Fact]
    public void WriteReview_Conflict_EmitsBothSidesSizeAndMtime()
    {
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);
        writer.WriteReview("conflict", "docs/report.docx",
            2100000, "2026-07-20T14:30:52.0000000Z",
            2050112, "2026-07-20T14:31:10.0000000Z");
        var json = sw.ToString().Trim();
        var doc = JsonDocument.Parse(json);
        Assert.Equal("review", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("conflict", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("docs/report.docx", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal(2100000, doc.RootElement.GetProperty("client_size").GetInt64());
        Assert.Equal("2026-07-20T14:30:52.0000000Z", doc.RootElement.GetProperty("client_mtime").GetString());
        Assert.Equal(2050112, doc.RootElement.GetProperty("server_size").GetInt64());
        Assert.Equal("2026-07-20T14:31:10.0000000Z", doc.RootElement.GetProperty("server_mtime").GetString());
    }

    [Fact]
    public void WriteReview_Resurrection_EmitsOneLinePerItem()
    {
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);
        writer.WriteReview("resurrection", "notes/todo.txt",
            1024, "2026-07-20T09:15:00.0000000Z",
            900, "2026-07-19T17:00:00.0000000Z");
        writer.WriteReview("conflict", "a.docx",
            1, "2026-07-20T09:15:00.0000000Z",
            2, "2026-07-19T17:00:00.0000000Z");

        var lines = sw.ToString().Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("resurrection", JsonDocument.Parse(lines[0]).RootElement.GetProperty("kind").GetString());
        Assert.Equal("conflict", JsonDocument.Parse(lines[1]).RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public void NullWriter_NoOutput()
    {
        var writer = JsonProgressWriter.Null;
        writer.WriteStatus("connecting");
        writer.WriteComplete(0, 0, 0, 0, 0);
        writer.WriteReview("conflict", "a.docx", 1, "", 2, "");
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~WriteReview"`
Expected: FAIL — `error CS1061: 'JsonProgressWriter' does not contain a definition for 'WriteReview' and no accessible extension method 'WriteReview' accepting a first argument of type 'JsonProgressWriter' could be found`

- [ ] **Step 3: Implement**

Current `src/RemoteFileSync/Progress/JsonProgressWriter.cs:60-68`:

```csharp
    public void WriteComplete(int files_transferred, int files_deleted, long bytes, long elapsed_ms, int exit_code)
    {
        WriteLine(new { @event = "complete", files_transferred, files_deleted, bytes, elapsed_ms, exit_code });
    }

    public void WriteError(string message, bool fatal)
    {
        WriteLine(new { @event = "error", message, fatal });
    }
```

Replace with:

```csharp
    public void WriteComplete(int files_transferred, int files_deleted, long bytes, long elapsed_ms, int exit_code)
    {
        WriteLine(new { @event = "complete", files_transferred, files_deleted, bytes, elapsed_ms, exit_code });
    }

    // One line per reviewed item, like file_end and delete — the GUI's ProgressEvent is a flat
    // bag of nullables and cannot carry a nested array. kind = "conflict" | "resurrection";
    // a size of -1 with an empty mtime means the stored detail could not be parsed.
    public void WriteReview(string kind, string path,
                            long client_size, string client_mtime,
                            long server_size, string server_mtime)
    {
        WriteLine(new { @event = "review", kind, path, client_size, client_mtime, server_size, server_mtime });
    }

    public void WriteError(string message, bool fatal)
    {
        WriteLine(new { @event = "error", message, fatal });
    }
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~WriteReview"`
Expected: PASS

### Task 8.4: ProgressEvent parses the review event

- [ ] **Step 1: Write the failing test**

Current `tests/ExecRFS.Tests/Models/ProgressEventTests.cs:73-80`:

```csharp
    [Fact]
    public void TryParse_ErrorEvent()
    {
        var evt = ProgressEvent.TryParse(@"{""event"":""error"",""message"":""Connection refused"",""fatal"":true}");
        Assert.NotNull(evt);
        Assert.True(evt.Fatal);
    }
}
```

Replace with:

```csharp
    [Fact]
    public void TryParse_ErrorEvent()
    {
        var evt = ProgressEvent.TryParse(@"{""event"":""error"",""message"":""Connection refused"",""fatal"":true}");
        Assert.NotNull(evt);
        Assert.True(evt.Fatal);
    }

    [Fact]
    public void TryParse_ReviewConflictEvent_CarriesBothSides()
    {
        var evt = ProgressEvent.TryParse(
            @"{""event"":""review"",""kind"":""conflict"",""path"":""docs/report.docx""," +
            @"""client_size"":2100000,""client_mtime"":""2026-07-20T14:30:52.0000000Z""," +
            @"""server_size"":2050112,""server_mtime"":""2026-07-20T14:31:10.0000000Z""}");
        Assert.NotNull(evt);
        Assert.Equal("review", evt.Event);
        Assert.Equal("conflict", evt.Kind);
        Assert.Equal("docs/report.docx", evt.Path);
        Assert.Equal(2100000, evt.ClientSize);
        Assert.Equal("2026-07-20T14:30:52.0000000Z", evt.ClientMtime);
        Assert.Equal(2050112, evt.ServerSize);
        Assert.Equal("2026-07-20T14:31:10.0000000Z", evt.ServerMtime);
    }

    [Fact]
    public void TryParse_ReviewResurrectionEvent_UnknownSizesStayNegative()
    {
        // -1 is the CLI's "detail unparsable" sentinel; the GUI must not render it as 0 bytes.
        var evt = ProgressEvent.TryParse(
            @"{""event"":""review"",""kind"":""resurrection"",""path"":""notes/todo.txt""," +
            @"""client_size"":-1,""client_mtime"":"""",""server_size"":-1,""server_mtime"":""""}");
        Assert.NotNull(evt);
        Assert.Equal("resurrection", evt.Kind);
        Assert.Equal(-1, evt.ClientSize);
        Assert.Equal("", evt.ServerMtime);
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~TryParse_Review"`
Expected: FAIL — `error CS1061: 'ProgressEvent' does not contain a definition for 'Kind' and no accessible extension method 'Kind' accepting a first argument of type 'ProgressEvent' could be found`

- [ ] **Step 3: Implement**

Current `src/ExecRFS/Models/ProgressEvent.cs:34-36`:

```csharp
    [JsonPropertyName("error")] public string? Error { get; set; }

    public static ProgressEvent? TryParse(string line)
```

Replace with:

```csharp
    [JsonPropertyName("error")] public string? Error { get; set; }

    // "review" event: one per conflict or resurrection, emitted after "complete".
    // Kind is "conflict" | "resurrection"; a size of -1 means the CLI could not parse the
    // stored detail and the mtime string is empty.
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("client_size")] public long? ClientSize { get; set; }
    [JsonPropertyName("client_mtime")] public string? ClientMtime { get; set; }
    [JsonPropertyName("server_size")] public long? ServerSize { get; set; }
    [JsonPropertyName("server_mtime")] public string? ServerMtime { get; set; }

    public static ProgressEvent? TryParse(string line)
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~TryParse_Review"`
Expected: PASS

### Task 8.5: ReviewReport.Emit end-to-end, wired into SyncClient

- [ ] **Step 1: Write the failing test**

`tests/RemoteFileSync.Tests/Sync/ReviewReportEmitTests.cs`

```csharp
using System.Text.Json;
using RemoteFileSync.Logging;
using RemoteFileSync.Progress;
using RemoteFileSync.State;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class ReviewReportEmitTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly string _logPath;

    public ReviewReportEmitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rfs_review_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "sync.db");
        _logPath = Path.Combine(_tempDir, "sync.log");
    }

    private static string ConflictDetailText() => new ConflictDetail(
        ClientSize: 2100000,
        ClientMtimeUtc: new DateTime(2026, 7, 20, 14, 30, 52, DateTimeKind.Utc),
        ServerSize: 2050112,
        ServerMtimeUtc: new DateTime(2026, 7, 20, 14, 31, 10, DateTimeKind.Utc),
        Resurrected: false).Encode();

    private static string ResurrectionDetailText() => new ConflictDetail(
        ClientSize: 1024,
        ClientMtimeUtc: new DateTime(2026, 7, 20, 9, 15, 0, DateTimeKind.Utc),
        ServerSize: 900,
        ServerMtimeUtc: new DateTime(2026, 7, 19, 17, 0, 0, DateTimeKind.Utc),
        Resurrected: true).Encode();

    [Fact]
    public void Emit_ReadsBackConflictAndResurrectionSeparately()
    {
        // Pins the routing rule LogConflict's frozen single-writer signature forces: a detail
        // starting with "resurrected:" must land in GetSessionResurrections, everything else
        // in GetSessionConflicts. If SyncDatabase discriminates some other way, this fails here
        // rather than silently producing an empty review.
        using var db = new SyncDatabase(_dbPath);
        var sessionId = db.StartSession("two-way", _tempDir, "localhost", 15782);
        db.LogConflict("docs/report.docx", sessionId, ConflictDetailText());
        db.LogConflict("notes/todo.txt", sessionId, ResurrectionDetailText());

        var conflicts = db.GetSessionConflicts(sessionId);
        var resurrections = db.GetSessionResurrections(sessionId);

        Assert.Equal("docs/report.docx", Assert.Single(conflicts).Path);
        Assert.Equal("notes/todo.txt", Assert.Single(resurrections).Path);
    }

    [Fact]
    public void Emit_LogsReviewWithBothSidesAndEmitsOneJsonEventPerItem()
    {
        using var sw = new StringWriter();
        var progress = new JsonProgressWriter(sw);

        using (var db = new SyncDatabase(_dbPath))
        using (var logger = new SyncLogger(verbose: false, logFile: _logPath, suppressConsole: true))
        {
            var sessionId = db.StartSession("two-way", _tempDir, "localhost", 15782);
            db.LogConflict("docs/report.docx", sessionId, ConflictDetailText());
            db.LogConflict("notes/todo.txt", sessionId, ResurrectionDetailText());

            ReviewReport.Emit(db, sessionId, logger, progress);
        }

        var log = File.ReadAllText(_logPath);
        Assert.Contains("Review — 2 item(s) need your attention:", log);
        Assert.Contains("[CONFLICT] docs/report.docx", log);
        Assert.Contains("client: 2100000 bytes  2026-07-20 14:30:52Z", log);
        Assert.Contains("server: 2050112 bytes  2026-07-20 14:31:10Z", log);
        Assert.Contains("[RESURRECTED] notes/todo.txt", log);
        Assert.Contains("client: 1024 bytes  2026-07-20 09:15:00Z", log);
        Assert.Contains("server: 900 bytes  2026-07-19 17:00:00Z", log);

        var events = sw.ToString().Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, events.Length);
        var first = JsonDocument.Parse(events[0]).RootElement;
        Assert.Equal("review", first.GetProperty("event").GetString());
        Assert.Equal("conflict", first.GetProperty("kind").GetString());
        Assert.Equal(2100000, first.GetProperty("client_size").GetInt64());
        var second = JsonDocument.Parse(events[1]).RootElement;
        Assert.Equal("resurrection", second.GetProperty("kind").GetString());
        Assert.Equal("notes/todo.txt", second.GetProperty("path").GetString());
    }

    [Fact]
    public void Emit_CleanSession_PrintsAndEmitsNothing()
    {
        // A quiet sync must stay quiet — an empty "Review" header every run trains the
        // operator to ignore the section.
        using var sw = new StringWriter();
        var progress = new JsonProgressWriter(sw);

        using (var db = new SyncDatabase(_dbPath))
        using (var logger = new SyncLogger(verbose: false, logFile: _logPath, suppressConsole: true))
        {
            var sessionId = db.StartSession("two-way", _tempDir, "localhost", 15782);
            ReviewReport.Emit(db, sessionId, logger, progress);
        }

        Assert.DoesNotContain("Review", File.ReadAllText(_logPath));
        Assert.Equal("", sw.ToString());
    }

    [Fact]
    public void Emit_NullDatabase_DoesNothing()
    {
        // SyncClient runs with _db == null in the binary-state fallback path.
        using var sw = new StringWriter();
        var progress = new JsonProgressWriter(sw);
        using var logger = new SyncLogger(verbose: false, logFile: _logPath, suppressConsole: true);

        ReviewReport.Emit(null, 1, logger, progress);

        Assert.Equal("", sw.ToString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ReviewReportEmitTests"`
Expected: FAIL — `error CS1061: 'SyncDatabase' does not contain a definition for 'LogConflict'` (until the SyncDatabase phase lands). Once it compiles, the remaining failure is the assertion `Assert.Contains("Review — 2 item(s) need your attention:", log)` on an empty log.

- [ ] **Step 3: Implement**

`ReviewReport.Emit` is already written in Task 8.2. This step wires it into the client.

Current `src/RemoteFileSync/Network/SyncClient.cs:478-482`:

```csharp
        var (scType, scData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var deletedLabel = filesDeleted > 0 ? $", {filesDeleted} deleted" : "";
        _logger.Summary($"Sync complete: {filesTransferred} files transferred{deletedLabel}, {bytesTransferred / (1024.0 * 1024.0):F1} MB, {sw.ElapsedMilliseconds}ms");

        // Fallback: save binary state when db is null (backward compat)
```

Replace with:

```csharp
        var (scType, scData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var deletedLabel = filesDeleted > 0 ? $", {filesDeleted} deleted" : "";
        _logger.Summary($"Sync complete: {filesTransferred} files transferred{deletedLabel}, {bytesTransferred / (1024.0 * 1024.0):F1} MB, {sw.ElapsedMilliseconds}ms");

        // Rule [2]: what the sync could not decide for the operator has to be shown, not buried
        // in per-file INF lines they never see. Printed after the summary so it is last on screen.
        ReviewReport.Emit(_db, sessionId, _logger, _progress);

        // Fallback: save binary state when db is null (backward compat)
```

`SyncClient.cs:9` already has `using RemoteFileSync.Sync;`, so `ReviewReport` resolves without a new using.

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ReviewReportEmitTests"`
Expected: PASS

### Phase 8 commit

```bash
git add src/RemoteFileSync/Sync/ConflictDetail.cs \
        src/RemoteFileSync/Sync/ReviewReport.cs \
        src/RemoteFileSync/Progress/JsonProgressWriter.cs \
        src/RemoteFileSync/Network/SyncClient.cs \
        src/ExecRFS/Models/ProgressEvent.cs \
        tests/RemoteFileSync.Tests/Sync/ConflictDetailTests.cs \
        tests/RemoteFileSync.Tests/Sync/ReviewReportTests.cs \
        tests/RemoteFileSync.Tests/Sync/ReviewReportEmitTests.cs \
        tests/RemoteFileSync.Tests/Progress/JsonProgressWriterTests.cs \
        tests/ExecRFS.Tests/Models/ProgressEventTests.cs
git commit -m "feat: end-of-sync review report for conflicts and resurrections

Rule [2] requires the operator to see every case the sync could not decide.
Adds a review section printed after the SyncComplete summary listing each
ConflictKeepBoth and each resurrection with both sides' size and mtime, read
back from SyncDatabase via GetSessionConflicts/GetSessionResurrections.

ConflictDetail defines the LogConflict detail payload CONTRACT.md left open,
and carries the 'resurrected:' prefix that is the only discriminator the frozen
single-writer LogConflict signature leaves room for.

Mirrored as one 'review' JSON progress event per item (flat, like file_end and
delete) so ExecRFS can list them; ProgressEvent gains the matching fields.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
git push -u origin feat/deletion-sync-ancestor-merge
```

**Verification before commit:**
```bash
dotnet build -c Release
dotnet test -c Release
```
Expected: 0 errors. One existing test changes knowingly: `JsonProgressWriterTests.NullWriter_NoOutput` gains a `writer.WriteReview(...)` call so the Null writer is proven to swallow the new event too — no assertion in it was altered. `ProgressEventTests` and `JsonProgressWriterTests` are otherwise append-only. No other existing test changes.

---

## Phase 9: End-to-end acceptance tests and documentation

**Goal:** Prove the ancestor/three-way merge, mirror semantics, convergence and the no-ancestor safety gate over a real loopback socket, migrate the existing integration suite to the new API and archive layout, and document `--mode`, `--mirror`, the archive, protocol v3 and the upgrade path.

**Files:**
- Create: `tests/RemoteFileSync.Tests/Integration/TwoWayMergeE2ETests.cs`
- Modify: `tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs:1-8`, `:52`, `:86`, `:108-114`, `:126`, `:142-144`, `:151-159`, `:181`, `:201`, `:209`, `:230-236`
- Modify: `tests/RemoteFileSync.Tests/Integration/DeleteSyncTests.cs:1-7`, `:53`, `:108-109`, `:159-162`
- Modify: `tests/RemoteFileSync.Tests/Integration/DatabaseDeleteSyncTests.cs:56`
- Modify: `tests/RemoteFileSync.Tests/Integration/DeleteThresholdTests.cs:50-54`, `:73-85`
- Modify: `README.md:36-41`, `:52`, `:56`, `:102-104`, `:108-115`, `:135-141`
- Test: `tests/RemoteFileSync.Tests/Integration/TwoWayMergeE2ETests.cs`

**Interfaces:**
- Consumes: `SyncMode` (`Push`/`Pull`/`TwoWay`); `SyncOptions.Mode`, `SyncOptions.MirrorDeletes`, `SyncOptions.Bidirectional` (get-only); `SyncDatabase.GetRow(string path)`, `SyncDatabase.UpsertSynced(string, long, long, long, long, long, string)`, `SyncDatabase.GetSessionConflicts(long)`, `SyncDatabase.GetSessionResurrections(long)`, `SyncDatabase.GetRecentSessions(int)`, `SyncDatabase.StartSession(string, string, string, int)`, `SyncDatabase.CompleteSession(long, int, int, int, int)`; `AncestorRow.Status`, `AncestorRow.DeletedUtcTicks`; `ConflictEntry.Path`; `PairMarker.Exists(string)`, `PairMarker.Write(string)`; `ArchiveReason.Deleted/Overwritten/Conflict`; `SyncClient(SyncOptions, SyncLogger, SyncStateManager?, JsonProgressWriter?, StdinCommandReader?, SyncDatabase?)`; `SyncServer(SyncOptions, SyncLogger)`.
- Produces: nothing consumed by a later phase. This is the terminal phase.

> **Stated deviation from strict TDD, and why.** Tasks 9.2–9.8 are *acceptance* tests over behaviour phases 1–8 already implemented; writing them red first would mean reverting those phases. They therefore use a two-step rhythm — write, then run — and each Step 2 names the concrete diagnostic that identifies *which* earlier phase is wrong if it fails. Task 9.1 is genuinely red-first: the suite does not compile at the start of this phase, because Phase 3 removed the `Bidirectional` setter and Phase 6 replaced `BackupManager` with `ArchiveManager`.

---

### Task 9.1: Migrate the existing integration suite to `SyncMode` and the archive layout

- [ ] **Step 1: Run the existing suite and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Integration"`

Expected: FAIL — compile errors, one per assignment site:
```
error CS0200: Property or indexer 'SyncOptions.Bidirectional' cannot be assigned to -- it is read only
```
at `EndToEndTests.cs:52`, `:86`, `:126`, `:158`, `DeleteSyncTests.cs:53`, `DatabaseDeleteSyncTests.cs:56`, `DeleteThresholdTests.cs:53`.

- [ ] **Step 2: Fix the call sites and the archive assertions**

`EndToEndTests.cs:1-8` — current:

```csharp
using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;

namespace RemoteFileSync.Tests.Integration;
```

replacement:

```csharp
using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Backup;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;

namespace RemoteFileSync.Tests.Integration;
```

`EndToEndTests.cs:52` — current:

```csharp
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Bidirectional = false };
```

replacement:

```csharp
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Mode = SyncMode.Push };
```

`EndToEndTests.cs:86` and `:126` — current (identical text at both lines):

```csharp
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Bidirectional = true };
```

replacement (both lines):

```csharp
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Mode = SyncMode.TwoWay };
```

`EndToEndTests.cs:108-114` — current:

```csharp
        // Server's old shared.txt should be backed up — beside the sync folder, not inside it.
        var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
        var backupPath = Path.Combine(_testRoot, ".rfs-backups-server", dateStr, "shared.txt");
        Assert.True(File.Exists(backupPath), $"expected backup at {backupPath}");
        Assert.Equal("server older", File.ReadAllText(backupPath));
        // And nothing may have been written inside the sync folder itself.
        Assert.False(Directory.Exists(Path.Combine(_serverDir, dateStr)));
```

replacement:

```csharp
        // Server's old shared.txt must be archived — beside the sync folder, not inside it.
        var archived = AssertArchived(Path.Combine(_testRoot, ".rfs-archive-server"),
            ArchiveReason.Overwritten, "shared.txt");
        Assert.Equal("server older", File.ReadAllText(archived));
        // And nothing may have been written inside the sync folder itself.
        Assert.Empty(Directory.GetDirectories(_serverDir));
```

`EndToEndTests.cs:142-144` — current:

```csharp
        var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
        Assert.False(Directory.Exists(Path.Combine(_serverDir, dateStr)));
        Assert.False(Directory.Exists(Path.Combine(_clientDir, dateStr)));
```

replacement:

```csharp
        // Nothing was replaced, so nothing may be archived and no session folder may appear
        // inside either sync folder.
        AssertNothingArchived(Path.Combine(_testRoot, ".rfs-archive-server"));
        AssertNothingArchived(Path.Combine(_testRoot, ".rfs-archive-client"));
        Assert.Empty(Directory.GetDirectories(_serverDir));
        Assert.Empty(Directory.GetDirectories(_clientDir));
```

`EndToEndTests.cs:151-159` — current:

```csharp
    private async Task<(int client, int server)> RunSyncAsync(bool bidirectional)
    {
        int port = GetFreePort();
        var serverOpts = new SyncOptions { IsServer = true, Once = true, Port = port, Folder = _serverDir };
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port,
            Folder = _clientDir, Bidirectional = bidirectional
        };
```

replacement:

```csharp
    private async Task<(int client, int server)> RunSyncAsync(SyncMode mode)
    {
        int port = GetFreePort();
        var serverOpts = new SyncOptions { IsServer = true, Once = true, Port = port, Folder = _serverDir };
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port,
            Folder = _clientDir, Mode = mode
        };
```

`EndToEndTests.cs:181` — current:

```csharp
        await RunSyncAsync(bidirectional: false);
```

replacement:

```csharp
        await RunSyncAsync(SyncMode.Push);
```

`EndToEndTests.cs:201` — current:

```csharp
        var first = await RunSyncAsync(bidirectional: true);
```

replacement:

```csharp
        var first = await RunSyncAsync(SyncMode.TwoWay);
```

`EndToEndTests.cs:209` — current:

```csharp
        var second = await RunSyncAsync(bidirectional: true);
```

replacement:

```csharp
        var second = await RunSyncAsync(SyncMode.TwoWay);
```

`EndToEndTests.cs:230` — current:

```csharp
        await RunSyncAsync(bidirectional: true);
        var clientStamp = File.GetLastWriteTimeUtc(Path.Combine(_clientDir, "shared.txt"));
```

replacement:

```csharp
        await RunSyncAsync(SyncMode.TwoWay);
        var clientStamp = File.GetLastWriteTimeUtc(Path.Combine(_clientDir, "shared.txt"));
```

`EndToEndTests.cs:235-236` — current:

```csharp
        await RunSyncAsync(bidirectional: true);
        await RunSyncAsync(bidirectional: true);
```

replacement:

```csharp
        await RunSyncAsync(SyncMode.TwoWay);
        await RunSyncAsync(SyncMode.TwoWay);
```

`EndToEndTests.cs:243-250` — the two archive helpers go beside `GetFreePort`. Current:

```csharp
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
```

replacement:

```csharp
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Asserts exactly one archived copy of <paramref name="fileName"/> exists under the
    /// expected reason folder and returns its path. The session folder is stamped
    /// yyyyMMdd-HHmmss at sync start, so a test cannot spell it out.
    /// </summary>
    private static string AssertArchived(string archiveRoot, ArchiveReason reason, string fileName)
    {
        Assert.True(Directory.Exists(archiveRoot), $"no archive root at {archiveRoot}");
        var hits = Directory.GetFiles(archiveRoot, fileName, SearchOption.AllDirectories);
        Assert.Single(hits);
        var segment = $"{Path.DirectorySeparatorChar}{reason.ToString().ToLowerInvariant()}{Path.DirectorySeparatorChar}";
        Assert.Contains(segment, hits[0]);
        return hits[0];
    }

    /// <summary>
    /// An absent archive root and an empty one are equally acceptable: ArchiveManager may
    /// create its root eagerly, so only the file count is a real signal.
    /// </summary>
    private static void AssertNothingArchived(string archiveRoot)
    {
        if (!Directory.Exists(archiveRoot)) return;
        Assert.Empty(Directory.GetFiles(archiveRoot, "*", SearchOption.AllDirectories));
    }
```

`DeleteSyncTests.cs:1-7` — current:

```csharp
using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.State;
```

replacement:

```csharp
using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Backup;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.State;
```

`DeleteSyncTests.cs:53` — current:

```csharp
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Bidirectional = bidirectional, DeleteEnabled = deleteEnabled };
```

replacement:

```csharp
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Mode = bidirectional ? SyncMode.TwoWay : SyncMode.Push, DeleteEnabled = deleteEnabled };
```

`DeleteSyncTests.cs:108-109` — current:

```csharp
        var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
        Assert.True(File.Exists(Path.Combine(_testRoot, ".rfs-backups-server", dateStr, "to-delete.txt")));
```

replacement:

```csharp
        AssertArchived(Path.Combine(_testRoot, ".rfs-archive-server"), ArchiveReason.Deleted, "to-delete.txt");
```

`DeleteSyncTests.cs:159-162` — current:

```csharp
        // Both files should be backed up before deletion
        var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
        Assert.True(File.Exists(Path.Combine(_testRoot, ".rfs-backups-server", dateStr, "client-deleted.txt")));
        Assert.True(File.Exists(Path.Combine(_testRoot, ".rfs-backups-client", dateStr, "server-deleted.txt")));
```

replacement:

```csharp
        // Both files must be archived before deletion, on the side that lost them.
        AssertArchived(Path.Combine(_testRoot, ".rfs-archive-server"), ArchiveReason.Deleted, "client-deleted.txt");
        AssertArchived(Path.Combine(_testRoot, ".rfs-archive-client"), ArchiveReason.Deleted, "server-deleted.txt");
```

`DeleteSyncTests.cs:41-48` — the archive helper goes beside `GetFreePort`. Current:

```csharp
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
```

replacement:

```csharp
    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Asserts exactly one archived copy of <paramref name="fileName"/> exists under the
    /// expected reason folder. The session folder is stamped yyyyMMdd-HHmmss at sync start,
    /// so a test cannot spell it out.
    /// </summary>
    private static string AssertArchived(string archiveRoot, ArchiveReason reason, string fileName)
    {
        Assert.True(Directory.Exists(archiveRoot), $"no archive root at {archiveRoot}");
        var hits = Directory.GetFiles(archiveRoot, fileName, SearchOption.AllDirectories);
        Assert.Single(hits);
        var segment = $"{Path.DirectorySeparatorChar}{reason.ToString().ToLowerInvariant()}{Path.DirectorySeparatorChar}";
        Assert.Contains(segment, hits[0]);
        return hits[0];
    }
```

`DatabaseDeleteSyncTests.cs:56` — current:

```csharp
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Bidirectional = bidirectional, DeleteEnabled = deleteEnabled };
```

replacement:

```csharp
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Mode = bidirectional ? SyncMode.TwoWay : SyncMode.Push, DeleteEnabled = deleteEnabled };
```

`DeleteThresholdTests.cs:50-54` — current:

```csharp
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir,
            Bidirectional = true, DeleteEnabled = true, ForceDelete = forceDelete,
        };
```

replacement:

```csharp
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir,
            Mode = SyncMode.TwoWay, DeleteEnabled = true, ForceDelete = forceDelete,
        };
```

`DeleteThresholdTests.cs:73-85` — current:

```csharp
    private SyncDatabase SeedTrackedFiles(int count)
    {
        var db = new SyncDatabase(Path.Combine(_stateDir, "state.db"));
        var session = db.StartSession("bidi+delete", _clientDir, "127.0.0.1", 1234);
        for (int i = 0; i < count; i++)
        {
            var name = $"file{i:D3}.txt";
            File.WriteAllText(Path.Combine(_clientDir, name), $"content {i}");
            db.MarkSynced(name, 9, DateTime.UtcNow.AddDays(-1), session, "to_server");
        }
        db.CompleteSession(session, count, 0, 0, 0);
        return db;
    }
```

replacement:

```csharp
    private SyncDatabase SeedTrackedFiles(int count)
    {
        var dbPath = Path.Combine(_stateDir, "state.db");
        var db = new SyncDatabase(dbPath);
        var session = db.StartSession("two-way+delete", _clientDir, "127.0.0.1", 1234);
        var mtime = DateTime.UtcNow.AddDays(-1).Ticks;
        for (int i = 0; i < count; i++)
        {
            var name = $"file{i:D3}.txt";
            var text = $"content {i}";
            File.WriteAllText(Path.Combine(_clientDir, name), text);
            // Both sides recorded identical and unchanged, so the threshold guard is what fires
            // on the emptied server folder — not a bogus size/mtime mismatch. The old seed
            // hardcoded size 9, which stopped matching at file010.txt.
            db.UpsertSynced(name, text.Length, mtime, text.Length, mtime, session, "to_server");
        }
        db.CompleteSession(session, count, 0, 0, 0);
        // Without the marker the no-ancestor gate would abort first and this test would pass
        // for the wrong reason.
        PairMarker.Write(dbPath);
        return db;
    }
```

- [ ] **Step 3: Run the migrated suite and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Integration"`
Expected: PASS

---

### Task 9.2: Two-way — a client delete removes the server copy and tombstones the row

- [ ] **Step 1: Write the test (and the shared harness)**

Create `tests/RemoteFileSync.Tests/Integration/TwoWayMergeE2ETests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using RemoteFileSync.Backup;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.Integration;

/// <summary>
/// Acceptance tests for the ancestor-based merge, over a real loopback socket. These exist
/// because every unit-level merge bug this redesign fixed presented in the field as data loss
/// across a full client/server round trip, not as a wrong return value.
/// </summary>
public class TwoWayMergeE2ETests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _serverDir;
    private readonly string _clientDir;
    private readonly string _dbDir;

    public TwoWayMergeE2ETests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"rfs_merge_e2e_{Guid.NewGuid()}");
        _serverDir = Path.Combine(_testRoot, "server");
        _clientDir = Path.Combine(_testRoot, "client");
        _dbDir = Path.Combine(_testRoot, "db");
        Directory.CreateDirectory(_serverDir);
        Directory.CreateDirectory(_clientDir);
        Directory.CreateDirectory(_dbDir);
    }

    public void Dispose()
    {
        // SQLite keeps the file handle in a pool; without this the temp tree cannot be deleted.
        SqliteConnection.ClearAllPools();
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

    /// <summary>
    /// One full client/server sync. Once=true on the server or the test hangs waiting for a
    /// second connection that never arrives.
    /// </summary>
    private async Task<(int clientResult, int serverResult)> RunSyncAsync(
        SyncMode mode, bool deleteEnabled = true, bool mirror = false, SyncDatabase? db = null)
    {
        int port = GetFreePort();
        var serverOpts = new SyncOptions
        {
            IsServer = true, Once = true, Port = port, Folder = _serverDir,
            DeleteEnabled = deleteEnabled,
        };
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir,
            Mode = mode, DeleteEnabled = deleteEnabled, MirrorDeletes = mirror,
        };

        using var serverLogger = new SyncLogger(false, null);
        using var clientLogger = new SyncLogger(false, null);
        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger, db: db);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(500);
        var clientResult = await client.RunAsync(cts.Token);

        // A safety guard aborts the client mid-session, which tears the socket down under the
        // server too. Its exit code is not the subject of those tests.
        int serverResult;
        try { serverResult = await serverTask; } catch { serverResult = -1; }
        return (clientResult, serverResult);
    }

    /// <summary>
    /// Asserts exactly one archived copy of <paramref name="fileName"/> exists under the
    /// expected reason folder and returns its path. The session folder is stamped
    /// yyyyMMdd-HHmmss at sync start, so a test cannot spell it out.
    /// </summary>
    private static string AssertArchived(string archiveRoot, ArchiveReason reason, string fileName)
    {
        Assert.True(Directory.Exists(archiveRoot), $"no archive root at {archiveRoot}");
        var hits = Directory.GetFiles(archiveRoot, fileName, SearchOption.AllDirectories);
        Assert.Single(hits);
        var segment = $"{Path.DirectorySeparatorChar}{reason.ToString().ToLowerInvariant()}{Path.DirectorySeparatorChar}";
        Assert.Contains(segment, hits[0]);
        return hits[0];
    }

    [Fact]
    public async Task TwoWay_ClientDelete_RemovesServerCopyAndTombstonesRow()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "gone.txt", "bye", ts);
        CreateFileWithTimestamp(_clientDir, "stay.txt", "keep", ts);

        var dbPath = Path.Combine(_dbDir, "sync.db");
        using (var db = new SyncDatabase(dbPath))
            await RunSyncAsync(SyncMode.TwoWay, db: db);

        Assert.True(File.Exists(Path.Combine(_serverDir, "gone.txt")));

        File.Delete(Path.Combine(_clientDir, "gone.txt"));

        using (var db = new SyncDatabase(dbPath))
        {
            var (clientResult, _) = await RunSyncAsync(SyncMode.TwoWay, db: db);
            Assert.Equal(0, clientResult);

            var row = db.GetRow("gone.txt");
            Assert.NotNull(row);
            Assert.Equal("deleted", row!.Status);
            // A tombstone without a timestamp can never be purged and the table grows forever.
            Assert.NotNull(row.DeletedUtcTicks);
            Assert.Equal("exists", db.GetRow("stay.txt")!.Status);
        }

        Assert.False(File.Exists(Path.Combine(_serverDir, "gone.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "stay.txt")));
        AssertArchived(Path.Combine(_testRoot, ".rfs-archive-server"), ArchiveReason.Deleted, "gone.txt");
    }
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test -c Release --filter "FullyQualifiedName~TwoWay_ClientDelete_RemovesServerCopyAndTombstonesRow"`
Expected: PASS.

If it fails, the message names the broken phase:
- `Assert.False() Failure` on `_serverDir/gone.txt` — `ComputePlan` did not emit `DeleteOnServer` for the `present/absent, C unchanged` row (Phase: SyncEngine two-way table).
- `Assert.Equal() Failure: Expected "deleted", Actual "exists"` — `SyncClient` did not call `SyncDatabase.Tombstone` after applying the delete (Phase: client/DB wiring).
- `Assert.Single() Failure` inside `AssertArchived` — the server deleted without archiving (Phase: ArchiveManager).

---

### Task 9.3: Two-way — a client delete loses to a server edit (rule [2]) and is reported

- [ ] **Step 1: Write the test**

Append to `tests/RemoteFileSync.Tests/Integration/TwoWayMergeE2ETests.cs`:

```csharp
    [Fact]
    public async Task TwoWay_ClientDeleteVsServerEdit_RestoresFileAndLogsResurrection()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var later = new DateTime(2026, 3, 27, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "contested.txt", "original", ts);

        var dbPath = Path.Combine(_dbDir, "sync.db");
        using (var db = new SyncDatabase(dbPath))
            await RunSyncAsync(SyncMode.TwoWay, db: db);

        // Rule [2]: an edit outranks a deletion. Deleting the peer's newer work because the
        // local copy vanished is the single most destructive outcome this design prevents.
        File.Delete(Path.Combine(_clientDir, "contested.txt"));
        CreateFileWithTimestamp(_serverDir, "contested.txt", "server edited it", later);

        using (var db = new SyncDatabase(dbPath))
        {
            var (clientResult, _) = await RunSyncAsync(SyncMode.TwoWay, db: db);
            Assert.Equal(0, clientResult);

            // The restore is surprising to the user, so it must surface in the review report
            // rather than happening silently.
            var sessionId = db.GetRecentSessions(1).First().Id;
            Assert.Contains(db.GetSessionResurrections(sessionId),
                r => r.Path.Equals("contested.txt", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("exists", db.GetRow("contested.txt")!.Status);
        }

        Assert.True(File.Exists(Path.Combine(_clientDir, "contested.txt")));
        Assert.Equal("server edited it", File.ReadAllText(Path.Combine(_clientDir, "contested.txt")));
        Assert.Equal("server edited it", File.ReadAllText(Path.Combine(_serverDir, "contested.txt")));
    }
```

- [ ] **Step 2: Run the test**

Run: `dotnet test -c Release --filter "FullyQualifiedName~TwoWay_ClientDeleteVsServerEdit_RestoresFileAndLogsResurrection"`
Expected: PASS.

If it fails:
- `Assert.True() Failure` on `_clientDir/contested.txt` — `ComputePlan` emitted `DeleteOnServer` instead of `SendToClient` for `absent/present, S changed` (Phase: SyncEngine two-way table).
- `Assert.Contains() Failure` on the resurrection list — the plan was right but no `'resurrected'` row was written (Phase: client/DB wiring).

---

### Task 9.4: Two-way — edits on both sides keep both copies, loser renamed

- [ ] **Step 1: Write the test**

Append to `tests/RemoteFileSync.Tests/Integration/TwoWayMergeE2ETests.cs`:

```csharp
    [Fact]
    public async Task TwoWay_EditBothSides_KeepsBothCopiesWithRenamedLoser()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "shared.txt", "original", ts);

        var dbPath = Path.Combine(_dbDir, "sync.db");
        using (var db = new SyncDatabase(dbPath))
            await RunSyncAsync(SyncMode.TwoWay, db: db);

        CreateFileWithTimestamp(_clientDir, "shared.txt", "client edit",
            new DateTime(2026, 3, 27, 9, 0, 0, DateTimeKind.Utc));
        CreateFileWithTimestamp(_serverDir, "shared.txt", "server edit",
            new DateTime(2026, 3, 27, 11, 0, 0, DateTimeKind.Utc));

        using (var db = new SyncDatabase(dbPath))
        {
            var (clientResult, _) = await RunSyncAsync(SyncMode.TwoWay, db: db);
            Assert.Equal(0, clientResult);

            var sessionId = db.GetRecentSessions(1).First().Id;
            Assert.Contains(db.GetSessionConflicts(sessionId),
                c => c.Path.Equals("shared.txt", StringComparison.OrdinalIgnoreCase));
        }

        // Neither edit may be lost. Each side ends with the winner under the original name and
        // the loser beside it — picking a winner and discarding the other is silent data loss.
        foreach (var dir in new[] { _clientDir, _serverDir })
        {
            var conflicts = Directory.GetFiles(dir, "shared.conflict-*.txt");
            Assert.Single(conflicts);
            Assert.Matches(@"shared\.conflict-\d{8}-\d{6}-(client|server)\.txt$", conflicts[0]);

            var contents = new[]
            {
                File.ReadAllText(Path.Combine(dir, "shared.txt")),
                File.ReadAllText(conflicts[0]),
            };
            Assert.Contains("client edit", contents);
            Assert.Contains("server edit", contents);
        }
    }
```

- [ ] **Step 2: Run the test**

Run: `dotnet test -c Release --filter "FullyQualifiedName~TwoWay_EditBothSides_KeepsBothCopiesWithRenamedLoser"`
Expected: PASS.

If it fails:
- `Assert.Single() Failure: The collection was empty` on `shared.conflict-*.txt` — `ComputePlan` resolved `yes/yes` by newest-wins instead of `ConflictKeepBoth` (Phase: SyncEngine two-way table).
- `Assert.Matches() Failure` — the rename does not follow `{name}.conflict-{yyyyMMdd-HHmmss}-{side}{ext}` (Phase: conflict rename).
- `Assert.Contains() Failure` on `contents` — the conflict file was written but one edit was still overwritten (Phase: apply order).

---

### Task 9.5: Push mode — a server-only file survives without `--mirror`, dies with it

- [ ] **Step 1: Write the tests**

Append to `tests/RemoteFileSync.Tests/Integration/TwoWayMergeE2ETests.cs`:

```csharp
    [Fact]
    public async Task Push_ServerOnlyFile_SurvivesWithoutMirror()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "pushed.txt", "from client", ts);
        CreateFileWithTimestamp(_serverDir, "server-only.txt", "server keeps this", ts);

        using var db = new SyncDatabase(Path.Combine(_dbDir, "sync.db"));
        var (clientResult, _) = await RunSyncAsync(SyncMode.Push, deleteEnabled: true, mirror: false, db: db);

        Assert.Equal(0, clientResult);
        Assert.True(File.Exists(Path.Combine(_serverDir, "pushed.txt")));
        // No ancestor row ever said the client had this file, so its absence on the client is
        // not evidence of a deletion. Deleting it would destroy files the client never knew about.
        Assert.True(File.Exists(Path.Combine(_serverDir, "server-only.txt")));
    }

    [Fact]
    public async Task Push_Mirror_DeletesServerOnlyFile()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "pushed.txt", "from client", ts);
        CreateFileWithTimestamp(_serverDir, "server-only.txt", "server loses this", ts);

        using var db = new SyncDatabase(Path.Combine(_dbDir, "sync.db"));
        var (clientResult, _) = await RunSyncAsync(SyncMode.Push, deleteEnabled: true, mirror: true, db: db);

        Assert.Equal(0, clientResult);
        Assert.True(File.Exists(Path.Combine(_serverDir, "pushed.txt")));
        // --mirror is the explicit "make the peer identical" opt-in: history no longer matters.
        Assert.False(File.Exists(Path.Combine(_serverDir, "server-only.txt")));
        AssertArchived(Path.Combine(_testRoot, ".rfs-archive-server"), ArchiveReason.Deleted, "server-only.txt");
    }
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Push_ServerOnlyFile_SurvivesWithoutMirror|FullyQualifiedName~Push_Mirror_DeletesServerOnlyFile"`
Expected: PASS (2 tests).

If `Push_ServerOnlyFile_SurvivesWithoutMirror` fails on `Assert.True`, the Push table is deleting on `client absent, server present` without an ancestor row — the exact bug `--mirror` exists to gate. If `Push_Mirror_DeletesServerOnlyFile` fails on `Assert.False`, `MirrorDeletes` is not reaching the plan (check handshake bit 3).

---

### Task 9.6: Pull mode — a client-only file survives without `--mirror`, dies with it

- [ ] **Step 1: Write the tests**

Append to `tests/RemoteFileSync.Tests/Integration/TwoWayMergeE2ETests.cs`:

```csharp
    [Fact]
    public async Task Pull_ClientOnlyFile_SurvivesWithoutMirror()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_serverDir, "pulled.txt", "from server", ts);
        CreateFileWithTimestamp(_clientDir, "client-only.txt", "client keeps this", ts);

        using var db = new SyncDatabase(Path.Combine(_dbDir, "sync.db"));
        var (clientResult, _) = await RunSyncAsync(SyncMode.Pull, deleteEnabled: true, mirror: false, db: db);

        Assert.Equal(0, clientResult);
        Assert.True(File.Exists(Path.Combine(_clientDir, "pulled.txt")));
        // Exact mirror of the Push case: no ancestor row, so no evidence of a deletion.
        Assert.True(File.Exists(Path.Combine(_clientDir, "client-only.txt")));
    }

    [Fact]
    public async Task Pull_Mirror_DeletesClientOnlyFile()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_serverDir, "pulled.txt", "from server", ts);
        CreateFileWithTimestamp(_clientDir, "client-only.txt", "client loses this", ts);

        using var db = new SyncDatabase(Path.Combine(_dbDir, "sync.db"));
        var (clientResult, _) = await RunSyncAsync(SyncMode.Pull, deleteEnabled: true, mirror: true, db: db);

        Assert.Equal(0, clientResult);
        Assert.True(File.Exists(Path.Combine(_clientDir, "pulled.txt")));
        Assert.False(File.Exists(Path.Combine(_clientDir, "client-only.txt")));
        AssertArchived(Path.Combine(_testRoot, ".rfs-archive-client"), ArchiveReason.Deleted, "client-only.txt");
    }
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Pull_ClientOnlyFile_SurvivesWithoutMirror|FullyQualifiedName~Pull_Mirror_DeletesClientOnlyFile"`
Expected: PASS (2 tests).

If `Pull_ClientOnlyFile_SurvivesWithoutMirror` fails, Pull is not the exact mirror of Push. If `Pull_Mirror_DeletesClientOnlyFile` archives under `.rfs-archive-server`, the deleting side is archiving to the wrong root.

---

### Task 9.7: Convergence — runs 2 and 3 transfer nothing and delete nothing

- [ ] **Step 1: Write the test**

Append to `tests/RemoteFileSync.Tests/Integration/TwoWayMergeE2ETests.cs`:

```csharp
    [Fact]
    public async Task ThreeIdenticalRuns_Converge_NoTransfersOrDeletesAfterTheFirst()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "a.txt", "alpha", ts);
        CreateFileWithTimestamp(_clientDir, Path.Combine("sub", "b.txt"), "bravo", ts);
        CreateFileWithTimestamp(_serverDir, "c.txt", "charlie", ts);

        using var db = new SyncDatabase(Path.Combine(_dbDir, "sync.db"));
        for (int i = 0; i < 3; i++)
        {
            var (clientResult, _) = await RunSyncAsync(SyncMode.TwoWay, db: db);
            Assert.Equal(0, clientResult);
        }

        // GetRecentSessions is newest-first: [0] = run 3, [1] = run 2. A merge that keeps
        // re-sending or re-deleting the same files never settles — that ping-pong is invisible
        // to a per-file assertion but obvious in the session counters.
        var sessions = db.GetRecentSessions(3).ToList();
        Assert.Equal(3, sessions.Count);
        Assert.Equal(0, sessions[0].FilesTransferred);
        Assert.Equal(0, sessions[0].FilesDeleted);
        Assert.Equal(0, sessions[1].FilesTransferred);
        Assert.Equal(0, sessions[1].FilesDeleted);

        // And the tree really did converge.
        Assert.True(File.Exists(Path.Combine(_serverDir, "a.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "sub", "b.txt")));
        Assert.True(File.Exists(Path.Combine(_clientDir, "c.txt")));
    }
```

- [ ] **Step 2: Run the test**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ThreeIdenticalRuns_Converge_NoTransfersOrDeletesAfterTheFirst"`
Expected: PASS.

If `sessions[1].FilesTransferred` is non-zero, `UpsertSynced` is recording an mtime that `ChangeDetector.Unchanged` then rejects on the next run — usually receive-time instead of source-time, or ticks stored at the wrong precision. If `FilesDeleted` is non-zero, run 2 is tombstoning rows for files that are still present on both sides.

---

### Task 9.8: The no-ancestor gate — a lost database with a surviving `pair.marker` aborts

- [ ] **Step 1: Write the tests**

Append to `tests/RemoteFileSync.Tests/Integration/TwoWayMergeE2ETests.cs`:

```csharp
    /// <summary>
    /// Deletes the database file and its WAL sidecars, leaving pair.marker in place. This is
    /// how the loss presents in the field: a restored profile, or a cleaned %LOCALAPPDATA%,
    /// takes sync.db but the next run recreates an empty one beside the surviving marker.
    /// </summary>
    private void LoseDatabaseKeepMarker(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        File.Delete(dbPath);
        foreach (var sidecar in Directory.GetFiles(_dbDir, "sync.db-*"))
            File.Delete(sidecar);
        Assert.True(PairMarker.Exists(dbPath));
    }

    [Fact]
    public async Task LostDatabase_WithSurvivingPairMarker_AbortsWithoutDeleting()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "one.txt", "1", ts);
        CreateFileWithTimestamp(_clientDir, "two.txt", "2", ts);

        var dbPath = Path.Combine(_dbDir, "sync.db");
        using (var db = new SyncDatabase(dbPath))
            await RunSyncAsync(SyncMode.TwoWay, db: db);

        // A successful first run claims the pair.
        Assert.True(PairMarker.Exists(dbPath));

        LoseDatabaseKeepMarker(dbPath);

        using (var db = new SyncDatabase(dbPath))
        {
            var (clientResult, _) = await RunSyncAsync(SyncMode.TwoWay, db: db);
            // An empty ancestor table plus a marker means "state lost", not "nothing was ever
            // synced". Treating it as a first run would delete every file on both peers.
            Assert.Equal(4, clientResult);
        }

        Assert.True(File.Exists(Path.Combine(_clientDir, "one.txt")));
        Assert.True(File.Exists(Path.Combine(_clientDir, "two.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "one.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "two.txt")));
    }

    [Fact]
    public async Task LostDatabase_WithMirror_ProceedsInsteadOfAborting()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "one.txt", "1", ts);

        var dbPath = Path.Combine(_dbDir, "sync.db");
        using (var db = new SyncDatabase(dbPath))
            await RunSyncAsync(SyncMode.TwoWay, db: db);

        LoseDatabaseKeepMarker(dbPath);

        using (var db = new SyncDatabase(dbPath))
        {
            // --mirror is the documented escape hatch: the user has declared which side is
            // authoritative, so missing history is no longer a reason to refuse.
            var (clientResult, _) = await RunSyncAsync(SyncMode.TwoWay, mirror: true, db: db);
            Assert.NotEqual(4, clientResult);
        }

        Assert.True(File.Exists(Path.Combine(_clientDir, "one.txt")));
    }
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test -c Release --filter "FullyQualifiedName~LostDatabase_WithSurvivingPairMarker_AbortsWithoutDeleting|FullyQualifiedName~LostDatabase_WithMirror_ProceedsInsteadOfAborting"`
Expected: PASS (2 tests).

> **Precondition this test depends on — verify it in the gate implementation before running.** `Program.cs:65` opens `new SyncDatabase(dbPath)` *before* constructing the client, and that constructor recreates a missing file. The gate can therefore never observe "file absent" at the point the client runs; it must fire on **an empty ancestor table while `PairMarker.Exists(dbPath)` is true**, which is the same runtime condition the contract's `absent + present` row describes. If the gate was implemented as a `File.Exists` check inside `Program`, this test fails with `Assert.Equal() Failure: Expected 4, Actual 0` and the gate must move to the emptiness check — otherwise the real-world case (empty db recreated beside a surviving marker) is not covered at all.

---

### Task 9.9: Document modes, mirror, archive, protocol v3 and the upgrade path

- [ ] **Step 1: Rewrite the affected README sections**

`README.md:36-41` — current:

````markdown
On the other machine (the client):

```bash
RemoteFileSync.exe client --host 10.0.1.50 --folder "C:\Local" --bidirectional
```

Without `--bidirectional` the sync is one-way: the client pushes to the server.
````

replacement:

````markdown
On the other machine (the client):

```bash
RemoteFileSync.exe client --host 10.0.1.50 --folder "C:\Local" --mode two-way
```

`--mode` selects which side is authoritative, and defaults to `push`:

| Mode | Meaning |
|---|---|
| `push` | Client → server. The server is made to match the client; the client is never written to. |
| `pull` | Server → client. The exact mirror of `push`. |
| `two-way` | Both sides converge, using the ancestor table to tell an edit apart from a deletion. |

`--bidirectional` / `-b` is still accepted as a deprecated alias for `--mode two-way`.
````

`README.md:52` — current:

```markdown
| `--bidirectional` | `-b` | off | Sync both directions rather than client → server only |
```

replacement:

```markdown
| `--mode <push\|pull\|two-way>` | — | `push` | Which side is authoritative — see Quick start |
| `--bidirectional` | `-b` | off | Deprecated alias for `--mode two-way` |
| `--mirror` | — | off | Let deletions propagate even for files with no sync history. Destructive — see Safety behaviour |
```

`README.md:56` — current:

```markdown
| `--backup-folder <path>` | — | `.rfs-backups-NAME` beside the sync folder | Where replaced and deleted files are kept. **Must be outside the sync folder** |
```

replacement:

```markdown
| `--backup-folder <path>` | — | `.rfs-backups-NAME` beside the sync folder | Legacy backup location. **Must be outside the sync folder** |
| `--archive-folder <path>` | — | `.rfs-archive-NAME` beside the sync folder | Where replaced, deleted and conflicted files are kept. **Must be outside the sync folder** |
| `--archive-keep-days <n>` | — | `30` | Prune archive sessions older than this. `0` keeps them forever |
| `--archive-max-size <n>` | — | `0` (off) | Cap the total archive size; accepts `K`/`M`/`G` suffixes. Oldest sessions are pruned first |
```

`README.md:102-104` — current:

```markdown
- **Backups are copies.** Files replaced or deleted by a sync are copied into a dated backup
  tree first. The backup folder must live outside the sync folder, or backups would be
  re-synced to the peer and grow without bound.
```

replacement:

````markdown
- **Everything destroyed is archived first.** Files replaced, deleted, or displaced by a
  conflict are copied into the archive before the destructive step, under:

  ```
  <archive folder>/<yyyyMMdd-HHmmss of sync start>/<deleted|overwritten|conflict>/<original path>
  ```

  One folder per sync run, so a bad run is a single directory to restore from. The archive
  folder must live outside the sync folder, or archived copies would be re-scanned as new
  files, propagated to the peer, and grow without bound. `--archive-keep-days` and
  `--archive-max-size` prune the oldest sessions first.
- **`--mirror` is opt-in, and it is the dangerous one.** Without it, a file the peer has and
  you do not is only deleted when the ancestor table proves you *had* it and it was unchanged.
  With it, "the peer must match me" is taken literally and any unmatched file on the
  non-authoritative side is deleted. Use it for a genuine one-way mirror, never for a
  two-way pairing you care about.
- **Lost sync state never guesses.** A first run with no database is additive only: nothing is
  deleted, the ancestor table is built, and a `pair.marker` is written beside the database on
  success. If the database is later missing or unreadable *while that marker survives*, the
  run aborts with exit `4` rather than treating a decade of synced files as never-seen. Only
  `--mirror` — where you have explicitly named the authoritative side — proceeds anyway.
````

`README.md:108-115` — current:

```markdown
## Protocol compatibility

The wire protocol is **version 2**. Both peers must run the same build — a mismatch is rejected
during the handshake rather than silently misparsed. Version 1 did not carry file timestamps,
so a mixed pair could never converge.

A single protocol frame is capped at 64 MB. Since the file manifest is sent as one frame, that
bounds a synced tree at roughly 1.3 million files.
```

replacement:

````markdown
## How two-way sync decides

Two-way sync keeps an **ancestor table**: for every path, the size and mtime each side had at
the end of the last successful sync. Comparing the two current states against that common
ancestor is what separates the four cases that a straight two-way comparison cannot:

| Since the last sync | Result |
|---|---|
| Only the client changed | Send to server |
| Only the server changed | Send to client |
| Both changed | Conflict: both copies kept, loser renamed |
| Neither changed | Nothing |

Deletions are decided the same way. A file missing on one side is a *deletion* only if the
ancestor row says it was there and the other side has not touched it since; otherwise it is
simply a file that side never had. When one side deletes and the other edits, **the edit
wins** — the file is restored and the restore is listed in the run's report, because silently
resurrecting a file the user deleted is surprising, and silently deleting a file the user just
edited is unrecoverable.

Conflict losers are written next to the winner as
`{name}.conflict-{yyyyMMdd-HHmmss}-{client|server}{ext}`, so the pair sits together in the
folder and the losing side is named.

Clock skew between the peers is measured during the handshake and subtracted from the server's
timestamps before any comparison. A skew beyond 60 seconds is reported: it makes newest-wins
tie-breaks unreliable, and the real fix is NTP.

## Protocol compatibility

The wire protocol is **version 3**. Both peers must run the same build — a mismatch is rejected
during the handshake rather than silently misparsed.

- **v1 → v2** added file timestamps; without them a mixed pair could never converge.
- **v2 → v3** added the sync mode, the mirror flag, and the clock-skew exchange to the
  handshake. A v2 peer cannot express `pull` or `--mirror` and does not report its clock.

A single protocol frame is capped at 64 MB. Since the file manifest is sent as one frame, that
bounds a synced tree at roughly 1.3 million files.

## Upgrading from an earlier build

1. **Upgrade both peers together.** Protocol v3 is not compatible with v2. A mixed pair is
   rejected at the handshake with a clear error — it will not run half-configured — but it will
   not sync until both sides are on the new build.
2. **`--bidirectional` still works**, as an alias for `--mode two-way`. Existing scripts and
   saved GUI profiles keep running unchanged. New scripts should use `--mode`.
3. **The default is still `push`.** A command line with no mode flag behaves as before.
4. **The state database upgrades in place** to schema v2 on first open, splitting the single
   recorded size/mtime into per-side client and server columns. The first two-way run after the
   upgrade therefore treats both sides as matching the old record; verify a run with `--verbose`
   before enabling `--delete` on a large tree.
5. **Deletions and replacements now land in `.rfs-archive-NAME`**, not `.rfs-backups-NAME`, and
   are grouped per run rather than per day. Old backup folders are left alone; delete them by
   hand once you no longer need them.
````

`README.md:135-141` — current:

```markdown
## Known gaps

- `--max-threads` is parsed but transfers are sequential.
- Mid-transfer resume is not implemented; an interrupted sync restarts the affected file.
- Empty directories are not synced.
- No authentication or transport encryption (see the security notice above).
```

replacement:

```markdown
## Known gaps

- `--max-threads` is parsed but transfers are sequential.
- Mid-transfer resume is not implemented; an interrupted sync restarts the affected file.
- Empty directories are not synced.
- Renames are seen as a delete plus an add: the file is re-transferred, and the old name is
  archived rather than moved.
- Conflict resolution never merges file contents. Both copies are kept and the user reconciles
  them by hand.
- The ancestor table lives only on the client. A server paired with two clients has no shared
  history between them, so each pairing converges independently.
- No authentication or transport encryption (see the security notice above).
```

- [ ] **Step 2: Verify the documented flags and layout match the code**

Run: `dotnet run --project src/RemoteFileSync -- --help`
Expected: every flag in the options table appears in the help output with the same default; `--mode`, `--mirror`, `--archive-folder`, `--archive-keep-days` and `--archive-max-size` are all listed, and `--bidirectional` is marked deprecated.

---

### Existing integration tests that change

| Test / member | File:line | Change |
|---|---|---|
| *(usings)* | `EndToEndTests.cs:1-8` | add `using RemoteFileSync.Backup;` for `ArchiveReason` |
| `UniDirectional_ClientPushesToServer` | `EndToEndTests.cs:52` | `Bidirectional = false` → `Mode = SyncMode.Push` |
| `BiDirectional_BothSidesSync` | `EndToEndTests.cs:86`, `:108-114` | `Bidirectional = true` → `Mode = SyncMode.TwoWay`; dated `.rfs-backups-server` path → `AssertArchived(..., ArchiveReason.Overwritten, ...)` |
| `IdenticalFiles_NothingTransferred` | `EndToEndTests.cs:126`, `:142-144` | `Bidirectional = true` → `Mode = SyncMode.TwoWay`; dated-folder checks → `AssertNothingArchived` on both archive roots |
| `RunSyncAsync` helper | `EndToEndTests.cs:151-159` | `bool bidirectional` parameter → `SyncMode mode` |
| `TransferredFile_HasSourceTimestamp` | `EndToEndTests.cs:181` | `RunSyncAsync(bidirectional: false)` → `RunSyncAsync(SyncMode.Push)` |
| `SecondSync_TransfersNothing_WhenNothingChanged` | `EndToEndTests.cs:201`, `:209` | `bidirectional: true` → `SyncMode.TwoWay` |
| `ThreeBidirectionalSyncs_DoNotPingPong` | `EndToEndTests.cs:230`, `:235-236` | `bidirectional: true` → `SyncMode.TwoWay` |
| *(new helpers)* | `EndToEndTests.cs:243-250` | add `AssertArchived` and `AssertNothingArchived` |
| *(usings)* | `DeleteSyncTests.cs:1-7` | add `using RemoteFileSync.Backup;` |
| `RunSyncAsync` helper | `DeleteSyncTests.cs:53` | `Bidirectional = bidirectional` → `Mode = bidirectional ? SyncMode.TwoWay : SyncMode.Push` |
| `DeleteSync_Case1_PropagatesDeletion` | `DeleteSyncTests.cs:108-109` | dated backup path → `AssertArchived(..., ArchiveReason.Deleted, ...)` |
| `DeleteSync_BidiSymmetric` | `DeleteSyncTests.cs:159-162` | two dated backup paths → two `AssertArchived` calls |
| *(new helper)* | `DeleteSyncTests.cs:41-48` | add `AssertArchived` |
| `RunSyncAsync` helper | `DatabaseDeleteSyncTests.cs:56` | `Bidirectional = bidirectional` → `Mode = bidirectional ? SyncMode.TwoWay : SyncMode.Push` |
| `RunClientAsync` helper | `DeleteThresholdTests.cs:50-54` | `Bidirectional = true` → `Mode = SyncMode.TwoWay` |
| `SeedTrackedFiles` helper | `DeleteThresholdTests.cs:73-85` | `MarkSynced(name, 9, ...)` → `UpsertSynced(name, text.Length, mtime, text.Length, mtime, ...)`; session mode label `"bidi+delete"` → `"two-way+delete"`; add `PairMarker.Write(dbPath)` so the threshold guard, not the no-ancestor gate, is what aborts |

Assertion semantics are preserved in every case. The only behavioural expectation that changes is the archive path shape, which is required by the archive layout decided in the contract.

### Phase 9 commit

```bash
git add tests/RemoteFileSync.Tests/Integration/TwoWayMergeE2ETests.cs \
        tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs \
        tests/RemoteFileSync.Tests/Integration/DeleteSyncTests.cs \
        tests/RemoteFileSync.Tests/Integration/DatabaseDeleteSyncTests.cs \
        tests/RemoteFileSync.Tests/Integration/DeleteThresholdTests.cs \
        README.md
git commit -m "test: add end-to-end acceptance tests for ancestor merge, mirror and the no-ancestor gate

Covers over a real loopback socket: client delete tombstones the ancestor row
and removes the server copy; a client delete losing to a server edit restores
the file and reports the resurrection; simultaneous edits keep both copies with
the loser renamed; push/pull leave peer-only files alone without --mirror and
delete them with it; three identical runs converge to zero transfers and zero
deletes; and a lost database beside a surviving pair.marker aborts with exit 4
without deleting anything.

Migrates the existing integration suite off the removed Bidirectional setter and
onto the archive layout. Documents --mode, --mirror, the archive and its
retention flags, protocol v3, the ancestor model, and the upgrade path.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
git push -u origin feat/deletion-sync-ancestor-merge
```

**Verification before commit:**
```bash
dotnet build -c Release
dotnet test -c Release
```
Expected: 0 errors. Existing tests knowingly changed: all seven integration call sites listed in the table above — six are mechanical `Bidirectional` → `Mode` migrations forced by the contract's removal of the setter, and the remainder (`EndToEndTests.BiDirectional_BothSidesSync`, `EndToEndTests.IdenticalFiles_NothingTransferred`, `DeleteSyncTests.DeleteSync_Case1_PropagatesDeletion`, `DeleteSyncTests.DeleteSync_BidiSymmetric`, `DeleteThresholdTests.SeedTrackedFiles`) assert the new archive layout and the per-side ancestor columns instead of the retired dated-backup tree and single-size `MarkSynced`. No test's intent changes.

---

## Appendix A — Findings inventory

The 34 problems identified during design review of the original proposal, and where each is addressed. "Accepted" means the behaviour is a deliberate trade-off recorded in Appendix C rather than a defect.

| # | Finding | Evidence | Disposition |
|---|---|---|---|
| 1 | DB-less mirror-delete turns a corrupt database into a mass-deletion event | `SyncEngine.cs:24`, `SyncStateManager.cs:65-68` | Phase 7 — `PairMarker` gate |
| 2 | Blast-radius guard denominator is 0 on a wiped DB, so it never fires exactly when needed | `SyncClient.cs:238-242` | Phase 7 — denominator becomes destination manifest |
| 3 | "No row → copy" cannot distinguish a new file from a peer deletion whose row was lost | proposal rule (2) | Phase 7 — marker gate; Appendix C |
| 4 | Pruning deleted rows destroys the evidence the rules depend on | `SyncDatabase.cs:298`, `SyncEngine.cs:153-157` | Phase 3 — tombstones retained with `deleted_utc` |
| 5 | Modified-on-both-sides silently loses one side's edits | `ConflictResolver.Resolve` | Phase 4 + 5 |
| 6 | Rule [1] deletes restored-from-backup files (old mtime matches the row) | proposal rule [1] | Phase 4 — size is part of the predicate |
| 7 | Rule [1] inverts on a metadata-only touch, resurrecting an intentional deletion | proposal rule [1] | Phase 4 — 2s tolerance absorbs it; Phase 8 reports it |
| 8 | Tightening `--include`/`--exclude` reads as a deletion under mirroring | `SyncClient.cs:154-167` | Phase 7 — filter exclusion runs before the guard |
| 9 | Binary migration replays a *merged* manifest as per-side truth | `SyncDatabase.cs:420-468`, `SyncEngine.cs:207-234` | Phase 3 — migrated rows land as `client_*` == `server_*` |
| 10 | "Identical tables on both sides" is unimplementable: the server has no DB | `SyncServer.cs:21,26,32` (`_db` never used), `Program.cs:51` | Design decision — client-only |
| 11 | The server cannot compute the pairing identity | `SyncDatabase.cs:57-63` | Design decision — client-only |
| 12 | Nothing on the wire carries table contents | `MessageType.cs` | Design decision — client-only |
| 13 | Mode 1 (Pull) does not exist and cannot be expressed | `SyncOptions.cs:9`, `SyncClient.cs:90` | Phase 1 + 7 |
| 14 | Mode 1 deletions would be planned but never executed | `SyncClient.cs:405`, `SyncServer.cs:356` | Phase 7 — ungated |
| 15 | One server, many clients: a delete fans out to clients that never agreed | architectural | Appendix C |
| 16 | One client, many servers: same in mirror image | `SyncDatabase.cs:57-63` | Appendix C |
| 17 | Exact mtime equality is unstable across FAT/exFAT/SMB round-trips | `SyncDatabase.cs:77` | Phase 4 — 2s tolerance |
| 18 | Clock skew has no representation anywhere | `FileScanner.cs:66` | Phase 2 — `ClockSkew` |
| 19 | `file_size` is captured but no rule uses it | `ConflictResolver.cs:20-21` | Phase 4 — part of `Unchanged` |
| 20 | "Transaction" is undefined; per-file and per-session both have problems | `SyncDatabase.cs:248-281` | Phase 3 — per-file, documented |
| 21 | Crash mid-sync leaves a table describing a state that never existed | `StartSession:116` / `CompleteSession:131` | Phase 3 — rows written only after peer confirms |
| 22 | A lost `DeleteConfirm` diverges state with no reconciliation rule | `SyncClient.cs:336` has no `else` | Phase 7 — explicit desync abort |
| 23 | Routine large delete counts train users into `--force-delete` | `SyncOptions.cs:51` | Phase 7 — `--mirror` is separate from `--force-delete` |
| 24 | Server-side guard only bounds `DeleteOnServer`; Pull-mode deletes are unbounded | `SyncServer.cs:226` | Phase 7 |
| 25 | Backups grow without bound; no retention anywhere | `BackupManager.cs` | Phase 6 |
| 26 | "Identical tables" impossible across case-sensitive and case-insensitive peers | `SyncDatabase.cs:75`, `SyncEngine.cs:62` | Appendix C — `COLLATE NOCASE` retained |
| 27 | Which tables must be identical is unspecified; two cannot be | `SyncDatabase.cs:84,98` | Design decision — client-only |
| 28 | Deleted-on-both-sides is not covered by any stated rule | `SyncEngine.cs:198-201` | Phase 4 — explicit tombstone row |
| 29 | "Newest wins" states no tolerance and no tie-break | `ConflictResolver.cs:7` | Phase 4 — tolerance, then size, then Skip |
| 30 | DB-less two-way never deletes, and then records the resurrection as truth | proposal | Phase 7 — marker gate; Appendix C |
| 31 | The design assumes the table always exists, but the DB is opt-in and client-only | `Program.cs:56-66` | Phase 7 |
| 32 | "SQLite with an index" overstates what exists: no index on `files` beyond the PK | `SyncDatabase.cs:201` | Phase 3 — `idx_files_status` |
| 33 | Rule [2]'s "log for review" has no sink; `GetFileHistory` returns the *oldest* N | `SyncDatabase.cs:387` | Phase 8 |
| 34 | The server cannot act on an authoritative role it has no state for | `SyncServer.cs` | Phase 7 — server obeys the plan; client decides |

---

## Appendix B — Rollback

Each phase is a single commit on `feat/deletion-sync-ancestor-merge`, so any phase can be reverted independently:

```bash
git revert --no-commit <phase-sha>
dotnet build -c Release && dotnet test -c Release
git commit -m "revert: phase N — <reason>"
```

**Phases 2 and 3 need more than a code revert.**

*Phase 2 (protocol v3):* reverting one peer without the other produces a handshake rejection, not corruption — that is the intended failure mode. Both peers must be rolled back together.

*Phase 3 (schema v2):* the migration rewrites `files` in place. Before running any v2 build against real state, take a copy:

```bash
# Windows, per pairing — the pair id is a hash of (folder | host:port)
copy "%LOCALAPPDATA%\RemoteFileSync\<pairid>\sync.db" "%LOCALAPPDATA%\RemoteFileSync\<pairid>\sync.db.v1backup"
```

To roll back to a v1 build, restore that copy. A v1 build opening a v2 database will not crash — `InitSchema` uses `CREATE TABLE IF NOT EXISTS` — but it will read `file_size` and `last_modified` columns that no longer exist, throwing `SqliteException` on first query. There is no automatic downgrade path; restoring the backup is the supported route.

**Worst case, no backup:** delete the database directory entirely and re-run with `--mirror`. This rebuilds the ancestor from the two live folders. It cannot recover deletion knowledge, so anything deleted on one side while the DB was missing will be resurrected from the other. That is the failure mode `pair.marker` exists to make loud rather than silent.

---

## Appendix C — Deliberately out of scope

Recorded so a future reader knows these were considered and consciously deferred, not overlooked.

**Server-side state.** The server remains stateless. A server serving many clients therefore has no way to reconcile deletions across them: if client A deletes a file and client B still holds a matching ancestor row, B's next sync will delete its copy too (findings #15, #16). This is correct behaviour for a single-pairing deployment and wrong for a hub. Fixing it needs a per-(client, folder) identity on the wire and a server-side ancestor — a materially larger design.

**Protocol authentication and transport encryption.** Unchanged from `main`. The protocol still has no authentication and no TLS; `--bind 0.0.0.0` still prints the warning. Deletion correctness does not depend on it, but an unauthenticated peer can still request deletions bounded only by the percentage guard.

**Content hashing as the change predicate.** `Unchanged` uses size + mtime. A file edited in place, preserving both its size and its mtime to within two seconds, will be seen as unchanged. Closing that needs a `sha256` column and a full read of every delete-candidate each session. The schema leaves room to add it; the cost was not judged worth paying until a real false-delete is observed.

**TOCTOU between scan and transfer.** A file modified between `FileScanner.Scan()` and the moment it is sent is transferred in its new state but recorded with its scanned metadata. The next sync corrects it. Unchanged from `main`.

**Case-sensitivity across platforms.** `files.path` stays `COLLATE NOCASE` and manifests merge `OrdinalIgnoreCase`. A Linux peer holding both `Readme.md` and `README.md` collapses to one row (#26). Windows-to-Windows is unaffected.

**Empty directories.** Still not synced; the manifest is files-only. A directory that becomes empty because its files were deleted is left behind on both sides.

**Mid-transfer resume.** A failed transfer restarts from byte zero next session.

**`--max-threads`.** Parsed and validated, but transfers remain sequential. Ancestor state is written per file from a single thread; `SyncDatabase` is explicitly not thread-safe (`SyncDatabase.cs:34-36`), so parallel transfers need a write queue before they can be enabled.
