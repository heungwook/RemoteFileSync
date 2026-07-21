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
- **Green gate — build always, tests per-phase.** `dotnet build -c Release` must report **0 errors** at *every* commit, without exception. The full test suite is **not** green throughout: some phases knowingly leave assertions red for a later phase to repair, because the alternative is a phase that edits a file it does not own. Each phase's verification block names (a) the filtered test runs that must pass at that commit and (b) any test it knowingly leaves red, with the phase that repairs it. A test red at a commit and *not* on that phase's hand-off list is a regression — stop and fix it. The baseline at branch point is **260 passing, 0 failing**, and the suite must be fully green again at Phase 10.
- **No database mutation may precede any `return 4` in `HandleConnectionAsync`.** An abort must leave the ancestor table exactly as it found it, or the next run resolves fabricated state into a deletion.
- **Nothing is destroyed on the strength of an unchecked archive.** Every call to `ArchiveManager.Archive` that precedes a delete or an overwrite must branch on its `bool`. `Archive` returns `false` *without throwing* when `PathGuard.TryResolveWithinRoot` fails, and `PathGuard` fails closed on transient IO (`PathGuard.cs:85-86`) — so `false` does not mean "there was nothing to archive".
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

Phases are ordered by **dependency**: a phase only consumes types defined in a lower-numbered phase, so the build is green at every commit.

| Phase | Deliverable | Depends on | Risk |
|---|---|---|---|
| 1 | `SyncMode` enum, `SyncOptions`, CLI flags | — | Medium — removing the `Bidirectional` setter breaks every assignment |
| 2 | `AncestorRow`, `ChangeDetector`, `ClockSkew`, `PlanResult` — pure types, no call sites | 1 | Low |
| 3 | Protocol v3 handshake | 1, 2 | Medium — wire format change, existing tests break |
| 4 | Schema v2, migration, `ConflictDetail`, `PairMarker` | 2 | **High** — a bad migration corrupts real user state |
| 5 | `ArchiveManager` + retention | 1 | Medium — `Prune` deletes directories |
| 6 | Ancestor merge engine | 2, 4 | **High** — this is the correctness core |
| 7 | Conflict keep-both execution | 5, 6 | **High** — a wrong wire encoding desyncs the stream |
| 8 | Mode dispatch, Pull, reworked guards, no-ancestor gate | 3–7 | High — touches every safety gate |
| 9 | End-of-sync review report | 4, 7 | Low |
| 10 | E2E tests + documentation | all | Low |

Phases 4, 6 and 7 are the ones to slow down on. Phase 4 because it rewrites state users already have on disk; Phase 6 because every deletion decision flows through it; Phase 7 because an asymmetric plan interpretation between the two peers misaligns the frame stream, and the resulting corruption is silent.

### Edit ownership

Exactly one phase may edit each region. A phase needing a region it does not own **consumes the result** rather than re-applying the edit — including reusing locals such as `archive`, `mode` and `skew` rather than redeclaring them.

| Region | Sole owner |
|---|---|
| Every `Bidirectional =` assignment, in `src/` **and** `tests/` | Phase 1 |
| `SyncOptions.cs`, `Program.ParseArgs`, `PrintUsage`, `SyncAction.cs` | Phase 1 |
| `Transfer/FileTransfer.cs`; the `File.Delete` deletion branches on both peers | Phase 5 |
| The resurrection + conflict drains into `SyncDatabase`; relocating Phase 6's ancestor-write block | Phase 7 |
| `ProtocolHandler` handshake methods; the handshake blocks in `SyncClient.cs:89-113` and `SyncServer.cs:132-152` | Phase 3 |
| `SyncDatabase.cs` | Phase 4 |
| `BackupManager` → `ArchiveManager` at all six call sites; the single `archive` local in each session method | Phase 5 |
| `SyncEngine.cs`; the `ComputePlan` call site; the DB-write block at `SyncClient.cs:185-206` | Phase 6 |
| Delete guards; mode gating of the transfer loops; the no-ancestor gate | Phase 8 |
| Integration test files | Phase 10 (except Phase 1's `Bidirectional` migration) |

---

## Review record

The first draft of this plan was audited by twelve expert reviewers against the real codebase, and each finding was then put to an independent agent instructed to **refute** it. Of 105 raw findings, 46 survived refutation and are fixed here; 23 were refuted and dropped.

The dominant defect class was **edit collision** — separate phases editing the same region, each quoting the original source as "current code", so the second edit had no anchor. That is why this revision introduces the dependency ordering and the ownership table above rather than patching findings individually.

| Finding | Fix |
|---|---|
| `ApplyLocalRenames` discarded `Archive()`'s return value, then deleted unconditionally. `PathGuard` fails closed on transient IO (`PathGuard.cs:85-86`), so a momentary stat failure destroyed the user's file with no archived copy. | Phase 7 — the delete is gated on a proven archive. The `removeOriginal: false` archive a line later is deliberately *not* gated: a failure there loses only a precautionary copy, and aborting the session would be worse. |
| `SyncClient.cs:191`'s `clientManifest.Get(p) ?? serverManifest.Get(p)` wrote a **two-sided** ancestor row from **one side's** manifest. In Pull mode this stamped `server_mtime` with the client's values for a client-only file, so run 2 emitted `DeleteOnClient` and destroyed local-only files. | Phase 6 — `UpsertSynced` only when both manifests hold the path, each side's own values; `MarkSkipped` otherwise. |
| The DB-write block ran *before* the delete guards, so an exit-4 abort still persisted ancestor state. | Phase 6 — the block moves below the guards. |
| `ComputePlan` was expected to record resurrections, but it must stay pure, so `GetSessionResurrections` was never fed and the review report's resurrection section was dead. | Phase 2 adds `PlanResult`; Phase 6 populates `Resurrections`/`Conflicts`; Phase 9 renders them. |
| Two phases specified `LogConflict` incompatibly — one writing a fixed action, the other sniffing a `"resurrected:"` prefix out of the detail string. | Phase 4 — two separate methods, no prefix sniffing, both taking an encoded `ConflictDetail`. |
| Phase 7 passed free-form English to `LogConflict` where the report expected structured data, so every entry would fall back to the unparsable path. | Phase 4 defines `ConflictDetail.Encode()/Decode()`; Phase 7 uses it. |
| `Prune` was called with `TimeSpan.MaxValue` to mean "keep forever"; `DateTime.UtcNow - TimeSpan.MaxValue` throws, aborting the sync before it starts. | Phase 5 — `TimeSpan.Zero` disables the age rule and the cutoff is computed inside the guard. |
| The no-ancestor gate lived in `Program.Main`, so the E2E test asserting it constructs `SyncClient` directly and could never trigger it. | Phase 8 — the gate moves into `SyncClient.RunAsync`; `Program` only surfaces the exit code. |
| Three different definitions of the gate's condition appeared across the plan (file-readability, empty table, contract table). | Phase 8 — exactly `PairMarker.Exists` AND (database absent or unreadable). |
| `Bidirectional` → `Mode` migration was applied twice, in Phases 1 and 9. | Phase 1 owns it outright. |
| `BackupManager` → `ArchiveManager` migration was applied twice, in Phases 6 and 7, at the same six call sites. | Phase 5 owns it outright. |
| Protocol-v3 handshake migration was applied twice, in Phases 2 and 7. | Phase 3 owns it outright. |
| Two phases each declared a local named `archive` in the same method → CS0128. | Phase 5 declares one per session; later phases reuse it. |
| Phase 5 referenced `bidirectional` in `SyncServer` after Phase 7 deleted that local → CS0103. | Phase 3 removes it; Phases 7 and 8 use `mode`. |
| Phase 5 used `ArchiveManager` (Phase 6) and Phase 3 used `AncestorRow` (Phase 4) — both consuming types from later phases, breaking the green-build constraint. | Dependency reordering; `AncestorRow` and friends move to Phase 2. |
| Exact test counts ("PASS (25 tests)") were asserted throughout and were wrong. | Removed; steps now name the specific test methods that must be green. |
| `bool clientHadIt = row is { Status: "exists" }` gated Push/Pull deletion on the row merely existing, not on the authoritative side being unchanged. | Phase 6 — corrected predicate. |
| Phase 8 silently dropped the `SyncStateManager` binary-state ancestor path from the `ComputePlan` call. | Phase 8 — stated explicitly as a consequence. |

Refuted findings are not listed. Two are worth recording because the refutation is the interesting part: a claim that `PurgeTombstonesOlderThan` is dead code was rejected because retention is deliberately a manual operation for now, and a claim that the archive-folder containment check was missing was rejected because Phase 1 already adds it.

### Third review round

The revision was audited again, specifically to confirm the collision class was gone. It largely was — but four ownership gaps and two further data-loss paths survived, including one the first round missed entirely.

| Finding | Fix |
|---|---|
| **`FileTransfer.ReceiveFileAsync` discards `onBeforeCommit`'s bool and commits regardless.** This is the *pre-overwrite* snapshot, so a transient IO failure leaves no archived copy and replaces the destination anyway — the same defect class as the conflict-squatter bug, on both peers. An earlier phase inspected this line and ruled it inert, but that reasoning only holds for newly-created files, not the overwrite case the hook exists for. | Phase 5 — the commit is conditional on a proven archive, with the hook distinguishing "nothing to archive" from "archive failed". |
| **The `backupFirst == false` deletion branch calls `File.Delete` with no archive at all**, and `backupFirst` is decoded straight off the wire. Our client always sends `true`, so the branch is reachable only from a hostile or buggy peer — which can then delete on either side with no restore point. | Phase 5 — both peers always archive before removing; the wire flag is retained for compatibility but ignored locally. |
| **`SyncActionType.ConflictKeepBoth = 7` was used by two phases and added by none.** Both attributed it to Phase 1, which never touched `SyncAction.cs`. | Phase 1 — appended without renumbering, since those bytes are serialised in `SerializeSyncPlan`. |
| **The resurrection drain had circular ownership** — Phase 6 said Phase 9 owned it, Phase 9 said Phase 6 did, and neither implemented it. `LogResurrection` had zero callers, so the report's resurrection section was dead and rule (c-middle) was not wired end to end. | Phase 7 — drained alongside the conflict drain, same anchor. |
| **Phase 10 targeted a `SyncClient` seam Phase 8 never built** (`SyncDatabase.DatabasePath` / `ExistedBeforeOpen` versus Phase 8's `dbPath` parameter), so every E2E test that primes a pairing would fail. | Phase 10 — uses Phase 8's `dbPath:` seam. |
| **Phase 8 still quoted pre-Phase-3 text** for the two `bidirectional` conditions, and asserted as a premise that Phase 3 leaves that local behind when Phase 3 deletes it. | Phase 8 — quotes Phase 3's post-edit text. |
| Phase 6's ancestor-write block ended up above a `return 4` in Phase 7's rename pass, re-creating the abort-writes-state defect. | Phase 7 — relocates the block below its rename pass; new constraint added above. |
| `db.GetLastSessionId()` does not exist and no phase adds it. | Phase 7 — uses `GetRecentSessions(1).First().Id`. |
| Three phases committed with a knowingly red suite while Global Constraints demanded full green. | Constraint amended: build green always, tests per-phase with an explicit hand-off list. |

---

# Implementation phases

## Phase 1: `SyncMode`, `SyncOptions` archive settings, and CLI parsing

**Goal:** Replace the boolean `Bidirectional` switch with a three-valued `SyncMode`, add the mirror/archive/skew settings to `SyncOptions`, and wire the new CLI flags — leaving `Bidirectional` as a read-only compatibility shim so every existing *read* site keeps compiling untouched.

**Edit ownership (per CONTRACT.md):** this phase is the **sole owner** of every `Bidirectional =` assignment in `src/` **and** `tests/` (including all four integration-test files), of all of `SyncOptions.cs`, and of `Program.ParseArgs` / `Program.PrintUsage`. No later phase re-applies any of this. Phase 8 adds only the `PairMarker` write to `Program`; it must quote `Program.cs` as this phase leaves it.

**Files:**
- Create: `src/RemoteFileSync/Models/SyncMode.cs`
- Modify: `src/RemoteFileSync/Models/SyncAction.cs:3-12` (Task 1.0)
- Modify: `src/RemoteFileSync/Models/SyncOptions.cs:9` (Task 1.1), insertion after `:81` (Task 1.2), `:113-121` (Task 1.3)
- Modify: `src/RemoteFileSync/Program.cs` — insertion between `:109` and `:111` (Tasks 1.4, 1.5), `:136-138` (Tasks 1.1, 1.4), `:139-141` (Task 1.5), `:197-199` and `:205-206` (Task 1.6)
- Modify (mandatory compile fix, this phase only): `tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs:52`, `:86`, `:126`, `:155-159`
- Modify (mandatory compile fix, this phase only): `tests/RemoteFileSync.Tests/Integration/DeleteSyncTests.cs:53`
- Modify (mandatory compile fix, this phase only): `tests/RemoteFileSync.Tests/Integration/DatabaseDeleteSyncTests.cs:56`
- Modify (mandatory compile fix, this phase only): `tests/RemoteFileSync.Tests/Integration/DeleteThresholdTests.cs:50-54`
- Test: create `tests/RemoteFileSync.Tests/Models/SyncActionTypeTests.cs` (Task 1.0)
- Test: `tests/RemoteFileSync.Tests/Models/SyncOptionsTests.cs` (append before the class's closing brace at `:89`)
- Test: `tests/RemoteFileSync.Tests/CliParserTests.cs` (append before the class's closing brace at `:166`)

**Interfaces:**

- **Consumes:** nothing. This is the first phase; no earlier phase has edited any region it touches, so every "Replace exactly" block below quotes `main` verbatim.
- **Produces** (all in `namespace RemoteFileSync.Models`, so no new `using` is needed in any file that already has `using RemoteFileSync.Models;`):
  - `SyncActionType.ConflictKeepBoth = 7` — appended to the existing `SyncActionType` enum in `SyncAction.cs`; no existing member is renumbered.
  - `public enum SyncMode : byte { Push = 1, Pull = 2, TwoWay = 3 }`
  - `public SyncMode Mode { get; set; } = SyncMode.Push;`
  - `public bool Bidirectional => Mode == SyncMode.TwoWay;` — read-only, setter **removed**
  - `public bool MirrorDeletes { get; set; }`
  - `public string? ArchiveFolder { get; set; }`
  - `public string EffectiveArchiveFolder { get; }`
  - `public int ArchiveKeepDays { get; set; } = 30;`
  - `public long ArchiveMaxBytes { get; set; }`
  - `public const int SuspiciousSkewSeconds = 60;`
  - `Validate()` additionally rejects a negative `ArchiveKeepDays`, a negative `ArchiveMaxBytes`, and an archive folder inside or equal to the sync folder.
  - CLI flags `--mode`, `--mirror`, `--archive-folder`, `--archive-keep-days`, `--archive-max-size`; `--bidirectional` / `-b` retained as a deprecated alias for `--mode two-way`.
- **Consumed by later phases:** Phases 6 and 7 emit `SyncActionType.ConflictKeepBoth` (Phase 6 from the plan builder, Phase 7 when materialising the keep-both rename); neither declares it, so it must exist after this phase or both fail to compile. Phase 3 reads `_options.Mode` / `_options.MirrorDeletes` when building the v3 handshake byte; Phase 5 reads `EffectiveArchiveFolder`, `ArchiveKeepDays`, `ArchiveMaxBytes`; Phase 6 reads `_options.Mode` and `_options.MirrorDeletes` at the `ComputePlan` call site; Phase 8 reads `_options.Mode` and `_options.MirrorDeletes` for mode dispatch and the no-ancestor gate.

**Interim state this phase knowingly leaves behind — read before shipping any intermediate build.** After this phase, `--mode pull` parses and stores `SyncMode.Pull`, but nothing reads `Mode` yet: `SyncClient` still branches on `Bidirectional`, which is `false` for `Pull`, so a `--mode pull` run behaves exactly like a Push. Pull dispatch lands in Phase 8. This is an accepted interim state of the phase sequence, not a shippable one; no guard is added here because rejecting `Pull` in `Validate()` would put a throw in a region Phase 8 would then have to remove, and CONTRACT.md sanctions no such guard.

---

### Task 1.0: `SyncActionType.ConflictKeepBoth = 7`

Phase 6 emits `ConflictKeepBoth` from the plan builder and Phase 7 materialises it as the keep-both rename. Neither phase declares it — both assume it already exists — so it is added here, in `Models/`, which this phase owns.

- [ ] **Step 1: Write the failing test**

Create `tests/RemoteFileSync.Tests/Models/SyncActionTypeTests.cs`:

```csharp
using RemoteFileSync.Models;

namespace RemoteFileSync.Tests.Models;

public class SyncActionTypeTests
{
    [Fact]
    public void ConflictKeepBoth_IsSeven()
    {
        Assert.Equal(7, (byte)SyncActionType.ConflictKeepBoth);
    }

    [Fact]
    public void ExistingActionTypes_KeepTheirWireNumbers()
    {
        // These bytes are written by SerializeSyncPlan and read by the peer's deserializer,
        // so they are wire format. Renumbering any of them silently repoints a peer's action:
        // a plan that said "SendToServer" would arrive as some other action entirely.
        Assert.Equal(0, (byte)SyncActionType.SendToServer);
        Assert.Equal(1, (byte)SyncActionType.SendToClient);
        Assert.Equal(2, (byte)SyncActionType.ClientOnly);
        Assert.Equal(3, (byte)SyncActionType.ServerOnly);
        Assert.Equal(4, (byte)SyncActionType.Skip);
        Assert.Equal(5, (byte)SyncActionType.DeleteOnServer);
        Assert.Equal(6, (byte)SyncActionType.DeleteOnClient);
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncActionTypeTests"`

Expected: FAIL — the test project does not build. `CS0117: 'SyncActionType' does not contain a definition for 'ConflictKeepBoth'` in `ConflictKeepBoth_IsSeven`. (`ExistingActionTypes_KeepTheirWireNumbers` compiles fine but cannot run while the assembly is broken.)

- [ ] **Step 3: Implement**

`src/RemoteFileSync/Models/SyncAction.cs:3-12` — replace exactly:

```csharp
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
```

with:

```csharp
/// <summary>
/// What to do with one path. The numeric values are WIRE FORMAT: SerializeSyncPlan writes each
/// one as a single byte and the peer's deserializer casts that byte straight back to this enum.
/// New members are therefore APPENDED with the next free number — never renumbered, never
/// reordered, and no member is ever removed. Renumbering would not break the build, which is
/// exactly why it is dangerous: an old peer would keep sending 5 for DeleteOnServer while a
/// renumbered new peer read 5 as something else, and the mismatch only surfaces as files being
/// deleted or overwritten on the wrong side.
/// </summary>
public enum SyncActionType : byte
{
    SendToServer = 0,
    SendToClient = 1,
    ClientOnly = 2,
    ServerOnly = 3,
    Skip = 4,
    DeleteOnServer = 5,
    DeleteOnClient = 6,

    /// <summary>
    /// Both sides changed the file since the common ancestor and neither edit can be discarded,
    /// so the loser is kept under a renamed sibling instead of being overwritten. Emitted by the
    /// plan builder (Phase 6) and materialised as the rename (Phase 7).
    /// </summary>
    ConflictKeepBoth = 7,
}
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncActionTypeTests"`

Expected: PASS — `ConflictKeepBoth_IsSeven` and `ExistingActionTypes_KeepTheirWireNumbers` both green.

---

### Task 1.1: `SyncMode` enum, `Mode` property, and the read-only `Bidirectional` shim

This task is atomic by necessity: the moment the `Bidirectional` setter is removed, every assignment to it stops compiling. All eight assignment sites — one in `src/`, seven in `tests/` — are fixed in Step 3 of this task, and no later phase revisits any of them.

- [ ] **Step 1: Write the failing test**

Append to `tests/RemoteFileSync.Tests/Models/SyncOptionsTests.cs`, immediately before the class's closing brace at line 89:

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
        // drift apart, which is how a Pull sync could silently keep taking the branches
        // that write to the server.
        var options = new SyncOptions { IsServer = true, Folder = _syncDir, Mode = mode };

        Assert.Equal(expected, options.Bidirectional);
    }

    [Fact]
    public void SyncMode_ValuesAreStableWireNumbers()
    {
        // These numbers travel in the low 2 bits of the handshake's syncMode byte.
        // Renumbering them silently repoints an existing peer's sync direction.
        Assert.Equal(1, (byte)SyncMode.Push);
        Assert.Equal(2, (byte)SyncMode.Pull);
        Assert.Equal(3, (byte)SyncMode.TwoWay);
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Bidirectional_TracksMode"`

Expected: FAIL — the test project does not build. `SyncMode` is an undeclared type name in `[InlineData(SyncMode.Push, false)]` and in the `Bidirectional_TracksMode` parameter list, so Roslyn emits `CS0246: The type or namespace name 'SyncMode' could not be found (are you missing a using directive or an assembly reference?)` in `SyncOptionsTests.cs`.

- [ ] **Step 3: Implement**

**3a. Create `src/RemoteFileSync/Models/SyncMode.cs`:**

```csharp
namespace RemoteFileSync.Models;

/// <summary>
/// Which side is authoritative for a sync. The numeric values travel in the low 2 bits of the
/// protocol handshake's syncMode byte, so they are wire format — do not renumber them, and do
/// not add a zero member: 0 is what an unauthenticated peer sends when it sends nothing.
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

**3b. `src/RemoteFileSync/Models/SyncOptions.cs:9` — replace exactly:**

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

**3c. `src/RemoteFileSync/Program.cs:136-138` — replace exactly:**

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

**3d–3j. The seven integration-test assignment sites.** These are the complete set of remaining `Bidirectional =` writes, found with `rg 'Bidirectional\s*=[^=]' src/ tests/` and hand-filtered to exclude `ExecRFS.Models.SyncProfile.Bidirectional`, a different type on a different class. **This phase migrates all seven. No later phase re-applies any of them, and no later phase may quote the pre-migration text as "current".**

| # | File:line | Current | Replacement |
|---|---|---|---|
| 3d | `tests/…/Integration/EndToEndTests.cs:52` | `Bidirectional = false` | `Mode = SyncMode.Push` |
| 3e | `tests/…/Integration/EndToEndTests.cs:86` | `Bidirectional = true` | `Mode = SyncMode.TwoWay` |
| 3f | `tests/…/Integration/EndToEndTests.cs:126` | `Bidirectional = true` | `Mode = SyncMode.TwoWay` |
| 3g | `tests/…/Integration/EndToEndTests.cs:158` | `Bidirectional = bidirectional` | `Mode = bidirectional ? SyncMode.TwoWay : SyncMode.Push` |
| 3h | `tests/…/Integration/DeleteSyncTests.cs:53` | `Bidirectional = bidirectional` | `Mode = bidirectional ? SyncMode.TwoWay : SyncMode.Push` |
| 3i | `tests/…/Integration/DatabaseDeleteSyncTests.cs:56` | `Bidirectional = bidirectional` | `Mode = bidirectional ? SyncMode.TwoWay : SyncMode.Push` |
| 3j | `tests/…/Integration/DeleteThresholdTests.cs:53` | `Bidirectional = true` | `Mode = SyncMode.TwoWay` |

All four files already carry `using RemoteFileSync.Models;` (`EndToEndTests.cs:4`, `DeleteSyncTests.cs:4`, `DatabaseDeleteSyncTests.cs:5`, `DeleteThresholdTests.cs:4`), so `SyncMode` resolves with no added `using`.

**Exact edits for 3d–3j.**

`tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs:52` — replace exactly:

```csharp
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Bidirectional = false };
```

with:

```csharp
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Mode = SyncMode.Push };
```

`tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs:86` and `:126` — both currently read (byte-identical):

```csharp
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Bidirectional = true };
```

Replace **both** occurrences with:

```csharp
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Mode = SyncMode.TwoWay };
```

`tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs:155-159` — replace exactly:

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

Replace the occurrence in **each** file with:

```csharp
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Mode = bidirectional ? SyncMode.TwoWay : SyncMode.Push, DeleteEnabled = deleteEnabled };
```

`tests/RemoteFileSync.Tests/Integration/DeleteThresholdTests.cs:50-54` — replace exactly:

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

**Deliberately NOT changed** — these are *reads*, which the shim still answers, so they compile unmodified:

| Site | Migrated by |
|---|---|
| `src/RemoteFileSync/Network/SyncClient.cs:73` (`modeLabel` in `RunAsync`) | Phase 8 (mode dispatch) |
| `src/RemoteFileSync/Network/SyncClient.cs:90` (handshake `syncMode` byte) | Phase 3 (owns `SyncClient.cs:89-113`) |
| `src/RemoteFileSync/Network/SyncClient.cs:119` (session-mode log string) | Phase 8 |
| `src/RemoteFileSync/Network/SyncClient.cs:151-152` (`ComputePlan` call site) | Phase 6 (owns that call site) |
| `src/RemoteFileSync/Network/SyncClient.cs:357, :405` (transfer-loop gates) | Phase 8 (mode gating of the transfer loops) |

Also unchanged, and by design:
- `tests/RemoteFileSync.Tests/CliParserTests.cs:97, :116` — `Assert.True(result.Bidirectional)` reads. They now additionally prove the `--bidirectional` / `-b` alias routes through `Mode`.
- `tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs:60` — `ServerOnly_Bidirectional_ProducesServerOnlyAction` is a method *name*, not a member access.
- `src/ExecRFS/Models/SyncProfile.cs:21`, `src/ExecRFS/Services/CommandBuilder.cs:21`, `src/ExecRFS/Components/Panels/ClientPanel.razor:44-45`, `tests/ExecRFS.Tests/Services/ProfileServiceTests.cs:28, :35`, `tests/ExecRFS.Tests/Services/CommandBuilderTests.cs:44` — all `ExecRFS.Models.SyncProfile.Bidirectional`, an unrelated settable `bool` on a different class. ExecRFS shells out to the CLI and keeps emitting `--bidirectional`, which remains a supported alias.
- `Plans/**/*.md`, `.superpowers/**` — historical documents, not compiled.

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Bidirectional_TracksMode|FullyQualifiedName~Mode_DefaultsToPush|FullyQualifiedName~SyncMode_ValuesAreStableWireNumbers"`

Expected: PASS — `Mode_DefaultsToPush`, all three `Bidirectional_TracksMode` cases, and `SyncMode_ValuesAreStableWireNumbers` green.

Then confirm the migration left nothing behind:

```bash
dotnet build -c Release
rg 'options\.Bidirectional\s*=[^=]|Bidirectional = (true|false|bidirectional)' src/RemoteFileSync tests/RemoteFileSync.Tests
```

Expected: build reports 0 errors, and `rg` prints nothing.

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

Expected: FAIL — the test project does not build, with two distinct diagnostics:
- `CS1061: 'SyncOptions' does not contain a definition for 'EffectiveArchiveFolder' and no accessible extension method 'EffectiveArchiveFolder' accepting a first argument of type 'SyncOptions' could be found` — and the same for the instance members `ArchiveKeepDays`, `ArchiveMaxBytes` and `MirrorDeletes`.
- `CS0117: 'SyncOptions' does not contain a definition for 'ArchiveFolder'` for the object-initializer member in `EffectiveArchiveFolder_HonoursExplicitOverride`, and the same for the static access `SyncOptions.SuspiciousSkewSeconds`.

- [ ] **Step 3: Implement**

`src/RemoteFileSync/Models/SyncOptions.cs` — insert immediately after the closing brace of the `EffectiveBackupFolder` property (`:81` on `main`; `:88` after Task 1.1's `+7`-line replacement) and immediately before `public void Validate()`:

```csharp
    /// <summary>
    /// Propagate deletions from the authoritative side even when the ancestor table has no
    /// evidence the file was ever synced. Off by default: without an ancestor row a missing
    /// file is indistinguishable from a file that was simply never sent, so mirroring would
    /// delete work the peer created independently.
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

    /// <summary>Prune archived sessions older than this many days. 0 = keep forever.</summary>
    public int ArchiveKeepDays { get; set; } = 30;

    /// <summary>Prune oldest archived sessions once the archive exceeds this size. 0 = no cap.</summary>
    public long ArchiveMaxBytes { get; set; }

    /// <summary>
    /// Clock offsets above this are reported rather than silently trusted. Newest-wins
    /// comparisons are only meaningful within a small skew; a peer an hour ahead would make
    /// every one of its files look newer and overwrite the whole other side.
    /// </summary>
    public const int SuspiciousSkewSeconds = 60;
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncOptionsTests"`

Expected: PASS — `EffectiveArchiveFolder_DefaultsBesideSyncFolder_NotInsideIt`, `EffectiveArchiveFolder_HonoursExplicitOverride`, `EffectiveArchiveFolder_ThrowsForDriveRoot` and `ArchiveRetention_HasSafeDefaults` green, alongside the pre-existing `EffectiveBackupFolder_*` and `Validate_*` tests.

---

### Task 1.3: `Validate()` rejects an unsafe archive folder and negative retention

- [ ] **Step 1: Write the failing test**

Append to `tests/RemoteFileSync.Tests/Models/SyncOptionsTests.cs`:

```csharp
    [Fact]
    public void Validate_RejectsArchiveFolderInsideSyncFolder()
    {
        // An archive inside the synced tree re-syncs to the peer, which recreates every file
        // the archive is holding — the deletion undoes itself on the next run.
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
    public void Validate_RejectsArchiveFolderEqualToSyncFolder()
    {
        // The worst case, and the one a naive prefix test misses: archiveFull has no trailing
        // separator, so "is the archive under the sync folder?" answers false for the sync
        // folder itself. Every archived deletion would then be written straight back into the
        // tree it was deleted from.
        var options = new SyncOptions
        {
            IsServer = true,
            Folder = _syncDir,
            ArchiveFolder = _syncDir,
        };

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("--archive-folder", ex.Message);
    }

    [Fact]
    public void Validate_RejectsNegativeArchiveKeepDays()
    {
        // A negative keep-age makes every session older than the cutoff, so the first prune
        // would empty the archive that is holding the user's only copy of deleted files.
        var options = new SyncOptions { IsServer = true, Folder = _syncDir, ArchiveKeepDays = -1 };

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("--archive-keep-days", ex.Message);
    }

    [Fact]
    public void Validate_RejectsNegativeArchiveMaxBytes()
    {
        // A negative cap is below any real archive size, so the size rule would prune every
        // session on every run. 0 — and only 0 — means "no cap".
        var options = new SyncOptions { IsServer = true, Folder = _syncDir, ArchiveMaxBytes = -1 };

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("--archive-max-size", ex.Message);
    }
```

**No `Validate_AcceptsTheDefaultArchiveFolder` test is added.** It would pass identically before and after this task, so it has no teeth. The guarantee it would have offered — that the new containment check does not mis-fire on the default archive folder — is already carried by the pre-existing `Validate_AcceptsTheDefaultBackupFolder` (`SyncOptionsTests.cs:82-88`) and `Validate_AcceptsBackupFolderOutsideSyncFolder` (`:69-80`), both of which call `Validate()` with `ArchiveFolder` left null and would start failing the moment the archive guard rejected its own default.

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Validate_RejectsArchiveFolder|FullyQualifiedName~Validate_RejectsNegativeArchive"`

Expected: FAIL — all four tests fail with `Assert.Throws() Failure: No exception was thrown`. `Validate()` currently ends at the backup-folder containment check and inspects none of the archive settings.

- [ ] **Step 3: Implement**

`src/RemoteFileSync/Models/SyncOptions.cs` — the backup-containment block that closes `Validate()`. It is at `:113-121` on `main`; Tasks 1.1 and 1.2 both insert above it, so **anchor on the quoted text, not on the line number**. Tasks 1.1 and 1.2 do not modify this text, so it is byte-identical to `main`.

Replace exactly:

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
                $"--archive-max-size must be >= 0 (0 = no cap), got {ArchiveMaxBytes}. " +
                "A negative cap is below any real archive size, so every session would be pruned.");

        // Backups inside the sync folder are re-scanned as new files and propagated to the
        // peer, growing without bound. Reject that outright rather than discovering it later.
        var syncFull = Path.GetFullPath(Folder);
        if (!syncFull.EndsWith(Path.DirectorySeparatorChar)) syncFull += Path.DirectorySeparatorChar;
        var backupFull = Path.GetFullPath(EffectiveBackupFolder);
        if (backupFull.StartsWith(syncFull, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"--backup-folder must be outside the sync folder (got '{backupFull}' inside '{syncFull}'). " +
                "Backups inside the sync folder are re-synced to the peer and grow without bound.");

        // Same containment rule for the archive, but a worse failure than the backup case: an
        // archived deletion sitting inside the synced tree propagates back to the peer and
        // recreates the file that was just deleted, so the deletion silently undoes itself on
        // the next run.
        var archiveFull = Path.GetFullPath(EffectiveArchiveFolder);
        // Compare with a trailing separator on BOTH sides. Without it the sync folder itself —
        // the most destructive value --archive-folder can take — is not a prefix match of
        // syncFull and slips through.
        var archiveProbe = archiveFull.EndsWith(Path.DirectorySeparatorChar)
            ? archiveFull
            : archiveFull + Path.DirectorySeparatorChar;
        if (archiveProbe.StartsWith(syncFull, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"--archive-folder must be outside the sync folder (got '{archiveFull}' inside '{syncFull}'). " +
                "Archived deletions inside the sync folder are re-synced to the peer and resurrect " +
                "the files that were just deleted.");
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncOptionsTests"`

Expected: PASS — `Validate_RejectsArchiveFolderInsideSyncFolder`, `Validate_RejectsArchiveFolderEqualToSyncFolder`, `Validate_RejectsNegativeArchiveKeepDays` and `Validate_RejectsNegativeArchiveMaxBytes` green, with `Validate_AcceptsTheDefaultBackupFolder` and `Validate_AcceptsBackupFolderOutsideSyncFolder` still green (proving the new archive guard does not reject its own default).

---

### Task 1.4: `--mode` parsing

- [ ] **Step 1: Write the failing test**

Append to `tests/RemoteFileSync.Tests/CliParserTests.cs`, immediately before the class's closing brace at line 166:

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

Expected: FAIL — every case throws `System.ArgumentException : Unknown option: --mode`, raised by the `default:` arm at `src/RemoteFileSync/Program.cs:178-179`.

- [ ] **Step 3: Implement**

**3a.** `src/RemoteFileSync/Program.cs` — insert this helper between the closing brace of `NextInt` (line 109) and `public static SyncOptions ParseArgs` (line 111), i.e. into the blank line 110:

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

**3b.** `src/RemoteFileSync/Program.cs` — in the `ParseArgs` switch, replace the `--bidirectional` case **as Task 1.1 Step 3c left it**:

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

Expected: PASS — all six `ParseArgs_ModeFlag_IsCaseInsensitive` cases, all four `ParseArgs_UnknownMode_ThrowsArgumentException` cases, `ParseArgs_NoModeFlag_DefaultsToPush`, and both `ParseArgs_BidirectionalAlias_SetsTwoWayMode` cases green.

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
        // Asserting the MESSAGE, not just the type: an unrecognised flag also throws
        // ArgumentException (from the default: arm), so a bare Assert.Throws would pass even
        // if the flag were never wired up, and would keep passing for a flag that read
        // args[++i] directly instead of going through NextValue.
        var ex = Assert.Throws<ArgumentException>(() => Program.ParseArgs(new[] { "client", flag }));
        Assert.Contains("Missing value for", ex.Message);
        Assert.Contains(flag, ex.Message);
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ParseArgs_ArchiveMaxSize_AcceptsSuffixes|FullyQualifiedName~ParseArgs_MissingValueAfterNewFlag_ThrowsArgumentException"`

Expected: FAIL —
- every `ParseArgs_ArchiveMaxSize_AcceptsSuffixes` case throws `System.ArgumentException : Unknown option: --archive-max-size` from the `default:` arm at `Program.cs:178-179`;
- `ParseArgs_MissingValueAfterNewFlag_ThrowsArgumentException` fails on `Assert.Contains("Missing value for", ex.Message)` — the actual message is `Unknown option: --archive-folder` (and likewise for the other three flags). The `--mode` case is the exception: Task 1.4 already wired it, so that one row is green before this task and red only for the three archive flags.

- [ ] **Step 3: Implement**

**3a.** `src/RemoteFileSync/Program.cs` — insert `ParseSize` immediately after the `ParseMode` helper added in Task 1.4 Step 3a, and before `public static SyncOptions ParseArgs`:

```csharp
    /// <summary>
    /// Parses a byte count with an optional K/M/G(B) suffix, 1024-based. Sizes are typed by
    /// humans as "500M"; a bare long.Parse rejects that common case, and a lenient parse that
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
        // Multiplying past long.MaxValue wraps negative, which Validate() would then reject
        // with a confusing message about a value the user never typed.
        if (value > long.MaxValue / multiplier)
            throw new ArgumentException($"{flag} value '{raw}' is too large for a 64-bit byte count.");

        return value * multiplier;
    }
```

`Program.cs:1` already has `using System.Globalization;`, so `NumberStyles` and `CultureInfo` resolve with no added `using`.

**3b.** `src/RemoteFileSync/Program.cs:139-141` — in the `ParseArgs` switch, replace exactly:

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

Expected: PASS — `ParseArgs_MirrorFlag_SetsMirrorDeletes`, `ParseArgs_NoMirrorFlag_DefaultsFalse`, `ParseArgs_ArchiveFolderAndRetentionFlags`, all nine `ParseArgs_ArchiveMaxSize_AcceptsSuffixes` cases, all eight `ParseArgs_ArchiveMaxSize_RejectsGarbage` cases and all four `ParseArgs_MissingValueAfterNewFlag_ThrowsArgumentException` cases green, with every pre-existing test in the file still green.

---

### Task 1.6: `PrintUsage()` documents the new flags

`PrintUsage` writes to stderr and has no automated coverage in this repo; it is verified by running the binary. No test step.

- [ ] **Step 1: Implement**

`src/RemoteFileSync/Program.cs:197-199` — replace exactly:

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

`src/RemoteFileSync/Program.cs:205-206` (line numbers as on `main`; the edit above shifts them by +7) — replace exactly:

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

```bash
dotnet run -c Release --project src/RemoteFileSync -- client
```

Expected: `Error: --folder is required.` followed by usage text listing `--mode`, `--mirror`, `--archive-folder`, `--archive-keep-days` and `--archive-max-size`; process exit code 3.

---

### Phase 1 commit

```bash
git add src/RemoteFileSync/Models/SyncMode.cs \
        src/RemoteFileSync/Models/SyncAction.cs \
        src/RemoteFileSync/Models/SyncOptions.cs \
        src/RemoteFileSync/Program.cs \
        tests/RemoteFileSync.Tests/Models/SyncActionTypeTests.cs \
        tests/RemoteFileSync.Tests/Models/SyncOptionsTests.cs \
        tests/RemoteFileSync.Tests/CliParserTests.cs \
        tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs \
        tests/RemoteFileSync.Tests/Integration/DeleteSyncTests.cs \
        tests/RemoteFileSync.Tests/Integration/DatabaseDeleteSyncTests.cs \
        tests/RemoteFileSync.Tests/Integration/DeleteThresholdTests.cs
git commit -m "feat: replace Bidirectional bool with SyncMode and add archive options

Appends SyncActionType.ConflictKeepBoth = 7 for the keep-both conflict outcome
that later phases emit. Appended rather than slotted in: these values are
serialized as single bytes by SerializeSyncPlan and cast straight back on the
peer, so renumbering an existing member would still compile while silently
repointing every action an older peer sends.

Bidirectional could only express push-or-two-way, so a pull sync had no
representation at all. Mode carries push/pull/two-way; Bidirectional stays as a
read-only shim (Mode == TwoWay) so SyncClient's seven read sites and the ExecRFS
--bidirectional alias keep working until later phases migrate them. Removing the
setter forces every assignment to move now: one in Program.cs and seven in the
integration tests, all migrated here so no later phase has to touch them.

Adds MirrorDeletes, ArchiveFolder/EffectiveArchiveFolder, ArchiveKeepDays,
ArchiveMaxBytes and SuspiciousSkewSeconds, plus --mode, --mirror,
--archive-folder, --archive-keep-days and --archive-max-size. The archive folder
gets the same drive-root guard as the backup folder and a stricter containment
guard: the comparison pads both paths with a trailing separator so
--archive-folder pointed at the sync folder itself is rejected rather than
slipping through the prefix test. An archive inside the synced tree re-syncs to
the peer and resurrects the files it is holding.

Mode is stored but not yet read: --mode pull parses and behaves as push until
mode dispatch lands. This commit is a build-green step in the sequence, not a
shippable state.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
git push -u origin feat/deletion-sync-ancestor-merge
```

**Verification before commit:**

```bash
dotnet build -c Release
dotnet test -c Release
rg 'options\.Bidirectional\s*=[^=]|Bidirectional = (true|false|bidirectional)' src/RemoteFileSync tests/RemoteFileSync.Tests
```

Expected: build reports 0 errors; the full test run is green; `rg` prints nothing (proving no `Bidirectional` write survives for a later phase to trip over).

**Existing tests knowingly changed — all mechanical, none alters an assertion or a sync direction:**

| File:line | Change |
|---|---|
| `EndToEndTests.cs:52` | `Bidirectional = false` → `Mode = SyncMode.Push` |
| `EndToEndTests.cs:86` | `Bidirectional = true` → `Mode = SyncMode.TwoWay` |
| `EndToEndTests.cs:126` | `Bidirectional = true` → `Mode = SyncMode.TwoWay` |
| `EndToEndTests.cs:158` | `Bidirectional = bidirectional` → `Mode = bidirectional ? SyncMode.TwoWay : SyncMode.Push` |
| `DeleteSyncTests.cs:53` | same ternary rewrite |
| `DatabaseDeleteSyncTests.cs:56` | same ternary rewrite |
| `DeleteThresholdTests.cs:53` | `Bidirectional = true` → `Mode = SyncMode.TwoWay` |

Every mapping is `true -> SyncMode.TwoWay`, `false -> SyncMode.Push`, so each test exercises exactly the sync direction it did before. `CliParserTests.cs:97` and `:116` are **unchanged**: they read `result.Bidirectional`, which the shim still answers, and they now additionally prove the `--bidirectional` / `-b` alias routes through `Mode`. No ExecRFS file changes — `SyncProfile.Bidirectional` is a separate settable `bool`, and `CommandBuilder` keeps emitting `--bidirectional`, which remains a supported alias.

---

## Phase 2: Pure types — `AncestorRow`, `ChangeDetector`, `ClockSkew`, `PlanResult`

**Goal:** Land every new *pure* type the ancestor-merge redesign needs — the ancestor row itself, the change primitive that reads it, the clock-skew correction, and the structured plan result — as four brand-new files with **zero edits to any existing source or test file**. Phases 3, 4 and 6 all reference these types in their public signatures; delivering them first means no later phase has to invent a type it does not own, and no later phase's "Replace exactly" anchor is disturbed by this one.

**Why this phase exists at all (ordering fix):** the previous draft created `AncestorRow` inside the `SyncEngine` phase while the `SyncDatabase` phase — which runs *earlier* — already declared `AncestorRow? GetRow(string path)` and `Dictionary<string, AncestorRow> LoadAll()` on its public surface. The database phase could not compile. Hoisting all four pure types into a single call-site-free phase removes that inversion permanently and gives every downstream phase a green build to start from.

**Files:**
- Create: `src/RemoteFileSync/Sync/AncestorRow.cs`
- Create: `src/RemoteFileSync/Sync/ChangeDetector.cs`
- Create: `src/RemoteFileSync/Sync/ClockSkew.cs`
- Create: `src/RemoteFileSync/Sync/PlanResult.cs` (holds `PlanResult`, `ResurrectionInfo`, `ConflictInfo`)
- Test: `tests/RemoteFileSync.Tests/Sync/ChangeDetectorTests.cs` (new)
- Test: `tests/RemoteFileSync.Tests/Sync/ClockSkewTests.cs` (new)
- Test: `tests/RemoteFileSync.Tests/Sync/PlanTypesTests.cs` (new)

**Modified: none.** Both projects use SDK-style globbing (`src/RemoteFileSync/RemoteFileSync.csproj`, `tests/RemoteFileSync.Tests/RemoteFileSync.Tests.csproj`), so new `.cs` files under those trees compile with no project-file edit. `src/RemoteFileSync/Sync/` currently contains only `ConflictResolver.cs`, `FileScanner.cs` and `SyncEngine.cs`, so none of the four new filenames collides. `tests/RemoteFileSync.Tests/Sync/` currently contains only `ConflictResolverTests.cs`, `FileScannerTests.cs` and `SyncEngineTests.cs` — likewise no collision.

**Interfaces:**

- **Consumes (Phase 1 — must have landed, this phase does not compile otherwise):**
  - `public const int SyncOptions.SuspiciousSkewSeconds = 60;` — read by `ClockSkew.IsSuspicious` and by `ClockSkewTests`' `[InlineData]` rows, so it must be a `const` (an attribute argument), not a property.
- **Consumes (already on `main`, unchanged by any phase):**
  - `RemoteFileSync.Models.FileEntry` — constructor `FileEntry(string relativePath, long fileSize, DateTime lastModifiedUtc)` at `src/RemoteFileSync/Models/FileEntry.cs:9`; properties `RelativePath`, `FileSize`, `LastModifiedUtc` at `:5-7`.
  - `RemoteFileSync.Models.SyncPlanEntry` — `src/RemoteFileSync/Models/SyncAction.cs:14-26`, constructor `SyncPlanEntry(SyncActionType action, string relativePath)` at `:19`. `PlanResult.Entries` is a list of these.
- **Consumes no local from any earlier phase.** This phase declares no local variable inside any existing method and edits no existing method body, so there is no `archive` / `mode` / `skew` local to reuse or redeclare. CS0128 is not reachable from this phase.
- **Produces (every one of these is consumed by a strictly higher-numbered phase):**
  - `public sealed record RemoteFileSync.Sync.AncestorRow(string Path, long ClientSize, long ClientMtimeTicks, long ServerSize, long ServerMtimeTicks, string Status, long LastSyncedTicks, long? DeletedUtcTicks)` — consumed by Phase 3 (protocol phase does not use it), Phase 4 (`SyncDatabase.GetRow` / `LoadAll` return it), Phase 6 (`ComputePlan`'s `ancestor` parameter).
  - `public static readonly TimeSpan ChangeDetector.Tolerance` and `public static bool ChangeDetector.Unchanged(FileEntry current, long rowSize, long rowMtimeTicks)` — consumed by Phase 6. **Phase 6 must route every "did this side change?" question through `Unchanged`, including the Push/Pull deletion gate**, which the contract defines as "ancestor row says the peer had it **and was unchanged**, OR mirrorDeletes" (CONTRACT.md, Push/Pull tables). A bare `Status == "exists"` test is not that rule. Phase 2 supplies the primitive; Phase 6 owns the call sites.
  - `public readonly record struct RemoteFileSync.Sync.ClockSkew(TimeSpan Offset)` with `static ClockSkew None { get; }`, `static ClockSkew Measure(long clientSentTicks, long serverTicks, long clientRecvTicks)`, `DateTime NormaliseServerTime(DateTime serverUtc)`, `bool IsSuspicious { get; }` — consumed by Phase 3 (measures it from the v3 handshake and warns) and Phase 6 (`ComputePlan`'s `skew` parameter).
  - `public sealed class RemoteFileSync.Sync.PlanResult` with `List<SyncPlanEntry> Entries`, `List<ResurrectionInfo> Resurrections`, `List<ConflictInfo> Conflicts` (all `init`, all default-initialised); `public sealed record ResurrectionInfo(string Path, bool KeptClientCopy, long KeptSize, long KeptMtimeTicks)`; `public sealed record ConflictInfo(string Path, long ClientSize, long ClientMtimeTicks, long ServerSize, long ServerMtimeTicks)` — `PlanResult` is Phase 6's `ComputePlan` return type; `Resurrections` and `Conflicts` are drained by the client-wiring phase into `LogResurrection` / `LogConflict` after the transfer phase succeeds.

**Known-inert for this phase, by design.** Nothing in the production tree calls any of these four types when Phase 2 lands. That is the point: a phase with zero call sites cannot collide with another phase's edit region, and it cannot change runtime behaviour, so `dotnet test` at the Phase 2 commit must show exactly the pre-phase results plus the new unit tests. If any pre-existing test changes status during this phase, something outside the four new files was touched and must be reverted.

---

### Task 2.1: `AncestorRow` and `ChangeDetector`

`AncestorRow` and `ChangeDetector` land together because the detector's whole job is to answer a question phrased in the row's terms, and the tests for one read most clearly beside the other.

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
        // The size half of the check, isolated. An in-place rewrite that lands in the same mtime
        // slot is invisible to a timestamp-only comparison; the engine would then read the file
        // as untouched and let the peer's deletion propagate over a live edit.
        var current = new FileEntry("f.txt", 250, RowTime);
        Assert.False(ChangeDetector.Unchanged(current, 100, RowTime.Ticks));
    }

    [Fact]
    public void SizeChanged_MtimeInsideTolerance_ReportsChanged()
    {
        // Same failure one step subtler: the mtime moved, but by less than the tolerance, so the
        // timestamp half votes "unchanged". Size must still veto.
        var current = new FileEntry("f.txt", 250, RowTime.AddSeconds(1));
        Assert.False(ChangeDetector.Unchanged(current, 100, RowTime.Ticks));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.5)]
    [InlineData(-1.5)]
    [InlineData(2.0)]
    [InlineData(-2.0)]
    public void MtimeDriftWithinTolerance_Unchanged(double seconds)
    {
        var current = new FileEntry("f.txt", 100, RowTime.AddSeconds(seconds));
        Assert.True(ChangeDetector.Unchanged(current, 100, RowTime.Ticks));
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(-3.0)]
    public void MtimeDriftBeyondTolerance_ReportsChanged(double seconds)
    {
        // The mtime half of the check, isolated: size is identical in both rows, so only the
        // timestamp comparison can produce False here.
        var current = new FileEntry("f.txt", 100, RowTime.AddSeconds(seconds));
        Assert.False(ChangeDetector.Unchanged(current, 100, RowTime.Ticks));
    }

    [Fact]
    public void ToleranceIsTwoSeconds()
    {
        // Pinned because the decision tables are specified against this exact window and the
        // integration fixtures stamp files relative to it.
        Assert.Equal(TimeSpan.FromSeconds(2), ChangeDetector.Tolerance);
    }
}
```

Create `tests/RemoteFileSync.Tests/Sync/PlanTypesTests.cs` — the `AncestorRow` half now; the `PlanResult` half is added in Task 2.3:

```csharp
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class PlanTypesTests
{
    [Fact]
    public void AncestorRow_PositionalOrderIsClientThenServer()
    {
        // Every value is distinct so a transposed parameter in the record declaration is caught.
        // The order matters beyond style: the Push table deletes on the server when the CLIENT
        // columns say the client had the file unchanged, and the Pull table is the mirror. Swap
        // the two pairs and every one-sided deletion resolves against the wrong side.
        var row = new AncestorRow(
            Path: "docs/report.docx",
            ClientSize: 11,
            ClientMtimeTicks: 22,
            ServerSize: 33,
            ServerMtimeTicks: 44,
            Status: "exists",
            LastSyncedTicks: 55,
            DeletedUtcTicks: 66);

        Assert.Equal("docs/report.docx", row.Path);
        Assert.Equal(11, row.ClientSize);
        Assert.Equal(22, row.ClientMtimeTicks);
        Assert.Equal(33, row.ServerSize);
        Assert.Equal(44, row.ServerMtimeTicks);
        Assert.Equal("exists", row.Status);
        Assert.Equal(55, row.LastSyncedTicks);
        Assert.Equal(66, row.DeletedUtcTicks);
    }

    [Fact]
    public void AncestorRow_LiveRowHasNoDeletionTimestamp()
    {
        // DeletedUtcTicks is nullable precisely so "exists" rows carry no deletion instant.
        // Making it non-nullable would force a sentinel that the tombstone purge would then
        // read as a real deletion date.
        var row = new AncestorRow("a.txt", 1, 2, 1, 2, "exists", 3, null);
        Assert.Null(row.DeletedUtcTicks);
    }
}
```

**Teeth check.** Every assertion above is unreachable before Step 3 because the types do not exist. Beyond that, each test isolates one mutation: drop the size comparison from `Unchanged` and `SizeChanged_MtimeIdentical_ReportsChanged` plus `SizeChanged_MtimeInsideTolerance_ReportsChanged` go red while everything else stays green; drop the mtime comparison and `MtimeDriftBeyondTolerance_ReportsChanged` goes red alone; change the tolerance to 1s and the `1.5`/`-1.5` rows plus `ToleranceIsTwoSeconds` go red; transpose the client/server pairs in the record and `AncestorRow_PositionalOrderIsClientThenServer` goes red alone.

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ChangeDetectorTests|FullyQualifiedName~PlanTypesTests"`

Expected: FAIL — the test project does not compile.
- `error CS0103: The name 'ChangeDetector' does not exist in the current context` at every `ChangeDetector.Unchanged(...)` and `ChangeDetector.Tolerance` reference in `ChangeDetectorTests.cs`.
- `error CS0246: The type or namespace name 'AncestorRow' could not be found (are you missing a using directive or an assembly reference?)` at both `new AncestorRow(...)` sites in `PlanTypesTests.cs`.

- [ ] **Step 3: Implement**

Create `src/RemoteFileSync/Sync/AncestorRow.cs`:

```csharp
namespace RemoteFileSync.Sync;

/// <summary>
/// What the two sides looked like the last time they were known to agree. Storing BOTH sides
/// separately is the whole point: a single snapshot cannot tell an edited client copy from an
/// edited server copy, and that missing distinction is how a one-sided deletion used to be
/// mistaken for consensus and propagated over a live edit.
/// </summary>
/// <param name="Path">Relative path, forward-slash separated, matched case-insensitively.</param>
/// <param name="Status">"exists" while both sides hold the file; "deleted" once tombstoned.</param>
/// <param name="DeletedUtcTicks">
/// Null while the row is live. Set when the row is tombstoned, so the tombstone purge has a real
/// deletion instant to age against instead of reusing LastSyncedTicks as a sentinel.
/// </param>
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

/// <summary>
/// The single primitive that answers "has this side changed since the ancestor row was written?".
/// Every changed/unchanged decision in the planner must go through here so the two halves of the
/// test — size and mtime — can never drift apart between call sites.
/// </summary>
public static class ChangeDetector
{
    /// <summary>
    /// Filesystems round mtimes (FAT to 2s, some SMB shares to 1s), so a byte-identical file can
    /// come back with a slightly different stamp after a round trip. Matches the window
    /// <see cref="ConflictResolver"/> has always used for the same reason
    /// (src/RemoteFileSync/Sync/ConflictResolver.cs:7).
    /// </summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// True when <paramref name="current"/> still matches what the ancestor row recorded for that
    /// side. Both halves are required. Size is compared exactly and first: sizes never drift, and
    /// an in-place rewrite that changes length while landing inside the mtime tolerance window
    /// would otherwise read as untouched — which is exactly the state the decision tables treat
    /// as "safe to delete on this side".
    /// </summary>
    /// <param name="rowSize">The size column for the side being tested (client or server).</param>
    /// <param name="rowMtimeTicks">The mtime column for that same side.</param>
    public static bool Unchanged(FileEntry current, long rowSize, long rowMtimeTicks)
    {
        if (current.FileSize != rowSize) return false;
        return Math.Abs(current.LastModifiedUtc.Ticks - rowMtimeTicks) <= Tolerance.Ticks;
    }
}
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ChangeDetectorTests|FullyQualifiedName~PlanTypesTests"`

Expected: PASS. Green methods: `ChangeDetectorTests.SameSizeSameMtime_Unchanged`, `SizeChanged_MtimeIdentical_ReportsChanged`, `SizeChanged_MtimeInsideTolerance_ReportsChanged`, `MtimeDriftWithinTolerance_Unchanged` (all five rows), `MtimeDriftBeyondTolerance_ReportsChanged` (both rows), `ToleranceIsTwoSeconds`; `PlanTypesTests.AncestorRow_PositionalOrderIsClientThenServer`, `AncestorRow_LiveRowHasNoDeletionTimestamp`.

---

### Task 2.2: `ClockSkew`

- [ ] **Step 1: Write the failing test**

Create `tests/RemoteFileSync.Tests/Sync/ClockSkewTests.cs`:

```csharp
using RemoteFileSync.Models;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class ClockSkewTests
{
    private static readonly DateTime Base = new(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Measure_ServerAhead_RecoversOffsetWithoutTransitTime()
    {
        // Server clock runs 5 minutes fast; the handshake round-trip takes 200ms and the server
        // stamps its reply at the midpoint. The estimate must recover exactly the 5 minutes and
        // none of the transit time — folding transit into the offset would make every sync over
        // a slow link look like a clock problem.
        var expected = TimeSpan.FromMinutes(5);
        long clientSent = Base.Ticks;
        long clientRecv = clientSent + TimeSpan.FromMilliseconds(200).Ticks;
        long serverTicks = clientSent + TimeSpan.FromMilliseconds(100).Ticks + expected.Ticks;

        var skew = ClockSkew.Measure(clientSent, serverTicks, clientRecv);

        Assert.Equal(expected, skew.Offset);
    }

    [Fact]
    public void Measure_ServerBehind_ProducesNegativeOffset()
    {
        var behind = TimeSpan.FromSeconds(90);
        long clientSent = Base.Ticks;
        long clientRecv = clientSent + TimeSpan.FromMilliseconds(40).Ticks;
        long serverTicks = clientSent + TimeSpan.FromMilliseconds(20).Ticks - behind.Ticks;

        var skew = ClockSkew.Measure(clientSent, serverTicks, clientRecv);

        Assert.Equal(-behind, skew.Offset);
    }

    [Fact]
    public void Measure_ClocksAgree_ProducesZeroOffset()
    {
        long clientSent = Base.Ticks;
        long clientRecv = clientSent + TimeSpan.FromMilliseconds(80).Ticks;
        long serverTicks = clientSent + TimeSpan.FromMilliseconds(40).Ticks;

        Assert.Equal(TimeSpan.Zero, ClockSkew.Measure(clientSent, serverTicks, clientRecv).Offset);
    }

    [Fact]
    public void NormaliseServerTime_SubtractsOffsetAndKeepsUtcKind()
    {
        var skew = new ClockSkew(TimeSpan.FromMinutes(5));
        var serverUtc = new DateTime(2026, 7, 20, 10, 5, 0, DateTimeKind.Utc);

        var normalised = skew.NormaliseServerTime(serverUtc);

        Assert.Equal(new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc), normalised);
        // Kind must survive: a normalised time that silently became Unspecified compares wrong
        // against the client's Utc mtimes everywhere downstream.
        Assert.Equal(DateTimeKind.Utc, normalised.Kind);
    }

    [Fact]
    public void NormaliseServerTime_NegativeOffsetMovesForward()
    {
        var skew = new ClockSkew(TimeSpan.FromMinutes(-5));
        var serverUtc = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 7, 20, 10, 5, 0, DateTimeKind.Utc),
                     skew.NormaliseServerTime(serverUtc));
    }

    [Fact]
    public void None_IsZeroNotSuspiciousAndIdentity()
    {
        Assert.Equal(TimeSpan.Zero, ClockSkew.None.Offset);
        Assert.False(ClockSkew.None.IsSuspicious);
        Assert.Equal(Base, ClockSkew.None.NormaliseServerTime(Base));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(SyncOptions.SuspiciousSkewSeconds - 1, false)]
    [InlineData(SyncOptions.SuspiciousSkewSeconds, false)]
    [InlineData(SyncOptions.SuspiciousSkewSeconds + 1, true)]
    [InlineData(-(SyncOptions.SuspiciousSkewSeconds + 1), true)]
    public void IsSuspicious_TripsBothDirectionsStrictlyAboveThreshold(int offsetSeconds, bool expected)
    {
        // Strictly above, and symmetric: a server an hour behind mis-orders timestamps exactly
        // as badly as one an hour ahead, so an unsigned or one-sided check would miss half the
        // real cases.
        var skew = new ClockSkew(TimeSpan.FromSeconds(offsetSeconds));
        Assert.Equal(expected, skew.IsSuspicious);
    }
}
```

**Teeth check.** `Measure_ServerAhead_RecoversOffsetWithoutTransitTime` fails if the `rtt / 2` term is dropped (the offset comes back 100ms too large) or if the subtraction is inverted. `Measure_ServerBehind_ProducesNegativeOffset` fails if the result is ever clamped or absolute-valued. `NormaliseServerTime_SubtractsOffsetAndKeepsUtcKind` and its negative twin fail if the operator is `+` instead of `-`. The `IsSuspicious` theory fails on `>=` instead of `>` (the at-threshold row), and fails on `Offset.TotalSeconds > …` without `Math.Abs` (the negative row).

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ClockSkewTests"`

Expected: FAIL — the test project does not compile.
- `error CS0246: The type or namespace name 'ClockSkew' could not be found (are you missing a using directive or an assembly reference?)` at each `new ClockSkew(...)`.
- `error CS0103: The name 'ClockSkew' does not exist in the current context` at each `ClockSkew.Measure(...)` and `ClockSkew.None`.

- [ ] **Step 3: Implement**

Create `src/RemoteFileSync/Sync/ClockSkew.cs`:

```csharp
using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

/// <summary>
/// Difference between the peer's wall clock and ours, measured over the handshake round-trip.
/// Newest-wins resolution compares an mtime stamped by the server against one stamped by the
/// client; on machines whose clocks disagree that comparison picks the wrong winner and the
/// loser's edit is silently overwritten — and because the offset is constant, it picks wrong
/// the same way on every subsequent run, so the same bytes are re-copied forever. Every
/// cross-side timestamp comparison must go through <see cref="NormaliseServerTime"/> first.
/// </summary>
public readonly record struct ClockSkew(TimeSpan Offset)
{
    /// <summary>No correction. Use only where the peer's clock reading is genuinely unavailable.</summary>
    public static ClockSkew None { get; } = new(TimeSpan.Zero);

    /// <summary>
    /// NTP-style single-sample estimate: assume the server stamped its reply at the midpoint of
    /// the round-trip, so offset = serverTicks - (clientSentTicks + rtt/2). Halving the
    /// round-trip is what keeps ordinary network latency out of the offset — without it a slow
    /// link reads as a fast clock. Positive means the server clock is ahead of ours.
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
    /// between the two sides cannot be trusted, so the user must be told. Compared on the
    /// absolute value: a server an hour behind mis-orders timestamps exactly as badly as one an
    /// hour ahead.
    /// </summary>
    public bool IsSuspicious =>
        Math.Abs(Offset.TotalSeconds) > SyncOptions.SuspiciousSkewSeconds;
}
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ClockSkewTests"`

Expected: PASS. Green methods: `Measure_ServerAhead_RecoversOffsetWithoutTransitTime`, `Measure_ServerBehind_ProducesNegativeOffset`, `Measure_ClocksAgree_ProducesZeroOffset`, `NormaliseServerTime_SubtractsOffsetAndKeepsUtcKind`, `NormaliseServerTime_NegativeOffsetMovesForward`, `None_IsZeroNotSuspiciousAndIdentity`, `IsSuspicious_TripsBothDirectionsStrictlyAboveThreshold` (all five rows).

---

### Task 2.3: `PlanResult`, `ResurrectionInfo`, `ConflictInfo`

`ComputePlan` must stay pure — it has no `sessionId` and must never touch the database — yet the resurrection and conflict cases have to reach the review report. `PlanResult` is the channel: the planner fills it, and the caller drains it into the database *after* the transfer phase succeeds, so an aborted run records nothing.

- [ ] **Step 1: Write the failing test**

Append to `tests/RemoteFileSync.Tests/Sync/PlanTypesTests.cs`, inside the existing `PlanTypesTests` class, immediately after `AncestorRow_LiveRowHasNoDeletionTimestamp`. Exact current text of the end of the file, as Task 2.1 left it:

```csharp
    [Fact]
    public void AncestorRow_LiveRowHasNoDeletionTimestamp()
    {
        // DeletedUtcTicks is nullable precisely so "exists" rows carry no deletion instant.
        // Making it non-nullable would force a sentinel that the tombstone purge would then
        // read as a real deletion date.
        var row = new AncestorRow("a.txt", 1, 2, 1, 2, "exists", 3, null);
        Assert.Null(row.DeletedUtcTicks);
    }
}
```

Exact replacement:

```csharp
    [Fact]
    public void AncestorRow_LiveRowHasNoDeletionTimestamp()
    {
        // DeletedUtcTicks is nullable precisely so "exists" rows carry no deletion instant.
        // Making it non-nullable would force a sentinel that the tombstone purge would then
        // read as a real deletion date.
        var row = new AncestorRow("a.txt", 1, 2, 1, 2, "exists", 3, null);
        Assert.Null(row.DeletedUtcTicks);
    }

    [Fact]
    public void PlanResult_DefaultsToEmptyNonNullLists()
    {
        // The caller drains all three lists unconditionally after the transfer phase. If any of
        // them defaulted to null, a plan that produced no conflicts — the overwhelmingly common
        // case — would NullReferenceException on the drain instead of doing nothing.
        var result = new PlanResult();

        Assert.NotNull(result.Entries);
        Assert.NotNull(result.Resurrections);
        Assert.NotNull(result.Conflicts);
        Assert.Empty(result.Entries);
        Assert.Empty(result.Resurrections);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void PlanResult_ObjectInitialiserPopulatesAllThreeLists()
    {
        var result = new PlanResult
        {
            Entries = new List<SyncPlanEntry> { new(SyncActionType.SendToServer, "a.txt") },
            Resurrections = new List<ResurrectionInfo>
            {
                new("b.txt", KeptClientCopy: true, KeptSize: 10, KeptMtimeTicks: 20),
            },
            Conflicts = new List<ConflictInfo>
            {
                new("c.txt", ClientSize: 1, ClientMtimeTicks: 2, ServerSize: 3, ServerMtimeTicks: 4),
            },
        };

        Assert.Equal("a.txt", Assert.Single(result.Entries).RelativePath);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[0].Action);

        var resurrection = Assert.Single(result.Resurrections);
        Assert.Equal("b.txt", resurrection.Path);
        Assert.True(resurrection.KeptClientCopy);
        Assert.Equal(10, resurrection.KeptSize);
        Assert.Equal(20, resurrection.KeptMtimeTicks);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("c.txt", conflict.Path);
        Assert.Equal(1, conflict.ClientSize);
        Assert.Equal(2, conflict.ClientMtimeTicks);
        Assert.Equal(3, conflict.ServerSize);
        Assert.Equal(4, conflict.ServerMtimeTicks);
    }

    [Fact]
    public void ConflictInfo_UsesValueEquality()
    {
        // The end-of-sync report and the E2E suites locate entries with Assert.Contains against a
        // constructed expected value. Declaring these as classes rather than records would make
        // every such assertion a reference comparison and fail for the wrong reason.
        var a = new ConflictInfo("c.txt", 1, 2, 3, 4);
        var b = new ConflictInfo("c.txt", 1, 2, 3, 4);
        Assert.Equal(a, b);
        Assert.NotEqual(a, new ConflictInfo("c.txt", 1, 2, 3, 5));
    }

    [Fact]
    public void ResurrectionInfo_UsesValueEquality()
    {
        var a = new ResurrectionInfo("b.txt", true, 10, 20);
        var b = new ResurrectionInfo("b.txt", true, 10, 20);
        Assert.Equal(a, b);
        // KeptClientCopy is the side discriminator the report renders; it must participate.
        Assert.NotEqual(a, new ResurrectionInfo("b.txt", false, 10, 20));
    }
}
```

The `SyncPlanEntry` / `SyncActionType` references require `RemoteFileSync.Models`. Add the using. Exact current text of the top of `tests/RemoteFileSync.Tests/Sync/PlanTypesTests.cs`, as Task 2.1 left it:

```csharp
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;
```

Exact replacement:

```csharp
using RemoteFileSync.Models;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;
```

**Teeth check.** `PlanResult_DefaultsToEmptyNonNullLists` fails with a `NullReferenceException` inside `Assert.Empty` (or `Assert.NotNull` first) if any `= new()` initialiser is dropped. `PlanResult_ObjectInitialiserPopulatesAllThreeLists` fails to compile if the properties are `get`-only rather than `init`, and fails on a value assertion if any positional parameter is transposed. The two equality tests fail if `ConflictInfo` / `ResurrectionInfo` are declared `class` instead of `record`, or if a member is dropped from the positional list.

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~PlanTypesTests"`

Expected: FAIL — the test project does not compile.
- `error CS0246: The type or namespace name 'PlanResult' could not be found (are you missing a using directive or an assembly reference?)` at `new PlanResult()` and the object initialiser.
- `error CS0246: The type or namespace name 'ResurrectionInfo' could not be found ...` and `error CS0246: The type or namespace name 'ConflictInfo' could not be found ...` at their construction sites.

The Task 2.1 tests in the same file (`AncestorRow_PositionalOrderIsClientThenServer`, `AncestorRow_LiveRowHasNoDeletionTimestamp`) do not execute either while the assembly is broken; they go green again in Step 4.

- [ ] **Step 3: Implement**

Create `src/RemoteFileSync/Sync/PlanResult.cs`:

```csharp
using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

/// <summary>
/// Everything the planner learned in one pass. It is not a bare list of entries because two of
/// the decision-table outcomes carry information the plan itself cannot express: a path kept
/// because this side edited it after the peer deleted it, and a path both sides changed.
/// Both must reach the end-of-sync review report, and the planner is pure — it has no sessionId
/// and must never write to the database — so it hands them back here and the caller persists
/// them AFTER the transfer phase succeeds. An aborted run therefore records nothing.
/// </summary>
public sealed class PlanResult
{
    /// <summary>The plan proper, in the order the executor should walk it.</summary>
    public List<SyncPlanEntry> Entries { get; init; } = new();

    /// <summary>
    /// Paths kept because this side modified them after the peer deleted them. Empty on the vast
    /// majority of runs; never null, because the caller drains it unconditionally.
    /// </summary>
    public List<ResurrectionInfo> Resurrections { get; init; } = new();

    /// <summary>
    /// Paths where both sides changed since the ancestor. Same non-null contract as
    /// <see cref="Resurrections"/>.
    /// </summary>
    public List<ConflictInfo> Conflicts { get; init; } = new();
}

/// <summary>
/// A deletion that lost to an edit. Losing the edit would be unrecoverable; an unwanted
/// resurrection costs the user one more delete, so the surviving copy wins and the fact is
/// reported rather than applied silently.
/// </summary>
/// <param name="KeptClientCopy">
/// True when the client's copy survived (the server had deleted it), false when the server's
/// copy survived. The report renders the side, so it cannot be inferred later.
/// </param>
public sealed record ResurrectionInfo(string Path, bool KeptClientCopy, long KeptSize, long KeptMtimeTicks);

/// <summary>
/// Both sides changed since the ancestor. Carries each side's own size and mtime so the report
/// can show the user what the two copies were without re-scanning either tree.
/// </summary>
public sealed record ConflictInfo(string Path, long ClientSize, long ClientMtimeTicks, long ServerSize, long ServerMtimeTicks);
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~PlanTypesTests"`

Expected: PASS. Green methods: `AncestorRow_PositionalOrderIsClientThenServer`, `AncestorRow_LiveRowHasNoDeletionTimestamp`, `PlanResult_DefaultsToEmptyNonNullLists`, `PlanResult_ObjectInitialiserPopulatesAllThreeLists`, `ConflictInfo_UsesValueEquality`, `ResurrectionInfo_UsesValueEquality`.

Then re-run the whole phase's suites together:

Run: `dotnet test -c Release --filter "FullyQualifiedName~ChangeDetectorTests|FullyQualifiedName~ClockSkewTests|FullyQualifiedName~PlanTypesTests"`

Expected: PASS.

---

### Phase 2 commit

```bash
git add src/RemoteFileSync/Sync/AncestorRow.cs \
        src/RemoteFileSync/Sync/ChangeDetector.cs \
        src/RemoteFileSync/Sync/ClockSkew.cs \
        src/RemoteFileSync/Sync/PlanResult.cs \
        tests/RemoteFileSync.Tests/Sync/ChangeDetectorTests.cs \
        tests/RemoteFileSync.Tests/Sync/ClockSkewTests.cs \
        tests/RemoteFileSync.Tests/Sync/PlanTypesTests.cs
git commit -m "feat(sync): add AncestorRow, ChangeDetector, ClockSkew and PlanResult

Four pure types with no call sites yet, landed together so every later
phase has them available and none has to invent one it does not own.

AncestorRow records what BOTH sides looked like when they last agreed. A
single snapshot cannot tell an edited client copy from an edited server
copy, and that missing distinction is how a one-sided deletion was
mistaken for consensus and propagated over a live edit.

ChangeDetector.Unchanged is the one primitive that answers 'did this side
change?'. It compares size exactly as well as mtime within two seconds:
sizes never drift, and an in-place rewrite that lands inside the mtime
tolerance window would otherwise read as untouched, which the decision
tables treat as safe to delete.

ClockSkew turns the two clock readings from the handshake into an offset,
halving the round-trip so network latency does not read as a fast clock.
Without it a server whose clock is ahead wins every newest-wins
comparison forever and the same bytes are re-copied on every run.

PlanResult lets the planner report resurrections and conflicts without
touching the database, so the caller can persist them only after the
transfer phase succeeds and an aborted run records nothing.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
git push -u origin feat/deletion-sync-ancestor-merge
```

**Verification before commit:**

```bash
cd E:/RemoteFileSync
git status --short
dotnet build -c Release
dotnet test -c Release
```

Expected:
- `git status --short` lists exactly the seven files above, all with status `??` (untracked) before `git add`. **If any existing file appears as modified, this phase touched something it does not own — revert that file before committing.**
- `dotnet build -c Release`: 0 errors, 0 warnings.
- `dotnet test -c Release`: 0 failures. The pre-existing suites (`ConflictResolverTests`, `SyncEngineTests`, `FileScannerTests`, `ProtocolHandlerTests`, `SyncDatabaseTests`, the `Integration` suites, everything else) run with exactly the same results as before this phase, because no production code path calls any of the four new types yet.

**Existing tests knowingly changed:** none. This phase creates seven files and modifies zero. It edits no region owned by any other phase, declares no local inside any existing method, and introduces no call site — so nothing later in the plan needs to re-quote or re-anchor around it.

**Handoff to later phases:**
- Phase 3 (protocol v3) consumes `ClockSkew.Measure` / `IsSuspicious` in the client handshake block and leaves a `skew` local behind. It does **not** create `ClockSkew.cs`.
- Phase 4 (schema v2 / `SyncDatabase`) consumes `AncestorRow` as the return type of `GetRow` and the value type of `LoadAll`. It does **not** create `AncestorRow.cs`.
- Phase 6 (`SyncEngine` ancestor merge) consumes all four: `AncestorRow` and `ClockSkew` as `ComputePlan` parameters, `PlanResult` as its return type, and `ChangeDetector.Unchanged` for every changed/unchanged decision — including the Push and Pull deletion gates, where the contract requires "the peer had it **and was unchanged**", not merely `Status == "exists"`.

---

---

## Phase 3: Protocol v3 handshake (mode byte + clock readings)

**Goal:** Raise the wire protocol to v3 so the handshake carries the full `SyncMode` plus the delete and mirror flags (v2 had a single "bidirectional" bit), and so both sides stamp a UTC tick count that the client feeds to `ClockSkew`. This phase is the **sole owner** of both handshake blocks: no later phase re-applies this migration, so it must also leave behind the locals later phases consume.

**Files:**
- Modify: `src/RemoteFileSync/Network/ProtocolHandler.cs:8-13` (version constant + doc comment)
- Modify: `src/RemoteFileSync/Network/ProtocolHandler.cs:75-91` (the four handshake methods)
- Modify: `src/RemoteFileSync/Network/SyncClient.cs:89-113` (client handshake block)
- Modify: `src/RemoteFileSync/Network/SyncServer.cs:132-152` (server handshake block)
- Modify: `src/RemoteFileSync/Network/SyncServer.cs:304-305` and `:356` (mechanical `bidirectional` → `mode` substitution — forced, see Produces note 4)
- Test: `tests/RemoteFileSync.Tests/Network/ProtocolHandlerTests.cs` (modify lines 62-67 and 103-128)
- Test: `tests/RemoteFileSync.Tests/Network/HandshakeCompatibilityTests.cs` (new)

**Interfaces:**

*Consumes (Phase 1 — `src/RemoteFileSync/Models/SyncOptions.cs` and `SyncMode.cs`):*
- `RemoteFileSync.Models.SyncMode` — `Push = 1, Pull = 2, TwoWay = 3`, underlying type `byte`
- `SyncOptions.Mode` (`SyncMode`), `SyncOptions.MirrorDeletes` (`bool`), `SyncOptions.SuspiciousSkewSeconds` (`const int`)
- Phase 1 has already removed the `Bidirectional` setter; the read-only shim `Bidirectional => Mode == SyncMode.TwoWay` still exists. This phase does **not** touch `SyncClient.cs:73` (`var modeLabel = _options.Bidirectional ? ...`) — Phase 8 owns that line and will find it exactly as Phase 1 left it.

*Consumes (Phase 2 — `src/RemoteFileSync/Sync/ClockSkew.cs`):*
- `public readonly record struct ClockSkew(TimeSpan Offset)` with `static ClockSkew None`, `static ClockSkew Measure(long clientSentTicks, long serverTicks, long clientRecvTicks)`, `DateTime NormaliseServerTime(DateTime serverUtc)`, `bool IsSuspicious`
- `using RemoteFileSync.Sync;` is already present at `SyncClient.cs:9` and `SyncServer.cs:10`, so no using is added.

*Produces:*
1. `public const byte ProtocolHandler.ProtocolVersion = 3;`
2. The four v3 frame methods, exactly as frozen in CONTRACT.md:
   - `public static byte[] SerializeHandshake(byte version, byte syncMode, long clientSentTicks);`
   - `public static (byte version, byte syncMode, long clientSentTicks) DeserializeHandshake(byte[] data);`
   - `public static byte[] SerializeHandshakeAck(byte version, bool accepted, long serverTicks);`
   - `public static (byte version, bool accepted, long serverTicks) DeserializeHandshakeAck(byte[] data);`
3. **Exactly two sets of locals are left behind for later phases. Later phases MUST reuse them, never redeclare them (CS0128):**
   - In `SyncClient.HandleConnectionAsync`, at method scope: **`skew`** (`ClockSkew`), declared after the `if (!accepted)` block. Phase 6 passes it as the final argument of `SyncEngine.ComputePlan` in place of `ClockSkew.None`.
     - Name collision warning for Phase 8: `SyncClient.cs:119` already declares a **`string mode`** inside the `if (_options.DeleteEnabled && _db != null)` block. Phase 8 must not introduce a method-scope `mode` in `SyncClient` (CS0136).
   - In `SyncServer.HandleConnectionAsync`, at method scope: **`mode`** (`SyncMode`), **`deleteEnabled`** (`bool`), **`mirrorDeletes`** (`bool`).
4. **The `bidirectional` local in `SyncServer.HandleConnectionAsync` is deleted outright.** It had three references (`SyncServer.cs:140` declaration, `:142` log, `:305`, `:356`). Deleting the declaration would leave `:305` and `:356` as CS0103 and the phase commit would not build, so this phase performs the **minimal mechanical substitution** `bidirectional` → `mode == SyncMode.TwoWay` at both sites. That is a compile-preserving rename only — **the semantic re-gating of the transfer loops (admitting Pull, which also writes to the client) remains Phase 8's**, and Phase 8 must quote the post-edit text given in Task 3.1 step 3d/3e, not the text on `main`.
5. Post-edit anchors for Phase 8: **anchor on the quoted text below, not on line numbers.** Task 3.1 grows the `SyncClient` block by roughly 21 lines and Task 3.2 by roughly 9 more, and the `SyncServer` block by roughly 8; every downstream line number in `SyncClient.cs` and `SyncServer.cs` shifts accordingly.

*Findings fixed here:* #12 and #24 (the handshake migration has one owner — this phase; no later phase re-quotes the v2 text), #38 (the `bidirectional` local is gone and both of its consumers are rewritten in the same commit), #23 (the client's ack parse is guarded before it is dereferenced), minor #4 (the peer's mode bits are clamped, not cast), minor #34 (`mirrorDeletes` carries a comment saying why the server does not act on it).

---

### Task 3.1: v3 frames, and both call sites migrated atomically

Changing the four signatures breaks `SyncClient.cs:91,101` and `SyncServer.cs:139,146` in the same compilation unit, so the frame change and both call sites are one atomic task. Splitting them would leave the solution unbuildable at a task boundary, which makes every intermediate `dotnet test` gate unrunnable.

- [ ] **Step 1: Write the failing test**

First replace `tests/RemoteFileSync.Tests/Network/ProtocolHandlerTests.cs:62-67`.

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
        // Reading past the end of a short frame would fabricate a clock reading out of
        // whatever bytes followed and hand ClockSkew garbage, so the length guard must fire
        // before any indexing. A v2 peer's 2-byte handshake and 2-byte ack land here too.
        Assert.Throws<InvalidDataException>(() => ProtocolHandler.DeserializeHandshake(new byte[] { 2 }));
        Assert.Throws<InvalidDataException>(() => ProtocolHandler.DeserializeHandshake(new byte[] { 2, 1 }));
        Assert.Throws<InvalidDataException>(() => ProtocolHandler.DeserializeHandshake(new byte[10]));
        Assert.Throws<InvalidDataException>(() => ProtocolHandler.DeserializeHandshakeAck(Array.Empty<byte>()));
        Assert.Throws<InvalidDataException>(() => ProtocolHandler.DeserializeHandshakeAck(new byte[] { 2, 1 }));
        Assert.Throws<InvalidDataException>(() => ProtocolHandler.DeserializeHandshakeAck(new byte[9]));
    }
```

Then replace `tests/RemoteFileSync.Tests/Network/ProtocolHandlerTests.cs:103-128`.

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
        // The reserved byte is sent, and sent as zero, so both v3 peers agree on the frame
        // length; a future flag can occupy it without another version bump.
        Assert.Equal(0, bytes[10]);
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

        // Byte 1 keeps v2's polarity (0 == accepted) so a rejection is still legible to a
        // peer that only reads the first two bytes.
        var rejected = ProtocolHandler.SerializeHandshakeAck(3, accepted: false, serverTicks);
        Assert.Equal(1, rejected[1]);
        Assert.False(ProtocolHandler.DeserializeHandshakeAck(rejected).accepted);
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet build -c Release`

Expected: FAIL to build. `tests/RemoteFileSync.Tests/Network/ProtocolHandlerTests.cs` reports `CS1501: No overload for method 'SerializeHandshake' takes 3 arguments`, `CS1501: No overload for method 'SerializeHandshakeAck' takes 3 arguments`, and `CS8132: Cannot deconstruct a tuple of '2' elements into '3' variables` at the two `DeserializeHandshake`/`DeserializeHandshakeAck` deconstructions.

- [ ] **Step 3: Implement**

**3a — `src/RemoteFileSync/Network/ProtocolHandler.cs:8-13`.**

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
    /// the handshake to carry the full SyncMode plus the delete and mirror flags — v2 had a
    /// single "bidirectional" bit, which cannot express push/pull/two-way — and made both sides
    /// stamp DateTime.UtcNow.Ticks so the client can measure the peer's clock offset; see
    /// <see cref="Sync.ClockSkew"/>.
    /// Peers running different versions are rejected during handshake: a v1 peer silently
    /// ignores the trailing timestamp bytes, which makes sync never converge.
    /// </summary>
    public const byte ProtocolVersion = 3;
```

**3b — `src/RemoteFileSync/Network/ProtocolHandler.cs:75-91`.**

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
        // result[10] stays 0. It is reserved but still transmitted, so both v3 peers agree on
        // the frame length and a later flag can occupy it without another version bump.
        return result;
    }

    public static (byte version, byte syncMode, long clientSentTicks) DeserializeHandshake(byte[] data)
    {
        // Length is checked before any indexing: a short frame from an older or hostile peer
        // would otherwise read whatever followed the buffer as a clock reading, and ClockSkew
        // would silently normalise every server mtime by a garbage offset.
        if (data.Length < 11) throw new InvalidDataException("Handshake payload truncated.");
        return (data[0], data[1], BitConverter.ToInt64(data, 2));
    }

    /// <summary>
    /// v3 ack, 10 bytes: [0] version, [1] accepted (0 = accepted — v2's polarity, kept so an
    /// older peer reading only the first two bytes still sees the verdict), [2..9] serverTicks.
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

**3c — `src/RemoteFileSync/Network/SyncClient.cs:89-113`.**

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
        // Stamped immediately before the write so the round-trip ClockSkew halves is the
        // network's latency and not our own frame-building time.
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

        // Measured once per session and reused by every cross-side timestamp comparison.
        // Newest-wins resolution pits a client-stamped mtime against a server-stamped one; on
        // machines whose clocks disagree that comparison picks the wrong winner and the loser's
        // edit is overwritten with no conflict recorded.
        var skew = ClockSkew.Measure(clientSentTicks, serverTicks, clientRecvTicks);
        if (skew.IsSuspicious)
        {
            _logger.Warning(
                $"Server clock differs from this machine by {skew.Offset.TotalSeconds:+0.0;-0.0} seconds " +
                $"(threshold {SyncOptions.SuspiciousSkewSeconds}s; positive means the server is ahead). " +
                "Two-way sync breaks ties by comparing the two sides' modification times, so a skew " +
                "this large can select the older edit as the winner and overwrite the newer one. " +
                "Fix NTP on both machines before relying on two-way sync.");
        }
```

**3d — `src/RemoteFileSync/Network/SyncServer.cs:132-152`.**

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
        var (version, syncMode, _) = ProtocolHandler.DeserializeHandshake(hsData);

        // Clamped through a switch rather than cast: syncMode arrives from an unauthenticated
        // peer and 0 is not a defined SyncMode member, so a raw cast would yield an enum value
        // that every later "== SyncMode.Push" comparison reads as "not Push" and admits writes
        // to the server's tree on.
        var mode = (syncMode & 0b11) switch
        {
            2 => SyncMode.Pull,
            3 => SyncMode.TwoWay,
            _ => SyncMode.Push,
        };
        bool deleteEnabled = (syncMode & 4) != 0;
        // Decoded for reporting only. The server executes the plan the client computed, and the
        // mirror decision is already baked into that plan, so acting on this bit here would
        // apply the rule twice. Kept as a named local so the log line and Phase 8's server-side
        // delete accounting read the same value.
        bool mirrorDeletes = (syncMode & 8) != 0;
        _logger.Info($"Handshake: v{version}, mode={mode}" +
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

**3e — `src/RemoteFileSync/Network/SyncServer.cs:304-305`.** Forced by 3d: the `bidirectional` local no longer exists.

Exact current code being replaced:

```csharp
        // 8. Send files to client (SendToClient + ServerOnly) if bidirectional
        if (bidirectional)
```

Exact replacement:

```csharp
        // 8. Send files to client (SendToClient + ServerOnly). Two-way only at this stage; the
        // mode-dispatch phase widens the condition to admit Pull, which also writes to the
        // client. Behaviour is unchanged here — this is the rename forced by dropping the
        // `bidirectional` local in favour of `mode`.
        if (mode == SyncMode.TwoWay)
```

**3f — `src/RemoteFileSync/Network/SyncServer.cs:356`.** Same reason.

Exact current code being replaced:

```csharp
        if (deleteEnabled && bidirectional)
```

Exact replacement:

```csharp
        if (deleteEnabled && mode == SyncMode.TwoWay)
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ProtocolHandlerTests"`

Expected: PASS. Specifically green: `SerializeHandshake_CorrectBytes`, `DeserializeHandshake_ParsesCorrectly`, `Handshake_SyncMode_RoundTrips` (all four `[InlineData]` rows), `HandshakeAck_RoundTripsServerTicks`, `DeserializeHandshake_RejectsTruncatedPayload`.

Run: `dotnet test -c Release --filter "FullyQualifiedName~EndToEndTests|FullyQualifiedName~DeleteSyncTests"`

Expected: PASS. Both peers are in-process and speak v3, so the migration is invisible to them.

---

### Task 3.2: The client survives an older server's ack (finding #23)

The v3 ack guard added in Task 3.1 throws on a 2-byte v2 ack, so the carefully worded "Protocol mismatch: server speaks v2…" branch became unreachable for the exact scenario it was written for. The exception escapes `RunAsync` uncaught (`SyncClient.cs:80` is a bare `return await HandleConnectionAsync(stream, ct);`) and the user gets `Fatal error: HandshakeAck payload truncated.` from `Program.cs`.

- [ ] **Step 1: Write the failing test**

Create `tests/RemoteFileSync.Tests/Network/HandshakeCompatibilityTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;

namespace RemoteFileSync.Tests.Network;

public class HandshakeCompatibilityTests : IDisposable
{
    private readonly string _folder;

    public HandshakeCompatibilityTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"rfs_hs_{Guid.NewGuid()}");
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Client_AgainstOlderServerAck_ReportsMismatchInsteadOfThrowing()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var fakeV2Server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync();
            using var stream = peer.GetStream();
            // Drain the client's handshake, then reply the way a v2 build does: two bytes,
            // version 2, byte[1] == 1 meaning "rejected".
            await ProtocolHandler.ReadMessageAsync(stream);
            await ProtocolHandler.WriteMessageAsync(
                stream, MessageType.HandshakeAck, new byte[] { 2, 1 });
        });

        var options = new SyncOptions
        {
            IsServer = false,
            Host = "127.0.0.1",
            Port = port,
            Folder = _folder,
            Mode = SyncMode.Push,
        };
        using var logger = new SyncLogger(verbose: false, logFile: null, suppressConsole: true);
        var client = new SyncClient(options, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int exit = await client.RunAsync(cts.Token);

        await fakeV2Server;
        listener.Stop();

        // Without the guard the 2-byte ack trips the v3 length check and InvalidDataException
        // escapes RunAsync, so this await throws and no exit code is ever produced.
        // 2 = connection failure, matching the existing protocol-mismatch branch.
        Assert.Equal(2, exit);
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~HandshakeCompatibilityTests"`

Expected: FAIL — `Client_AgainstOlderServerAck_ReportsMismatchInsteadOfThrowing` fails with `System.IO.InvalidDataException : HandshakeAck payload truncated.` thrown out of `SyncClient.RunAsync`.

- [ ] **Step 3: Implement**

Modify the ack deconstruction in `src/RemoteFileSync/Network/SyncClient.cs`. Anchor on the text Task 3.1 left behind (its line number has shifted from 101).

Exact current code being replaced:

```csharp
        var (serverVersion, accepted, serverTicks) = ProtocolHandler.DeserializeHandshakeAck(ackData);
```

Exact replacement:

```csharp
        byte serverVersion;
        bool accepted;
        long serverTicks;
        try
        {
            (serverVersion, accepted, serverTicks) = ProtocolHandler.DeserializeHandshakeAck(ackData);
        }
        catch (InvalidDataException)
        {
            // A v2 server answers with a 2-byte ack, which the v3 length guard rejects before
            // the version byte can be read — so the "server speaks v{n}" branch below would
            // never run for the one case it was written for. Without this catch the exception
            // escapes RunAsync entirely and the user sees Program.cs's generic
            // "Fatal error: HandshakeAck payload truncated." with no idea what to do about it.
            _logger.Error(
                "Protocol mismatch: the server's HandshakeAck is shorter than protocol " +
                $"v{ProtocolHandler.ProtocolVersion} requires, so the server is an older build. " +
                "Upgrade both sides to the same build.");
            return 2;
        }
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~HandshakeCompatibilityTests"`

Expected: PASS — `Client_AgainstOlderServerAck_ReportsMismatchInsteadOfThrowing` green.

---

### Task 3.3: The server answers an older client instead of dropping the socket

The mirror image of Task 3.2. A v2 client sends a 2-byte handshake; the v3 length guard throws before the version byte is readable, the accept loop's `catch (Exception ex)` at `SyncServer.cs:96` logs "Session failed" and closes the socket, and the peer sees an unexplained disconnect rather than a version verdict.

- [ ] **Step 1: Write the failing test**

Add to `tests/RemoteFileSync.Tests/Network/HandshakeCompatibilityTests.cs`, inside the existing class, immediately after `Client_AgainstOlderServerAck_ReportsMismatchInsteadOfThrowing`:

```csharp
    [Fact]
    public async Task Server_AgainstOlderClientHandshake_StillSendsAWellFormedAck()
    {
        var options = new SyncOptions
        {
            IsServer = true,
            Once = true,
            BindAddress = "127.0.0.1",
            Port = GetFreePort(),
            Folder = _folder,
        };
        using var logger = new SyncLogger(verbose: false, logFile: null, suppressConsole: true);
        var server = new SyncServer(options, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);

        using var peer = new TcpClient();
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await peer.ConnectAsync(IPAddress.Loopback, options.Port, cts.Token);
                break;
            }
            catch (SocketException) when (attempt < 20)
            {
                // The listener may not have bound yet; RunAsync starts it asynchronously.
                await Task.Delay(100, cts.Token);
            }
        }

        using var stream = peer.GetStream();
        // A v2 client's handshake is exactly two bytes: version 2, syncMode 0.
        await ProtocolHandler.WriteMessageAsync(
            stream, MessageType.Handshake, new byte[] { 2, 0 }, cts.Token);

        // Without the truncation guard the server throws before writing anything, the accept
        // loop closes the socket, and this read fails with EndOfStreamException.
        var (type, payload) = await ProtocolHandler.ReadMessageAsync(stream, cts.Token);

        Assert.Equal(MessageType.HandshakeAck, type);
        var (version, accepted, _) = ProtocolHandler.DeserializeHandshakeAck(payload);
        Assert.Equal(ProtocolHandler.ProtocolVersion, version);
        Assert.False(accepted);

        Assert.Equal(3, await serverTask);
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~HandshakeCompatibilityTests"`

Expected: FAIL — `Server_AgainstOlderClientHandshake_StillSendsAWellFormedAck` fails with `System.IO.EndOfStreamException : Connection closed unexpectedly.` at the `ReadMessageAsync` call. `Client_AgainstOlderServerAck_ReportsMismatchInsteadOfThrowing` stays green.

- [ ] **Step 3: Implement**

Modify the handshake decode in `src/RemoteFileSync/Network/SyncServer.cs`. Anchor on the text Task 3.1 step 3d left behind (its line number has shifted from 139).

Exact current code being replaced:

```csharp
        var (version, syncMode, _) = ProtocolHandler.DeserializeHandshake(hsData);
```

Exact replacement:

```csharp
        byte version;
        byte syncMode;
        try
        {
            (version, syncMode, _) = ProtocolHandler.DeserializeHandshake(hsData);
        }
        catch (InvalidDataException)
        {
            // A v2 client's handshake is 2 bytes, which the v3 length guard rejects before its
            // version byte can be read, so the versionOk path below cannot report the mismatch.
            // Answer with a well-formed rejecting ack anyway: otherwise the accept loop's
            // catch-all closes the socket and the peer reports an unexplained disconnect
            // instead of "upgrade both sides".
            await ProtocolHandler.WriteMessageAsync(stream, MessageType.HandshakeAck,
                ProtocolHandler.SerializeHandshakeAck(
                    ProtocolHandler.ProtocolVersion, accepted: false, DateTime.UtcNow.Ticks), ct);
            _logger.Error("Rejected client: its handshake is shorter than protocol " +
                          $"v{ProtocolHandler.ProtocolVersion} requires — the peer is an older build.");
            return 3;
        }
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~HandshakeCompatibilityTests"`

Expected: PASS — both `Client_AgainstOlderServerAck_ReportsMismatchInsteadOfThrowing` and `Server_AgainstOlderClientHandshake_StillSendsAWellFormedAck` green.

Run: `dotnet test -c Release --filter "FullyQualifiedName~ProtocolHandlerTests"`

Expected: PASS.

---

### Phase 3 commit

```bash
git add src/RemoteFileSync/Network/ProtocolHandler.cs \
        src/RemoteFileSync/Network/SyncClient.cs \
        src/RemoteFileSync/Network/SyncServer.cs \
        tests/RemoteFileSync.Tests/Network/ProtocolHandlerTests.cs \
        tests/RemoteFileSync.Tests/Network/HandshakeCompatibilityTests.cs
git commit -m "feat: protocol v3 handshake carries sync mode and clock readings

The v2 handshake had a single bit for 'bidirectional', which cannot express
push/pull/two-way plus the delete and mirror flags. v3 widens the mode byte
and has both sides stamp DateTime.UtcNow.Ticks, so the client can measure the
peer's clock offset over the round-trip and warn before two-way sync resolves
a tie using timestamps from disagreeing clocks.

Both sides now guard the frame length before dereferencing it. An older peer's
2-byte frame previously escaped as an unhandled InvalidDataException on the
client and as a silently dropped socket on the server; each side now reports a
version mismatch the user can act on.

The server's 'bidirectional' local is replaced by a clamped SyncMode 'mode'
local plus deleteEnabled and mirrorDeletes. The two transfer-loop conditions
that read it are renamed with no change in behaviour.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
git push -u origin feat/deletion-sync-ancestor-merge
```

**Verification before commit:**

```bash
dotnet build -c Release
dotnet test -c Release
```

Expected: 0 build errors, 0 build warnings introduced by this phase, whole suite green.

Existing tests knowingly rewritten, and why each had to change:
- `ProtocolHandlerTests.SerializeHandshake_CorrectBytes` — asserted `Assert.Equal(2, bytes.Length)`; the v3 frame is 11 bytes.
- `ProtocolHandlerTests.DeserializeHandshake_ParsesCorrectly` — deconstructed a 2-tuple; the return type is now a 3-tuple.
- `ProtocolHandlerTests.Handshake_SyncMode_RoundTrips` — same 2-tuple problem, and promoted from `[Fact]` to `[Theory]` because the mode byte now carries three independent fields (mode, delete, mirror) that each need coverage.
- `ProtocolHandlerTests.DeserializeHandshake_RejectsTruncatedPayload` — its `new byte[] { 2 }` case still throws, but the interesting new boundaries (a 2-byte v2 frame, a 10-byte near-miss) did not exist under v2.

No other existing test references the handshake: `ProtocolHandlerTests.cs` is the only test file naming `SerializeHandshake`, `DeserializeHandshake`, `SerializeHandshakeAck` or `DeserializeHandshakeAck`. The integration suites (`EndToEndTests`, `DeleteSyncTests`, `DatabaseDeleteSyncTests`, `DeleteThresholdTests`) run both peers in-process, so both speak v3 and none of them observe the version change.

**Handoff to later phases — do not re-apply any of the above:**
- `SyncClient`: reuse the method-scope `skew` local. Do not declare a method-scope `mode` (`SyncClient.cs` already has a block-scoped `string mode` in the DB-session block).
- `SyncServer`: reuse the method-scope `mode`, `deleteEnabled`, `mirrorDeletes` locals. `bidirectional` no longer exists anywhere in `src/`.
- Phase 8 owns the semantic widening of `if (mode == SyncMode.TwoWay)` at server step 8 and step 9, and must quote those two conditions as this phase leaves them (steps 3e and 3f above), not as they appear on `main`.

---

## Phase 4: Schema v2 — per-side ancestor columns, `ConflictDetail`, `PairMarker`, and the new `SyncDatabase` API

**Goal:** Replace the single-sided v1 `files` table with the schema v2 ancestor table (separate client/server size+mtime, tombstone retention timestamps), migrate existing v1 databases in place under one transaction, add the structured `ConflictDetail` payload and the `PairMarker` sentinel, and expose the `AncestorRow`-based `SyncDatabase` API that Phases 6, 8 and 9 consume.

**Files:**
- Create: `src/RemoteFileSync/State/PairMarker.cs`
- Create: `src/RemoteFileSync/State/ConflictDetail.cs`
- Modify: `src/RemoteFileSync/State/SyncDatabase.cs` — `:1-3`, `:7-13`, `:24-32`, `:37-39`, `:65-112`, `:180-242`, `:244-327`, `:341-383`, `:385`
- Modify: `tests/RemoteFileSync.Tests/State/SyncDatabaseTests.cs:180-188` (one assertion deleted)
- Test: `tests/RemoteFileSync.Tests/State/PairMarkerTests.cs` (new)
- Test: `tests/RemoteFileSync.Tests/State/ConflictDetailTests.cs` (new)
- Test: `tests/RemoteFileSync.Tests/State/SyncDatabaseSchemaV2Tests.cs` (new)
- Test: `tests/RemoteFileSync.Tests/State/SyncDatabaseSchemaMigrationTests.cs` (new)

**Edit-ownership statement.** Per CONTRACT.md, `SyncDatabase.cs` is owned **solely by this phase**. Phases 1, 2 and 3 touch `SyncOptions.cs`, `Program.cs`, `ProtocolHandler.cs`, `SyncClient.cs:89-113` and `SyncServer.cs:132-152` — none of them touch `SyncDatabase.cs`, so every "Replace exactly" block below quotes the file **as it exists on `main` today**, verified by reading it. This phase touches **no** file under `src/RemoteFileSync/Network/` or `src/RemoteFileSync/Sync/`. `tests/.../State/SyncDatabaseTests.cs` is a unit-test file in `State/`, not under `Integration/`, and contains no `Bidirectional =` assignment, so it is claimed by neither Phase 1 nor Phase 10.

**Interfaces:**

- **Consumes (from Phase 2, already landed):**
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
`SyncDatabase.cs` gains `using RemoteFileSync.Sync;` to reach it. No local from an earlier phase is redeclared — this phase declares no locals in any file another phase owns.

- **Produces:**
```csharp
namespace RemoteFileSync.State;

public record ConflictEntry(string Path, string Detail, DateTime Timestamp);

public sealed record ConflictDetail(
    long ClientSize, long ClientMtimeTicks,
    long ServerSize, long ServerMtimeTicks,
    string? RenamedTo)
{
    public string Encode();
    public static ConflictDetail? Decode(string? detail);
}

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
    public Dictionary<string, AncestorRow> LoadAll();          // OrdinalIgnoreCase
    public void UpsertSynced(string path,
                             long clientSize, long clientMtimeTicks,
                             long serverSize, long serverMtimeTicks,
                             long sessionId, string direction);
    public void Tombstone(string path, long sessionId, string? detail);
    public int  PurgeTombstonesOlderThan(TimeSpan age);
    public void LogConflict(string path, long sessionId, string detail);
    public void LogResurrection(string path, long sessionId, string detail);
    public IReadOnlyList<ConflictEntry> GetSessionConflicts(long sessionId);
    public IReadOnlyList<ConflictEntry> GetSessionResurrections(long sessionId);

    // Preserved unchanged; Phase 6 needs it for the one-sided-skip fix.
    public void MarkSkipped(string path, long sessionId);
}
```

`LogConflict` and `LogResurrection` are **two separate writers** storing `action='conflict'` and `action='resurrected'` respectively, per CONTRACT.md "Corrections applied after expert review" item 2. Neither inspects `detail`; there is no prefix sniffing anywhere in this phase. Both take an already-encoded `ConflictDetail.Encode()` string — never free-form English. This closes findings #4, #7 and #18: the storage discriminator is the method called, and `ConflictDetail` carries render data only.

**Consumers downstream:** Phase 6 consumes `UpsertSynced` / `MarkSkipped` / `LoadAll` / `Tombstone`. Phase 7 consumes `ConflictDetail.Encode()` and `LogConflict`. Phase 8 consumes `PairMarker.Exists` / `PairMarker.Write`. Phase 9 consumes `GetSessionConflicts` / `GetSessionResurrections` / `ConflictDetail.Decode`.

---

### Decision: the `MarkSynced` compat shim is KEPT, and Phase 6 must fix `SyncClient.cs:185-206`

Finding #2 is the data-loss bug: `SyncClient.cs:187-194` reads

```csharp
var entry = clientManifest.Get(skip.RelativePath) ?? serverManifest.Get(skip.RelativePath);
if (entry != null)
    _db.MarkSynced(skip.RelativePath, entry.FileSize, entry.LastModifiedUtc, sessionId, "skipped");
```

— one side's manifest values written to a two-sided ancestor row with `status='exists'`. Under Pull, a client-only file gets `server_size`/`server_mtime` stamped from the client's own entry; run 2 reads `serverHadIt == true` and emits `DeleteOnClient`, destroying local-only files.

**The shim stays. Justification:**

1. **The build must be green at every phase commit** (CONTRACT.md, "PHASE ORDER AND EDIT OWNERSHIP"). Deleting `MarkSynced` breaks five production call sites (`SyncClient.cs:194, :307, :387`, plus `SyncDatabase.MigrateFromBinary` at `:454`) and eleven test call sites (`SyncDatabaseTests.cs:65,86,87,103,119,139,140,153,154,166,195,196,240,242`, `SyncEngineTests.cs:264,305,327,350,374,395`, `DeleteThresholdTests.cs:81`). Phase 4 owns none of those files. The build would be red across Phases 4 and 5 and only recover at Phase 6.
2. **The fix is already assigned.** CONTRACT.md correction 6 mandates it verbatim: replace the `?? serverManifest.Get(p)` fallback, call `UpsertSynced` only when both `Get` calls return non-null, `MarkSkipped` otherwise. The ownership table assigns `SyncClient.cs:185-206` to **Phase 6**. Editing it here would be exactly the two-phases-one-region failure this redraft exists to eliminate.
3. **Deleting the shim would not even reach the bug cleanly.** `MigrateFromBinary` is a genuine one-sided import (a v1 binary state file records one size+mtime), so the shim's semantics are *correct* there. The defect is the call site's `??` fallback, not the shim.

**Hand-off to Phase 6, stated as a hard precondition:** `SyncClient.cs:185-206` must be rewritten to the CONTRACT.md correction-6 shape and moved below the delete guards (correction 10). Until that lands, the Push/Pull decision tables must not be trusted against a database written by an unpatched client. Task 4.3 pins the shim's exact semantics with a characterisation test so the hazard is visible in the test suite rather than buried in a comment.

### Existing-test disposition (complete inventory)

`tests/RemoteFileSync.Tests/State/SyncDatabaseTests.cs` — 17 facts. Sixteen are **unchanged**; the read shims report `Side: "both"`, which is exactly what v1 `MarkSynced` wrote, so `Assert.Equal("both", state.Side)` at `:71` still passes. The one change:

| Test (line) | Disposition |
|---|---|
| `MarkNew_SetsStatusNew` (`:180`) | **CHANGED — one line deleted.** `Assert.Equal("remote", state.Side)` at `:187` is the only assertion in the repo that cannot survive: schema v2 has no `side` column, so `"remote"` is unrecoverable. The `status == "new"` assertion is kept. |

`SyncDatabaseMigrationTests.cs` — 3 facts, all **unchanged** (`:53` `GetAllTrackedFiles`, `:56` `GetFileState`; no `Side` assertion — verified by grep).
`SyncEngineTests.cs:264,305,306,327,350,374,395` and `DeleteThresholdTests.cs:81` — **unchanged**, they route through the shims. `SyncEngine.cs:105-113` still builds its `Dictionary<string, FileState>` from `GetAllTrackedFiles()` and still compiles.

---

### Task 4.1: `PairMarker`

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
        // inferred from the presence of sync.db — otherwise the gate can never fire.
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
Expected: FAIL — build error `CS0103: The name 'PairMarker' does not exist in the current context` at every call site.

- [ ] **Step 3: Implement**

Create `src/RemoteFileSync/State/PairMarker.cs`:

```csharp
namespace RemoteFileSync.State;

/// <summary>
/// A sentinel file written beside sync.db after the first clean sync of a pair.
/// Its presence next to a MISSING or unreadable database is what distinguishes lost sync
/// state from a genuine first run. Without it the two are indistinguishable, and a wiped
/// database makes every peer file look brand new — which under --mirror mirrors a
/// full-tree delete back onto the peer.
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

        // Content is diagnostic only; the gate reads existence, never the bytes. Rewriting
        // on every clean exit keeps Write idempotent without a pre-existence check.
        File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O"));
    }
}
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~PairMarkerTests"`
Expected: PASS — `PathFor_PlacesMarkerBesideDatabase`, `PathFor_BareFileName_ReturnsBareMarkerName`, `Exists_FalseBeforeWrite_TrueAfterWrite`, `Exists_IgnoresTheDatabaseItself`, `Write_CreatesMissingDirectory`, `Write_IsIdempotent` all green.

---

### Task 4.2: `ConflictDetail` — structured, round-tripping `file_versions.detail`

- [ ] **Step 1: Write the failing test**

Create `tests/RemoteFileSync.Tests/State/ConflictDetailTests.cs`:

```csharp
using System;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.State;

public sealed class ConflictDetailTests
{
    private static ConflictDetail Sample(string? renamedTo = null) =>
        new ConflictDetail(
            ClientSize: 1024,
            ClientMtimeTicks: new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc).Ticks,
            ServerSize: 2048,
            ServerMtimeTicks: new DateTime(2026, 7, 2, 17, 30, 0, DateTimeKind.Utc).Ticks,
            RenamedTo: renamedTo);

    [Fact]
    public void Encode_IsSingleLineAndVersioned()
    {
        var encoded = Sample("report.conflict-20260720-143052-server.docx").Encode();

        Assert.StartsWith("v1\t", encoded);
        // file_versions.detail is rendered one row per line by the review report; an embedded
        // newline would split one conflict across two report lines.
        Assert.DoesNotContain("\n", encoded);
        Assert.DoesNotContain("\r", encoded);
    }

    [Fact]
    public void Decode_RoundTripsWithoutRename()
    {
        var original = Sample();
        Assert.Equal(original, ConflictDetail.Decode(original.Encode()));
    }

    [Fact]
    public void Decode_RoundTripsWithRename()
    {
        var original = Sample("report.conflict-20260720-143052-server.docx");
        var decoded = ConflictDetail.Decode(original.Encode());

        Assert.Equal(original, decoded);
        Assert.Equal("report.conflict-20260720-143052-server.docx", decoded!.RenamedTo);
    }

    [Fact]
    public void Decode_DistinguishesNullRenameFromEmptyRename()
    {
        // A bare sentinel would make RenamedTo == "" and RenamedTo == null encode identically,
        // and the review report would then claim a rename that never happened.
        Assert.Null(ConflictDetail.Decode(Sample(null).Encode())!.RenamedTo);
        Assert.Equal("", ConflictDetail.Decode(Sample("").Encode())!.RenamedTo);
    }

    [Theory]
    [InlineData("has\ttab.txt")]
    [InlineData("has\nnewline.txt")]
    [InlineData("has\\backslash.txt")]
    [InlineData("has\r\nCRLF.txt")]
    public void Decode_RoundTripsRenamesContainingDelimiterCharacters(string renamedTo)
    {
        var original = Sample(renamedTo);
        var encoded = original.Encode();

        Assert.DoesNotContain("\n", encoded);
        Assert.Equal(original, ConflictDetail.Decode(encoded));
    }

    [Fact]
    public void Decode_RoundTripsNegativeAndZeroSizes()
    {
        var original = new ConflictDetail(0, 0, -1, long.MaxValue, null);
        Assert.Equal(original, ConflictDetail.Decode(original.Encode()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("both sides changed since last sync")]   // legacy free-form English
    [InlineData("v2\t1\t2\t3\t4\t-")]                    // unknown version
    [InlineData("v1\t1\t2\t3\t-")]                       // too few fields
    [InlineData("v1\t1\t2\t3\t4\t-\textra")]             // too many fields
    [InlineData("v1\tnotanumber\t2\t3\t4\t-")]           // unparsable size
    [InlineData("v1\t1\t2\t3\t4\t?name")]                // bad rename flag
    [InlineData("v1\t1\t2\t3\t4\t+trailing\\")]          // dangling escape
    [InlineData("v1\t1\t2\t3\t4\t+bad\\qescape")]        // unknown escape
    public void Decode_ReturnsNullOnAnythingUnparsable(string? detail)
    {
        Assert.Null(ConflictDetail.Decode(detail));
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ConflictDetailTests"`
Expected: FAIL — build error `CS0246: The type or namespace name 'ConflictDetail' could not be found`.

- [ ] **Step 3: Implement**

Create `src/RemoteFileSync/State/ConflictDetail.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace RemoteFileSync.State;

/// <summary>
/// Structured payload for file_versions.detail. LogConflict / LogResurrection decide the
/// `action` column; this record only carries the data the review report renders, so nothing
/// downstream ever has to sniff the detail string to work out what kind of event it was.
/// Encode is single-line and tab-separated so a detail can never split a report row, and is
/// versioned so a future field can be added without misreading v1 rows already on disk.
/// </summary>
public sealed record ConflictDetail(
    long ClientSize, long ClientMtimeTicks,
    long ServerSize, long ServerMtimeTicks,
    string? RenamedTo)
{
    private const string FormatVersion = "v1";
    private const char Separator = '\t';
    private const int FieldCount = 6;

    /// <summary>Flags an absent rename. A bare empty field cannot be used: RenamedTo == ""
    /// and RenamedTo == null must survive the round trip as distinct values.</summary>
    private const char NoRename = '-';
    private const char HasRename = '+';

    public string Encode()
    {
        var sb = new StringBuilder();
        sb.Append(FormatVersion).Append(Separator);
        sb.Append(ClientSize.ToString(CultureInfo.InvariantCulture)).Append(Separator);
        sb.Append(ClientMtimeTicks.ToString(CultureInfo.InvariantCulture)).Append(Separator);
        sb.Append(ServerSize.ToString(CultureInfo.InvariantCulture)).Append(Separator);
        sb.Append(ServerMtimeTicks.ToString(CultureInfo.InvariantCulture)).Append(Separator);

        if (RenamedTo is null)
            sb.Append(NoRename);
        else
            sb.Append(HasRename).Append(Escape(RenamedTo));

        return sb.ToString();
    }

    public static ConflictDetail? Decode(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return null;

        var parts = detail.Split(Separator);
        if (parts.Length != FieldCount) return null;
        if (!string.Equals(parts[0], FormatVersion, StringComparison.Ordinal)) return null;

        if (!TryParseTicks(parts[1], out var clientSize))       return null;
        if (!TryParseTicks(parts[2], out var clientMtimeTicks)) return null;
        if (!TryParseTicks(parts[3], out var serverSize))       return null;
        if (!TryParseTicks(parts[4], out var serverMtimeTicks)) return null;

        var renameField = parts[5];
        string? renamedTo;
        if (renameField.Length == 1 && renameField[0] == NoRename)
        {
            renamedTo = null;
        }
        else if (renameField.Length >= 1 && renameField[0] == HasRename)
        {
            if (!TryUnescape(renameField.Substring(1), out renamedTo)) return null;
        }
        else
        {
            return null;
        }

        return new ConflictDetail(clientSize, clientMtimeTicks, serverSize, serverMtimeTicks, renamedTo);
    }

    // AllowLeadingSign only: whitespace, thousands separators and hex must all be rejected,
    // because anything Encode did not produce is by definition not a v1 detail.
    private static bool TryParseTicks(string field, out long value) =>
        long.TryParse(field, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\t': sb.Append("\\t");  break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                default:   sb.Append(ch);     break;
            }
        }
        return sb.ToString();
    }

    private static bool TryUnescape(string value, out string? result)
    {
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\') { sb.Append(value[i]); continue; }

            // A trailing lone backslash means the row was truncated; decoding it as a literal
            // would silently hand the caller a rename target that is not the one recorded.
            if (i + 1 >= value.Length) { result = null; return false; }

            switch (value[++i])
            {
                case '\\': sb.Append('\\'); break;
                case 't':  sb.Append('\t'); break;
                case 'n':  sb.Append('\n'); break;
                case 'r':  sb.Append('\r'); break;
                default:   result = null; return false;
            }
        }

        result = sb.ToString();
        return true;
    }
}
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ConflictDetailTests"`
Expected: PASS — `Encode_IsSingleLineAndVersioned`, `Decode_RoundTripsWithoutRename`, `Decode_RoundTripsWithRename`, `Decode_DistinguishesNullRenameFromEmptyRename`, `Decode_RoundTripsRenamesContainingDelimiterCharacters` (all four `[InlineData]` cases), `Decode_RoundTripsNegativeAndZeroSizes`, `Decode_ReturnsNullOnAnythingUnparsable` (all ten cases) green.

---

### Task 4.3: Schema v2 table, `SchemaVersion`, and the ancestor-row API

This task creates the v2 table for a **fresh** database and makes opening a v1 database throw a named error. Task 4.5 replaces that throw with the real migration — that ordering is what gives Task 4.5's tests teeth instead of letting them pass on arrival.

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
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncDatabaseSchemaV2Tests"`
Expected: FAIL — build errors `CS0117: 'SyncDatabase' does not contain a definition for 'SchemaVersion'` and `CS1061: 'SyncDatabase' does not contain a definition for 'UpsertSynced'` / `'GetRow'` / `'LoadAll'` / `'Tombstone'`.

- [ ] **Step 3: Implement**

**Edit 4.3a — `src/RemoteFileSync/State/SyncDatabase.cs:1-3`.** Replace exactly:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
```

with:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RemoteFileSync.Sync;
```

**Edit 4.3b — `src/RemoteFileSync/State/SyncDatabase.cs:7-13`.** Replace exactly:

```csharp
public record FileState(
    string Path,
    long FileSize,
    DateTime LastModified,
    string Status,
    DateTime LastSynced,
    string Side);
```

with:

```csharp
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
```

**Edit 4.3c — `src/RemoteFileSync/State/SyncDatabase.cs:24-32`.** Replace exactly:

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

with:

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

/// <summary>
/// One conflict or resurrection recorded during a sync session. <c>Detail</c> is a
/// <see cref="ConflictDetail"/>-encoded string; pass it to
/// <see cref="ConflictDetail.Decode"/> rather than parsing it by hand.
/// </summary>
public record ConflictEntry(string Path, string Detail, DateTime Timestamp);
```

**Edit 4.3d — `src/RemoteFileSync/State/SyncDatabase.cs:37-39`.** Replace exactly:

```csharp
public sealed class SyncDatabase : IDisposable
{
    private readonly SqliteConnection _conn;
```

with:

```csharp
public sealed class SyncDatabase : IDisposable
{
    /// <summary>Stamped into PRAGMA user_version. Bump only alongside a migration step in InitSchema.</summary>
    public const int SchemaVersion = 2;

    private readonly SqliteConnection _conn;
```

**Edit 4.3e — `src/RemoteFileSync/State/SyncDatabase.cs:65-112`.** Replace exactly:

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

with:

```csharp
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
            if (isV1)
                throw new InvalidOperationException(
                    "sync.db uses schema v1 and cannot be opened until the v1 -> v2 migration lands.");

            CreateFilesV2(txn);

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

**Edit 4.3f — `src/RemoteFileSync/State/SyncDatabase.cs:180-242`.** Replace exactly:

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

with:

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
        // OrdinalIgnoreCase mirrors the table's NOCASE primary key. An ordinal dictionary
        // would miss rows whose casing drifted between scans on Windows, and a missed
        // ancestor reads as "never synced" — which is how a file gets re-sent or deleted.
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

**Edit 4.3g — `src/RemoteFileSync/State/SyncDatabase.cs:244-327`.** Replace exactly:

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

with:

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

    // ── Legacy v1 write surface (thin shims over the v2 API) ──────────────────

    /// <summary>
    /// Legacy one-sided upsert: v1 stored a single size+mtime, so both v2 sides receive it.
    /// SAFE only when the caller genuinely knows both sides hold that value — which is true
    /// for <see cref="MigrateFromBinary"/> and false for SyncClient's Skip loop, where the
    /// value comes from whichever side happened to have the file. Phase 6 owns
    /// SyncClient.cs:185-206 and must split that call into a both-sides-present
    /// <see cref="UpsertSynced"/> and a <see cref="MarkSkipped"/>; until then a Push or Pull
    /// run can fabricate a peer state that never existed and delete on the next pass.
    /// </summary>
    public void MarkSynced(string path, long fileSize, DateTime lastModified, long sessionId, string direction)
    {
        var ticks = lastModified.ToUniversalTime().Ticks;
        UpsertSynced(path, fileSize, ticks, fileSize, ticks, sessionId, direction);
    }

    public void MarkDeleted(string path, long sessionId, string? detail) =>
        Tombstone(path, sessionId, detail);
```

**Edit 4.3h — `src/RemoteFileSync/State/SyncDatabase.cs:341-383`.** Replace exactly:

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

with:

```csharp
    /// <summary>
    /// Legacy discovery marker, retargeted onto the v2 columns. The <paramref name="side"/>
    /// argument is accepted but not stored — v2 dropped the column. Rows land with
    /// status='new', which every v2 decision table treats as "no usable ancestor" and routes
    /// down the newest-wins path, never down the delete path.
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

**Edit 4.3i — `tests/RemoteFileSync.Tests/State/SyncDatabaseTests.cs:179-188`.** Replace exactly:

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

with:

```csharp
    [Fact]
    public void MarkNew_SetsStatusNew()
    {
        _db.MarkNew("incoming/newfile.txt", fileSize: 512, lastModified: DateTime.UtcNow, side: "remote");

        var state = _db.GetFileState("incoming/newfile.txt");
        Assert.NotNull(state);
        Assert.Equal("new", state!.Status);
        // No Side assertion: schema v2 dropped the `side` column per CONTRACT.md, so the
        // value "remote" is unrecoverable. FileState.Side is now synthetic ("both").
    }
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncDatabaseSchemaV2Tests"`
Expected: PASS — `NewDatabase_HasSchemaVersion2AndPerSideColumns`, `UpsertSynced_RoundTripsDifferentClientAndServerMtimes`, `UpsertSynced_Twice_OverwritesBothSidesIndependently`, `GetRow_IsCaseInsensitive_AndNullWhenAbsent`, `LoadAll_IsKeyedCaseInsensitively`, `UpsertSynced_AfterTombstone_ClearsDeletedUtc`, `Tombstone_UntrackedPath_WritesNothing`, `MarkSynced_Shim_StampsOneSidesValuesOntoBothSides` green.

Then confirm the shims held the old suites:

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncDatabaseTests|FullyQualifiedName~SyncDatabaseMigrationTests|FullyQualifiedName~SyncEngineTests|FullyQualifiedName~DeleteThresholdTests"`
Expected: PASS — everything previously green is still green, including `MarkSynced_CreatesFileAndVersion` (whose `Assert.Equal("both", state.Side)` at `SyncDatabaseTests.cs:71` now hits the synthetic value) and `PreviouslyDeleted_Reappeared_CanBeMarkedExists`.

---

### Task 4.4: `PurgeTombstonesOlderThan`

- [ ] **Step 1: Write the failing test**

Append inside the `SyncDatabaseSchemaV2Tests` class:

```csharp
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

Insert into `src/RemoteFileSync/State/SyncDatabase.cs` immediately after the `Tombstone` method added in Edit 4.3g and immediately before the `// ── Legacy v1 write surface (thin shims over the v2 API) ──` banner:

```csharp
    public int PurgeTombstonesOlderThan(TimeSpan age)
    {
        // A negative retention puts the cutoff in the future, which would sweep away every
        // tombstone including ones written seconds ago — losing exactly the evidence that
        // stops a deleted file from being resurrected on the next run.
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
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~PurgeTombstonesOlderThan"`
Expected: PASS — `PurgeTombstonesOlderThan_RemovesOldTombstoneKeepsRecentOne`, `PurgeTombstonesOlderThan_NeverTouchesExistingRows`, `PurgeTombstonesOlderThan_KeepsTombstonesWithNullDeletedUtc`, `PurgeTombstonesOlderThan_NegativeAge_Throws` green.

---

### Task 4.5: v1 → v2 migration, transactional and idempotent

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
Expected: FAIL — `OpeningV1Database_RebuildsTableInV2Shape`, `OpeningV1Database_CopiesSizeAndMtimeToBothSides`, `OpeningV1Database_SeedsDeletedUtcFromLastSyncedForTombstonesOnly`, `OpeningV1Database_PreservesVersionHistoryAndSessions`, `OpeningMigratedDatabaseAgain_IsANoOp` and `MigratedDatabase_AcceptsPerSideUpdates` all throw `InvalidOperationException: sync.db uses schema v1 and cannot be opened until the v1 -> v2 migration lands.` from the guard Task 4.3 installed. `V1DatabaseHasNoUserVersionStamp` and `FailedMigration_LeavesTheV1DatabaseIntact` pass already — the former is a fixture assertion, the latter passes for the wrong reason (the throw) and gains its real meaning in Step 4.

- [ ] **Step 3: Implement**

**Edit 4.5a — `src/RemoteFileSync/State/SyncDatabase.cs`, inside `InitSchema` as Edit 4.3e left it.** Replace exactly:

```csharp
            if (isV1)
                throw new InvalidOperationException(
                    "sync.db uses schema v1 and cannot be opened until the v1 -> v2 migration lands.");

            CreateFilesV2(txn);
```

with:

```csharp
            if (isV1) MigrateV1ToV2(txn);
            else      CreateFilesV2(txn);
```

**Edit 4.5b — `src/RemoteFileSync/State/SyncDatabase.cs`, insert immediately after the `CreateFilesV2` method added by Edit 4.3e and immediately before `private int ReadUserVersion()`:**

```csharp
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
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncDatabaseSchemaMigrationTests"`
Expected: PASS — `V1Database_HasNoUserVersionStamp`, `OpeningV1Database_RebuildsTableInV2Shape`, `OpeningV1Database_CopiesSizeAndMtimeToBothSides`, `OpeningV1Database_SeedsDeletedUtcFromLastSyncedForTombstonesOnly`, `OpeningV1Database_PreservesVersionHistoryAndSessions`, `OpeningMigratedDatabaseAgain_IsANoOp`, `FailedMigration_LeavesTheV1DatabaseIntact`, `MigratedDatabase_AcceptsPerSideUpdates` green. `FailedMigration_LeavesTheV1DatabaseIntact` now proves rollback rather than the guard.

---

### Task 4.6: Conflict and resurrection logging — two writers, no prefix sniffing

- [ ] **Step 1: Write the failing test**

Append inside the `SyncDatabaseSchemaV2Tests` class:

```csharp
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
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncDatabaseSchemaV2Tests"`
Expected: FAIL — build errors `CS1061: 'SyncDatabase' does not contain a definition for 'LogConflict'`, `'LogResurrection'`, `'GetSessionConflicts'`, `'GetSessionResurrections'`.

- [ ] **Step 3: Implement**

**Edit 4.6a — `src/RemoteFileSync/State/SyncDatabase.cs:385`.** Replace exactly:

```csharp
    // ── History ───────────────────────────────────────────────────────────────
```

with:

```csharp
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
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncDatabaseSchemaV2Tests"`
Expected: PASS — `LogConflictAndLogResurrection_AreSeparatedByActionNotByDetail`, `LogConflict_NeverRoutesOnTheDetailString`, `SessionEntryDetails_DecodeBackToConflictDetail`, `GetSessionConflictsAndResurrections_NoneLogged_ReturnEmpty`, `LogConflict_DoesNotDisturbTheAncestorRow`, `LogResurrection_UntrackedPath_IsStillRecorded` green, alongside every test from Tasks 4.3 and 4.4.

---

### Phase 4 commit

**Verification before commit:**
```bash
cd E:/RemoteFileSync
dotnet build -c Release
dotnet test -c Release
```
Expected: 0 build errors, 0 test failures.

Exactly one existing test changes knowingly: `SyncDatabaseTests.MarkNew_SetsStatusNew` loses its `Assert.Equal("remote", state.Side)` assertion, because CONTRACT.md's schema v2 drops the `side` column and the value is unrecoverable; the `status == "new"` assertion is retained. Every other test in `SyncDatabaseTests.cs`, `SyncDatabaseMigrationTests.cs`, `SyncEngineTests.cs` and `DeleteThresholdTests.cs` passes unmodified through the read/write shims — that is the intended regression evidence that the column rebuild lost no data.

```bash
git add src/RemoteFileSync/State/PairMarker.cs \
        src/RemoteFileSync/State/ConflictDetail.cs \
        src/RemoteFileSync/State/SyncDatabase.cs \
        tests/RemoteFileSync.Tests/State/PairMarkerTests.cs \
        tests/RemoteFileSync.Tests/State/ConflictDetailTests.cs \
        tests/RemoteFileSync.Tests/State/SyncDatabaseSchemaV2Tests.cs \
        tests/RemoteFileSync.Tests/State/SyncDatabaseSchemaMigrationTests.cs \
        tests/RemoteFileSync.Tests/State/SyncDatabaseTests.cs

git commit -m "feat(state): schema v2 ancestor columns, ConflictDetail, PairMarker

Rebuild the files table with separate client/server size+mtime so a two-way
merge can tell which side moved, add deleted_utc so tombstones can age out,
and drop the meaningless side column. Migration is create/copy/drop/rename
inside one transaction, gated and stamped by PRAGMA user_version (v1 never
stamped it, so a 0 is disambiguated by the presence of file_size). Reopening
a migrated database is a no-op; a failed migration rolls back to intact v1.

Adds GetRow/LoadAll/UpsertSynced/Tombstone/PurgeTombstonesOlderThan, plus
LogConflict and LogResurrection as two separate writers of action='conflict'
and action='resurrected'. Neither inspects the detail string: the kind of
event is decided by the call site, not inferred from a payload a filename
could spoof. Both take a ConflictDetail.Encode() string, which round-trips
per-side sizes, mtimes and the rename target on one tab-separated line.

MarkSynced/MarkDeleted/MarkNew/GetFileState/GetAllTrackedFiles/GetDeletedFiles
stay as thin shims so SyncClient and SyncEngine keep compiling until their own
phases land. MarkSynced writes one side's values to both sides, which is
correct for MigrateFromBinary and wrong for SyncClient's Skip loop; the
ancestor-merge phase owns SyncClient.cs:185-206 and must split that call into
a both-sides-present UpsertSynced and a MarkSkipped.

PairMarker records that a pair has synced at least once, so a later phase can
tell a genuine first run from lost state instead of mirroring a full delete.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"

git push -u origin feat/deletion-sync-ancestor-merge
```

**Post-commit verification:**
```bash
git log --oneline -1
git status --short          # expect: clean
dotnet test -c Release --filter "FullyQualifiedName~State"
```
Expected: the commit is on `feat/deletion-sync-ancestor-merge`, the working tree is clean, and every test under `RemoteFileSync.Tests.State` is green.

---

## Phase 5: ArchiveManager — one session folder per run, reason partitioning, retention

**Goal:** Replace `BackupManager` with `ArchiveManager`, whose session timestamp is captured **once per run** by the caller, which partitions archived copies by reason and prunes whole session folders by age and size cap. This phase owns the migration at all six `BackupManager` call sites and creates the single `archive` local that Phases 7 and 8 reuse.

**Files:**
- Create: `src/RemoteFileSync/Backup/ArchiveManager.cs`
- Delete: `src/RemoteFileSync/Backup/BackupManager.cs`
- Delete: `tests/RemoteFileSync.Tests/Backup/BackupManagerTests.cs`
- Modify: `src/RemoteFileSync/Network/SyncClient.cs` — the locals block at `:85-87`, and `:209`, `:371`, and the whole deletion branch at `:419-458`
- Modify: `src/RemoteFileSync/Network/SyncServer.cs` — the locals block at `:128-130`, and `:173`, `:193`, and the whole deletion branch at `:254-291`
- Modify: `src/RemoteFileSync/Transfer/FileTransfer.cs` — `ReceiveFileAsync`'s pre-commit hook contract (Task 5.5). **Phase 5 owns this file**, and owns both `onBeforeCommit` call sites (`SyncClient.cs:370-371`, `SyncServer.cs:192-193`). No other phase may edit it.
- Create: `tests/RemoteFileSync.Tests/Backup/ArchiveManagerTests.cs`
- Modify: `tests/RemoteFileSync.Tests/Transfer/FileTransferTests.cs` — append the Task 5.5 receiver tests

All eight line numbers above were read from `main` and are exact. **They will have drifted** by the net line delta Phase 3 introduces in the handshake blocks (`SyncClient.cs:89-113`, `SyncServer.cs:132-152`), which sit above every one of my anchors. Every edit below is therefore expressed as an exact-text replacement; anchor on the text, not the number. None of my anchor text is inside a region owned by Phases 1-4, so all of it is byte-identical to `main` when this phase runs (verified: Phase 1 touches `SyncOptions.cs` / `Program.cs` / test initialisers only — `SyncClient` and `SyncServer` *read* `_options.Bidirectional`, which survives as the read-only shim; Phase 2 has zero call sites; Phase 3 is confined to the two handshake blocks; Phase 4 is confined to `SyncDatabase.cs`).

---

### Interfaces

**Consumes (Phase 1 — `SyncOptions`):**
- `public string EffectiveArchiveFolder { get; }` — same fallback rules as `EffectiveBackupFolder` (CONTRACT.md:77)
- `public int ArchiveKeepDays { get; set; }` — `0 = keep forever` (CONTRACT.md:78)
- `public long ArchiveMaxBytes { get; set; }` — `0 = no cap` (CONTRACT.md:79)

**Consumes (existing, unchanged by any earlier phase):**
- `PathGuard.TryResolveWithinRoot(string root, string relativePath, out string fullPath)` — `src/RemoteFileSync/Security/PathGuard.cs:13`

**Produces:**
- `public enum ArchiveReason { Deleted, Overwritten, Conflict }` (CONTRACT.md:140)
- `public ArchiveManager(string syncFolder, string archiveRoot, DateTime sessionStartUtc)` (CONTRACT.md:143)
- `public const string SessionFolderFormat = "yyyyMMdd-HHmmss";` (CONTRACT.md:204 — contract-backed public API, consumed from the test assembly)
- `public string SessionFolderName { get; }` / `public string SessionRoot { get; }` (CONTRACT.md:144-145)
- `public bool Archive(string relativePath, ArchiveReason reason, bool removeOriginal)` (CONTRACT.md:146)
- `public enum ArchiveOutcome { Archived, NothingToArchive, Failed }` (Task 5.5) — the three-way answer `bool` cannot give. `Archive` survives unchanged as `TryArchive(...) == ArchiveOutcome.Archived`, so CONTRACT.md:146 still holds; every caller that must distinguish "there was nothing to preserve" from "we could not preserve it" calls `TryArchive`.
- `public ArchiveOutcome TryArchive(string relativePath, ArchiveReason reason, bool removeOriginal)` (Task 5.5)
- `public static PruneResult Prune(string archiveRoot, TimeSpan keepAge, long maxBytes)` (CONTRACT.md:147)
- `public readonly record struct PruneResult(int SessionsRemoved, long BytesFreed)` (CONTRACT.md:149)

**Produces — locals that later phases MUST REUSE, never redeclare:**

| Local | Declared in | Reused by |
|---|---|---|
| `DateTime sessionStartUtc` | top of `SyncClient.HandleConnectionAsync` and of `SyncServer.HandleConnectionAsync` | **Phase 7** for the `ConflictNamer` timestamp — the conflict filename stamp and the archive folder stamp must be the same instant |
| `ArchiveManager archive` | `SyncClient.HandleConnectionAsync` (at the old `:209`) and `SyncServer.HandleConnectionAsync` (at the old `:173`) | **Phases 7 and 8** for every `Archive(...)` call |

Phases 7 and 8 must **not** write `var archive = new ArchiveManager(...)` or `var sessionStartUtc = DateTime.UtcNow;` anywhere in these two methods. A second declaration at method scope is CS0128; a second declaration in a nested block (inside the `try` at `SyncClient.cs:216`) is CS0136. Both are hard build breaks, and a second `DateTime.UtcNow` read would scatter one run's conflict/, deleted/ and overwritten/ folders across two session names as soon as the manifest exchange takes more than a second — exactly the defect this phase exists to remove.

`Archive()` returns `bool`. Per CONTRACT.md:205-208, **no caller may run a destructive step on a discarded return value.** `PathGuard` fails closed on transient IO (`PathGuard.cs:85-86` returns `true` from `HasReparsePointAncestor`, which makes `TryResolveWithinRoot` return `false`), so `false` does **not** mean "there was nothing to archive". Phase 7 must branch on it. Any caller that needs to *proceed* when there was genuinely nothing to preserve, but *refuse* when preservation failed, must call `TryArchive` and compare against `ArchiveOutcome.Failed` — see Task 5.5, which is exactly that case and the reason the enum exists.

---

### Decision: delete `BackupManager`, do not keep a delegating shim

A shim cannot delegate honestly. `BackupManager`'s constructor takes no timestamp, so a shim would have to synthesise `sessionStartUtc` either at construction (in which case the type name lies about the `yyyyMMdd` layout its **seven** existing tests assert) or per call (which reintroduces the per-file clock read this phase removes). Worse, a surviving `BackupManager` keeps writing `yyyyMMdd` folders into the same archive root, and `Prune` deliberately refuses to parse those names, so every folder it writes would leak forever. There are six call sites and all six are migrated below.

`SyncOptions.EffectiveBackupFolder` and its containment check at `SyncOptions.cs:117` are left untouched — `SyncOptions.cs` belongs to Phase 1, and `EffectiveArchiveFolder` is specified to reuse those fallback rules.

**Where `Prune` runs:** once per session, on the line that constructs the `ArchiveManager` — after the plan exchange and before any transfer or archive write. (It is *not* at the top of `HandleConnectionAsync`: that point is 124 lines earlier in `SyncClient` and 45 in `SyncServer`.) The guarantee that matters is that it runs before the first archive write, so this run's own session folder does not exist yet and can never be a prune candidate, even under a tiny `--archive-max-size`.

**Layout note for sign-off:** CONTRACT.md:229 specifies `<archiveRoot>/<session>/<reason>/<relative path>`. The `<reason>` level is an intentional extension of the originally-requested `<folder>/<date-time>/<structure>/<file>` layout: restoring "what this run deleted" must not sweep up the overwrite snapshots taken in the same run.

---

### Task 5.1: session folders, reason partitioning, copy-before-delete

- [ ] **Step 1: Write the failing tests**

Create `tests/RemoteFileSync.Tests/Backup/ArchiveManagerTests.cs`:

```csharp
using System.Globalization;
using RemoteFileSync.Backup;
using RemoteFileSync.Security;

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

    private static DateTime Stamp => new(2026, 7, 19, 14, 30, 52, DateTimeKind.Utc);

    private const string StampFolder = "20260719-143052";

    [Fact]
    public void SessionFolderName_IsSessionStartStamp_AndSessionRootHangsOffArchiveRoot()
    {
        var mgr = NewManager(Stamp);

        Assert.Equal(StampFolder, mgr.SessionFolderName);
        Assert.Equal(Path.Combine(Path.GetFullPath(_archiveDir), StampFolder), mgr.SessionRoot);
    }

    [Theory]
    [InlineData(ArchiveReason.Deleted, "deleted")]
    [InlineData(ArchiveReason.Overwritten, "overwritten")]
    [InlineData(ArchiveReason.Conflict, "conflict")]
    public void Archive_PartitionsByReason(ArchiveReason reason, string expectedFolder)
    {
        CreateSyncFile("report.docx");
        var mgr = NewManager(Stamp);

        Assert.True(mgr.Archive("report.docx", reason, removeOriginal: false));
        Assert.True(File.Exists(Path.Combine(_archiveDir, StampFolder, expectedFolder, "report.docx")));
    }

    [Fact]
    public void Archive_PreservesNestedStructureUnderTheReasonFolder()
    {
        CreateSyncFile("docs/sub/file.txt");
        var mgr = NewManager(Stamp);

        Assert.True(mgr.Archive("docs/sub/file.txt", ArchiveReason.Overwritten, removeOriginal: false));
        Assert.True(File.Exists(Path.Combine(
            _archiveDir, StampFolder, "overwritten", "docs", "sub", "file.txt")));
    }

    [Fact]
    public void Archive_RemoveOriginalFalse_LeavesOriginalInPlace()
    {
        CreateSyncFile("report.docx");
        var mgr = NewManager(Stamp);

        Assert.True(mgr.Archive("report.docx", ArchiveReason.Overwritten, removeOriginal: false));
        // Copy, not move: a failed transfer must not leave the sync folder without the file.
        Assert.True(File.Exists(Path.Combine(_syncDir, "report.docx")));
        Assert.Equal("original", File.ReadAllText(
            Path.Combine(_archiveDir, StampFolder, "overwritten", "report.docx")));
    }

    [Fact]
    public void Archive_RemoveOriginalTrue_CopiesThenDeletesOriginal()
    {
        CreateSyncFile("report.docx");
        var mgr = NewManager(Stamp);

        Assert.True(mgr.Archive("report.docx", ArchiveReason.Deleted, removeOriginal: true));
        // Deletion propagation: the original goes away, but only after the copy succeeded.
        Assert.False(File.Exists(Path.Combine(_syncDir, "report.docx")));
        Assert.Equal("original", File.ReadAllText(
            Path.Combine(_archiveDir, StampFolder, "deleted", "report.docx")));
    }

    [Fact]
    public void Archive_SamePathTwiceInOneSession_AppendsNumericSuffix()
    {
        var mgr = NewManager(Stamp);
        CreateSyncFile("report.docx", "version1");
        Assert.True(mgr.Archive("report.docx", ArchiveReason.Overwritten, removeOriginal: false));
        CreateSyncFile("report.docx", "version2");
        Assert.True(mgr.Archive("report.docx", ArchiveReason.Overwritten, removeOriginal: false));

        // One path can be archived twice in a session; a clobbering copy would destroy the
        // earlier version and the session would no longer be a faithful restore point.
        var dir = Path.Combine(_archiveDir, StampFolder, "overwritten");
        Assert.Equal("version1", File.ReadAllText(Path.Combine(dir, "report.docx")));
        Assert.Equal("version2", File.ReadAllText(Path.Combine(dir, "report_1.docx")));
    }

    [Fact]
    public void Archive_RejectsPathEscapingTheSyncRoot()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_syncDir)!, "outside.txt");
        File.WriteAllText(outside, "secret");
        var mgr = NewManager(Stamp);

        // relativePath arrives from the network on deletion propagation, so containment must
        // hold before the path reaches the filesystem.
        Assert.False(mgr.Archive("../outside.txt", ArchiveReason.Deleted, removeOriginal: true));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void Archive_MissingFile_ReturnsFalse()
    {
        var mgr = NewManager(Stamp);
        Assert.False(mgr.Archive("nonexistent.txt", ArchiveReason.Deleted, removeOriginal: true));
    }

    [Fact]
    public async Task Archive_ConcurrentCalls_AllSucceed()
    {
        for (int i = 0; i < 10; i++) CreateSyncFile($"file{i}.txt", $"content{i}");
        var mgr = NewManager(Stamp);

        var tasks = Enumerable.Range(0, 10)
            .Select(i => Task.Run(() => mgr.Archive($"file{i}.txt", ArchiveReason.Deleted, removeOriginal: false)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, Assert.True);
    }

    [Fact]
    public void Archive_RunSpanningMidnightUtc_LandsInExactlyOneSessionFolder()
    {
        // Regression lock: BackupManager derived its folder from DateTime.UtcNow on EVERY call,
        // so a run starting at 23:59:59 and finishing at 00:00:01 split into two dated folders
        // and neither half was a complete restore point. The stamp is now fixed at construction,
        // so the folder is a function of the session start alone, never of the wall clock.
        var sessionStart = new DateTime(2026, 7, 19, 23, 59, 59, DateTimeKind.Utc);
        var mgr = NewManager(sessionStart);

        CreateSyncFile("before-midnight.txt", "before");
        Assert.True(mgr.Archive("before-midnight.txt", ArchiveReason.Deleted, removeOriginal: true));
        CreateSyncFile("after-midnight.txt", "after");
        Assert.True(mgr.Archive("after-midnight.txt", ArchiveReason.Deleted, removeOriginal: true));

        var sessionFolders = Directory.GetDirectories(_archiveDir);
        Assert.Single(sessionFolders);
        Assert.Equal("20260719-235959", Path.GetFileName(sessionFolders[0]));

        // sessionStart is a fixed past instant, so the wall clock cannot coincide with it:
        // this proves the folder name did not come from DateTime.UtcNow.
        Assert.NotEqual(
            DateTime.UtcNow.ToString(ArchiveManager.SessionFolderFormat, CultureInfo.InvariantCulture),
            Path.GetFileName(sessionFolders[0]));

        var deletedDir = Path.Combine(_archiveDir, "20260719-235959", "deleted");
        Assert.Equal("before", File.ReadAllText(Path.Combine(deletedDir, "before-midnight.txt")));
        Assert.Equal("after", File.ReadAllText(Path.Combine(deletedDir, "after-midnight.txt")));
    }
}
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ArchiveManagerTests"`

Expected: FAIL to build — `CS0246: The type or namespace name 'ArchiveManager' could not be found` and `CS0246: The type or namespace name 'ArchiveReason' could not be found`.

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
    /// <summary>
    /// Session folder format. Public because Prune parses folder names back with this exact
    /// format, and a caller that fabricates or locates a session folder must use the same one.
    /// </summary>
    public const string SessionFolderFormat = "yyyyMMdd-HHmmss";

    private readonly string _syncFolder;
    private readonly object _lock = new();

    /// <param name="sessionStartUtc">
    /// The instant the sync session began, in UTC. Captured once by the caller; see the class
    /// remarks for why it is not read from the clock here.
    /// </param>
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
    /// root, or the file does not exist. Callers MUST NOT run a destructive step on a discarded
    /// result: PathGuard fails closed on transient IO, so false does not imply "nothing to do".
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

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ArchiveManagerTests"`

Expected: PASS — every method above, including `Archive_RunSpanningMidnightUtc_LandsInExactlyOneSessionFolder`.

---

### Task 5.2: the destination is derived from the GUARDED path, not the wire string

Task 5.1's `Archive` is a faithful port of `BackupManager.Snapshot` (`BackupManager.cs:29-59`): it guards the **source** and then rebuilds the **destination** from the raw `relativePath`. That is not sufficient, and `Archive_RejectsPathEscapingTheSyncRoot` does not detect it — `"../outside.txt"` resolves outside the root and is caught by the source-side guard, so that test passes with or without this task.

`PathGuard` accepts dot segments as long as the *final* resolved path lands inside the root: `PathGuard.cs:35` (`if (segment == "." || segment == "..") continue;`) skips per-segment validation, and `PathGuard.cs:63` prefix-checks only the fully-resolved `combined`. So a peer can send an alias — enough `..` segments to clamp at the drive root, then back down into the sync folder — that names a file we legitimately own. Applying that same alias from `SessionRoot/<reason>` walks the *destination* out of the archive root and into the live sync tree, where the copy is re-scanned as a brand-new file and pushed to the peer forever, while `Prune` (which only enumerates parsable session folders directly under the archive root) can never reclaim it and the deletion has no restore point.

- [ ] **Step 1: Write the failing test**

Append inside `ArchiveManagerTests`:

```csharp
    /// <summary>
    /// Builds a peer-supplied path that PathGuard ACCEPTS — it resolves to a file inside the
    /// sync root — but which walks out of the archive root if it is used to build the
    /// destination: enough ".." to clamp at the drive root, then back down into the sync folder.
    /// </summary>
    private string BuildAliasPathIntoSyncRoot(string fileName)
    {
        var syncFull = Path.GetFullPath(_syncDir);
        var driveRoot = Path.GetPathRoot(syncFull)!;
        var tail = syncFull.Substring(driveRoot.Length);   // no drive letter: PathGuard rejects ':'
        var climb = string.Concat(Enumerable.Repeat(".." + Path.DirectorySeparatorChar, 40));
        return climb + Path.Combine(tail, fileName);
    }

    [Fact]
    public void Archive_DotSegmentAliasOfAnInsideFile_StillLandsUnderTheSessionFolder()
    {
        CreateSyncFile("aliased.txt", "aliased");
        var alias = BuildAliasPathIntoSyncRoot("aliased.txt");
        var mgr = NewManager(Stamp);

        // Precondition: this alias is ACCEPTED by PathGuard (it resolves inside the root), so
        // the source-side guard cannot be what protects the destination.
        Assert.True(PathGuard.TryResolveWithinRoot(_syncDir, alias, out var resolved));
        Assert.Equal(Path.Combine(Path.GetFullPath(_syncDir), "aliased.txt"), resolved);

        Assert.True(mgr.Archive(alias, ArchiveReason.Deleted, removeOriginal: true));

        // The copy must be in the archive, not squatting in the live sync tree where the next
        // scan would re-sync it to the peer and where Prune could never reclaim it.
        Assert.Equal("aliased", File.ReadAllText(
            Path.Combine(_archiveDir, StampFolder, "deleted", "aliased.txt")));
        Assert.Empty(Directory.GetFiles(_syncDir));
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Archive_DotSegmentAliasOfAnInsideFile_StillLandsUnderTheSessionFolder"`

Expected: FAIL — `File.ReadAllText` throws `DirectoryNotFoundException`/`FileNotFoundException` for `<archive>/20260719-143052/deleted/aliased.txt`, because the copy was written into the sync folder instead. (If the copy is reached before the read assertion, `Assert.Empty(Directory.GetFiles(_syncDir))` reports one file, `aliased_1.txt` — the collision suffix, because the naive destination collided with the source itself.)

- [ ] **Step 3: Implement**

In `src/RemoteFileSync/Backup/ArchiveManager.cs`, replace exactly:

```csharp
        lock (_lock)
        {
            var relDir = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? "";
            var destDir = Path.Combine(SessionRoot, ReasonFolder(reason), relDir);
            Directory.CreateDirectory(destDir);

            var fileName = Path.GetFileNameWithoutExtension(relativePath);
            var ext = Path.GetExtension(relativePath);
            var destPath = Path.Combine(destDir, Path.GetFileName(relativePath));
```

with:

```csharp
        lock (_lock)
        {
            // Derive the archive-relative path from the GUARDED source, never from the wire
            // string. PathGuard accepts dot segments as long as the final resolved path lands
            // inside the root, so "../../../<tail of the sync root>/f.txt" is a legal alias for
            // a file we own. Replaying that alias from SessionRoot pushes the destination back
            // OUT of the archive (GetFullPath clamps ".." at the drive root) and into the live
            // sync tree, where the next scan re-syncs the "archive" copy to the peer and Prune
            // can never reclaim it.
            var rel = Path.GetRelativePath(_syncFolder, sourcePath);
            var reasonRoot = Path.Combine(SessionRoot, ReasonFolder(reason));
            var destDir = Path.Combine(reasonRoot, Path.GetDirectoryName(rel) ?? "");
            var destPath = Path.Combine(destDir, Path.GetFileName(rel));

            // Invariant assertion, not a second guard: `rel` cannot escape today. It exists so
            // that a future edit reintroducing an unguarded string here fails by returning
            // false instead of silently writing outside the session folder.
            var reasonRootFull = Path.GetFullPath(reasonRoot);
            if (!reasonRootFull.EndsWith(Path.DirectorySeparatorChar))
                reasonRootFull += Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(destPath).StartsWith(reasonRootFull, StringComparison.Ordinal))
                return false;

            Directory.CreateDirectory(destDir);

            var fileName = Path.GetFileNameWithoutExtension(rel);
            var ext = Path.GetExtension(rel);
```

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ArchiveManagerTests"`

Expected: PASS — `Archive_DotSegmentAliasOfAnInsideFile_StillLandsUnderTheSessionFolder` is now green, and every Task 5.1 method stays green (`Path.GetRelativePath` reproduces the same nested layout for well-formed paths, so `Archive_PreservesNestedStructureUnderTheReasonFolder` is unaffected).

---

### Task 5.3: prune whole session folders — by age, then by size cap

- [ ] **Step 1: Write the failing tests**

Append inside `ArchiveManagerTests`:

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
    public void Prune_KeepAgeLargerThanTheCalendar_KeepsEverythingInsteadOfThrowing()
    {
        // DateTime.UtcNow - TimeSpan.MaxValue underflows DateTime.MinValue and throws
        // ArgumentOutOfRangeException. Prune runs at session start, before any transfer, so an
        // out-of-range keepAge must degrade to "keep everything", never abort the whole sync.
        var ancient = CreateArchivedSession(DateTime.UtcNow.AddDays(-4000), "a.txt", 16);

        var result = ArchiveManager.Prune(_archiveDir, TimeSpan.MaxValue, maxBytes: 0);

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

        // Maximally aggressive retention: anything we had written would go. Nothing here was.
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

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ArchiveManagerTests.Prune"`

Expected: FAIL to build — `CS0117: 'ArchiveManager' does not contain a definition for 'Prune'`.

- [ ] **Step 3: Implement**

In `src/RemoteFileSync/Backup/ArchiveManager.cs`, insert immediately after the `ReasonFolder` method and before the closing brace of the class:

```csharp
    /// <summary>
    /// Applies retention to <paramref name="archiveRoot"/>: first drops sessions older than
    /// <paramref name="keepAge"/>, then drops the oldest survivors until the total falls to
    /// <paramref name="maxBytes"/>. <c>keepAge &lt;= TimeSpan.Zero</c> disables the age rule
    /// (--archive-keep-days 0 = keep forever); <c>maxBytes &lt;= 0</c> disables the size cap.
    /// Whole session folders only — a half-emptied session is not a restore point.
    /// </summary>
    public static PruneResult Prune(string archiveRoot, TimeSpan keepAge, long maxBytes)
    {
        // Both rules off: skip the directory walk entirely rather than sizing the whole archive
        // on every sync just to decide that nothing is eligible.
        if (keepAge <= TimeSpan.Zero && maxBytes <= 0) return new PruneResult(0, 0);

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

        if (keepAge > TimeSpan.Zero)
        {
            // The cutoff is computed INSIDE this guard, and clamped. Prune runs at session
            // start, before any transfer: `DateTime.UtcNow - keepAge` throws
            // ArgumentOutOfRangeException for a keepAge older than the calendar itself, which
            // would abort the entire sync rather than merely skipping retention.
            var now = DateTime.UtcNow;
            var cutoff = keepAge < now - DateTime.MinValue ? now - keepAge : DateTime.MinValue;

            foreach (var s in sessions)
            {
                if (s.Start < cutoff && TryDeleteSession(s.Path))
                {
                    removed++;
                    freed += s.Bytes;
                    continue;
                }
                survivors.Add(s);
            }
        }
        else
        {
            survivors.AddRange(sessions);
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
    /// Retention is best-effort: a locked or unreadable session folder must not fail the sync
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

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ArchiveManagerTests"`

Expected: PASS — every `Prune_*` method above plus all Task 5.1/5.2 methods.

---

### Task 5.4: migrate all six call sites, create the shared `archive` local, delete `BackupManager`

- [ ] **Step 1: Remove the superseded type and its tests**

```bash
git rm src/RemoteFileSync/Backup/BackupManager.cs
git rm tests/RemoteFileSync.Tests/Backup/BackupManagerTests.cs
```

`BackupManagerTests.cs` holds seven test methods — `BackupFile_CopiesToDatedFolder_LeavingOriginalInPlace`, `BackupAndRemove_CopiesThenDeletesOriginal`, `BackupAndRemove_FileDoesNotExist_ReturnsFalse`, `BackupFile_PreservesSubdirectoryStructure`, `BackupFile_DuplicateSameDay_AppendsNumericSuffix`, `BackupFile_FileDoesNotExist_ReturnsFalse`, `BackupFile_ThreadSafe_NoCrash`. Every property they assert is re-asserted against `ArchiveManager` in Tasks 5.1-5.2, plus destination-containment coverage the old suite lacked.

- [ ] **Step 2: Run and watch the build fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ArchiveManagerTests"`

Expected: FAIL to build — `SyncClient.cs(209,26): error CS0246: The type or namespace name 'BackupManager' could not be found` and the same at `SyncServer.cs(173,26)`. The whole test assembly stops compiling until the six call sites are migrated.

- [ ] **Step 3: Implement**

**3a — `SyncClient.cs:85-87`, capture the one session instant.** Replace exactly:

```csharp
        var sw = Stopwatch.StartNew();
        int skippedFiles = 0;
        bool stopped = false;
```

with:

```csharp
        var sw = Stopwatch.StartNew();
        int skippedFiles = 0;
        bool stopped = false;

        // ONE clock read for the whole run. Everything stamped with this instant — the archive
        // session folder below and the conflict-rename filenames a later phase adds — must
        // agree, or a run longer than a second scatters its own output across two session
        // names and neither is a complete restore point.
        var sessionStartUtc = DateTime.UtcNow;
```

**3b — `SyncClient.cs:209`, the shared `archive` local.** Replace exactly:

```csharp
        var backup = new BackupManager(_options.Folder, _options.EffectiveBackupFolder);
```

with:

```csharp
        // Retention runs here, before the first archive write and before the first transfer,
        // so the session folder this run is about to create can never be a prune candidate.
        // TimeSpan.Zero — never TimeSpan.MaxValue — is the "keep forever" sentinel:
        // DateTime.UtcNow - TimeSpan.MaxValue throws and would abort the sync at session start.
        var keepAge = _options.ArchiveKeepDays > 0
            ? TimeSpan.FromDays(_options.ArchiveKeepDays)
            : TimeSpan.Zero;
        var pruned = ArchiveManager.Prune(_options.EffectiveArchiveFolder, keepAge, _options.ArchiveMaxBytes);
        if (pruned.SessionsRemoved > 0)
            _logger.Info($"Archive retention: removed {pruned.SessionsRemoved} session(s), " +
                         $"freed {pruned.BytesFreed / 1024} KB.");

        // The single ArchiveManager for this session. Later phases REUSE this local; a second
        // instance means a second session folder for the same run.
        var archive = new ArchiveManager(_options.Folder, _options.EffectiveArchiveFolder, sessionStartUtc);
```

**3c — `SyncClient.cs:370-371`, overwrite snapshot.** Replace exactly:

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

**3d — `SyncClient.cs:425`, deletion propagation.** Replace exactly:

```csharp
                        if (backup.BackupAndRemove(path))
```

with:

```csharp
                        if (archive.Archive(path, ArchiveReason.Deleted, removeOriginal: true))
```

**3e — `SyncServer.cs:128-130`, capture the one session instant.** Replace exactly:

```csharp
        var sw = Stopwatch.StartNew();
        int skippedFiles = 0;
        bool stopped = false;
```

with:

```csharp
        var sw = Stopwatch.StartNew();
        int skippedFiles = 0;
        bool stopped = false;

        // ONE clock read for the whole run. Everything stamped with this instant — the archive
        // session folder below and the conflict-rename filenames a later phase adds — must
        // agree, or a run longer than a second scatters its own output across two session
        // names and neither is a complete restore point.
        var sessionStartUtc = DateTime.UtcNow;
```

**3f — `SyncServer.cs:173`, the shared `archive` local.** Replace exactly:

```csharp
        var backup = new BackupManager(_options.Folder, _options.EffectiveBackupFolder);
```

with:

```csharp
        // Retention runs here, before the first archive write and before the first transfer,
        // so the session folder this run is about to create can never be a prune candidate.
        // TimeSpan.Zero — never TimeSpan.MaxValue — is the "keep forever" sentinel:
        // DateTime.UtcNow - TimeSpan.MaxValue throws and would abort the sync at session start.
        var keepAge = _options.ArchiveKeepDays > 0
            ? TimeSpan.FromDays(_options.ArchiveKeepDays)
            : TimeSpan.Zero;
        var pruned = ArchiveManager.Prune(_options.EffectiveArchiveFolder, keepAge, _options.ArchiveMaxBytes);
        if (pruned.SessionsRemoved > 0)
            _logger.Info($"Archive retention: removed {pruned.SessionsRemoved} session(s), " +
                         $"freed {pruned.BytesFreed / 1024} KB.");

        // The single ArchiveManager for this session. Later phases REUSE this local; a second
        // instance means a second session folder for the same run.
        var archive = new ArchiveManager(_options.Folder, _options.EffectiveArchiveFolder, sessionStartUtc);
```

**3g — `SyncServer.cs:192-193`, overwrite snapshot.** Replace exactly:

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

**3h — `SyncServer.cs:260`, deletion propagation.** Replace exactly:

```csharp
                        if (backup.BackupAndRemove(path))
```

with:

```csharp
                        if (archive.Archive(path, ArchiveReason.Deleted, removeOriginal: true))
```

No `using` changes: `using RemoteFileSync.Backup;` is already present at `SyncClient.cs:3` and `SyncServer.cs:4`.

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet build -c Release` then `dotnet test -c Release --filter "FullyQualifiedName~ArchiveManagerTests"`

Expected: build succeeds with `BackupManager` gone, and every `ArchiveManagerTests` method is green.

---

### Task 5.5: the overwrite commit must be gated on a *proven* archive

Task 5.4's 3c/3g wire `archive.Archive(...)` into `onBeforeCommit`, but `ReceiveFileAsync` **throws the hook's answer away**. `src/RemoteFileSync/Transfer/FileTransfer.cs:163-164`, verbatim on `main`:

```csharp
                        onBeforeCommit?.Invoke(relativePath);
                        CommitWithRetry(stagingPath, destPath);
```

`CommitWithRetry` runs unconditionally. That was survivable when the hook was `BackupManager.BackupFile`, whose only interesting failure was "the destination did not exist yet". It is **data loss** now: `Archive` returns `false` *without throwing* when `PathGuard.TryResolveWithinRoot` fails, and `PathGuard` fails **closed** on transient IO — `PathGuard.cs:85-86` returns `true` from `HasReparsePointAncestor` for `IOException`/`UnauthorizedAccessException`, which makes `TryResolveWithinRoot` return `false`. A momentary sharing violation or an AV scanner touching any ancestor directory is enough. So an ordinary overwrite of a real user file proceeds with **no archived copy anywhere** — the pre-overwrite snapshot this whole phase exists to produce is silently skipped, and the previous version is gone. Nothing logs, nothing fails, the sync reports success.

The fix has two halves, and the second is what makes it correct:

1. `ReceiveFileAsync` must refuse to commit when the hook says the outgoing version is unprotected.
2. The hook must be able to *say which*. `bool` is not enough: `Archive` returns `false` both for "the file does not exist, there is nothing to preserve" (a brand-new file arriving — must commit) and "we could not preserve it" (must refuse). Gating on a bare `Archive(...)` would break every first-time file transfer in the product. Hence `ArchiveOutcome`.

- [ ] **Step 1: Write the failing tests**

Append inside `FileTransferTests` in `tests/RemoteFileSync.Tests/Transfer/FileTransferTests.cs`, and add `using RemoteFileSync.Backup;` to its using block:

```csharp
    [Fact]
    public async Task Receive_PreCommitHookReturnsFalse_RefusesToOverwriteAndKeepsOldBytes()
    {
        var sourceDir = Path.Combine(_tempDir, "gate_source");
        var destDir = Path.Combine(_tempDir, "gate_dest");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);

        var destFile = Path.Combine(destDir, "important.txt");
        File.WriteAllText(destFile, "PRECIOUS ORIGINAL");
        File.WriteAllText(Path.Combine(sourceDir, "important.txt"), "replacement payload");

        // A REAL ArchiveManager, made to fail the way production fails: its archive root is
        // unreachable because a plain FILE sits where the session folder must be created, so
        // Directory.CreateDirectory throws IOException. This is the same observable outcome as
        // PathGuard failing closed on transient IO (PathGuard.cs:85-86) — Archive returns false
        // and does NOT throw — but it is deterministic, whereas a reparse-point/locking race
        // is not reproducible in CI.
        var blocker = Path.Combine(_tempDir, "not-a-directory");
        File.WriteAllText(blocker, "x");
        var archive = new ArchiveManager(destDir, Path.Combine(blocker, "archive"),
                                         new DateTime(2026, 7, 19, 14, 30, 52, DateTimeKind.Utc));

        using var pipeStream = new MemoryStream();
        var sender = new FileTransferSender(sourceDir, blockSize: 1024);
        var receiver = new FileTransferReceiver(destDir);
        await sender.SendFileAsync(pipeStream, fileId: 1, relativePath: "important.txt", CancellationToken.None);
        pipeStream.Position = 0;

        var result = await receiver.ReceiveFileAsync(pipeStream, CancellationToken.None,
            onBeforeCommit: p =>
                archive.TryArchive(p, ArchiveReason.Overwritten, removeOriginal: false)
                    != ArchiveOutcome.Failed);

        // The commit is refused, loudly. Before this task the transfer reported success and the
        // only copy of "PRECIOUS ORIGINAL" ceased to exist.
        Assert.False(result.Success);
        Assert.Equal("Refusing to overwrite: pre-overwrite archive failed", result.ErrorMessage);
        Assert.Equal("PRECIOUS ORIGINAL", File.ReadAllText(destFile));
        Assert.Empty(Directory.GetFiles(destDir, $"*{FileTransferReceiver.StagingSuffix}*"));
    }

    [Fact]
    public async Task Receive_ArchiveManagerRootedOutsideTheSyncFolder_HasNothingToArchiveAndStillCommits()
    {
        // The companion case, and the reason the gate is NOT a bare `&& archive.Archive(...)`.
        // Rooting the manager elsewhere means the source path it guards simply does not exist,
        // which is indistinguishable — through `bool` — from the failure above. It is the
        // BRAND-NEW-FILE shape: there is no outgoing version to preserve, so the commit MUST
        // proceed. Gating on `Archive(...)` alone would break every first-ever file transfer.
        var sourceDir = Path.Combine(_tempDir, "new_source");
        var destDir = Path.Combine(_tempDir, "new_dest");
        var elsewhere = Path.Combine(_tempDir, "elsewhere");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(sourceDir, "brand-new.txt"), "first version");

        var archive = new ArchiveManager(elsewhere, Path.Combine(_tempDir, "arc"),
                                         new DateTime(2026, 7, 19, 14, 30, 52, DateTimeKind.Utc));

        using var pipeStream = new MemoryStream();
        var sender = new FileTransferSender(sourceDir, blockSize: 1024);
        var receiver = new FileTransferReceiver(destDir);
        await sender.SendFileAsync(pipeStream, fileId: 1, relativePath: "brand-new.txt", CancellationToken.None);
        pipeStream.Position = 0;

        Assert.Equal(ArchiveOutcome.NothingToArchive,
            archive.TryArchive("brand-new.txt", ArchiveReason.Overwritten, removeOriginal: false));

        var result = await receiver.ReceiveFileAsync(pipeStream, CancellationToken.None,
            onBeforeCommit: p =>
                archive.TryArchive(p, ArchiveReason.Overwritten, removeOriginal: false)
                    != ArchiveOutcome.Failed);

        Assert.True(result.Success);
        Assert.Equal("first version", File.ReadAllText(Path.Combine(destDir, "brand-new.txt")));
    }
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~FileTransferTests"`

Expected: FAIL to build — `CS0117: 'ArchiveManager' does not contain a definition for 'TryArchive'` and `CS0246: The type or namespace name 'ArchiveOutcome' could not be found`. After Step 3a alone (enum + `TryArchive`, no receiver change), it compiles and `Receive_PreCommitHookReturnsFalse_RefusesToOverwriteAndKeepsOldBytes` fails on `Assert.False(result.Success)` — the exact live defect: the commit happened anyway and `important.txt` now reads `"replacement payload"`.

- [ ] **Step 3: Implement**

**3a — `ArchiveManager`: add the three-way outcome.** In `src/RemoteFileSync/Backup/ArchiveManager.cs`, add beside `ArchiveReason`:

```csharp
/// <summary>
/// Why an archive attempt ended. `bool` conflates the last two: a caller about to destroy the
/// original must proceed on NothingToArchive (there was no previous version) and refuse on
/// Failed (there was one and we could not preserve it).
/// </summary>
public enum ArchiveOutcome { Archived, NothingToArchive, Failed }
```

Then replace the `Archive` method's signature line and its two guard lines, exactly:

```csharp
    public bool Archive(string relativePath, ArchiveReason reason, bool removeOriginal)
    {
        // relativePath can arrive from the network (deletion propagation), so it must be
        // contained before it reaches the filesystem.
        if (!PathGuard.TryResolveWithinRoot(_syncFolder, relativePath, out var sourcePath)) return false;
        if (!File.Exists(sourcePath)) return false;
```

with:

```csharp
    public bool Archive(string relativePath, ArchiveReason reason, bool removeOriginal)
        => TryArchive(relativePath, reason, removeOriginal) == ArchiveOutcome.Archived;

    /// <summary>
    /// As <see cref="Archive"/>, but distinguishes "there was no file to preserve" from "we
    /// could not preserve it". PathGuard fails CLOSED on transient IO (PathGuard.cs:85-86), so
    /// containment failure is reported as Failed, never as NothingToArchive.
    /// </summary>
    public ArchiveOutcome TryArchive(string relativePath, ArchiveReason reason, bool removeOriginal)
    {
        // relativePath can arrive from the network (deletion propagation), so it must be
        // contained before it reaches the filesystem.
        if (!PathGuard.TryResolveWithinRoot(_syncFolder, relativePath, out var sourcePath))
            return ArchiveOutcome.Failed;
        if (!File.Exists(sourcePath)) return ArchiveOutcome.NothingToArchive;
```

Then replace the whole `lock (_lock) { ... }` body — as Task 5.2 left it — exactly:

```csharp
        lock (_lock)
        {
            var rel = Path.GetRelativePath(_syncFolder, sourcePath);
            var reasonRoot = Path.Combine(SessionRoot, ReasonFolder(reason));
            var destDir = Path.Combine(reasonRoot, Path.GetDirectoryName(rel) ?? "");
            var destPath = Path.Combine(destDir, Path.GetFileName(rel));

            var reasonRootFull = Path.GetFullPath(reasonRoot);
            if (!reasonRootFull.EndsWith(Path.DirectorySeparatorChar))
                reasonRootFull += Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(destPath).StartsWith(reasonRootFull, StringComparison.Ordinal))
                return false;

            Directory.CreateDirectory(destDir);

            var fileName = Path.GetFileNameWithoutExtension(rel);
            var ext = Path.GetExtension(rel);

            int suffix = 1;
            while (File.Exists(destPath))
            {
                destPath = Path.Combine(destDir, $"{fileName}_{suffix}{ext}");
                suffix++;
            }

            File.Copy(sourcePath, destPath, overwrite: false);
            if (removeOriginal) File.Delete(sourcePath);
            return true;
        }
    }
```

with:

```csharp
        lock (_lock)
        {
            var rel = Path.GetRelativePath(_syncFolder, sourcePath);
            var reasonRoot = Path.Combine(SessionRoot, ReasonFolder(reason));
            var destDir = Path.Combine(reasonRoot, Path.GetDirectoryName(rel) ?? "");
            var destPath = Path.Combine(destDir, Path.GetFileName(rel));

            var reasonRootFull = Path.GetFullPath(reasonRoot);
            if (!reasonRootFull.EndsWith(Path.DirectorySeparatorChar))
                reasonRootFull += Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(destPath).StartsWith(reasonRootFull, StringComparison.Ordinal))
                return ArchiveOutcome.Failed;

            // Everything from here down touches the filesystem. An exception escaping this
            // method would unwind ReceiveFileAsync's onBeforeCommit callback, which is invoked
            // outside its own try, so the caller would get no FileReceiveResult at all and the
            // staging file would be stranded. Report the failure instead of throwing it.
            try
            {
                Directory.CreateDirectory(destDir);

                var fileName = Path.GetFileNameWithoutExtension(rel);
                var ext = Path.GetExtension(rel);

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
                return ArchiveOutcome.Archived;
            }
            catch (IOException) { return ArchiveOutcome.Failed; }
            catch (UnauthorizedAccessException) { return ArchiveOutcome.Failed; }
        }
    }
```

Note the asymmetry in the `removeOriginal: true` case: if `File.Delete` throws after `File.Copy` succeeded, the outcome is `Failed` even though a copy was written. That is the safe direction — the caller abandons its destructive step, and the worst outcome is a redundant archived copy plus a file that is still present.

**3b — `FileTransfer.cs`: gate the commit.** Replace the `ReceiveFileAsync` doc comment exactly:

```csharp
    /// <summary>
    /// <paramref name="onBeforeCommit"/> receives the verified file's relative path immediately
    /// before the destination is replaced, so callers can snapshot the outgoing version. It is
    /// driven by the path actually received, not by plan order.
    /// </summary>
```

with:

```csharp
    /// <summary>
    /// <paramref name="onBeforeCommit"/> receives the verified file's relative path immediately
    /// before the destination is replaced, so callers can snapshot the outgoing version. It is
    /// driven by the path actually received, not by plan order.
    /// <para>
    /// Its return value is a COMMIT GATE, not advisory: false means "the outgoing version is
    /// not protected" and the destination is left untouched. A hook that had nothing to
    /// snapshot (brand-new file) must return true. The result was previously discarded, so an
    /// archive that failed silently — PathGuard fails closed on transient IO — still let the
    /// overwrite destroy the only copy of the previous version.
    /// </para>
    /// </summary>
```

Then replace the commit region exactly (`FileTransfer.cs:157-165` on `main`):

```csharp
                        // Preserve the source timestamp so the file compares equal on the next
                        // sync. A hostile peer can send arbitrary ticks, so clamp to valid range.
                        // File.Move preserves the stamp, so set it before committing.
                        var ticks = Math.Clamp(lastModifiedUtcTicks, 0, DateTime.MaxValue.Ticks);
                        File.SetLastWriteTimeUtc(stagingPath, new DateTime(ticks, DateTimeKind.Utc));

                        onBeforeCommit?.Invoke(relativePath);
                        CommitWithRetry(stagingPath, destPath);
                        return new FileReceiveResult(true, relativePath);
```

with:

```csharp
                        // Preserve the source timestamp so the file compares equal on the next
                        // sync. A hostile peer can send arbitrary ticks, so clamp to valid range.
                        // File.Move preserves the stamp, so set it before committing.
                        var ticks = Math.Clamp(lastModifiedUtcTicks, 0, DateTime.MaxValue.Ticks);
                        File.SetLastWriteTimeUtc(stagingPath, new DateTime(ticks, DateTimeKind.Utc));

                        // The hook is a gate. It returns false only when it was asked to
                        // preserve an existing destination and could not; "nothing to preserve"
                        // returns true. Committing anyway would destroy the previous version
                        // with no restore point, which is precisely the failure this phase's
                        // archive exists to prevent — so the destination stays as it is and the
                        // finally block sweeps the staging file. The transfer is retried on the
                        // next sync, when the archive root may well be reachable again.
                        if (onBeforeCommit != null && !onBeforeCommit(relativePath))
                        {
                            return new FileReceiveResult(false, relativePath,
                                "Refusing to overwrite: pre-overwrite archive failed");
                        }

                        CommitWithRetry(stagingPath, destPath);
                        return new FileReceiveResult(true, relativePath);
```

**3c — `SyncClient.cs`, the receiving hook.** Replace the lambda Task 5.4/3c installed, exactly:

```csharp
                    result = await receiver.ReceiveFileAsync(stream, ct,
                        onBeforeCommit: p => action.Action == SyncActionType.SendToClient
                            && archive.Archive(p, ArchiveReason.Overwritten, removeOriginal: false));
```

with:

```csharp
                    result = await receiver.ReceiveFileAsync(stream, ct,
                        onBeforeCommit: p =>
                        {
                            // Not an overwrite of a file we already hold (ServerOnly pull):
                            // there is no previous version, so nothing to protect.
                            if (action.Action != SyncActionType.SendToClient) return true;

                            var outcome = archive.TryArchive(p, ArchiveReason.Overwritten, removeOriginal: false);
                            if (outcome == ArchiveOutcome.Failed)
                                _logger.Error($"Pre-overwrite archive failed for {p}; " +
                                              "refusing to overwrite the local copy.");
                            // NothingToArchive: no local file to preserve, commit is safe.
                            return outcome != ArchiveOutcome.Failed;
                        });
```

**3d — `SyncServer.cs`, the receiving hook.** Replace the lambda Task 5.4/3g installed, exactly:

```csharp
                result = await receiver.ReceiveFileAsync(stream, ct,
                    onBeforeCommit: p => action.Action == SyncActionType.SendToServer
                        && archive.Archive(p, ArchiveReason.Overwritten, removeOriginal: false));
```

with:

```csharp
                result = await receiver.ReceiveFileAsync(stream, ct,
                    onBeforeCommit: p =>
                    {
                        // Not an overwrite of a file we already hold (ClientOnly push):
                        // there is no previous version, so nothing to protect.
                        if (action.Action != SyncActionType.SendToServer) return true;

                        var outcome = archive.TryArchive(p, ArchiveReason.Overwritten, removeOriginal: false);
                        if (outcome == ArchiveOutcome.Failed)
                            _logger.Error($"Pre-overwrite archive failed for {p}; " +
                                          "refusing to overwrite the local copy.");
                        // NothingToArchive: no local file to preserve, commit is safe.
                        return outcome != ArchiveOutcome.Failed;
                    });
```

Both call sites already treat `result.Success == false` as a skipped file and increment `skippedFiles`, so a refused commit surfaces as a non-zero exit code with no further wiring.

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~FileTransferTests"` then `dotnet test -c Release --filter "FullyQualifiedName~ArchiveManagerTests"`

Expected: PASS — both new methods green, and the pre-existing `Receive_InvokesPreCommitHookWithTheReceivedPath` stays green (its hook already returns `true`, which under the new contract means "commit"). All `ArchiveManagerTests` stay green: `Archive` is now a wrapper whose `true` still means exactly `Archived`, and `Archive_MissingFile_ReturnsFalse` / `Archive_RejectsPathEscapingTheSyncRoot` still get `false` from `NothingToArchive` / `Failed`.

---

### Task 5.6: collapse the unarchived delete branch on both sides

`backupFirst` is decoded **straight off the wire** and then used to choose between an archiving delete and a raw `File.Delete`. Our own client always sends `true`, so the `false` branch is unreachable in a healthy pair — which means it is reachable **only** from a hostile or buggy peer, and what it grants them is deletion of a user's file with no restore point at all. A flag that only ever does harm should not be obeyed.

`src/RemoteFileSync/Network/SyncClient.cs:419-458`, verbatim on `main`:

```csharp
                var (path, backupFirst) = ProtocolHandler.DeserializeDeleteFile(delData);
                bool success = false;
                try
                {
                    if (backupFirst)
                    {
                        if (backup.BackupAndRemove(path))
                        {
                            success = true;
                            filesDeleted++;
                            _logger.Info($"[DEL] {path} (deleted locally)");
                            _db?.MarkDeleted(path, sessionId, "deleted on server, propagated to client");
                        }
                        else
                        {
                            _logger.Warning($"File not found for backup/delete: {path}. Skipping.");
                            skippedFiles++;
                        }
                    }
                    else if (!PathGuard.TryResolveWithinRoot(_options.Folder, path, out var fullPath))
                    {
                        _logger.Error($"Rejected delete for path outside sync root: {path}");
                        skippedFiles++;
                    }
                    else
                    {
                        if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                            success = true;
                            filesDeleted++;
                            _logger.Info($"[DEL] {path} (deleted locally)");
                            _db?.MarkDeleted(path, sessionId, "deleted on server, propagated to client");
                        }
                        else
                        {
                            _logger.Warning($"File not found for delete: {path}. Skipping.");
                            skippedFiles++;
                        }
                    }
                }
```

`src/RemoteFileSync/Network/SyncServer.cs:254-292` is the same chain without the `_db` calls and with a shorter log message (`$"[DEL] {path}"`), verbatim on `main`:

```csharp
                var (path, backupFirst) = ProtocolHandler.DeserializeDeleteFile(delData);
                bool success = false;
                try
                {
                    if (backupFirst)
                    {
                        if (backup.BackupAndRemove(path))
                        {
                            success = true;
                            filesDeleted++;
                            _logger.Info($"[DEL] {path}");
                        }
                        else
                        {
                            _logger.Warning($"File not found for backup/delete: {path}. Skipping.");
                            skippedFiles++;
                        }
                    }
                    else if (!PathGuard.TryResolveWithinRoot(_options.Folder, path, out var fullPath))
                    {
                        _logger.Error($"Rejected delete for path outside sync root: {path}");
                        skippedFiles++;
                    }
                    else
                    {
                        if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                            success = true;
                            filesDeleted++;
                            _logger.Info($"[DEL] {path}");
                        }
                        else
                        {
                            _logger.Warning($"File not found for delete: {path}. Skipping.");
                            skippedFiles++;
                        }
                    }
                }
```

Both collapse to a single archive-then-delete. The separate `PathGuard` branch goes with them: `Archive` performs the identical containment check on the same root (Task 5.1) and returns `false` when it fails, so keeping a second guard only preserves the illusion that the unguarded path below it was ever reachable. `backupFirst` stays on the wire — `ProtocolHandler.SerializeDeleteFile`/`DeserializeDeleteFile` are unchanged and old peers keep parsing — but its **value is ignored locally**, so a peer cannot talk us out of keeping a restore point.

This supersedes Task 5.4's 3d and 3h, which replaced only the `backup.BackupAndRemove(path)` line inside the `backupFirst` branch. Apply 5.4 first, then this; the anchors below quote the post-5.4 text.

- [ ] **Step 1: Write the failing test**

Append inside `ArchiveManagerTests` — the peer-driven behaviour is asserted end-to-end by Phase 10, but the local invariant belongs here:

```csharp
    [Fact]
    public void Archive_DeletedReason_IsTheOnlyDeletionPathAndAlwaysLeavesARestorePoint()
    {
        // Deletion propagation obeys a peer-supplied "back up first" flag. The flag is now
        // ignored: whatever the peer asks for, the file is archived before it is removed, so
        // a hostile or buggy peer cannot make us delete without a restore point.
        CreateSyncFile("victim.txt", "irreplaceable");
        var mgr = NewManager(Stamp);

        Assert.True(mgr.Archive("victim.txt", ArchiveReason.Deleted, removeOriginal: true));
        Assert.False(File.Exists(Path.Combine(_syncDir, "victim.txt")));
        Assert.Equal("irreplaceable", File.ReadAllText(
            Path.Combine(_archiveDir, StampFolder, "deleted", "victim.txt")));
    }
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Archive_DeletedReason_IsTheOnlyDeletionPathAndAlwaysLeavesARestorePoint"`

Expected: before Task 5.1 it fails to build; after 5.1 it is green and stands as the regression lock for Step 3. The behaviour that actually regresses — a peer sending `backupFirst: false` — is covered by Phase 10's integration test; the local guarantee this step must not lose is that the only deletion path in the codebase is the archiving one, which `git grep` verifies below.

- [ ] **Step 3: Implement**

**3a — `SyncClient.cs`.** Replace the whole chain, exactly (post-5.4 text — 5.4/3d already rewrote the inner `if`):

```csharp
                    if (backupFirst)
                    {
                        if (archive.Archive(path, ArchiveReason.Deleted, removeOriginal: true))
                        {
                            success = true;
                            filesDeleted++;
                            _logger.Info($"[DEL] {path} (deleted locally)");
                            _db?.MarkDeleted(path, sessionId, "deleted on server, propagated to client");
                        }
                        else
                        {
                            _logger.Warning($"File not found for backup/delete: {path}. Skipping.");
                            skippedFiles++;
                        }
                    }
                    else if (!PathGuard.TryResolveWithinRoot(_options.Folder, path, out var fullPath))
                    {
                        _logger.Error($"Rejected delete for path outside sync root: {path}");
                        skippedFiles++;
                    }
                    else
                    {
                        if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                            success = true;
                            filesDeleted++;
                            _logger.Info($"[DEL] {path} (deleted locally)");
                            _db?.MarkDeleted(path, sessionId, "deleted on server, propagated to client");
                        }
                        else
                        {
                            _logger.Warning($"File not found for delete: {path}. Skipping.");
                            skippedFiles++;
                        }
                    }
```

with:

```csharp
                    // `backupFirst` is decoded from the wire and DELIBERATELY IGNORED. It stays
                    // in the protocol so old peers keep parsing DeleteFile, and it is still
                    // echoed to the progress stream below as "what the peer asked for", but it
                    // no longer selects a delete-without-archive path: our own client always
                    // sends true, so that path was reachable only from a hostile or buggy peer,
                    // and all it could ever do is destroy a file with no restore point.
                    // Archive() already performs the same PathGuard containment check against
                    // _options.Folder and returns false when it fails, so the separate guard
                    // branch that used to sit here is redundant, not lost.
                    if (archive.Archive(path, ArchiveReason.Deleted, removeOriginal: true))
                    {
                        success = true;
                        filesDeleted++;
                        _logger.Info($"[DEL] {path} (deleted locally)");
                        _db?.MarkDeleted(path, sessionId, "deleted on server, propagated to client");
                    }
                    else
                    {
                        // Not found, outside the sync root, or unarchivable — in every case we
                        // decline to delete rather than delete unprotected.
                        _logger.Warning($"Could not archive {path} for deletion. Skipping.");
                        skippedFiles++;
                    }
```

**3b — `SyncServer.cs`.** Replace the whole chain, exactly (post-5.4 text — 5.4/3h already rewrote the inner `if`):

```csharp
                    if (backupFirst)
                    {
                        if (archive.Archive(path, ArchiveReason.Deleted, removeOriginal: true))
                        {
                            success = true;
                            filesDeleted++;
                            _logger.Info($"[DEL] {path}");
                        }
                        else
                        {
                            _logger.Warning($"File not found for backup/delete: {path}. Skipping.");
                            skippedFiles++;
                        }
                    }
                    else if (!PathGuard.TryResolveWithinRoot(_options.Folder, path, out var fullPath))
                    {
                        _logger.Error($"Rejected delete for path outside sync root: {path}");
                        skippedFiles++;
                    }
                    else
                    {
                        if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                            success = true;
                            filesDeleted++;
                            _logger.Info($"[DEL] {path}");
                        }
                        else
                        {
                            _logger.Warning($"File not found for delete: {path}. Skipping.");
                            skippedFiles++;
                        }
                    }
```

with:

```csharp
                    // `backupFirst` is decoded from the wire and DELIBERATELY IGNORED. It stays
                    // in the protocol so old peers keep parsing DeleteFile, but it no longer
                    // selects a delete-without-archive path: our own client always sends true,
                    // so that path was reachable only from a hostile or buggy peer, and all it
                    // could ever do is destroy a file with no restore point. Archive() already
                    // performs the same PathGuard containment check against _options.Folder and
                    // returns false when it fails, so the separate guard branch that used to sit
                    // here is redundant, not lost.
                    if (archive.Archive(path, ArchiveReason.Deleted, removeOriginal: true))
                    {
                        success = true;
                        filesDeleted++;
                        _logger.Info($"[DEL] {path}");
                    }
                    else
                    {
                        // Not found, outside the sync root, or unarchivable — in every case we
                        // decline to delete rather than delete unprotected.
                        _logger.Warning($"Could not archive {path} for deletion. Skipping.");
                        skippedFiles++;
                    }
```

The `_progress.WriteDelete(path, backed_up: backupFirst, success: success)` line in `SyncClient` is left as it is: it reports what the peer requested, and `backupFirst` remains in scope. Both `path` and `backupFirst` are still consumed, so the `DeserializeDeleteFile` tuple deconstruction needs no change and no unused-variable warning appears.

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet build -c Release` then `dotnet test -c Release --filter "FullyQualifiedName~ArchiveManagerTests"`

Expected: build succeeds — in particular no CS0219/CS8321 for `backupFirst` (still read by `WriteDelete` on the client; on the server it is intentionally unread, so if the compiler flags the deconstruction there, discard it as `var (path, _) = ...` and drop the comment's first sentence accordingly). Then:

```bash
git grep -n "File.Delete" -- src/RemoteFileSync/Network
```

Expected: **no matches.** Every deletion in the network layer now goes through `ArchiveManager.Archive(..., removeOriginal: true)`, which copies before it removes.

---

### Known-red tests handed to Phase 10

Four integration assertions hard-code the old `.rfs-backups-NAME/<yyyyMMdd>/` layout and go red at this commit. They live in `tests/RemoteFileSync.Tests/Integration/`, which CONTRACT.md assigns solely to **Phase 10** — this phase deliberately does not touch them, because a Phase 10 edit that re-quotes text I had already rewritten would have no anchor.

| Site | Current assertion | Must become |
|---|---|---|
| `DeleteSyncTests.cs:109` | `Path.Combine(_testRoot, ".rfs-backups-server", dateStr, "to-delete.txt")` | the run's session folder under `.rfs-archive-server`, `deleted/to-delete.txt` |
| `DeleteSyncTests.cs:161` | `.rfs-backups-server/<dateStr>/client-deleted.txt` | `.rfs-archive-server/<session>/deleted/client-deleted.txt` |
| `DeleteSyncTests.cs:162` | `.rfs-backups-client/<dateStr>/server-deleted.txt` | `.rfs-archive-client/<session>/deleted/server-deleted.txt` |
| `EndToEndTests.cs:110` | `.rfs-backups-server/<dateStr>/shared.txt` | `.rfs-archive-server/<session>/overwritten/shared.txt` |

The session folder name is not predictable from the test, so Phase 10 must locate it (a single directory under the archive root whose name parses with `ArchiveManager.SessionFolderFormat`) rather than recompute a stamp — this is the `AssertArchived` helper Phase 10 already plans. Each site's now-unused `dateStr` local must go with it.

---

### Phase 5 commit

```bash
git checkout feat/deletion-sync-ancestor-merge
git add -A src/RemoteFileSync/Backup tests/RemoteFileSync.Tests/Backup
git add src/RemoteFileSync/Network/SyncClient.cs src/RemoteFileSync/Network/SyncServer.cs
git add src/RemoteFileSync/Transfer/FileTransfer.cs tests/RemoteFileSync.Tests/Transfer/FileTransferTests.cs
git commit -m "feat: replace BackupManager with ArchiveManager (session folders, reasons, retention)

The session stamp is captured once per run by the caller and passed to the
constructor. BackupManager read DateTime.UtcNow on every call, so a run
crossing midnight UTC scattered one logical session across two dated
folders and neither half was a complete restore point. Each peer now
captures a single sessionStartUtc at the top of HandleConnectionAsync and
builds exactly one ArchiveManager from it.

Layout is <archiveRoot>/<yyyyMMdd-HHmmss>/<reason>/<relative path>, with
reason in {deleted, overwritten, conflict}. The destination is derived
from the PathGuard-resolved source, not from the wire string: PathGuard
accepts dot-segment aliases that resolve inside the sync root, and
replaying such an alias from the session folder pushed the archive copy
back into the live sync tree, where it re-synced to the peer forever and
Prune could never reclaim it.

Prune removes whole session folders, oldest first, by age and then by
size cap, and skips any directory whose name does not parse as a session
stamp so it can never delete something it did not create. TimeSpan.Zero
is the keep-forever sentinel and the age cutoff is computed inside that
guard and clamped, because DateTime.UtcNow - TimeSpan.MaxValue throws and
Prune runs at session start, before any transfer.

The pre-overwrite snapshot is now a commit gate. ReceiveFileAsync
discarded onBeforeCommit's bool and committed unconditionally, so an
archive that returned false without throwing - PathGuard fails closed on
transient IO - let the overwrite destroy the only copy of the previous
version, silently and with a success result. The hook now answers with
ArchiveOutcome so it can distinguish 'nothing to preserve' (brand-new
file, commit) from 'could not preserve it' (refuse), and the receiver
leaves the destination untouched in the second case.

Deletion propagation no longer honours the peer's backupFirst flag. The
flag stays on the wire for compatibility but its value is ignored: our
client always sends true, so the delete-without-archive branch was
reachable only from a hostile or buggy peer and could only ever destroy a
file with no restore point. Both sides now archive-then-delete
unconditionally; the separate PathGuard branch went with it because
Archive performs the same containment check on the same root.

BackupManager is deleted rather than kept as a shim: a shim has no
session stamp to delegate, so it would either lie about its layout or
reintroduce the per-file clock read, and its yyyyMMdd folders would be
unparseable to Prune and leak forever.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
git push -u origin feat/deletion-sync-ancestor-merge
```

**Verification before commit:**

```bash
dotnet build -c Release
dotnet test -c Release --filter "FullyQualifiedName~ArchiveManagerTests"
dotnet test -c Release --filter "FullyQualifiedName~FileTransferTests"
dotnet test -c Release
git grep -n "BackupManager" -- src tests
git grep -n "File.Delete" -- src/RemoteFileSync/Network
```

- `dotnet build -c Release`: 0 errors, 0 warnings. In particular no CS0128/CS0136 for `archive` or `sessionStartUtc` — this phase declares each exactly once per method, and nothing else in the tree declares them.
- `ArchiveManagerTests`: all green. The methods that must be green by name — `SessionFolderName_IsSessionStartStamp_AndSessionRootHangsOffArchiveRoot`, `Archive_PartitionsByReason`, `Archive_PreservesNestedStructureUnderTheReasonFolder`, `Archive_RemoveOriginalFalse_LeavesOriginalInPlace`, `Archive_RemoveOriginalTrue_CopiesThenDeletesOriginal`, `Archive_SamePathTwiceInOneSession_AppendsNumericSuffix`, `Archive_RejectsPathEscapingTheSyncRoot`, `Archive_MissingFile_ReturnsFalse`, `Archive_ConcurrentCalls_AllSucceed`, `Archive_RunSpanningMidnightUtc_LandsInExactlyOneSessionFolder`, `Archive_DotSegmentAliasOfAnInsideFile_StillLandsUnderTheSessionFolder`, `Prune_RemovesSessionsOlderThanKeepAge_AndKeepsNewerOnes`, `Prune_ZeroKeepAge_KeepsEverythingForever`, `Prune_KeepAgeLargerThanTheCalendar_KeepsEverythingInsteadOfThrowing`, `Prune_EnforcesSizeCap_DeletingWholeSessionsOldestFirst`, `Prune_IgnoresDirectoriesWhoseNameDoesNotParseAsASessionStamp`, `Prune_MissingArchiveRoot_IsANoOp`, `Archive_DeletedReason_IsTheOnlyDeletionPathAndAlwaysLeavesARestorePoint`.
- `FileTransferTests`: all green, including the two new methods `Receive_PreCommitHookReturnsFalse_RefusesToOverwriteAndKeepsOldBytes` and `Receive_ArchiveManagerRootedOutsideTheSyncFolder_HasNothingToArchiveAndStillCommits`, and the pre-existing `Receive_InvokesPreCommitHookWithTheReceivedPath` / `ChecksumMismatch_LeavesExistingDestinationUntouched` (the hook contract change is additive: `true` still means commit).
- `git grep -n "File.Delete" -- src/RemoteFileSync/Network`: no matches. A match means Task 5.6 was not applied on one of the two sides and that side can still be made to delete a user's file with no restore point.
- `dotnet test -c Release` (whole suite): green **except** the four archive-layout assertions listed in "Known-red tests handed to Phase 10" (`DeleteSyncTests.cs:109`, `:161`, `:162`, `EndToEndTests.cs:110`). Any other failure is a regression from this phase and must be fixed before committing.
- `git grep -n "BackupManager" -- src tests`: no matches. Deliberately unchanged and still referenced: `SyncOptions.EffectiveBackupFolder` (`SyncOptions.cs:60`, used by `Validate()` at `:117`) and `SyncOptionsTests.EffectiveBackupFolder_*` — `SyncOptions.cs` is Phase 1's region and `EffectiveArchiveFolder` is specified to mirror its fallback rules.

---

## Phase 6: The ancestor merge engine

**Goal:** Replace every timestamp-vs-`LastSynced` heuristic in the planner with a three-way merge against a per-file ancestor row, so "which side changed" is a recorded fact where we have one and an explicitly additive fallback everywhere else. `ComputePlan` becomes pure: it returns a `PlanResult` and never touches the database. The caller's ancestor-row writes are corrected so they can no longer fabricate a peer state that never existed, and they are moved below the delete guards so an aborted run persists nothing.

**Files:**
- Modify: `src/RemoteFileSync/Sync/SyncEngine.cs:1-205` (replace all three legacy overloads with the single new one) and `src/RemoteFileSync/Sync/SyncEngine.cs:223-231` (add the `ConflictKeepBoth` case to `BuildMergedManifest`). The file is 235 lines; line 234 closes `BuildMergedManifest` and 235 closes the class — neither is edited.
- Modify: `src/RemoteFileSync/Sync/ConflictResolver.cs:9` (add the missing XML doc that pins `Resolve` to the no-ancestor path) and `:26-39` (delete `ResolveDeleteConflict`).
- Modify: `src/RemoteFileSync/Network/SyncClient.cs:149-152` (the `ComputePlan` call site) and `:185-207` (the DB-write block — note the range is **185-207**, not 185-206: line 206 closes the second `foreach`, line 207 closes `if (_db != null)`).
- Test: `tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs:1-406` (full replacement).
- Test: `tests/RemoteFileSync.Tests/Sync/ConflictResolverTests.cs:9-11` and `:69-133` (delete).
- Test (new): `tests/RemoteFileSync.Tests/Network/AncestorRowWriteTests.cs`. Deliberately placed under `Network/`, **not** under `Integration/` — the ownership table gives every file under `tests/.../Integration/` to Phase 10, and this phase must not create work there.

**Interfaces:**

*Consumes (Phase 1):* `SyncMode { Push = 1, Pull = 2, TwoWay = 3 }`; `SyncActionType.ConflictKeepBoth = 7`; `SyncOptions.Mode`; `SyncOptions.MirrorDeletes`. Every `Bidirectional =` assignment in `src/` and `tests/` is already migrated by Phase 1 — this phase re-applies none of them.

*Consumes (Phase 2), as pure types with no call sites yet:*
- `AncestorRow(string Path, long ClientSize, long ClientMtimeTicks, long ServerSize, long ServerMtimeTicks, string Status, long LastSyncedTicks, long? DeletedUtcTicks)`
- `ChangeDetector.Unchanged(FileEntry, long rowSize, long rowMtimeTicks)` and `ChangeDetector.Tolerance`
- `ClockSkew` with `ClockSkew.None` and `NormaliseServerTime(DateTime)`
- `PlanResult` with `Entries` / `Resurrections` / `Conflicts`, plus `ResurrectionInfo(string Path, bool KeptClientCopy, long KeptSize, long KeptMtimeTicks)` and `ConflictInfo(string Path, long ClientSize, long ClientMtimeTicks, long ServerSize, long ServerMtimeTicks)`

This phase creates **none** of those files. `AncestorRow.cs`, `ChangeDetector.cs`, `ClockSkew.cs` and `PlanResult.cs` land in Phase 2 with their own tests; `ChangeDetectorTests.cs` is Phase 2's, not this phase's.

*Consumes (Phase 3) — a local, reused, never redeclared:* Phase 3's handshake rewrite of `SyncClient.cs:89-113` leaves a `ClockSkew skew` local in `HandleConnectionAsync`. This phase **reads that local**; declaring a second `skew` is CS0128. Phase 3's edit shifts line numbers below it, so every "Replace exactly" block here is anchored on **text**, not on the cited line number.

*Consumes (Phase 4), the frozen `SyncDatabase` surface:* `LoadAll()`, `GetRow(string)`, `UpsertSynced(path, clientSize, clientMtimeTicks, serverSize, serverMtimeTicks, sessionId, direction)`, `Tombstone(path, sessionId, detail)`, and the pre-existing `MarkSkipped(path, sessionId)` (sanctioned by CONTRACT correction 6) and `MarkDeleted(path, sessionId, detail)`. `MarkDeleted` is called at `SyncClient.cs:165` inside the local-filter block, which this phase does **not** touch; Phase 4 must keep it compiling.

*Consumes (Phase 5):* nothing. Phase 5's `archive` local at `SyncClient.cs:209` is below this phase's first edit and above its second; neither block references it and neither redeclares it.

*Produces:*
- `public static PlanResult SyncEngine.ComputePlan(FileManifest, FileManifest, SyncMode, IReadOnlyDictionary<string, AncestorRow>?, bool deleteEnabled, bool mirrorDeletes, ClockSkew skew)` — Phase 8 consumes this signature as already applied and must not re-quote the pre-Phase-6 ternary.
- `SyncEngine.BuildMergedManifest` gains a `ConflictKeepBoth` case.
- A `planResult` local in `SyncClient.HandleConnectionAsync`, in scope for the whole method, carrying `Resurrections` and `Conflicts` already pruned of filter-excluded paths.
- The corrected ancestor-write block, lifted out of its old position at `:185-207` and reinserted **below both delete guards**, immediately above the `// 7. Send files to server` landmark.

  **Handoff — this is not the block's final position.** Phase 7 inserts its conflict-rename pass at that same landmark, and that pass can `return 4`. Leaving the block where Phase 6 puts it would once again place a database mutation before a `return 4` in `HandleConnectionAsync`. **Phase 7 relocates this block below its rename pass**; Phase 6 establishes only the below-the-delete-guards half of the ordering and must not be read as establishing the full "no DB mutation precedes any `return 4`" invariant on its own.

*Removed — nothing later may call these:* `SyncEngine.ComputePlan(FileManifest, FileManifest, bool)`, `ComputePlan(…, bool, SyncState?, bool)`, `ComputePlan(…, bool, SyncDatabase?, bool)`, `ConflictResolver.ResolveDeleteConflict(bool, FileEntry, DateTime)`.

**Open seam this phase deliberately does not close.** CONTRACT correction 1 requires the caller to drain `planResult.Resurrections` and `planResult.Conflicts` into `LogResurrection` / `LogConflict` *after the transfer phase succeeds*. That insertion point sits below the transfer loops at `SyncClient.cs:472-474`, anchored at the top of the `// 11. Exchange SyncComplete` landmark and inserted above it. **Phase 7 owns both drains.** The resurrection drain (`planResult.Resurrections` → `_db.LogResurrection`) lands in the **same edit block, at the same anchor**, as Phase 7's existing conflict drain (`planResult.Conflicts` → `_db.LogConflict`) — it is one insertion, not two, and no other phase may add a second one. This phase populates the lists and keeps them in scope; it writes neither drain. Without them `GetSessionResurrections` returns empty forever, so Phase 7 must not treat the resurrection half as optional.

**Decision — the legacy overloads are DELETED, not kept as shims.** They cannot be faithfully delegated. `SyncState` stores a *single* manifest and a *single* `LastSyncUtc`, and `FileState` from `GetAllTrackedFiles` stores a single size/mtime pair. Neither can distinguish "the client changed" from "the server changed" — that missing distinction *is* the bug. A shim would have to fabricate `ClientSize == ServerSize`, which returns `Skip` for the both-changed case and silently loses an edit. The `bool bidirectional` parameter is independently unsalvageable: `false` now means Push *or* Pull, and those tables are mirror images.

**Known-inert for one phase, stated so it is not read as an oversight:**
1. `ConflictKeepBoth` is emitted by the planner here but not executed. `SyncClient` selects transfers by explicit action allow-lists (`SyncClient.cs:261`, `:328`, `:360`, `:407`; `SyncServer.cs:182`, `:242`, `:308`, `:358`), so a raw `ConflictKeepBoth` entry is a no-op — neither side transfers anything for that path and both copies survive untouched under their own name. The rename executor lands in Phase 7.
2. `PlanResult.Resurrections` is populated here but nothing writes a `'resurrected'` row until **Phase 7** lands the drain described above. `GetSessionResurrections` returns empty for the whole of Phase 6.

---

### Task 6.1: The new `ComputePlan`, its call site, and the removal of `ResolveDeleteConflict`

These land as one step. Deleting the legacy overloads breaks `SyncClient.cs:151-152` and `SyncEngineTests.cs`; the test project has `<ProjectReference Include="..\..\src\RemoteFileSync\RemoteFileSync.csproj" />` (`tests/RemoteFileSync.Tests/RemoteFileSync.Tests.csproj:22`), so nothing in the solution can even build until the production call site is updated in the same edit. Splitting them across tasks would place an unreachable PASS gate between them.

- [ ] **Step 1: Write the failing tests**

Full replacement for `tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs` (replaces lines 1-406 in their entirety; the per-test disposition of the old file is Task 6.4):

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

    private static PlanResult Plan(
        FileManifest client,
        FileManifest server,
        SyncMode mode,
        IReadOnlyDictionary<string, AncestorRow>? ancestor,
        bool deleteEnabled = true,
        bool mirrorDeletes = false,
        ClockSkew? skew = null) =>
        SyncEngine.ComputePlan(client, server, mode, ancestor, deleteEnabled, mirrorDeletes,
                               skew ?? ClockSkew.None);

    private static Dictionary<string, SyncActionType> Actions(PlanResult result) =>
        result.Entries.ToDictionary(p => p.RelativePath, p => p.Action,
                                    StringComparer.OrdinalIgnoreCase);

    // ── TwoWay, row present and Status == "exists" ────────────────────────────

    [Fact]
    public void TwoWay_UnchangedBothSides_Skip()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.Skip, result.Entries[0].Action);
    }

    [Fact]
    public void TwoWay_ClientChangedOnly_SendToServer()
    {
        var client = MakeManifest(new FileEntry("f.txt", 150, T2));
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[0].Action);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void TwoWay_ServerChangedOnly_SendToClient()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = MakeManifest(new FileEntry("f.txt", 150, T2));
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToClient, result.Entries[0].Action);
    }

    [Fact]
    public void TwoWay_BothChanged_ConflictKeepBothAndRecordsBothSides()
    {
        // Both sides edited since the ancestor. Neither edit may be silently discarded, so the
        // plan keeps both and the executor renames the loser. The conflict must also reach
        // PlanResult with each side's real size/mtime, because that is the only place the
        // review report can learn what the two copies were.
        var client = MakeManifest(new FileEntry("f.txt", 150, T2));
        var server = MakeManifest(new FileEntry("f.txt", 220, T2.AddMinutes(5)));
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));

        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.ConflictKeepBoth, result.Entries[0].Action);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("f.txt", conflict.Path);
        Assert.Equal(150, conflict.ClientSize);
        Assert.Equal(T2.Ticks, conflict.ClientMtimeTicks);
        Assert.Equal(220, conflict.ServerSize);
        Assert.Equal(T2.AddMinutes(5).Ticks, conflict.ServerMtimeTicks);
    }

    [Fact]
    public void TwoWay_ClientAbsent_ServerUnchanged_DeleteOnServer()
    {
        // The client deleted it and nobody touched the server copy, so the deletion is the only
        // edit in play and it propagates.
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.DeleteOnServer, result.Entries[0].Action);
        Assert.Equal("f.txt", result.Entries[0].RelativePath);
        Assert.Empty(result.Resurrections);
    }

    [Fact]
    public void TwoWay_ClientAbsent_ServerChanged_SendToClientAndRecordsResurrection()
    {
        // A delete on one side loses to a real edit on the other: losing the edit is
        // unrecoverable, an unwanted resurrection costs one more delete. The kept copy is
        // recorded so the review report can tell the user why the file came back.
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("f.txt", 220, T2));
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));

        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToClient, result.Entries[0].Action);

        var res = Assert.Single(result.Resurrections);
        Assert.Equal("f.txt", res.Path);
        Assert.False(res.KeptClientCopy);
        Assert.Equal(220, res.KeptSize);
        Assert.Equal(T2.Ticks, res.KeptMtimeTicks);
    }

    [Fact]
    public void TwoWay_ServerAbsent_ClientUnchanged_DeleteOnClient()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = new FileManifest();
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.DeleteOnClient, result.Entries[0].Action);
        Assert.Empty(result.Resurrections);
    }

    [Fact]
    public void TwoWay_ServerAbsent_ClientChanged_SendToServerAndRecordsResurrection()
    {
        var client = MakeManifest(new FileEntry("f.txt", 220, T2));
        var server = new FileManifest();
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));

        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[0].Action);

        var res = Assert.Single(result.Resurrections);
        Assert.Equal("f.txt", res.Path);
        Assert.True(res.KeptClientCopy);
        Assert.Equal(220, res.KeptSize);
        Assert.Equal(T2.Ticks, res.KeptMtimeTicks);
    }

    [Fact]
    public void TwoWay_AbsentBothSides_NoPlanEntry()
    {
        // Both sides already removed it. There is nothing to transfer and nothing to delete;
        // the caller tombstones the row. ComputePlan stays free of database writes.
        var result = Plan(new FileManifest(), new FileManifest(), SyncMode.TwoWay,
                          Ancestor(Row("f.txt", 100, T1)));
        Assert.Empty(result.Entries);
        Assert.Empty(result.Resurrections);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void TwoWay_SizeChangedMtimeIdentical_CountsAsChanged()
    {
        // An in-place rewrite keeps the mtime inside tolerance. Comparing mtimes alone returns
        // Skip here and the larger client copy is never pushed.
        var client = MakeManifest(new FileEntry("f.txt", 250, T1));
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[0].Action);
    }

    [Fact]
    public void TwoWay_DeleteDisabled_ReCopiesInsteadOfDeleting()
    {
        // Without --delete the deletion must not propagate, but dropping the path from the plan
        // would leave the two sides permanently divergent with no record of why.
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)),
                          deleteEnabled: false);
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToClient, result.Entries[0].Action);
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
        var result = Plan(client, server, SyncMode.TwoWay, ancestor: null, deleteEnabled: true);
        var actions = Actions(result);
        Assert.Equal(SyncActionType.SendToServer, actions["c-only.txt"]);
        Assert.Equal(SyncActionType.SendToClient, actions["s-only.txt"]);
        Assert.DoesNotContain(result.Entries, p => p.Action == SyncActionType.DeleteOnServer);
        Assert.DoesNotContain(result.Entries, p => p.Action == SyncActionType.DeleteOnClient);
    }

    [Fact]
    public void NoAncestor_BothPresent_NewestWins()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T2));
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var result = Plan(client, server, SyncMode.TwoWay, ancestor: null);
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[0].Action);
    }

    [Fact]
    public void NoAncestor_SameMtime_LargerWins()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = MakeManifest(new FileEntry("f.txt", 200, T1));
        var result = Plan(client, server, SyncMode.TwoWay, ancestor: null);
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToClient, result.Entries[0].Action);
    }

    [Fact]
    public void TombstonedRow_TreatedAsNoAncestor_NeverDeletes()
    {
        // A "deleted" row is settled history. Reading it as an ancestor would turn a file the
        // user deliberately re-created into an immediate re-deletion.
        var client = MakeManifest(new FileEntry("f.txt", 100, T2));
        var server = new FileManifest();
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Tombstoned("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[0].Action);
    }

    [Fact]
    public void BothEmpty_EmptyPlan()
    {
        var result = Plan(new FileManifest(), new FileManifest(), SyncMode.TwoWay, ancestor: null);
        Assert.Empty(result.Entries);
    }

    // ── Clock skew ───────────────────────────────────────────────────────────

    [Fact]
    public void ClockSkew_ServerOneHourFast_DoesNotWin()
    {
        // The server's clock is +1h. Its file is byte-identical and was written at the same real
        // instant, but its raw mtime is an hour "newer", so it wins newest-wins forever and every
        // run pulls the same bytes down again. The first half of this test proves the second half
        // bites: with ClockSkew.None the engine really does return SendToClient.
        var client = MakeManifest(new FileEntry("f.txt", 100, T2));
        var server = MakeManifest(new FileEntry("f.txt", 100, T2.AddHours(1)));

        var withoutSkew = Plan(client, server, SyncMode.TwoWay, ancestor: null,
                               skew: ClockSkew.None);
        Assert.Equal(SyncActionType.SendToClient, withoutSkew.Entries[0].Action);

        var withSkew = Plan(client, server, SyncMode.TwoWay, ancestor: null,
                            skew: new ClockSkew(TimeSpan.FromHours(1)));
        Assert.Single(withSkew.Entries);
        Assert.Equal(SyncActionType.Skip, withSkew.Entries[0].Action);
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

        var result = Plan(client, server, SyncMode.Push, ancestor);
        var actions = Actions(result);

        Assert.Equal(SyncActionType.Skip, actions["keep.txt"]);
        Assert.Equal(SyncActionType.SendToServer, actions["push-me.txt"]);
        Assert.Equal(SyncActionType.DeleteOnServer, actions["gone.txt"]);
        // No row proves the client ever had it, so it is left alone rather than wiped.
        Assert.Equal(SyncActionType.Skip, actions["server-extra.txt"]);

        Assert.DoesNotContain(result.Entries, p => p.Action == SyncActionType.SendToClient);
        Assert.DoesNotContain(result.Entries, p => p.Action == SyncActionType.DeleteOnClient);
    }

    [Fact]
    public void Push_UnknownDeletion_ServerEditedSinceAncestor_Skip()
    {
        // The client copy is gone and a row proves the client once had it — but the server copy
        // has been edited since that agreement. Deleting it destroys the only surviving version
        // of that edit, and Push has no resurrection path to bring it back. CONTRACT.md's Push
        // table requires the row to say the client had it AND that it is unchanged; checking
        // only Status == "exists" is what this test catches.
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("f.txt", 220, T2));
        var result = Plan(client, server, SyncMode.Push, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.Skip, result.Entries[0].Action);
    }

    [Fact]
    public void Push_UnknownDeletion_ServerEditedButMirror_DeleteOnServer()
    {
        // --mirror is the explicit "make the server match, whatever it is holding" opt-in, so it
        // overrides the unchanged check as well as the row check.
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("f.txt", 220, T2));
        var result = Plan(client, server, SyncMode.Push, Ancestor(Row("f.txt", 100, T1)),
                          mirrorDeletes: true);
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.DeleteOnServer, result.Entries[0].Action);
    }

    [Fact]
    public void Push_ServerChangedUnderneath_StillSendToServer()
    {
        // Push means the server does not get a vote, even when its copy is the newer one.
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = MakeManifest(new FileEntry("f.txt", 220, T2));
        var result = Plan(client, server, SyncMode.Push, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[0].Action);
    }

    [Fact]
    public void Push_ServerLostFile_RePushed()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = new FileManifest();
        var result = Plan(client, server, SyncMode.Push, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[0].Action);
    }

    [Fact]
    public void Push_UnknownServerFile_WithMirror_DeleteOnServer()
    {
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("stray.txt", 100, T1));
        var result = Plan(client, server, SyncMode.Push, ancestor: null, mirrorDeletes: true);
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.DeleteOnServer, result.Entries[0].Action);
    }

    [Fact]
    public void Push_DeleteDisabled_KeepsServerFile()
    {
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var result = Plan(client, server, SyncMode.Push, Ancestor(Row("f.txt", 100, T1)),
                          deleteEnabled: false);
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.Skip, result.Entries[0].Action);
    }

    // ── Pull (server authoritative) — the exact mirror of Push ────────────────

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

        var result = Plan(client, server, SyncMode.Pull, ancestor);
        var actions = Actions(result);

        Assert.Equal(SyncActionType.Skip, actions["keep.txt"]);
        Assert.Equal(SyncActionType.SendToClient, actions["pull-me.txt"]);
        Assert.Equal(SyncActionType.DeleteOnClient, actions["gone.txt"]);
        Assert.Equal(SyncActionType.Skip, actions["client-extra.txt"]);

        Assert.DoesNotContain(result.Entries, p => p.Action == SyncActionType.SendToServer);
        Assert.DoesNotContain(result.Entries, p => p.Action == SyncActionType.DeleteOnServer);
    }

    [Fact]
    public void Pull_UnknownDeletion_ClientEditedSinceAncestor_Skip()
    {
        // Mirror of Push_UnknownDeletion_ServerEditedSinceAncestor_Skip. This is the branch that
        // destroys the user's own local files, so it gets its own test rather than relying on
        // symmetry with the Push case.
        var client = MakeManifest(new FileEntry("f.txt", 220, T2));
        var server = new FileManifest();
        var result = Plan(client, server, SyncMode.Pull, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.Skip, result.Entries[0].Action);
    }

    [Fact]
    public void Pull_UnknownDeletion_ClientUnchanged_DeleteOnClient()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = new FileManifest();
        var result = Plan(client, server, SyncMode.Pull, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.DeleteOnClient, result.Entries[0].Action);
    }

    [Fact]
    public void Pull_ClientChangedUnderneath_StillSendToClient()
    {
        var client = MakeManifest(new FileEntry("f.txt", 220, T2));
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var result = Plan(client, server, SyncMode.Pull, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToClient, result.Entries[0].Action);
    }

    [Fact]
    public void Pull_UnknownClientFile_WithoutMirror_Skip()
    {
        var client = MakeManifest(new FileEntry("stray.txt", 100, T1));
        var server = new FileManifest();
        var result = Plan(client, server, SyncMode.Pull, ancestor: null, mirrorDeletes: false);
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.Skip, result.Entries[0].Action);
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

Now trim `tests/RemoteFileSync.Tests/Sync/ConflictResolverTests.cs`. Delete lines 9-11 — exact current text:

```csharp
    private static readonly DateTime LastSync = new(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BeforeSync = new(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AfterSync = new(2026, 3, 27, 8, 0, 0, DateTimeKind.Utc);
```

(replaced by nothing — `BaseTime` at line 8 stays). Then delete lines 69-133, exact current text from the blank line before `DeletedOnClient_UntouchedOnServer_ReturnsDeleteOnServer` through the closing brace of `DeleteConflict_TimestampJustBeyondTolerance_TreatedAsModified`:

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

(replaced by nothing; the file then ends after `Tolerance_JustOver2Seconds_NotSkipped` and the class-closing brace.)

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncEngineTests"`

Expected: FAIL, at compile time, in the **test** project:
- `CS1503: Argument 3: cannot convert from 'RemoteFileSync.Models.SyncMode' to 'bool'` at the `Plan` helper's call to `SyncEngine.ComputePlan`, because only the legacy overloads exist.
- `CS0029: Cannot implicitly convert type 'System.Collections.Generic.List<RemoteFileSync.Models.SyncPlanEntry>' to 'RemoteFileSync.Sync.PlanResult'` at the same site.

`AncestorRow`, `PlanResult`, `ClockSkew` and `SyncActionType.ConflictKeepBoth` all resolve — Phases 1 and 2 landed them.

- [ ] **Step 3: Implement**

**Edit 3a — `src/RemoteFileSync/Sync/SyncEngine.cs`.** Replace lines 1-205 in full. The text being replaced begins:

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

…and ends at line 205, the closing brace of the `SyncDatabase` overload:

```csharp
        return plan;
    }
```

The replacement (`BuildMergedManifest` at what is currently line 207 onward is left in place and edited separately in Task 6.2):

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
    /// ancestor answers the question outright.
    /// This method is pure. It never opens the database: the resurrection and conflict rows it
    /// discovers are returned on <see cref="PlanResult"/> so the caller can write them only after
    /// the transfer phase has actually succeeded, leaving an aborted run with no recorded state.
    /// </summary>
    public static PlanResult ComputePlan(
        FileManifest clientManifest,
        FileManifest serverManifest,
        SyncMode mode,
        IReadOnlyDictionary<string, AncestorRow>? ancestor,
        bool deleteEnabled,
        bool mirrorDeletes,
        ClockSkew skew)
    {
        var result = new PlanResult();

        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in clientManifest.AllPaths) allPaths.Add(path);
        foreach (var path in serverManifest.AllPaths) allPaths.Add(path);

        // A path present in neither manifest is invisible unless the ancestor names it, and that
        // absence is the only evidence a deletion ever happened. Tombstoned rows are deliberately
        // left out: they are settled history, and re-adding them would replan the same deletion
        // on every run forever.
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
                SyncMode.TwoWay => row is not null && row.Status == "exists"
                    ? PlanTwoWayWithAncestor(path, client, server, row, deleteEnabled, result)
                    : PlanNoAncestor(client, server, skew),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown sync mode."),
            };

            if (action.HasValue) result.Entries.Add(new SyncPlanEntry(action.Value, path));
        }

        return result;
    }

    /// <summary>
    /// The only path where we KNOW what happened: the row records what each side looked like when
    /// they last agreed, so "changed" is a recorded fact rather than a timestamp guess. No
    /// newest-wins comparison may appear in this method — guessing from timestamps is the bug
    /// being fixed. Clock skew is irrelevant here: the current server mtime and the stored server
    /// mtime both come from the server's clock, so any constant offset cancels.
    /// </summary>
    private static SyncActionType? PlanTwoWayWithAncestor(
        string path, FileEntry? client, FileEntry? server, AncestorRow row,
        bool deleteEnabled, PlanResult result)
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

            result.Conflicts.Add(new ConflictInfo(path,
                client.FileSize, client.LastModifiedUtc.Ticks,
                server.FileSize, server.LastModifiedUtc.Ticks));
            return SyncActionType.ConflictKeepBoth;
        }

        if (client != null && server == null)
        {
            // A delete loses to an edit: losing the edit is unrecoverable, an unwanted
            // resurrection costs one more delete. Record it so the review report can explain
            // why a file the user deleted on the server came back.
            if (clientChanged)
            {
                result.Resurrections.Add(new ResurrectionInfo(
                    path, KeptClientCopy: true, client.FileSize, client.LastModifiedUtc.Ticks));
                return SyncActionType.SendToServer;
            }

            // Without --delete the deletion does not propagate. Re-push rather than emit nothing,
            // which would leave the two sides divergent with no record of why.
            return deleteEnabled ? SyncActionType.DeleteOnClient : SyncActionType.SendToServer;
        }

        if (client == null && server != null)
        {
            if (serverChanged)
            {
                result.Resurrections.Add(new ResurrectionInfo(
                    path, KeptClientCopy: false, server.FileSize, server.LastModifiedUtc.Ticks));
                return SyncActionType.SendToClient;
            }

            return deleteEnabled ? SyncActionType.DeleteOnServer : SyncActionType.SendToClient;
        }

        // Gone from both sides. Nothing to transfer and nothing to delete; the caller tombstones
        // the row. ComputePlan has no sessionId in scope and must not write to the database.
        return null;
    }

    /// <summary>
    /// No usable row: we cannot tell an edit from a deletion, so this path is strictly additive
    /// and must never return DeleteOnServer or DeleteOnClient. Only TwoWay reaches it — Push and
    /// Pull have their own tables and handle the missing-row case themselves.
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
            // clock offset alone does not re-upload every file on every run.
            return SameContent(client, server, skew)
                ? SyncActionType.Skip
                : SyncActionType.SendToServer;
        }

        if (client == null && server != null)
        {
            if (!deleteEnabled) return SyncActionType.Skip;

            // Two independent conditions, both required.
            // Status == "exists" proves the client once held this path: without a row, an absent
            // client file is indistinguishable from one the client never had, and deleting would
            // wipe the server on the first run against a repointed or unrelated folder.
            // Unchanged proves nobody edited the server copy since that agreement: deleting an
            // edited copy destroys the only surviving version of that edit, and unlike TwoWay,
            // Push has no resurrection branch to bring it back.
            // --mirror is the explicit opt-in to skipping both checks.
            bool clientHadItAndPeerUntouched =
                row is not null
                && row.Status == "exists"
                && ChangeDetector.Unchanged(server, row.ServerSize, row.ServerMtimeTicks);

            return (clientHadItAndPeerUntouched || mirrorDeletes)
                ? SyncActionType.DeleteOnServer
                : SyncActionType.Skip;
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

            // Mirror of the Push gate, and the more dangerous of the two: this branch deletes
            // files out of the user's own local folder, so a row alone is not enough — the local
            // copy must also be unchanged since the last agreement.
            bool serverHadItAndPeerUntouched =
                row is not null
                && row.Status == "exists"
                && ChangeDetector.Unchanged(client, row.ClientSize, row.ClientMtimeTicks);

            return (serverHadItAndPeerUntouched || mirrorDeletes)
                ? SyncActionType.DeleteOnClient
                : SyncActionType.Skip;
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

**Edit 3b — `src/RemoteFileSync/Sync/ConflictResolver.cs:9`.** Add the doc comment that pins `Resolve` to the no-ancestor path. Exact current text:

```csharp
    public static SyncActionType Resolve(FileEntry clientEntry, FileEntry serverEntry)
```

Replacement:

```csharp
    /// <summary>
    /// Newest wins, ties broken by size. Only valid on the no-ancestor path: with no record of
    /// what the two sides last agreed on, the timestamp is the only signal available. Callers
    /// must normalise the server entry for clock skew before calling.
    /// </summary>
    public static SyncActionType Resolve(FileEntry clientEntry, FileEntry serverEntry)
```

**Edit 3c — `src/RemoteFileSync/Sync/ConflictResolver.cs:26-39`.** Delete `ResolveDeleteConflict`. It decided whether a surviving file had been edited by comparing its mtime against the session-wide `LastSynced`, so any file whose stamp merely *looked* older than the last sync was deleted on the peer. `SyncEngine` now answers that question from the per-side `AncestorRow`, so the method has no remaining caller and no defensible use. Exact current text:

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
    // comparing its mtime against the session-wide LastSynced, which deleted any file whose stamp
    // merely looked older. SyncEngine now answers that from the per-side AncestorRow.
```

After edits 3b and 3c the complete file reads:

```csharp
using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

public static class ConflictResolver
{
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Newest wins, ties broken by size. Only valid on the no-ancestor path: with no record of
    /// what the two sides last agreed on, the timestamp is the only signal available. Callers
    /// must normalise the server entry for clock skew before calling.
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
    // comparing its mtime against the session-wide LastSynced, which deleted any file whose stamp
    // merely looked older. SyncEngine now answers that from the per-side AncestorRow.
}
```

**Edit 3d — `src/RemoteFileSync/Network/SyncClient.cs`, the `ComputePlan` call site.** This region is currently at lines 149-152; Phase 3's handshake rewrite of `SyncClient.cs:89-113` shifts it, so anchor on the text. Neither Phase 3 nor Phase 5 alters these four lines, so this is byte-identical to `main`. Exact current text:

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
        // engine refuses to emit any deletion on that path.
        // `skew` is the measured client-vs-server clock offset from the v3 handshake above.
        // Passing ClockSkew.None here would leave a peer with a fast clock winning every
        // newest-wins comparison forever, re-transferring the same bytes on every run.
        IReadOnlyDictionary<string, AncestorRow>? ancestor = _db?.LoadAll();
        var planResult = SyncEngine.ComputePlan(
            clientManifest, serverManifest, _options.Mode, ancestor,
            _options.DeleteEnabled, _options.MirrorDeletes, skew);
        var syncPlan = planResult.Entries;
```

`skew` is the local Phase 3 leaves behind — **read, never redeclared**. `syncPlan` stays a `List<SyncPlanEntry>`, so the existing reassignment at the local-filter block (`syncPlan = syncPlan.Where(...).ToList()`) still compiles. `previousState` remains live: it is read at `SyncClient.cs:129-132` and again at `:240` inside the delete-percentage guard, so no unused-local warning results.

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncEngineTests|FullyQualifiedName~ConflictResolverTests"`

Expected: PASS. Specifically green: every `TwoWay_*`, `NoAncestor_*`, `Push_*` and `Pull_*` method listed above, `TombstonedRow_TreatedAsNoAncestor_NeverDeletes`, `ClockSkew_ServerOneHourFast_DoesNotWin`, `BothEmpty_EmptyPlan`, and the seven surviving `ConflictResolverTests` methods (`SameTimestampAndSize_ReturnsSkip`, `TimestampWithin2Seconds_SameSize_ReturnsSkip`, `ClientNewer_ReturnsSendToServer`, `ServerNewer_ReturnsSendToClient`, `SameTimestamp_LargerClient_ReturnsSendToServer`, `SameTimestamp_LargerServer_ReturnsSendToClient`, `Tolerance_JustOver2Seconds_NotSkipped`).

`BuildMergedManifest_ConflictKeepBoth_KeepsClientEntry` is still **red** at this point — Task 6.2 turns it green.

---

### Task 6.2: `BuildMergedManifest` handles `ConflictKeepBoth`

- [ ] **Step 1: The failing test already exists**

`BuildMergedManifest_ConflictKeepBoth_KeepsClientEntry` was written in Task 6.1 Step 1.

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~BuildMergedManifest_ConflictKeepBoth_KeepsClientEntry"`

Expected: FAIL — `Assert.Equal() Failure: Values differ. Expected: 150, Actual: (null reference)`, thrown by the `!` on `merged.Get("f.txt")!`. `ConflictKeepBoth` matches no `case` in the switch, so nothing is added to the merged manifest and the path silently disappears from tracking.

- [ ] **Step 3: Implement**

`src/RemoteFileSync/Sync/SyncEngine.cs:223-231`. Exact current text (unchanged by Task 6.1, which stopped at line 205):

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
                    // Record the client copy under the original name so the path is not dropped
                    // from tracking entirely — an untracked path is planned from scratch next
                    // run, which is how a resolved conflict turns back into a conflict.
                    var conflictEntry = clientManifest.Get(entry.RelativePath);
                    if (conflictEntry != null) merged.Add(conflictEntry);
                    break;
                case SyncActionType.DeleteOnServer:
                case SyncActionType.DeleteOnClient:
                    break;
            }
```

- [ ] **Step 4: Run it and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncEngineTests"`

Expected: PASS, including `BuildMergedManifest_ConflictKeepBoth_KeepsClientEntry`.

---

### Task 6.3: Correct and relocate the ancestor-write block (data-loss fix)

Two independent defects in `SyncClient.cs:185-207`, fixed together because the corrected block is also the block being moved.

**Defect 1 — the one-sided ancestor row.** The current code at `:191-194` reads `clientManifest.Get(p) ?? serverManifest.Get(p)` and writes that single entry's size and mtime into a row that asserts *both* sides held those bytes. In Push, a server-only file is planned `Skip`, the fallback takes the **server's** entry, and the row then claims the client had it — so run 2 emits `DeleteOnServer` for a file the client never had. In Pull the mirror is worse: a client-only file is planned `Skip`, the fallback takes the **client's** entry and stamps it into the server columns, and run 2 emits `DeleteOnClient`, destroying the user's own local-only files.

**Defect 2 — writes above the guards.** The block sits at `:185-207`, above the `try` at `:216` and above both delete guards. An exit-4 abort or a crash therefore leaves the fabricated rows committed, and the next run — planning fewer deletes and so slipping under the threshold — executes against them.

- [ ] **Step 1: Write the failing test**

New file `tests/RemoteFileSync.Tests/Network/AncestorRowWriteTests.cs`. Push mode only, so it does not depend on Phase 8's mode dispatch. It follows the repo's integration conventions (temp dir per fixture, `Once = true` on the server, `SqliteConnection.ClearAllPools()` in `Dispose`).

```csharp
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.Network;

public class AncestorRowWriteTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _serverDir;
    private readonly string _clientDir;
    private readonly string _dbDir;

    public AncestorRowWriteTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"rfs_ancestorwrite_{Guid.NewGuid()}");
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

    private static void CreateFileWithTimestamp(string baseDir, string relativePath,
                                                string content, DateTime utcTimestamp)
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

    private async Task<(int clientResult, int serverResult)> RunPushAsync(int port, SyncDatabase db)
    {
        var serverOpts = new SyncOptions
        {
            IsServer = true, Once = true, Port = port, Folder = _serverDir, DeleteEnabled = true,
        };
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir,
            Mode = SyncMode.Push, DeleteEnabled = true,
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
    public async Task Push_SkippedServerOnlyFile_WritesNoAncestorRow()
    {
        // A server-only file is planned Skip in Push (no row proves the client ever had it).
        // Recording an ancestor row for it asserts "both sides agreed on these bytes", which is
        // a state that never existed — and the Push table reads that row on the next run as
        // "the client had it", which is licence to delete.
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_serverDir, "server-only.txt", "only on the server", ts);

        var dbPath = Path.Combine(_dbDir, "sync.db");
        int port = GetFreePort();

        using (var db = new SyncDatabase(dbPath))
        {
            var (clientResult, serverResult) = await RunPushAsync(port, db);
            Assert.Equal(0, clientResult);
            Assert.Equal(0, serverResult);
        }

        using (var db = new SyncDatabase(dbPath))
        {
            Assert.Null(db.GetRow("server-only.txt"));
        }
    }

    [Fact]
    public async Task Push_SecondRun_DoesNotDeleteFileTheClientNeverHad()
    {
        // The end-to-end consequence of the fabricated row: run 1 invents the ancestor, run 2
        // reads it back as consensus and deletes the user's server-only file. Only one deletion
        // would be planned, which is below MinTrackedFilesForDeleteGuard, so neither the client
        // nor the server blast-radius guard intervenes — nothing stops it but this fix.
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_serverDir, "server-only.txt", "only on the server", ts);

        var dbPath = Path.Combine(_dbDir, "sync2.db");

        using (var db = new SyncDatabase(dbPath))
        {
            await RunPushAsync(GetFreePort(), db);
        }

        using (var db = new SyncDatabase(dbPath))
        {
            await RunPushAsync(GetFreePort(), db);
        }

        Assert.True(File.Exists(Path.Combine(_serverDir, "server-only.txt")));
    }
}
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~AncestorRowWriteTests"`

Expected: FAIL, both methods, for the reason under test:
- `Push_SkippedServerOnlyFile_WritesNoAncestorRow` — `Assert.Null() Failure: Value is not null`. The `?? serverManifest.Get(...)` fallback wrote a row from the server's entry.
- `Push_SecondRun_DoesNotDeleteFileTheClientNeverHad` — `Assert.True() Failure`. Run 2 read `Status == "exists"` from that fabricated row and deleted the file.

- [ ] **Step 3: Implement**

**Edit 3a — delete the block from its current position.** `src/RemoteFileSync/Network/SyncClient.cs:185-207` plus the blank line at `:208`. Exact current text (Phases 1-5 leave it untouched; Phase 5's `archive` local on the following line is not part of the match):

```csharp
        if (_db != null)
        {
            foreach (var skip in syncPlan.Where(p => p.Action == SyncActionType.Skip))
            {
                // Use client manifest entry (or server as fallback) to record in files table so
                // deletion can be detected on the next run.
                var entry = clientManifest.Get(skip.RelativePath)
                         ?? serverManifest.Get(skip.RelativePath);
                if (entry != null)
                    _db.MarkSynced(skip.RelativePath, entry.FileSize, entry.LastModifiedUtc, sessionId, "skipped");
                else
                    _db.MarkSkipped(skip.RelativePath, sessionId);
            }

            // Retire tracked rows for files absent on both sides. Left as 'exists', a later
            // restore on one side is resolved as a deletion on the other.
            foreach (var fs in _db.GetAllTrackedFiles())
            {
                if (fs.Status != "exists") continue;
                if (clientManifest.Contains(fs.Path) || serverManifest.Contains(fs.Path)) continue;
                _db.MarkDeleted(fs.Path, sessionId, "absent on both sides; retiring tracked row");
            }
        }

```

Replaced by nothing. The next statement, Phase 5's `var archive = new ArchiveManager(...);`, moves up to become the first line after the `_progress.WritePlan(...)` / `WriteMessageAsync(... SyncPlan ...)` pair.

**Edit 3b — reinsert the corrected block below the delete guards.** Anchor on the two closing braces of the guard block and the transfer-loop comment (`SyncClient.cs:256-259`). Exact current text:

```csharp
            }
        }

        // 7. Send files to server (SendToServer + ClientOnly)
```

Replacement:

```csharp
            }
        }

        // Moved below both delete guards. This block used to run above them, so an exit-4 abort
        // still committed its rows; the next run then planned fewer deletions, slipped under the
        // same threshold, and executed them against state that was never confirmed by a completed
        // sync. This is not the final position: Phase 7 adds a conflict-rename pass at the
        // '// 7. Send files to server' landmark that can also return 4, and Phase 7 moves this
        // block below that pass. Until then, only the delete guards are known to precede it.
        if (_db != null && ancestor != null)
        {
            // Paths the local filters excluded were already dropped from syncPlan above. Drop
            // them from the side channels too: an excluded path must stay invisible, and
            // reporting a resurrection for one names a file the user took out of scope.
            planResult.Resurrections.RemoveAll(r => !scanner.IsIncluded(r.Path));
            planResult.Conflicts.RemoveAll(c => !scanner.IsIncluded(c.Path));

            foreach (var skip in syncPlan.Where(p => p.Action == SyncActionType.Skip))
            {
                var skippedOnClient = clientManifest.Get(skip.RelativePath);
                var skippedOnServer = serverManifest.Get(skip.RelativePath);

                if (skippedOnClient != null && skippedOnServer != null)
                {
                    // An ancestor row asserts "both sides held this file and agreed", so it may
                    // only be written when both sides actually have it, each column carrying its
                    // own side's size and mtime. The old code fell back to
                    // `client ?? server` and stamped one side's values into both columns, which
                    // manufactured a peer state that never existed: in Push a server-only file
                    // was recorded as "the client had it too" and run 2 deleted it, and in Pull
                    // the mirror deleted the user's own local-only files.
                    _db.UpsertSynced(skip.RelativePath,
                        skippedOnClient.FileSize, skippedOnClient.LastModifiedUtc.Ticks,
                        skippedOnServer.FileSize, skippedOnServer.LastModifiedUtc.Ticks,
                        sessionId, "skipped");
                }
                else
                {
                    // One-sided skip: Push leaving a server-only file alone, or Pull leaving a
                    // client-only file alone. Record that we saw and skipped it, without
                    // claiming the peer ever had it.
                    _db.MarkSkipped(skip.RelativePath, sessionId);
                }
            }

            // Retire rows for files now absent on both sides. Left as 'exists', a later restore
            // on one side is resolved as a deletion on the other. The snapshot loaded before
            // planning is the right input here: it is precisely the last state both sides agreed
            // on, and re-reading the table would also pick up the rows just written above.
            foreach (var row in ancestor.Values)
            {
                if (row.Status != "exists") continue;
                if (clientManifest.Contains(row.Path) || serverManifest.Contains(row.Path)) continue;
                _db.Tombstone(row.Path, sessionId, "absent on both sides; retiring tracked row");
            }
        }

        // 7. Send files to server (SendToServer + ClientOnly)
```

Scope check for the relocated block: `_db` is the field at `SyncClient.cs:21`; `sessionId` is declared at `:116`; `scanner` at `:136`; `clientManifest` at `:137`; `serverManifest` at `:145`; `ancestor`, `planResult` and `syncPlan` come from Task 6.1 Edit 3d. All precede the insertion point in the same method. The block now sits inside the `try` that begins at `:216`, whose `finally` only calls `CompleteSession` — no behavioural change from that.

`ancestor` is non-null exactly when `_db` is non-null (`_db?.LoadAll()`), so the `&& ancestor != null` conjunct is there for the compiler's null-flow analysis, not as a distinct runtime case.

**Handoff to Phase 7 — do not treat this position as final.** Phase 7 inserts its conflict-rename pass at the same `// 7. Send files to server` landmark, and that pass can `return 4`. Phase 7 therefore **relocates this whole `if (_db != null && ancestor != null)` block to sit below its rename pass**, restoring the invariant that no database mutation precedes any `return 4` in `HandleConnectionAsync`. Phase 6 clears the block past the delete guards and no further; the remaining move is Phase 7's edit, not a Phase 6 omission.

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~AncestorRowWriteTests"`

Expected: PASS — `Push_SkippedServerOnlyFile_WritesNoAncestorRow` and `Push_SecondRun_DoesNotDeleteFileTheClientNeverHad`.

Then the whole suite: `dotnet test -c Release`.

---

### Task 6.4: Disposition of every pre-existing test

No TDD steps — this is the audit trail. `tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs` is 406 lines and contains 26 `[Fact]` methods plus the `CreateTestDb` helper (27 members); all are accounted for by name and original line.

| # | Original test (line) | Disposition | New form / justification |
|---|---|---|---|
| 1 | `BothEmpty_EmptyPlan` (25) | UPDATED | Same name; now `Plan(..., SyncMode.TwoWay, ancestor: null)` against `result.Entries`. |
| 2 | `IdenticalFiles_AllSkipped` (32) | UPDATED, renamed | `TwoWay_UnchangedBothSides_Skip` with a matching ancestor row. The old form asserted `Assert.All` over a possibly-empty plan, which passes vacuously; the new form asserts `Single` first. |
| 3 | `ClientOnly_Unidirectional_ProducesClientOnlyAction` (41) | UPDATED, renamed | `Push_NeverEmitsClientSideActions` (`push-me.txt` leg). `ClientOnly` is no longer emitted; the new tables produce `SendToServer`. Executor impact of that collapse is analysed below the table. |
| 4 | `ServerOnly_Unidirectional_Ignored` (51) | DELETED | Subsumed by `Push_NeverEmitsClientSideActions`, which asserts the stronger invariant (no `SendToClient` **and** no `DeleteOnClient` anywhere in the plan) rather than "nothing but Skip". |
| 5 | `ServerOnly_Bidirectional_ProducesServerOnlyAction` (60) | UPDATED, renamed | `NoAncestor_AdditiveOnly_NeverEmitsDelete` (`s-only.txt` leg); `ServerOnly` → `SendToClient`. |
| 6 | `ClientNewer_SendToServer` (70) | UPDATED, renamed | `NoAncestor_BothPresent_NewestWins`. Semantics unchanged; only reachable on the no-ancestor path now. |
| 7 | `ServerNewer_SendToClient` (80) | UPDATED, renamed | `NoAncestor_SameMtime_LargerWins` covers the size tie-break; the mtime direction is covered by #6 and by `ConflictResolverTests.ServerNewer_ReturnsSendToClient`, which survives untouched. |
| 8 | `MixedScenario_CorrectPlan` (90) | UPDATED, renamed | `TwoWay_NewFileWithNoRow_TakesAdditivePath` plus `NoAncestor_AdditiveOnly_NeverEmitsDelete`. `ClientOnly`→`SendToServer`, `ServerOnly`→`SendToClient`. |
| 9 | `DeletedOnClient_UntouchedOnServer_ProducesDeleteOnServer` (109) | DELETED | Replaced by `TwoWay_ClientAbsent_ServerUnchanged_DeleteOnServer`. The original drove the deleted `SyncState` overload and asserted the `LastSynced` heuristic. |
| 10 | `DeletedOnClient_ModifiedOnServer_ProducesSendToClient` (122) | DELETED | Replaced by `TwoWay_ClientAbsent_ServerChanged_SendToClientAndRecordsResurrection`, which additionally pins the `ResurrectionInfo` payload. |
| 11 | `DeletedOnServer_UntouchedOnClient_ProducesDeleteOnClient` (134) | DELETED | Replaced by `TwoWay_ServerAbsent_ClientUnchanged_DeleteOnClient`. |
| 12 | `DeletedOnServer_ModifiedOnClient_ProducesSendToServer` (146) | DELETED | Replaced by `TwoWay_ServerAbsent_ClientChanged_SendToServerAndRecordsResurrection`. |
| 13 | `BothDeleted_NoAction` (158) | DELETED | Replaced by `TwoWay_AbsentBothSides_NoPlanEntry`. |
| 14 | `NoState_FullyAdditive` (169) | DELETED | Replaced by `NoAncestor_AdditiveOnly_NeverEmitsDelete`, which additionally asserts no `Delete*` action appears. |
| 15 | `UniDirectional_OnlyClientDeletionsPropagate` (179) | DELETED | Replaced by `Push_NeverEmitsClientSideActions` (`gone.txt` leg). |
| 16 | `NewFileNotInSnapshot_NormalCopyBehavior` (194) | UPDATED, renamed | `TwoWay_NewFileWithNoRow_TakesAdditivePath`; `brand-new.txt` has no row so it takes `PlanNoAncestor` → `SendToServer` (was `ClientOnly`). |
| 17 | `TimestampTolerance_WithinTwoSeconds_TreatedAsUntouched` (209) | DELETED | It tested mtime-vs-`LastSync` tolerance, precisely the heuristic being removed. Tolerance is now specified against the ancestor row by Phase 2's `ChangeDetectorTests`. |
| 18 | `UniDirectional_ServerDeletionsIgnored` (223) | DELETED | Subsumed by `Push_NeverEmitsClientSideActions` and `Push_ServerLostFile_RePushed`. Behaviour deliberately changes: the old test asserted an empty plan (file silently dropped); Push now re-pushes, which is what client-authoritative means. |
| 19 | `DeleteEnabled_False_IgnoresDeletions` (236) | UPDATED, renamed | `TwoWay_DeleteDisabled_ReCopiesInsteadOfDeleting`; expectation changes `ServerOnly` → `SendToClient` (same effect, canonical action). |
| 20 | `CreateTestDb` helper (249) | DELETED | The engine now takes a plain `IReadOnlyDictionary`, so the tests no longer need SQLite. This removes `using Microsoft.Data.Sqlite;` (line 1) and `using RemoteFileSync.State;` (line 3), and eliminates seven temp-directory fixtures. |
| 21 | `Db_DeletedFile_InDb_ProducesDeleteAction` (257) | DELETED | Replaced by `TwoWay_ClientAbsent_ServerUnchanged_DeleteOnServer`. |
| 22 | `Db_NewFile_NotInDb_ProducesCopyAction` (279) | DELETED | Replaced by `NoAncestor_AdditiveOnly_NeverEmitsDelete`. |
| 23 | `Db_PreviouslyDeleted_Reappeared_CopiesAgain` (298) | UPDATED, renamed | `TombstonedRow_TreatedAsNoAncestor_NeverDeletes` — same intent, expressed with a `Status == "deleted"` `AncestorRow` instead of `MarkDeleted`. |
| 24 | `Db_UniDirectional_ServerLostFile_RePushed` (320) | UPDATED, renamed | `Push_ServerLostFile_RePushed`; `ClientOnly` → `SendToServer`. |
| 25 | `Db_PerFileTimestamp_UsedForDeletion` (342) | DELETED | This test *codified* the bug: it asserted that a server mtime later than `LastSynced` means "modified". Its intent is preserved by `TwoWay_ClientAbsent_ServerChanged_SendToClientAndRecordsResurrection`, which compares against the recorded server size/mtime instead. It also depended on `DateTime.UtcNow.AddDays(1)`, making it wall-clock dependent. |
| 26 | `Db_DeleteEnabled_False_NormalBehavior` (367) | DELETED | Duplicate of #19 with a DB fixture; subsumed by `TwoWay_DeleteDisabled_ReCopiesInsteadOfDeleting`. |
| 27 | `Db_BothDeletedFromDb_NoAction` (388) | DELETED | Replaced by `TwoWay_AbsentBothSides_NoPlanEntry`. |

New tests with no predecessor: `TwoWay_BothChanged_ConflictKeepBothAndRecordsBothSides`, `TwoWay_SizeChangedMtimeIdentical_CountsAsChanged`, `ClockSkew_ServerOneHourFast_DoesNotWin`, `Push_UnknownDeletion_ServerEditedSinceAncestor_Skip`, `Push_UnknownDeletion_ServerEditedButMirror_DeleteOnServer`, `Push_ServerChangedUnderneath_StillSendToServer`, `Push_UnknownServerFile_WithMirror_DeleteOnServer`, `Push_DeleteDisabled_KeepsServerFile`, `Pull_NeverEmitsServerSideActions`, `Pull_UnknownDeletion_ClientEditedSinceAncestor_Skip`, `Pull_UnknownDeletion_ClientUnchanged_DeleteOnClient`, `Pull_ClientChangedUnderneath_StillSendToClient`, `Pull_UnknownClientFile_WithoutMirror_Skip`, `BuildMergedManifest_ConflictKeepBoth_KeepsClientEntry`.

**Executor impact of collapsing `ClientOnly` into `SendToServer`** (row #3). Three call sites treat the two actions differently and must be checked, not assumed:
- `SyncClient.cs:260-261` — the send filter is `SendToServer || ClientOnly`, so the collapse is a no-op there.
- `SyncServer.cs:193` — `onBeforeCommit: p => action.Action == SyncActionType.SendToServer && backup.BackupFile(p)`. A newly-created file now takes the `SendToServer` branch and so attempts a backup where it previously did not. The attempt is inert: the file does not exist on the destination, `BackupManager.Snapshot` returns `false` for an absent source (`src/RemoteFileSync/Backup/BackupManager.cs:29-33`), and `FileTransfer.cs:163` discards the result (`onBeforeCommit?.Invoke(relativePath);`). Phase 5 replaces this with `ArchiveManager`; the same absent-source reasoning applies to `Archive`, which returns `false` without writing anything.
- `SyncClient.cs:371` — the mirror for `SendToClient` / `ServerOnly`, inert for the same reason.

**`tests/RemoteFileSync.Tests/Sync/ConflictResolverTests.cs`** (14 tests):

| Original test (line) | Disposition |
|---|---|
| `SameTimestampAndSize_ReturnsSkip` (14) | SURVIVES byte-identical — `Resolve` is still the no-ancestor tie-breaker. |
| `TimestampWithin2Seconds_SameSize_ReturnsSkip` (22) | SURVIVES byte-identical. |
| `ClientNewer_ReturnsSendToServer` (30) | SURVIVES byte-identical. |
| `ServerNewer_ReturnsSendToClient` (38) | SURVIVES byte-identical. |
| `SameTimestamp_LargerClient_ReturnsSendToServer` (46) | SURVIVES byte-identical. |
| `SameTimestamp_LargerServer_ReturnsSendToClient` (54) | SURVIVES byte-identical. |
| `Tolerance_JustOver2Seconds_NotSkipped` (62) | SURVIVES byte-identical. |
| `DeletedOnClient_UntouchedOnServer_ReturnsDeleteOnServer` (70) | DELETED — tests the removed method. |
| `DeletedOnClient_ModifiedOnServer_ReturnsSendToClient` (79) | DELETED — tests the removed method. |
| `DeletedOnServer_UntouchedOnClient_ReturnsDeleteOnClient` (88) | DELETED — tests the removed method. |
| `DeletedOnServer_ModifiedOnClient_ReturnsSendToServer` (97) | DELETED — tests the removed method. |
| `DeleteConflict_TimestampWithinTolerance_TreatedAsUntouched` (106) | DELETED — replaced by Phase 2's `ChangeDetectorTests.MtimeDriftWithinTolerance_Unchanged`. |
| `DeleteConflict_TimestampExactlyAtTolerance_TreatedAsUntouched` (116) | DELETED — replaced by the same theory's boundary case. |
| `DeleteConflict_TimestampJustBeyondTolerance_TreatedAsModified` (126) | DELETED — replaced by `ChangeDetectorTests.MtimeDriftBeyondTolerance_ReportsChanged`. |

Fields `LastSync` (9), `BeforeSync` (10) and `AfterSync` (11) are deleted with their only consumers; `BaseTime` (8) stays.

**Integration tests — checked, not assumed.** `grep -rn "ComputePlan\|ResolveDeleteConflict" --include=*.cs src tests` matches only `src/RemoteFileSync/Sync/SyncEngine.cs`, `src/RemoteFileSync/Network/SyncClient.cs`, `tests/.../Sync/SyncEngineTests.cs` and `tests/.../Sync/ConflictResolverTests.cs`. **No file under `tests/RemoteFileSync.Tests/Integration/` calls either method**, so all four integration files *compile* unchanged. They do not all *pass* unchanged, and the difference matters:

`tests/RemoteFileSync.Tests/Integration/DeleteSyncTests.cs` constructs its client as `new SyncClient(clientOpts, clientLogger, stateManager)` at `:59` — the `db` parameter (declared `SyncClient.cs:26`) defaults to null. After this phase, `ancestor = _db?.LoadAll()` is therefore null for every test in that file and `previousState` is no longer consulted by the planner at all, so the whole suite takes the strictly additive path. Four assertions break, all for the same root cause:

| Test | Assertion | New behaviour |
|---|---|---|
| `DeleteSync_Case1_PropagatesDeletion` | `:107` `Assert.False(File.Exists(_serverDir/"to-delete.txt"))` and the backup assertion at `:109` | Null ancestor, client absent / server present → `SendToClient`. The file survives and is restored to the client. |
| `DeleteSync_BidiSymmetric` | `:157-158` | Both files are restored rather than deleted. |
| `DeleteSync_SecondRun_DetectsDeletions` | `:216` | Run 2 restores `will-delete.txt` to the client instead of deleting it on the server. |
| `DeleteSync_UniDirectional_ServerDeletionIgnored` | `:189` | Phase 1 maps `bidirectional: false` to `SyncMode.Push`; client present / server absent → `SendToServer`, so `file.txt` **is** re-pushed. This is the integration twin of unit test #18 above and breaks for exactly the same reason. |

`DeleteSync_FirstRun_NoState_AdditiveOnly` and `DeleteSync_Case2_RestoresModifiedFile` still pass.

These four are **not** repaired here. `SyncStateManager`-based deletion is gone by design — a single manifest plus a single `LastSyncUtc` cannot say which side changed, which is the defect this phase exists to remove — and the replacement requires a `SyncDatabase` fixture plus the `PairMarker` handling that Phase 8's no-ancestor gate introduces. Files under `tests/.../Integration/` belong to **Phase 10**, which must either migrate `DeleteSyncTests` onto `SyncDatabase` or retire these four as superseded by `DatabaseDeleteSyncTests` and the Phase 10 E2E suite. **The full suite is red between this phase's commit and Phase 10's**, and the branch's green-gate is satisfied per-phase only for the filters named in each task's Step 4. This is stated here rather than discovered later.

---

### Phase 6 commit

```bash
git add src/RemoteFileSync/Sync/SyncEngine.cs \
        src/RemoteFileSync/Sync/ConflictResolver.cs \
        src/RemoteFileSync/Network/SyncClient.cs \
        tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs \
        tests/RemoteFileSync.Tests/Sync/ConflictResolverTests.cs \
        tests/RemoteFileSync.Tests/Network/AncestorRowWriteTests.cs
git commit -m "feat(sync): plan deletions from a per-file ancestor row instead of LastSynced

ResolveDeleteConflict decided whether a surviving file had been edited by
comparing its mtime against the session-wide LastSynced, so any file whose
stamp merely looked older than the last sync was deleted on the peer.

ComputePlan now takes an AncestorRow table recording what each side looked
like when they last agreed, and returns a PlanResult instead of a bare list.
The with-ancestor and no-ancestor paths are separate methods: the former
decides from recorded facts and may delete, the latter is strictly additive
and can never emit a delete. Push and Pull are explicit mirror tables rather
than a bidirectional bool, and both require the peer copy to be unchanged
since the ancestor before deleting it.

ComputePlan is pure. Resurrections and conflicts are returned on PlanResult
so the caller can record them only after a transfer actually succeeds.

Two fixes in the caller. The skip loop derived a two-sided ancestor row from
whichever manifest happened to have the file, fabricating a peer state that
never existed; in Pull that made the next run delete the user's client-only
files. It now writes a row only when both sides have the path, with each
side's own size and mtime, and records a plain skip otherwise. The whole
block also moves below both delete guards, so a threshold abort leaves no
ancestor state behind for a later run to act on.

Server mtimes are normalised through the measured ClockSkew before any
newest-wins comparison, and ChangeDetector compares size as well as mtime so
an in-place rewrite is not mistaken for an untouched file.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
git push -u origin feat/deletion-sync-ancestor-merge
```

**Verification before commit:**

```bash
dotnet build -c Release
dotnet test -c Release --filter "FullyQualifiedName~SyncEngineTests|FullyQualifiedName~ConflictResolverTests|FullyQualifiedName~AncestorRowWriteTests|FullyQualifiedName~ChangeDetectorTests"
dotnet test -c Release
```

- `dotnet build -c Release`: 0 errors, 0 warnings.
- The filtered run: PASS. Every method in `SyncEngineTests`, the seven surviving `ConflictResolverTests` methods, both `AncestorRowWriteTests` methods, and Phase 2's `ChangeDetectorTests` must be green.
- `grep -rn "ResolveDeleteConflict" --include=*.cs src tests` returns nothing.
- `grep -rn "ClockSkew.None" --include=*.cs src` returns nothing under `src/RemoteFileSync/Network/` — the production call site passes the measured `skew`.
- The full `dotnet test -c Release`: the four `DeleteSyncTests` methods enumerated in Task 6.4 are expected red, and only those four. Any other failure is unplanned fallout from this phase and must be fixed here before committing.

---

---

## Phase 7: ConflictKeepBoth execution — preserve both copies, rename the loser

**Goal:** Execute `SyncActionType.ConflictKeepBoth` by renaming the losing copy to the contract's conflict name, archiving it through the session's single `ArchiveManager` with `ArchiveReason.Conflict`, and moving both copies across the wire using only the pre-existing transfer actions so neither peer's frame sequence can desync. Record every conflict **and every resurrection** in the database as an encoded `ConflictDetail`, never as English prose, and place the whole rename pass above Phase 6's ancestor-write block so that no aborted run leaves committed rows behind.

**Files:**
- Create: `src/RemoteFileSync/Sync/ConflictNamer.cs`
- Create: `src/RemoteFileSync/Sync/ConflictKeepBothExecutor.cs`
- Modify: `src/RemoteFileSync/Network/SyncClient.cs:169-183` (plan summary + serialisation — insert the expansion above it)
- Modify: `src/RemoteFileSync/Network/SyncClient.cs` (insert the rename pass **above Phase 6's ancestor-write block**, relocating that block below it — see Task 7.3 Edit 2)
- Modify: `src/RemoteFileSync/Network/SyncClient.cs:472-474` (`// 11. Exchange SyncComplete` — insert the conflict **and resurrection** drains above it)
- Modify: `src/RemoteFileSync/Network/SyncServer.cs:180-182` (receive-phase header — insert the mirrored rename pass above it)
- Test: `tests/RemoteFileSync.Tests/Sync/ConflictNamerTests.cs`
- Test: `tests/RemoteFileSync.Tests/Sync/ConflictKeepBothExecutorTests.cs`
- Test: `tests/RemoteFileSync.Tests/Integration/ConflictKeepBothSyncTests.cs`

---

## Interfaces

### Consumes — types

| Symbol | Delivered by |
|---|---|
| `SyncActionType.ConflictKeepBoth = 7`, `SyncMode`, `SyncOptions.Mode` | Phase 1 |
| `ClockSkew` (`ClockSkew.None`, `DateTime NormaliseServerTime(DateTime)`) | Phase 2 |
| `SyncDatabase.LogConflict(string path, long sessionId, string detail)`, `SyncDatabase.LogResurrection(string path, long sessionId, string detail)`, `SyncDatabase.GetRecentSessions(int limit = 20)` → `SyncSessionEntry.Id`, `SyncDatabase.GetRow(string)`; `ConflictDetail(long ClientSize, long ClientMtimeTicks, long ServerSize, long ServerMtimeTicks, string? RenamedTo)` with `string Encode()` — namespace `RemoteFileSync.State` | Phase 4 |
| `ArchiveManager`, `ArchiveReason.Conflict`, `bool Archive(string, ArchiveReason, bool removeOriginal)`, `public const string SessionFolderFormat = "yyyyMMdd-HHmmss"`, `string SessionRoot` | Phase 5 |
| `PlanResult` with `List<SyncPlanEntry> Entries`, `List<ConflictInfo> Conflicts` and `List<ResurrectionInfo> Resurrections`; `ConflictInfo(string Path, long ClientSize, long ClientMtimeTicks, long ServerSize, long ServerMtimeTicks)`; `ResurrectionInfo(string Path, bool KeptClientCopy, long KeptSize, long KeptMtimeTicks)`; the ancestor-write block this phase relocates | Phase 2 declares the record types; Phase 6 populates them |
| `RemoteFileSync.Sync.DeleteBudget.Within(int deletes, int destinationCount, int maxDeletePercent)` | Phase 8 |

> **Ordering dependency on Phase 8.** The server's conflict-squatter guard (Task 7.3 Edit 4) calls `DeleteBudget.Within`, so `src/RemoteFileSync/Sync/DeleteBudget.cs` — created by Phase 8 Task 8.2 Step 3a — must exist before Phase 7's Task 7.3 compiles. Land Phase 8 Task 8.2 Step 3a before Task 7.3, or apply the phases in the order 1-6, 8, 7, 9, 10. This is deliberate: hand-rolling the percentage here is how the two guards drifted apart in the first place — `DeleteBudget.Within` is the one place that knows a zero denominator means "refuse", and that below `MinTrackedFilesForDeleteGuard` the percentage is noise.

Existing and unchanged: `FileTransferSender.SendFileAsync`, `FileTransferReceiver.ReceiveFileAsync`, `FileManifest.Get`, `PathGuard.TryResolveWithinRoot`, `SyncPlanEntry(SyncActionType, string)`, `SyncOptions.MaxDeletePercent`, `SyncOptions.MinTrackedFilesForDeleteGuard`, `SyncOptions.ForceDelete`.

### Consumes — **locals left behind by earlier phases. This phase declares NONE of them.**

Redeclaring any of these is CS0128 (same scope) or CS0136 (nested scope) and breaks the build.

| Local | Method | Owner | How Phase 7 uses it |
|---|---|---|---|
| `sessionStartUtc` (`DateTime`) | `SyncClient.HandleConnectionAsync`, declared at the top per CONTRACT correction #9 — **above** the handshake, therefore in scope at line 169 | Phase 5 | passed to `Expand` as the conflict-name stamp, so the conflict name and the archive session folder carry the identical `yyyyMMdd-HHmmss` |
| `archive` (`ArchiveManager`) | `SyncClient.HandleConnectionAsync` (replaces `var backup` at `SyncClient.cs:209`) and `SyncServer.HandleConnectionAsync` (replaces `var backup` at `SyncServer.cs:173`) | Phase 5 | passed into `ApplyLocalRenames` on both peers. **Phase 7 constructs no `ArchiveManager` of its own** — that was the CS0136 / split-session-folder defect (#13, #46) |
| `mode` (`SyncMode`) | `SyncServer.HandleConnectionAsync`, replacing `bool bidirectional` at `SyncServer.cs:140` | Phase 3 | the server's conflict guard is `mode != SyncMode.TwoWay`. **`bidirectional` no longer exists on the server** (#38) |
| `deleteEnabled` (`bool`) | `SyncServer.HandleConnectionAsync:141` (Phase 3 re-derives it from handshake bit 2) | Phase 3 | gates squatter removal on the server |
| `skew` (`ClockSkew`) | `SyncClient.HandleConnectionAsync` | Phase 3 | passed to `Expand` so the winner is decided in client time |
| `planResult` (`PlanResult`), `syncPlan` (`List<SyncPlanEntry>` = `planResult.Entries`) | `SyncClient.HandleConnectionAsync`, replacing `SyncClient.cs:150-152` | Phase 6 | `syncPlan` is reassigned to the expanded list; `planResult.Conflicts` is drained into `LogConflict` and `planResult.Resurrections` into `LogResurrection` |
| `ancestor` (`IReadOnlyDictionary<string, AncestorRow>?`) | `SyncClient.HandleConnectionAsync` | Phase 6 | not read by Phase 7; named here because Phase 7 relocates the block guarded by `_db != null && ancestor != null` |
| `sessionId` (`long`), `_db`, `_logger`, `_progress`, `_options`, `clientManifest`, `serverManifest`, `scanner` | pre-existing | read only |

These names are **fixed, not provisional**. Phase 3 declares `mode`, `deleteEnabled`, `mirrorDeletes` and `skew`; Phase 5 declares `sessionStartUtc` and `archive`; Phase 6 declares `planResult` and `ancestor`. Phase 7 uses exactly these identifiers, introduces no second declaration, and recomputes nothing an earlier phase already computed.

### Regions this phase does NOT touch

`SyncClient.cs:150-152` and `:185-206` (Phase 6's plan call and its removal of the old DB-write block); `SyncClient.cs:209` / `SyncServer.cs:173,193,260` (Phase 5's `BackupManager`→`ArchiveManager` swap); `SyncClient.cs:233-256` and `SyncServer.cs:226-240` (Phase 8's delete guards); `SyncServer.cs:132-152` and `SyncClient.cs:89-113` (Phase 3's handshake).

**One exception, by decree: Phase 7 owns the final position of Phase 6's ancestor-write block.** Phase 6 lands that block immediately above `// 7. Send files to server`; Phase 7's rename pass must run *above* it, because the rename pass can `return 4` and no database mutation may be committed by a run that aborts. The relocation is Task 7.3 Edit 2, spelled out there with the exact before/after ordering. Phase 6 does not perform it and Phase 6's own text is not edited — Phase 7 simply inserts above the block Phase 6 left behind.

Every "Replace exactly" block below **anchors on a landmark line and inserts above it**, never on the closing braces of a preceding block. This is deliberate: Phase 6 lands the ancestor-write block just above `// 7. Send files to server`, so an anchor that quoted the preceding `}` would no longer match, while an anchor that starts *at* a landmark comment survives any insertion above it.

### Produces

Neither type is in CONTRACT.md. They are declared here rather than silently invented; nothing outside this phase depends on them except Phase 9's review report, which reads only the `ConflictDetail` rows this phase writes.

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
public sealed record ConflictExpansion(
    List<SyncPlanEntry> Entries,
    IReadOnlyDictionary<string, string> RenamedTo);   // original path -> conflict name
public readonly record struct ConflictRenameOutcome(
    int Renamed,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> NotArchived);
public static class ConflictKeepBothExecutor
{
    public static ConflictExpansion Expand(
        IReadOnlyList<SyncPlanEntry> plan, FileManifest clientManifest, FileManifest serverManifest,
        ClockSkew skew, DateTime sessionStartUtc, string clientFolder);
    public static int CountOccupiedTargets(
        IReadOnlyList<SyncPlanEntry> plan, string side, string syncFolder);
    public static ConflictRenameOutcome ApplyLocalRenames(
        IReadOnlyList<SyncPlanEntry> plan, string side, string syncFolder, ArchiveManager archive);
}
```

---

## Wire design — recommendation re-confirmed, with the expansion shown

**Question:** should the client expand `ConflictKeepBoth` into `SendToServer` + `SendToClient` before serialising, so the wire never carries action 7?

**Re-confirmed answer: expand, but not *fully*.** Pure expansion cannot express both loser sides.

- Let `P` be the conflicted path and `N` the conflict name.
- **Case A — server copy wins, client copy loses** (`N` ends in `-client`). The client renames `P → N` on its own disk, then `SendToServer(N)` + `SendToClient(P)`. Fully expressible; the server needs zero new behaviour.
- **Case B — client copy wins, server copy loses** (`N` ends in `-server`). `N` must hold the *server's* old bytes and exist under that name in both folders. Only the server holds those bytes, and `FileTransferSender.SendFileAsync` opens the exact path it is handed (`FileTransfer.cs:24-25`), so `N` must already exist on the server's disk. No reordering rescues it: the server's receive phase (`SyncServer.cs:180-219`) runs *before* its send phase (`SyncServer.cs:308`), so `P` on the server is overwritten by the winner before it could ever be sent. **Case B is unrepresentable without a server-side rename.**

So: **hybrid — client-side expansion into three entries, one of which is a frame-free rename instruction.** For each `ConflictKeepBoth(P)` the client emits, in order:

| # | entry | who acts | frames exchanged |
|---|---|---|---|
| 1 | `ConflictKeepBoth(N)` | **only** the peer named by `N`'s `losingSide` | **none** |
| 2 | `SendToServer(...)` | client sends, server receives | `FileStart`, `FileChunk`×n, `FileEnd`, `BackupConfirm` |
| 3 | `SendToClient(...)` | server sends, client receives | `FileStart`, `FileChunk`×n, `FileEnd`, `BackupConfirm` |

Case A: entry 2 carries `N`, entry 3 carries `P`. Case B: entry 2 carries `P`, entry 3 carries `N`. Either way **exactly one file moves client→server and exactly one moves server→client.**

Worked expansion, Case B (`skew = None`, `sessionStartUtc = 2026-07-20 14:30:52Z`, client copy newer):

```
in:  [ ConflictKeepBoth("notes.md") ]
out: [ ConflictKeepBoth("notes.conflict-20260720-143052-server.md"),
       SendToServer   ("notes.md"),
       SendToClient   ("notes.conflict-20260720-143052-server.md") ]
RenamedTo: { "notes.md" -> "notes.conflict-20260720-143052-server.md" }
```

**Why this cannot desync.** The rename pass is the only step where the two peers do different work, and it exchanges no frames, so it cannot shift either side's frame position. Every message-bearing step derives its work list from `syncPlan.Where(p => p.Action == …)` over an identical `List<SyncPlanEntry>` — `SyncClient.cs:261` (`SendToServer || ClientOnly`), `:328` (`DeleteOnServer`), `:360` (`SendToClient || ServerOnly`), `:407` (`DeleteOnClient`), mirrored at `SyncServer.cs:182`, `:242`, `:308`, `:358` — and `ConflictKeepBoth` matches none of those allow-lists. `ProtocolHandler.DeserializeSyncPlan` casts the action byte without validation, so value 7 round-trips unchanged.

The residual risk is a *failed* rename leaving a promised source file missing. **Corrected mechanism (#28):** the previous draft claimed `SendFileAsync` throws at `sourceInfo.Length` (`FileTransfer.cs:50`). That is wrong on both counts — `FileTransfer.cs:49` is `sourceInfo.Length`, `:50` is the `isCompressed:` argument, and `new FileInfo()` at `:25` does not throw for a missing file. The real first throw sites are **`FileTransfer.cs:39` (`CompressionHelper.CompressFile` → `File.OpenRead`)** for ordinary extensions and **`FileTransfer.cs:47` (`CompressionHelper.ComputeSha256` → `File.OpenRead`)** for all files. Both precede the first `WriteMessageAsync` at `FileTransfer.cs:52`, so the conclusion survives intact: **nothing reaches the wire before the throw, and the peer blocks on a `FileStart` that never arrives.** Task 7.3 therefore makes a failed conflict rename **fatal (exit 4) before any frame is sent**, not skippable.

**Does the renamed loser get re-synced by the next scan?** No, and that is intended. `N` is transferred within the same session, `File.Move` preserves the loser's mtime, and `FileTransferReceiver` restores the sender's mtime from `FileStart`, so both sides hold `N` with identical size and mtime. The send/receive loops write its ancestor row, so the next `ComputePlan` resolves `N` to `Skip`. **No exclusion is needed and none is added.** If `N` fails the user's `--include` filters, the existing `filteredOut` guard at `SyncClient.cs:157-167` retires the row instead of deleting the file.

---

### Task 7.1: `ConflictNamer` — the frozen name format, collision walk, and round-trip parse

All three members land in one red-green cycle. `Compose`, `MakeUnique` and `TryParse` are a single
inseparable contract — `MakeUnique` is `Compose` in a loop and `TryParse` is `Compose` inverted, so
splitting them into separate tasks produced two "tasks" whose implementation step said "already
implemented" and whose red gate could therefore never be observed.

- [ ] **Step 1: Write the failing test**

Create `tests/RemoteFileSync.Tests/Sync/ConflictNamerTests.cs`:

```csharp
using RemoteFileSync.Backup;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class ConflictNamerTests : IDisposable
{
    private static readonly DateTime Stamp = new(2026, 7, 20, 14, 30, 52, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"rfs_cname_{Guid.NewGuid()}");

    public ConflictNamerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

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

    [Fact]
    public void Compose_StampMatchesTheArchiveSessionFolderName()
    {
        // The conflict copy and the archived snapshot of the same file must be findable by the
        // same timestamp string. Two independently-written format strings would drift apart the
        // first time either is edited, leaving the user unable to correlate them.
        var name = ConflictNamer.Compose("report.docx", Stamp, ConflictNamer.ServerSide);
        Assert.Contains(Stamp.ToString(ArchiveManager.SessionFolderFormat), name);
    }

    // ── MakeUnique: the collision walk ────────────────────────────────────────

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

    // ── TryParse: round-trip and rejection ────────────────────────────────────

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
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ConflictNamerTests"`
Expected: FAIL. `ConflictNamer` does not exist yet, so the test project does not compile — every one of the fourteen facts/theories above is red for the same reason.

- [ ] **Step 3: Implement**

Create `src/RemoteFileSync/Sync/ConflictNamer.cs`:

```csharp
using RemoteFileSync.Backup;

namespace RemoteFileSync.Sync;

/// <summary>
/// Builds the name a losing copy is renamed to when a ConflictKeepBoth entry is executed:
/// {nameWithoutExtension}.conflict-{yyyyMMdd-HHmmss}-{losingSide}{extension}
///
/// The name is chosen once, by the client, and travels inside the sync plan. Both peers must
/// land the loser on the byte-identical path: if they disagree, the next scan sees two unrelated
/// files and copies each one to the other side, forever.
/// </summary>
public static class ConflictNamer
{
    public const string Infix = ".conflict-";
    public const string ClientSide = "client";
    public const string ServerSide = "server";

    /// <summary>Upper bound on collision retries, so a directory the process cannot write to
    /// fails loudly instead of spinning.</summary>
    public const int MaxOrdinal = 1000;

    /// <summary>Deliberately the SAME constant the archive session folder uses. The conflict copy
    /// and its archived snapshot are correlated by this string; two independent format literals
    /// would drift the first time either was edited.</summary>
    private const string StampFormat = ArchiveManager.SessionFolderFormat;

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
Expected: PASS — `Compose_MatchesContractFormat` (all six rows), `Compose_OrdinalTwoAppendsSuffixBeforeExtension`, `Compose_RejectsUnknownLosingSide`, `Compose_StampMatchesTheArchiveSessionFolderName`, `MakeUnique_ReturnsBareNameWhenNothingOccupiesIt`, `MakeUnique_WalksOrdinalPastExistingFiles`, `MakeUnique_PreservesSubdirectoryAndCreatesNoFile`, `TryParse_RoundTripsCompose` (all five rows), `TryParse_RoundTripsOrdinalNames`, `TryParse_NestedConflictUnwrapsOnlyTheOuterLayer`, `TryParse_RejectsNamesItDidNotProduce` (all seven rows).

---

### Task 7.2: `ConflictKeepBothExecutor` — expansion, occupancy count, and the archive-gated rename pass

This is where finding #1 (CRITICAL, silent data loss) is fixed.

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
    private readonly string _elsewhere;
    private readonly string _archiveRoot;

    public ConflictKeepBothExecutorTests()
    {
        _sync = Path.Combine(_root, "sync");
        _elsewhere = Path.Combine(_root, "elsewhere");
        _archiveRoot = Path.Combine(_root, "archive");
        Directory.CreateDirectory(_sync);
        Directory.CreateDirectory(_elsewhere);
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

    private ArchiveManager WorkingArchive() => new(_sync, _archiveRoot, Stamp);

    /// <summary>An ArchiveManager rooted somewhere else. Every Archive() call returns false
    /// WITHOUT throwing, exactly as it does when PathGuard fails closed on transient IO
    /// (PathGuard.cs:85-86 -> :69). This is the only way to exercise the false branch.</summary>
    private ArchiveManager FailingArchive() => new(_elsewhere, _archiveRoot, Stamp);

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

        var result = ConflictKeepBothExecutor.Expand(plan, client, server, ClockSkew.None, Stamp, _sync);

        var expectedName = "report.conflict-20260720-143052-client.txt";
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(SyncActionType.ConflictKeepBoth, result.Entries[0].Action);
        Assert.Equal(expectedName, result.Entries[0].RelativePath);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[1].Action);
        Assert.Equal(expectedName, result.Entries[1].RelativePath);
        Assert.Equal(SyncActionType.SendToClient, result.Entries[2].Action);
        Assert.Equal("report.txt", result.Entries[2].RelativePath);
        Assert.Equal(expectedName, result.RenamedTo["report.txt"]);
    }

    [Fact]
    public void Expand_ClientNewer_RenamesServerCopyAndMovesOneFileEachWay()
    {
        var client = Manifest("report.txt", 20, Stamp.AddHours(1));
        var server = Manifest("report.txt", 10, Stamp);
        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, "report.txt") };

        var result = ConflictKeepBothExecutor.Expand(plan, client, server, ClockSkew.None, Stamp, _sync);

        var expectedName = "report.conflict-20260720-143052-server.txt";
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(SyncActionType.ConflictKeepBoth, result.Entries[0].Action);
        Assert.Equal(expectedName, result.Entries[0].RelativePath);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[1].Action);
        Assert.Equal("report.txt", result.Entries[1].RelativePath);
        Assert.Equal(SyncActionType.SendToClient, result.Entries[2].Action);
        Assert.Equal(expectedName, result.Entries[2].RelativePath);
        Assert.Equal(expectedName, result.RenamedTo["report.txt"]);
    }

    [Fact]
    public void Expand_WinnerIsDecidedAfterSkewNormalisation()
    {
        // Server clock runs one hour fast. Raw mtimes say the server is newer; in client time it
        // is older. Without normalisation the loser would be decided by how wrong a clock is.
        var client = Manifest("report.txt", 10, Stamp.AddMinutes(30));
        var server = Manifest("report.txt", 10, Stamp.AddMinutes(45));
        var skew = new ClockSkew(TimeSpan.FromHours(1));

        var result = ConflictKeepBothExecutor.Expand(plan: new List<SyncPlanEntry>
            { new(SyncActionType.ConflictKeepBoth, "report.txt") },
            clientManifest: client, serverManifest: server, skew: skew,
            sessionStartUtc: Stamp, clientFolder: _sync);

        Assert.Equal("report.conflict-20260720-143052-server.txt", result.Entries[0].RelativePath);
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

        var result = ConflictKeepBothExecutor.Expand(plan, client, server, ClockSkew.None, Stamp, _sync);

        Assert.Equal(2, result.Entries.Count(e => e.Action == SyncActionType.SendToServer));
        Assert.Equal(2, result.Entries.Count(e => e.Action == SyncActionType.SendToClient));
        Assert.Equal(2, result.Entries.Count(e => e.Action == SyncActionType.ConflictKeepBoth));
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

        var result = ConflictKeepBothExecutor.Expand(
            plan, new FileManifest(), new FileManifest(), ClockSkew.None, Stamp, _sync);

        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[0].Action);
        Assert.Equal(SyncActionType.Skip, result.Entries[1].Action);
        Assert.Equal(SyncActionType.DeleteOnClient, result.Entries[2].Action);
        Assert.Empty(result.RenamedTo);
    }

    [Fact]
    public void ApplyLocalRenames_LosingSideRenamesArchivesAndPreservesMtime()
    {
        var mtime = Stamp.AddHours(-3);
        Write("report.txt", "client edit", mtime);
        var name = "report.conflict-20260720-143052-client.txt";
        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, name) };
        var archive = WorkingArchive();

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ClientSide, _sync, archive);

        Assert.Equal(1, outcome.Renamed);
        Assert.Empty(outcome.Failures);
        Assert.Empty(outcome.NotArchived);
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

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ServerSide, _sync, WorkingArchive());

        Assert.Equal(0, outcome.Renamed);
        Assert.Empty(outcome.Failures);
        Assert.Equal("server edit", File.ReadAllText(Path.Combine(_sync, "report.txt")));
    }

    [Fact]
    public void ApplyLocalRenames_MissingOriginalIsAFailureNotASilentSkip()
    {
        // The plan already promises the peer a transfer under this name; a sender that cannot
        // open its source throws at FileTransfer.cs:39/:47 — before any frame is written — and
        // the peer blocks forever. Fail loudly so the caller can abort before sending.
        var plan = new List<SyncPlanEntry>
        {
            new(SyncActionType.ConflictKeepBoth, "gone.conflict-20260720-143052-client.txt"),
        };

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ClientSide, _sync, WorkingArchive());

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

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ClientSide, _sync, WorkingArchive());

        Assert.Equal(0, outcome.Renamed);
        Assert.Single(outcome.Failures);
    }

    [Fact]
    public void ApplyLocalRenames_MalformedEntryIsAFailure()
    {
        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, "not-a-conflict.txt") };

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ClientSide, _sync, WorkingArchive());

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
        var archive = WorkingArchive();

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ClientSide, _sync, archive);

        Assert.Equal(1, outcome.Renamed);
        Assert.Empty(outcome.Failures);
        Assert.Equal("loser", File.ReadAllText(Path.Combine(_sync, name)));
        Assert.True(File.Exists(Path.Combine(archive.SessionRoot, "conflict", name)));
    }

    [Fact]
    public void ApplyLocalRenames_SquatterSurvivesWhenTheArchiveDoesNotSucceed()
    {
        // THE data-loss regression. ArchiveManager.Archive returns false WITHOUT throwing
        // whenever PathGuard.TryResolveWithinRoot fails, and PathGuard fails closed on transient
        // IO (PathGuard.cs:85-86 -> :69). A delete that is not gated on the returned bool
        // destroys the user's file in exactly the case where no archived copy exists.
        var name = "report.conflict-20260720-143052-client.txt";
        Write("report.txt", "loser", Stamp);
        Write(name, "irreplaceable squatter", Stamp);
        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, name) };

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ClientSide, _sync, FailingArchive());

        Assert.Equal(0, outcome.Renamed);
        Assert.Single(outcome.Failures);
        Assert.Equal("irreplaceable squatter", File.ReadAllText(Path.Combine(_sync, name)));
        Assert.Equal("loser", File.ReadAllText(Path.Combine(_sync, "report.txt")));
    }

    [Fact]
    public void ApplyLocalRenames_RenameStillHappensWhenOnlyThePrecautionaryCopyFails()
    {
        // The removeOriginal:false archive is a belt-and-braces snapshot; File.Move preserves
        // the bytes regardless. Aborting the whole session over a redundant copy would be a
        // worse outcome than proceeding and reporting it.
        Write("report.txt", "loser", Stamp);
        var name = "report.conflict-20260720-143052-client.txt";
        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, name) };

        var outcome = ConflictKeepBothExecutor.ApplyLocalRenames(
            plan, ConflictNamer.ClientSide, _sync, FailingArchive());

        Assert.Equal(1, outcome.Renamed);
        Assert.Empty(outcome.Failures);
        Assert.Single(outcome.NotArchived);
        Assert.Equal("loser", File.ReadAllText(Path.Combine(_sync, name)));
    }

    [Fact]
    public void CountOccupiedTargets_CountsOnlyThisSidesOccupiedNames()
    {
        Write("a.conflict-20260720-143052-client.txt", "squatter", Stamp);
        var plan = new List<SyncPlanEntry>
        {
            new(SyncActionType.ConflictKeepBoth, "a.conflict-20260720-143052-client.txt"), // occupied, ours
            new(SyncActionType.ConflictKeepBoth, "b.conflict-20260720-143052-client.txt"), // free, ours
            new(SyncActionType.ConflictKeepBoth, "c.conflict-20260720-143052-server.txt"), // not ours
            new(SyncActionType.SendToServer, "d.txt"),
        };

        Assert.Equal(1, ConflictKeepBothExecutor.CountOccupiedTargets(
            plan, ConflictNamer.ClientSide, _sync));
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ConflictKeepBothExecutorTests"`
Expected: FAIL. `ConflictKeepBothExecutor` does not exist yet, so the test project does not compile.

- [ ] **Step 3: Implement**

Create `src/RemoteFileSync/Sync/ConflictKeepBothExecutor.cs`:

```csharp
using RemoteFileSync.Backup;
using RemoteFileSync.Models;
using RemoteFileSync.Security;

namespace RemoteFileSync.Sync;

/// <summary>The expanded plan plus the original-path -> conflict-name map, which the caller
/// needs to fill in ConflictDetail.RenamedTo when it logs the conflict.</summary>
public sealed record ConflictExpansion(
    List<SyncPlanEntry> Entries,
    IReadOnlyDictionary<string, string> RenamedTo);

/// <summary>Result of one peer's conflict rename pass. A non-empty <see cref="Failures"/> list is
/// fatal, not skippable. <see cref="NotArchived"/> is advisory: the rename succeeded but the
/// precautionary pre-rename snapshot did not.</summary>
public readonly record struct ConflictRenameOutcome(
    int Renamed,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> NotArchived);

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
/// exist on the server's disk before FileTransferSender opens it (FileTransfer.cs:24-25), and the
/// server's receive phase overwrites the original before its send phase runs — so no reordering
/// of existing actions can produce it.
/// </summary>
public static class ConflictKeepBothExecutor
{
    /// <summary>
    /// Rewrites ConflictKeepBoth entries into the three-entry form. Runs on the client only,
    /// before the plan is serialised.
    /// </summary>
    public static ConflictExpansion Expand(
        IReadOnlyList<SyncPlanEntry> plan,
        FileManifest clientManifest,
        FileManifest serverManifest,
        ClockSkew skew,
        DateTime sessionStartUtc,
        string clientFolder)
    {
        var expanded = new List<SyncPlanEntry>(plan.Count);
        var renamedTo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
            renamedTo[entry.RelativePath] = conflictName;

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

        return new ConflictExpansion(expanded, renamedTo);
    }

    /// <summary>
    /// How many conflict entries this peer owns would land on a name a local file already
    /// occupies — i.e. how many existing local files the rename pass would archive and remove.
    /// The server calls this BEFORE renaming: the plan arrives from a peer we do not
    /// authenticate, and a plan full of conflict names pointing at real local files is a way to
    /// destroy a folder without ever sending a DeleteFile frame.
    /// </summary>
    public static int CountOccupiedTargets(
        IReadOnlyList<SyncPlanEntry> plan, string side, string syncFolder)
    {
        int occupied = 0;
        foreach (var entry in plan)
        {
            if (entry.Action != SyncActionType.ConflictKeepBoth) continue;
            if (!ConflictNamer.TryParse(entry.RelativePath, out _, out var losingSide)) continue;
            if (losingSide != side) continue;
            if (!PathGuard.TryResolveWithinRoot(syncFolder, entry.RelativePath, out var full)) continue;
            if (File.Exists(full)) occupied++;
        }
        return occupied;
    }

    /// <summary>
    /// Runs on both peers before their transfer phases. <paramref name="side"/> is this peer's
    /// identity; only the peer the conflict name blames touches its disk, so the two sides never
    /// both rename and the per-direction file counts stay symmetric.
    ///
    /// Every entry this peer owns but cannot complete lands in Failures. Callers MUST abort the
    /// session on a non-empty list: the plan already promises the peer a transfer under the
    /// conflict name, and FileTransferSender throws while opening a missing source
    /// (FileTransfer.cs:39 CompressFile, :47 ComputeSha256) — both before the first
    /// WriteMessageAsync at :52 — leaving the peer blocked on a frame that never arrives.
    /// </summary>
    public static ConflictRenameOutcome ApplyLocalRenames(
        IReadOnlyList<SyncPlanEntry> plan, string side, string syncFolder, ArchiveManager archive)
    {
        int renamed = 0;
        var failures = new List<string>();
        var notArchived = new List<string>();

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
                if (File.Exists(conflictFull))
                {
                    // NEVER destroy an existing file on an unproven archive. Archive() returns
                    // false WITHOUT throwing when PathGuard.TryResolveWithinRoot fails, and
                    // PathGuard fails CLOSED on transient IO while walking for reparse-point
                    // ancestors (PathGuard.cs:85-86 -> :69). So `false` does not mean "there was
                    // nothing to archive" — deleting anyway is the one path on which the user's
                    // file is destroyed with no copy anywhere. Record and skip instead; the
                    // caller turns a non-empty Failures list into an abort before any frame moves.
                    if (!archive.Archive(entry.RelativePath, ArchiveReason.Conflict, removeOriginal: true))
                    {
                        failures.Add($"{entry.RelativePath}: could not archive the file already " +
                                     "occupying the conflict name; refusing to overwrite it");
                        continue;
                    }

                    // A successful Archive(removeOriginal: true) removed the source. A survivor
                    // here means the move half-failed, and File.Move onto it would still destroy
                    // a file whose archived copy we cannot vouch for.
                    if (File.Exists(conflictFull))
                    {
                        failures.Add($"{entry.RelativePath}: still present after archiving; " +
                                     "refusing to overwrite it");
                        continue;
                    }
                }

                // Precautionary pre-rename snapshot. Deliberately NOT gated the way the squatter
                // archive above is: a false here costs only a redundant copy, because File.Move
                // preserves the bytes under the new name either way. Aborting the whole session
                // over a belt-and-braces snapshot would strand the peer mid-plan for no gain.
                if (!archive.Archive(originalPath, ArchiveReason.Conflict, removeOriginal: false))
                    notArchived.Add(originalPath);

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

        return new ConflictRenameOutcome(renamed, failures, notArchived);
    }
}
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ConflictKeepBothExecutorTests"`
Expected: PASS — in particular `ApplyLocalRenames_SquatterSurvivesWhenTheArchiveDoesNotSucceed`, `ApplyLocalRenames_RenameStillHappensWhenOnlyThePrecautionaryCopyFails`, `ApplyLocalRenames_ArchivesALocalSquatterRatherThanDivergingFromThePlanName`, `CountOccupiedTargets_CountsOnlyThisSidesOccupiedNames`, `Expand_WinnerIsDecidedAfterSkewNormalisation` and the five other `Expand_*` / `ApplyLocalRenames_*` facts.

---

### Task 7.3: Wire both peers — expand, rename before any frame moves, log the conflict

The client and server edits land **together, in one step**. Splitting them leaves a commit point at which the plan promises the server a transfer it cannot make — precisely the hang the wire-design section argues must never exist (this is finding #20's recommended fix, and it also removes an intermediate red state whose exact message could not be predicted).

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

    /// <summary>
    /// Same wiring, but the server's outcome is observed and discarded. A client that aborts at
    /// the rename pass has already sent the plan and then goes away, so the server fails on a
    /// transfer that never arrives — its exit code is not what the abort tests pin, and an
    /// unobserved faulted task would surface later as an unrelated failure.
    /// </summary>
    private async Task<int> RunTwoWaySyncExpectingClientAbortAsync(SyncDatabase db)
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
        int clientResult = await client.RunAsync(cts.Token);
        try { await serverTask; } catch { /* expected: the peer went away mid-plan */ }
        return clientResult;
    }

    [Fact]
    public async Task TwoWayConflict_ClientCopyLosesWhenServerCopyIsNewer()
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

    [Fact]
    public async Task Conflict_IsLoggedAsAnEncodedConflictDetail()
    {
        // Phase 9's review report decodes this column. A free-form English sentence parses to
        // null there and the report silently degrades to "no sizes, no mtimes" for every real
        // conflict — the exact defect this assertion exists to prevent.
        var baseTs = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var dbPath = Path.Combine(_dbDir, "conflict-detail.db");

        CreateFileWithTimestamp(_clientDir, "report.txt", "original", baseTs);
        CreateFileWithTimestamp(_serverDir, "report.txt", "original", baseTs);
        using (var db = new SyncDatabase(dbPath))
            await RunTwoWaySyncAsync(db);

        CreateFileWithTimestamp(_clientDir, "report.txt", "client edit!!", baseTs.AddHours(1));
        CreateFileWithTimestamp(_serverDir, "report.txt", "server edit", baseTs.AddHours(2));

        long sessionId;
        using (var db = new SyncDatabase(dbPath))
        {
            var (clientResult, _) = await RunTwoWaySyncAsync(db);
            Assert.Equal(0, clientResult);
            // GetRecentSessions orders by id DESC, so limit 1 is the run that just finished.
            // (No `using System.Linq;` needed — the test project has ImplicitUsings enabled.)
            sessionId = db.GetRecentSessions(1).First().Id;
        }

        using (var db = new SyncDatabase(dbPath))
        {
            var conflicts = db.GetSessionConflicts(sessionId);
            var row = Assert.Single(conflicts, c => c.Path == "report.txt");
            var decoded = ConflictDetail.Decode(row.Detail);
            Assert.NotNull(decoded);
            Assert.Equal("client edit!!".Length, decoded!.ClientSize);
            Assert.Equal("server edit".Length, decoded.ServerSize);
            Assert.Equal(baseTs.AddHours(1).Ticks, decoded.ClientMtimeTicks);
            Assert.NotNull(decoded.RenamedTo);
            Assert.EndsWith("-client.txt", decoded.RenamedTo!);
        }
    }

    [Fact]
    public async Task ConflictRenameFailure_AbortsAboveTheAncestorWriteBlock()
    {
        // The ordering guarantee Edit 2 exists to create: the rename pass can return 4, and no
        // ancestor row may survive a run that returned 4. If the ancestor-write block were left
        // where Phase 6 put it -- above this pass -- the assertion at the bottom would find a
        // committed row for a file no completed sync ever confirmed, and the next run would plan
        // deletions against it.
        var baseTs = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var dbPath = Path.Combine(_dbDir, "conflict-rename-abort.db");

        // Run 1 establishes an ancestor row for report.txt only.
        CreateFileWithTimestamp(_clientDir, "report.txt", "original", baseTs);
        CreateFileWithTimestamp(_serverDir, "report.txt", "original", baseTs);
        using (var db = new SyncDatabase(dbPath))
            await RunTwoWaySyncAsync(db);

        // Two-sided edit with the server copy newer, so the CLIENT owns the rename.
        CreateFileWithTimestamp(_clientDir, "report.txt", "client edit", baseTs.AddHours(1));
        CreateFileWithTimestamp(_serverDir, "report.txt", "server edit", baseTs.AddHours(2));

        // A brand-new, byte- and mtime-identical pair. It plans as Skip and has no ancestor row
        // from run 1, so it is precisely the row the ancestor-write block would create -- if the
        // block ran. Nothing else in the run can write it.
        CreateFileWithTimestamp(_clientDir, "settled.txt", "same", baseTs);
        CreateFileWithTimestamp(_serverDir, "settled.txt", "same", baseTs);

        // Hold the losing copy open with FileShare.None. Scanning and planning read metadata
        // only, so the plan still says ConflictKeepBoth; File.Move then throws IOException inside
        // ApplyLocalRenames, which is the failure path the client must treat as fatal.
        using (var locked = new FileStream(Path.Combine(_clientDir, "report.txt"),
                   FileMode.Open, FileAccess.Read, FileShare.None))
        using (var db = new SyncDatabase(dbPath))
        {
            Assert.Equal(4, await RunTwoWaySyncExpectingClientAbortAsync(db));
        }

        using (var db = new SyncDatabase(dbPath))
        {
            // The abort happened above the ancestor-write block, so it committed nothing.
            Assert.Null(db.GetRow("settled.txt"));
        }

        // And the loser is still where it was: a failed rename destroys nothing.
        Assert.Equal("client edit", File.ReadAllText(Path.Combine(_clientDir, "report.txt")));
        Assert.Empty(Directory.GetFiles(_clientDir, "report.conflict-*"));
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ConflictKeepBothSyncTests"`
Expected: FAIL. A raw `ConflictKeepBoth` entry matches none of the work filters (`SyncClient.cs:261`, `:328`, `:360`, `:407`; `SyncServer.cs:182`, `:242`, `:308`, `:358`), so nothing is transferred for the conflicted path and **neither copy is touched**. Both peers exit 0 with their own edit still in place, so the first assertion reached is the winner check — `Assert.Equal("server edit", File.ReadAllText(Path.Combine(_clientDir, "report.txt")))`, actual `"client edit"`. (This corrects the previous draft's claim that the loser is "silently overwritten by the winner" — finding #27.) `Conflict_IsLoggedAsAnEncodedConflictDetail` fails earlier still, on `Assert.Single`, because no conflict row is written at all. `ConflictRenameFailure_AbortsAboveTheAncestorWriteBlock` fails on `Assert.Equal(4, ...)` with actual `0`: with no rename pass there is nothing to fail, the run completes, and the ancestor-write block commits the `settled.txt` row the last assertion forbids.

- [ ] **Step 3: Implement**

**Edit 1 — `src/RemoteFileSync/Network/SyncClient.cs:169-183`, the plan summary and serialisation.**

Region ownership: Phase 6 owns `:150-152` (above) and the removal of the old DB-write block at `:185-206`; Phase 5 owns `:209`. Nothing else edits `:169-183`, so this quotes it as it stands on `main`.

Replace exactly:

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

with:

```csharp
        // Every ConflictKeepBoth becomes a frame-free local rename plus one transfer in each
        // direction, and this MUST happen before the plan is serialised: both peers execute the
        // list they are handed, so a conflict the server has to interpret for itself is a desync
        // waiting to happen. sessionStartUtc is the session's single clock read, so the conflict
        // name and the archive session folder carry the same timestamp.
        var conflictExpansion = ConflictKeepBothExecutor.Expand(
            syncPlan, clientManifest, serverManifest, skew, sessionStartUtc, _options.Folder);
        syncPlan = conflictExpansion.Entries;

        // ConflictKeepBoth entries move no bytes, so they are not transfers: counting them would
        // make the GUI's progress bar overshoot and never reach 100%.
        var transferCount = syncPlan.Count(p => p.Action != SyncActionType.Skip
            && p.Action != SyncActionType.DeleteOnServer && p.Action != SyncActionType.DeleteOnClient
            && p.Action != SyncActionType.ConflictKeepBoth);
        var deleteCount = syncPlan.Count(p => p.Action == SyncActionType.DeleteOnServer || p.Action == SyncActionType.DeleteOnClient);
        var skipCount = syncPlan.Count(p => p.Action == SyncActionType.Skip);
        var deleteSummary = deleteCount > 0 ? $", {deleteCount} delete" : "";
        var conflictSummary = conflictExpansion.RenamedTo.Count > 0
            ? $", {conflictExpansion.RenamedTo.Count} conflict" : "";
        _logger.Info($"Sync plan: {transferCount} transfers{deleteSummary}{conflictSummary}, {skipCount} skipped");

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

**Edit 2 — `src/RemoteFileSync/Network/SyncClient.cs`, the conflict rename pass, inserted *above* Phase 6's ancestor-write block. This is the relocation Phase 7 owns.**

Ordering after Phases 1-6 have landed (Phase 6 Task 6.3 Edit 3b put its block immediately above the transfer-loop landmark):

| # | statement | writes to the database? |
|---|---|---|
| 1 | Phase 8's delete guards (`return 4` on failure) | no |
| 2 | Phase 6's ancestor-write block — `if (_db != null && ancestor != null)` → `UpsertSynced` / `MarkSkipped` / `Tombstone` | **yes** |
| 3 | `// 7. Send files to server (SendToServer + ClientOnly)` | no |

Required ordering after Phase 7:

| # | statement | writes to the database? |
|---|---|---|
| 1 | Phase 8's delete guards (`return 4` on failure) | no |
| 2 | **Phase 7's conflict rename pass — `return 4` on failure** | no |
| 3 | Phase 6's ancestor-write block | **yes** |
| 4 | `// 7. Send files to server (SendToServer + ClientOnly)` | no |

Why the block must move: a conflict rename that fails aborts the run with exit 4, and the same rule Phase 6 established for the delete guards applies verbatim here — **no `return 4` may leave committed ancestor rows behind.** Left above the rename pass, an aborted run would still have upserted rows asserting "both sides held this file and agreed" for every `Skip` path, and the next run would plan against state no completed sync ever confirmed. Every `return 4` in `HandleConnectionAsync` must therefore precede statement 3.

The insertion is anchored on the first two lines of Phase 6's block, which are unique in the file, so the rename pass lands above it and the block itself is not retyped.

Replace exactly:

```csharp
        // The ancestor table is written only once both delete guards have passed. This block used
        // to run above them, so an exit-4 abort still committed its rows; the next run then
```

with:

```csharp
        // 6b. Conflict renames. Frame-free, and BEFORE any transfer: this is the only step where
        // the two peers do different work, so it must finish on both sides before a single file
        // frame moves or their transfer sets stop lining up. `archive` is the session's one
        // ArchiveManager (CONTRACT correction #9) — a second instance here would fork the
        // restore point across two session folders and shadow the outer local.
        //
        // This block sits ABOVE the ancestor-write block that follows, deliberately: it can
        // return 4, and an aborted run must not leave committed ancestor rows behind.
        var conflictEntries = syncPlan.Where(p => p.Action == SyncActionType.ConflictKeepBoth).ToList();
        if (conflictEntries.Count > 0)
        {
            var conflictOutcome = ConflictKeepBothExecutor.ApplyLocalRenames(
                syncPlan, ConflictNamer.ClientSide, _options.Folder, archive);

            // Fatal, not skippable: the plan already promised the peer a transfer under the
            // conflict name, and a sender that cannot open its source throws in CompressFile
            // (FileTransfer.cs:39) or ComputeSha256 (:47) — both before the first frame is
            // written at :52 — leaving the peer blocked on a FileStart that never arrives.
            if (conflictOutcome.Failures.Count > 0)
            {
                var msg = $"Refusing to sync: conflict rename failed for {conflictOutcome.Failures.Count} " +
                          $"path(s): {string.Join("; ", conflictOutcome.Failures)}";
                _logger.Error(msg);
                _progress.WriteError(msg, fatal: true);
                return 4;
            }

            // The rename itself succeeded; only the belt-and-braces pre-rename snapshot did not.
            // Warn rather than abort — the bytes are intact under the new name.
            foreach (var path in conflictOutcome.NotArchived)
                _logger.Warning($"Conflict copy of {path} was renamed but could not be archived first.");

            foreach (var entry in conflictEntries)
            {
                if (!ConflictNamer.TryParse(entry.RelativePath, out var original, out var losingSide)) continue;
                _logger.Info($"[!] Conflict on {original}: {losingSide} copy kept as {entry.RelativePath}");
            }
        }

        // The ancestor table is written only once both delete guards have passed. This block used
        // to run above them, so an exit-4 abort still committed its rows; the next run then
```

The two quoted comment lines are re-emitted unchanged at the end of the replacement, so Phase 6's block continues from them verbatim and `// 7. Send files to server` keeps its position directly below it.

**Scope check.** `archive` (Phase 5) is declared at the top of `HandleConnectionAsync`; `syncPlan` is the expanded list assigned by Edit 1; `_options`, `_logger` and `_progress` are fields. `conflictEntries` and `conflictOutcome` are new locals declared only here, and `conflictExpansion` (Edit 1) is still in scope at Edit 3 because both live directly in `HandleConnectionAsync`'s `try` body, not in a nested block.

**Edit 3 — `src/RemoteFileSync/Network/SyncClient.cs:472-474`, the conflict **and resurrection** drains.**

Phase 7 owns both drains, in this one edit block, at this one anchor. No other phase writes to `file_versions`; a second drain added elsewhere would double-log.

Anchored at the top of the `// 11. Exchange SyncComplete` landmark and inserted above it.

**How `ResurrectionInfo` maps onto `ConflictDetail`'s four numeric columns.** `ResurrectionInfo(string Path, bool KeptClientCopy, long KeptSize, long KeptMtimeTicks)` carries only the *kept* side. The losing side of a resurrection is a **deletion**, so it has no size and no mtime to record — there is no file left to measure. The two columns belonging to the deleted side are therefore written as **`0`**, and `0` is unambiguous here: a real surviving file has a non-zero mtime tick count, so `ClientMtimeTicks == 0` reads as "the client's copy was the one that had been deleted" and `ServerMtimeTicks == 0` as the mirror. `RenamedTo` is `null` — a resurrection renames nothing.

`ResurrectionInfo` is **not** widened; Phase 2's record stands as written. Phase 9's report distinguishes conflict rows from resurrection rows by the `file_versions` action column (Phase 4 Task: `LogConflictAndLogResurrection_AreSeparatedByActionNotByDetail`), not by inspecting the detail, so it never mistakes a zeroed column for a measured one.

Replace exactly:

```csharp
        // 11. Exchange SyncComplete
        sw.Stop();
        int exitCode = (skippedFiles > 0 || stopped) ? 1 : 0;
```

with:

```csharp
        // 10b. Record the conflicts and resurrections, now that both transfer phases have
        // completed. Draining here rather than at plan time means a run that aborts mid-transfer
        // records nothing, so the review report can never claim an outcome that was not actually
        // executed (CONTRACT correction #1). Both drains live here, together: file_versions has
        // exactly one writer.
        //
        // The detail column is an ENCODED ConflictDetail, never English: Phase 9's report decodes
        // it to print both sides' size and mtime, and Decode returns null on anything else.
        if (_db != null)
        {
            foreach (var conflict in planResult.Conflicts)
            {
                conflictExpansion.RenamedTo.TryGetValue(conflict.Path, out var renamedTo);
                _db.LogConflict(conflict.Path, sessionId, new ConflictDetail(
                    conflict.ClientSize, conflict.ClientMtimeTicks,
                    conflict.ServerSize, conflict.ServerMtimeTicks,
                    renamedTo).Encode());
            }

            // ResurrectionInfo carries only the KEPT side. The losing side was deleted, so it has
            // no size and no mtime to record and its two columns are written as 0 — which is
            // unambiguous, because a surviving file always has a non-zero mtime tick count. A
            // zero mtime column therefore reads as "this side is the one that had been deleted".
            // RenamedTo is null: a resurrection renames nothing.
            //
            // Phase 9 tells these rows apart from conflict rows by the file_versions action
            // column, not by the detail, so a zeroed column is never read as a measured one.
            foreach (var resurrection in planResult.Resurrections)
            {
                var detail = resurrection.KeptClientCopy
                    ? new ConflictDetail(resurrection.KeptSize, resurrection.KeptMtimeTicks, 0, 0, null)
                    : new ConflictDetail(0, 0, resurrection.KeptSize, resurrection.KeptMtimeTicks, null);
                _db.LogResurrection(resurrection.Path, sessionId, detail.Encode());
            }
        }

        // 11. Exchange SyncComplete
        sw.Stop();
        int exitCode = (skippedFiles > 0 || stopped) ? 1 : 0;
```

**Edit 4 — `src/RemoteFileSync/Network/SyncServer.cs:180-182`, the mirrored rename pass.**

Region ownership: Phase 5 owns `:173`, Phase 3 owns `:132-152`, Phase 8 owns `:226-240`. Nothing else edits `:180-182`. Again anchored on the landmark comment and inserted above it.

Replace exactly:

```csharp
        // 6. Receive files from client (SendToServer + ClientOnly)
        var toReceive = syncPlan.Where(p =>
            p.Action == SyncActionType.SendToServer || p.Action == SyncActionType.ClientOnly).ToList();
```

with:

```csharp
        // 5a. Conflict renames. Mirror of the client's step 6b: frame-free, and completed before
        // the first file frame so both peers' transfer sets stay aligned.
        var conflictEntries = syncPlan.Where(p => p.Action == SyncActionType.ConflictKeepBoth).ToList();
        if (conflictEntries.Count > 0)
        {
            // The server only ever sends in the TwoWay branch below, so a conflict from a Push or
            // Pull peer would strand the renamed loser here with no phase to carry it back.
            if (mode != SyncMode.TwoWay)
            {
                var msg = $"Rejecting sync plan: {conflictEntries.Count} conflict action(s) from a " +
                          $"{mode} peer, which has no phase to receive the renamed copy.";
                _logger.Error(msg);
                _progress.WriteError(msg, fatal: true);
                return 4;
            }

            // Landing a conflict name on top of an existing local file removes that file. The
            // plan comes from a peer we do not authenticate, so a plan whose conflict names all
            // point at real local files would be a way to empty this folder without ever sending
            // a DeleteFile frame — bypassing both the negotiated delete flag and the budget the
            // server enforces on DeleteOnServer. Hold squatter removal to the same two rules.
            int occupied = ConflictKeepBothExecutor.CountOccupiedTargets(
                syncPlan, ConflictNamer.ServerSide, _options.Folder);
            if (occupied > 0 && !deleteEnabled)
            {
                var msg = $"Rejecting sync plan: {occupied} conflict name(s) would replace existing " +
                          "local files, but the peer did not negotiate deletion.";
                _logger.Error(msg);
                _progress.WriteError(msg, fatal: true);
                return 4;
            }
            // The percentage bound is DeleteBudget.Within (Phase 8), not arithmetic written out
            // again here. Squatter removal is a deletion by another name, so it must obey the
            // byte-identical rule the DeleteOnServer guard obeys — including the two edge cases
            // a hand-rolled `pct > max` gets wrong: a zero denominator refuses rather than
            // disarming, and a population below MinTrackedFilesForDeleteGuard is exempt because
            // the percentage there is noise.
            if (occupied > 0 && !_options.ForceDelete
                && !DeleteBudget.Within(occupied, serverManifest.Count, _options.MaxDeletePercent))
            {
                var msg = $"Rejecting sync plan: peer's conflict names would replace {occupied} of " +
                          $"{serverManifest.Count} local files, exceeding this server's " +
                          $"--max-delete-percent {_options.MaxDeletePercent}.";
                _logger.Error(msg);
                _progress.WriteError(msg, fatal: true);
                return 4;
            }

            // `archive` is the session's one ArchiveManager, created by the ArchiveManager phase
            // at SyncServer.cs:173. Constructing another here would shadow it and split this
            // run's restore point across two session folders.
            var conflictOutcome = ConflictKeepBothExecutor.ApplyLocalRenames(
                syncPlan, ConflictNamer.ServerSide, _options.Folder, archive);

            // See SyncClient step 6b: the plan already promised a transfer under the conflict
            // name, so a half-applied rename hangs the peer rather than merely skipping a file.
            if (conflictOutcome.Failures.Count > 0)
            {
                var msg = $"Refusing to sync: conflict rename failed for {conflictOutcome.Failures.Count} " +
                          $"path(s): {string.Join("; ", conflictOutcome.Failures)}";
                _logger.Error(msg);
                _progress.WriteError(msg, fatal: true);
                return 4;
            }

            foreach (var path in conflictOutcome.NotArchived)
                _logger.Warning($"Conflict copy of {path} was renamed but could not be archived first.");

            if (conflictOutcome.Renamed > 0)
                _logger.Info($"Conflict: {conflictOutcome.Renamed} losing copy/copies renamed and kept.");
        }

        // 6. Receive files from client (SendToServer + ClientOnly)
        var toReceive = syncPlan.Where(p =>
            p.Action == SyncActionType.SendToServer || p.Action == SyncActionType.ClientOnly).ToList();
```

**Usings.** `SyncServer.cs` already has `using RemoteFileSync.Backup;` (line 4) and `using RemoteFileSync.Sync;` (line 10), so both `ConflictKeepBothExecutor` and Phase 8's `DeleteBudget` — same namespace — resolve without a new using. `SyncClient.cs` has `using RemoteFileSync.Backup;` (line 3), `using RemoteFileSync.State;` (line 8) and `using RemoteFileSync.Sync;` (line 9), so `ConflictDetail` resolves too. No using changes are required in either file.

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ConflictKeepBothSyncTests"`
Expected: PASS — `TwoWayConflict_ClientCopyLosesWhenServerCopyIsNewer`, `TwoWayConflict_ServerCopyLosesWhenClientCopyIsNewer`, `ConflictCopy_IsNotResyncedByTheNextScan`, `Conflict_IsLoggedAsAnEncodedConflictDetail`, `ConflictRenameFailure_AbortsAboveTheAncestorWriteBlock`.

---

### Phase 7 commit

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
peer-specific decision during any message-bearing phase. The loser is renamed
to the contract format and archived through the session's single ArchiveManager
under ArchiveReason.Conflict, before the first file frame moves; a failed rename
aborts with exit 4 rather than leaving the peer blocked on a FileStart that
never arrives.

Never delete on an unproven archive: ArchiveManager.Archive returns false
without throwing when PathGuard fails closed on transient IO, so a file
occupying the conflict name is skipped and reported instead of destroyed.

The server refuses conflict actions from a non-TwoWay peer, and holds conflict
names that would replace existing local files to the same deleteEnabled flag
and --max-delete-percent budget it applies to DeleteOnServer.

Conflicts and resurrections are both logged after both transfer phases complete,
as an encoded ConflictDetail rather than free-form text, so the review report can
render both sides' size and mtime. A resurrection's deleted side has nothing to
measure, so its two columns are 0.

The conflict rename pass is placed above the ancestor-write block so that every
exit-4 abort precedes any database mutation.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
git push -u origin feat/deletion-sync-ancestor-merge
```

**Verification before commit:**

```bash
dotnet build -c Release
dotnet test -c Release
```

Expected: 0 build errors, and no previously-green test turns red.

Why the existing suites are unaffected:
- `SyncEngineTests`, `DeleteSyncTests`, `DatabaseDeleteSyncTests`, `DeleteThresholdTests` and `EndToEndTests` exercise plans containing no `ConflictKeepBoth` entries. For those, `Expand` is an identity transform whose `RenamedTo` map is empty, `conflictEntries` is empty on both peers, and the blocks added by Edits 2 and 4 are skipped in their entirety. Edit 3's drain runs whenever `_db != null`, but both `planResult.Conflicts` and `planResult.Resurrections` are empty in those suites, so it writes no rows — and where resurrections *do* occur, `GetSessionResurrections` moves from empty to populated, which no pre-existing test asserts against.
- `BackupManagerTests` is untouched: this phase adds no `BackupManager` call site and removes none — the `BackupManager`→`ArchiveManager` swap is Phase 5's, already landed.
- No `Bidirectional` assignment is added or changed anywhere in `src/` or `tests/` (Phase 1's exclusive region), and no `bidirectional` local is read on the server (Phase 3 deleted it).

Manual spot-check that the build is consistent with the ownership table:

```bash
# Exactly one ArchiveManager construction per peer method — Phase 5's.
grep -n "new ArchiveManager" src/RemoteFileSync/Network/SyncClient.cs \
                            src/RemoteFileSync/Network/SyncServer.cs
# Phase 3 removed the server's `bidirectional`; this must print nothing.
grep -n "bidirectional" src/RemoteFileSync/Network/SyncServer.cs
# Every LogConflict / LogResurrection call must pass an encoded detail, never a
# string literal, and both must appear exactly once, in the same block.
grep -n "LogConflict\|LogResurrection" src/RemoteFileSync/Network/SyncClient.cs
# The rename pass must precede the ancestor-write block: the first line number
# must be smaller than the second.
grep -n "ConflictKeepBothExecutor.ApplyLocalRenames\|ancestor != null" \
     src/RemoteFileSync/Network/SyncClient.cs
# The squatter guard must use the shared helper, not its own arithmetic.
grep -n "DeleteBudget.Within" src/RemoteFileSync/Network/SyncServer.cs
```

---

## Phase 8: Mode dispatch, Pull execution, reworked delete guards, and the no-ancestor gate

**Goal:** Make `SyncMode` actually drive both session loops — so a Pull run stops uploading stale client copies over the authoritative server and its `DeleteOnClient` actions execute instead of being planned and silently dropped — replace the two delete guards that go inert exactly when they matter, and put the no-ancestor safety gate inside `SyncClient.RunAsync` where it fires before the socket opens and where a test that constructs `SyncClient` directly can reach it.

**Files:**
- Create: `src/RemoteFileSync/Sync/ModeGate.cs`
- Create: `src/RemoteFileSync/Sync/DeleteBudget.cs`
- Modify: `src/RemoteFileSync/Network/SyncClient.cs` (field `:21`, ctor `:23-35`, `RunAsync` `:37-40` — split into a new `RunAsync` wrapper plus `RunSessionAsync` holding the existing body — and `:79-81`, `:73`, `:119-120`, guard `:233-256`, send gate `:259-261`, delete-send gate `:326`, receive gate `:356-357`, delete-receive gate `:404-405`, new private method before the class brace at `:503-504`)
- Modify: `src/RemoteFileSync/Network/SyncServer.cs` (`:168-171`, `:180-182`, `:221-241`, `:304-305`, `:355-356`, Phase 7's conflict-squatter percentage guard, new private method before the class brace at `:393-394`)
- Modify: `src/RemoteFileSync/Program.cs:55-78` (the client branch of `Main` — the handoff only)
- Test: `tests/RemoteFileSync.Tests/Sync/ModeGateTests.cs` (create)
- Test: `tests/RemoteFileSync.Tests/Sync/DeleteBudgetTests.cs` (create)
- Test: `tests/RemoteFileSync.Tests/Network/SyncClientGateTests.cs` (create)

Line numbers are positions in the files as they stand on `main` today (verified by reading them). Phases 3, 5, 6 and 7 land first and shift them; **every "Replace exactly" block below anchors on text, not on a line number.** Re-derive positions with a grep before applying, and apply the edits within a file in descending order.

**This phase does NOT touch, and must not re-apply:**

| Region | Owner | What this phase does instead |
|---|---|---|
| `SyncClient.cs:89-113`, `SyncServer.cs:132-152` (v3 handshake) | Phase 3 | consumes the locals it leaves |
| `SyncClient.cs:209`, `:370-371`, `:425`, `SyncServer.cs:173`, `:193`, `:260` (`BackupManager` → `ArchiveManager`, prune) | Phase 5 | consumes the single `archive` local; declaring a second one is CS0128 |
| `SyncClient.cs:150-152` (the `ComputePlan` call site) and `:185-206` (the DB-write block, which Phase 6 relocates below the delete guards) | Phase 6 | consumes both as already applied |
| Every `Bidirectional =` assignment in `src/` and `tests/` | Phase 1 | reads only |
| Every file under `tests/**/Integration/` | Phase 10 | **this phase adds no integration tests** |

---

### Interfaces

**Consumes (Phase 1)** — `SyncMode { Push = 1, Pull = 2, TwoWay = 3 }`; `SyncOptions.Mode`, `.MirrorDeletes`, `.MaxDeletePercent`, `.ForceDelete`, `.DeleteEnabled`, `.MinTrackedFilesForDeleteGuard`. `SyncOptions.Bidirectional` is a getter-only shim; this phase removes the last four *reads* of it in `src/` (`SyncClient.cs:73`, `:119`, `:357`, `:405`), so after Phase 8 no production file mentions it at all.

**Consumes (Phase 3)** — from `SyncServer.HandleConnectionAsync`: the method-scope locals `SyncMode mode` (clamped through a `switch`, never cast), `bool deleteEnabled`, `bool mirrorDeletes`, decoded from the v3 handshake. **Phase 3 deletes the `bool bidirectional` local outright** and, in the same commit, mechanically rewrites its two remaining consumers (server step 8 and step 9) to `mode == SyncMode.TwoWay` — a compile-preserving rename with no behaviour change. So `bidirectional` does not exist anywhere in `src/` when this phase starts, there is no dead declaration and no CS0219 to weigh, and **this phase's steps 3i and 3j must quote Phase 3's post-edit text** (Phase 3 steps 3e and 3f), not the text on `main`. What this phase does at those two sites is the *semantic* widening: `mode == SyncMode.TwoWay` becomes `ModeGate.ServerToClient(mode)`, which additionally admits Pull. From `SyncClient.HandleConnectionAsync`: the method-scope local `ClockSkew skew`. This phase never reads `skew` — Phase 6 already passes it to `ComputePlan`. Note Phase 3's collision warning: `SyncClient.cs` already has a block-scoped `string mode` in the DB-session block, so this phase must not introduce a method-scope `mode` in `SyncClient` (CS0136); step 3c below renames that block-scoped local to `sessionMode`, which removes the hazard rather than working around it.

**Consumes (Phase 4)** — `PairMarker.PathFor/Exists/Write`. Also the pre-existing statics `SyncDatabase.GetDbPath`, `SyncDatabase.DefaultBaseDir`, `SyncDatabase.MigrateFromBinary`, unchanged by Phase 4.

**Consumes (Phase 5)** — the one `archive` local (`ArchiveManager`) constructed at the top of each `HandleConnectionAsync`. **Reused, never redeclared.**

**Consumes (Phase 6)** — `ComputePlan` already returns `PlanResult`; the call site and the `syncPlan` local already exist in their post-Phase-6 form, and the `if (_db != null)` mutation block already sits *below* the delete guards. Consequently `previousState` (the `SyncStateManager` binary-state table) no longer feeds planning.

**Consumes (Phase 7)** — the `ConflictKeepBoth` execution blocks on both sides, already keyed on `mode`; and, in `SyncServer`, the conflict-squatter percentage guard. Task 8.2 step 3g rewrites **only** that guard's percentage arithmetic to call `DeleteBudget`, which is the one sanctioned edit into Phase 7's region: Phase 7 lands first and had no shared helper to call, and leaving a sixth private copy of the arithmetic is exactly the divergence `DeleteBudget` exists to prevent. Every other line Phase 7 wrote — `CountOccupiedTargets`, the `mode != SyncMode.TwoWay` rejection, the `!deleteEnabled` rejection, `ApplyLocalRenames` and the failure handling — is left untouched.

**Produces — CONTRACT EXTENSIONS, declared here rather than invented silently.** None of these appear in CONTRACT.md; each exists because the contract's own requirements have no seam without it.

1. `public static class RemoteFileSync.Sync.ModeGate` with `public static bool ClientToServer(SyncMode mode)` and `public static bool ServerToClient(SyncMode mode)`. The contract requires both peers to gate on mode; two hand-written predicates in two files is exactly the "one side waits for a frame the other never writes" bug, so the predicate is a single shared function with a test.
2. `public static class RemoteFileSync.Sync.DeleteBudget` with `public static bool Within(int deletes, int destinationCount, int maxDeletePercent)` — **the single named, shared, directly unit-testable home for the blast-radius arithmetic.** It is not an inline lambda or a copy-pasted expression: **every** percentage bound in the solution routes through it, namely (a) the client's `DeleteOnServer` bound, (b) the client's `DeleteOnClient` bound, (c) the server's `DeleteOnServer` bound, (d) the server's `DeleteOnClient` bound, and (e) **Phase 7's conflict-squatter guard in `SyncServer`**, which this phase retro-fits in step 3g of Task 8.2 — Phase 7 lands first and necessarily hand-rolled the arithmetic because `DeleteBudget` did not exist yet. Three properties are fixed once, for all five call sites: **a zero denominator refuses rather than passes** (the defect that disarmed the client guard on a wiped database — `Within` returns `false`, never `true`, when `destinationCount <= 0` and `deletes > 0`); the below-floor exemption is applied identically everywhere; and the boundary is `<=`, so a plan exactly at `--max-delete-percent` is allowed. `DeleteBudgetTests` is the only place these need proving.
3. `public static bool RemoteFileSync.Network.SyncClient.PairStateLost(string dbPath)` — the no-ancestor predicate, public and `static` so it is testable without a socket and without an instance.
4. `SyncClient`'s constructor gains a trailing optional parameter `string? dbPath = null`, and the field `_db` loses `readonly`. **Required by CONTRACT.md correction 7.** `new SyncDatabase(path)` *creates* the file it is given (`src/RemoteFileSync/State/SyncDatabase.cs:41-50`), so a gate keyed on "the database is absent" can never fire if anything opened the database first. Today `Program.cs:65` opens it before constructing the client. Ownership of the client's database therefore moves into `SyncClient`: `Program` passes a path, the gate runs, and only then is the database opened. Every existing call site passes `db:` by name and is unaffected.

   **The gate seam is exactly these two things and nothing else: the `string? dbPath = null` constructor parameter, and `SyncClient.PairStateLost(string)`.** `SyncDatabase` gains no new surface in this phase. In particular **`SyncDatabase.DatabasePath` and `SyncDatabase.ExistedBeforeOpen` do not exist** — they are not added here, not by Phase 4, and not by any other phase, and no file in `src/` or `tests/` may reference either name. An "did it exist before I opened it?" property on `SyncDatabase` cannot work, because by the time such a property could be read the constructor has already created the file; the gate has to run *before* any `SyncDatabase` exists, which is why it is a static predicate over a path.
5. `private bool WithinDeleteBudget(int deletes, int destinationCount, string destinationLabel)` on `SyncClient` and on `SyncServer` — a thin per-side wrapper that adds the operator-facing message and the logging only. The message wording differs per side; the arithmetic does not, and neither wrapper re-implements any of it.

Note on CONTRACT.md's row "*`Program.ParseArgs` / `PrintUsage` — Phase 8 adds only the `PairMarker` write, nothing else*": after correction 7 the marker write is no longer in `Program` at all. `Program`'s client branch changes only to stop opening the database and to hand over the path. `ParseArgs` and `PrintUsage` are not touched.

---

### Task 8.1: `ModeGate`, and gating all four loop pairs on both peers

Four transfer/deletion loops run in mirrored pairs. Each pair must be gated on the **same** predicate, or one peer blocks on a frame the other will never write and the session dies at the six-hour timeout.

| what moves | client loop | server loop | predicate |
|---|---|---|---|
| files up | send (step 7) | receive (step 6) | `ModeGate.ClientToServer(mode)` |
| deletions on server | send (step 8) | receive (step 7) | `deleteEnabled && ClientToServer(mode)` |
| files down | receive (step 9) | send (step 8) | `ModeGate.ServerToClient(mode)` |
| deletions on client | receive (step 10) | send (step 9) | `deleteEnabled && ServerToClient(mode)` |

- [ ] **Step 1: Write the failing test**

Create `tests/RemoteFileSync.Tests/Sync/ModeGateTests.cs`:

```csharp
using RemoteFileSync.Models;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

/// <summary>
/// Push and Pull used to be flattened into "not bidirectional", which made Pull permit uploads
/// and forbid downloads — the exact inversion of what the mode means.
/// </summary>
public class ModeGateTests
{
    [Theory]
    [InlineData(SyncMode.Push,   true,  false)]
    [InlineData(SyncMode.Pull,   false, true)]
    [InlineData(SyncMode.TwoWay, true,  true)]
    public void EachMode_PermitsExactlyTheDirectionsItsNameClaims(
        SyncMode mode, bool clientToServer, bool serverToClient)
    {
        Assert.Equal(clientToServer, ModeGate.ClientToServer(mode));
        Assert.Equal(serverToClient, ModeGate.ServerToClient(mode));
    }

    [Fact]
    public void Pull_PermitsTheDownwardDirection_WhichTheBidirectionalPredicateDenied()
    {
        // The old gate was `_options.Bidirectional`, false in Pull mode. A Pull run therefore
        // planned DeleteOnClient, the server sent DeleteFile for each, and the client never
        // entered the loop that reads them.
        Assert.True(ModeGate.ServerToClient(SyncMode.Pull));
        Assert.False(new SyncOptions { Mode = SyncMode.Pull }.Bidirectional);
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ModeGateTests"`
Expected: FAIL — the test project does not compile: `error CS0103: The name 'ModeGate' does not exist in the current context`.

- [ ] **Step 3: Implement**

**3a — create `src/RemoteFileSync/Sync/ModeGate.cs`:**

```csharp
using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

/// <summary>
/// Which directions a sync mode permits. Both peers derive their loop predicates from here
/// rather than each writing its own: the client's send loop and the server's receive loop are
/// two halves of one framed conversation, and if they disagree by even one entry the stream
/// desynchronises or one side blocks until the session timeout.
/// </summary>
public static class ModeGate
{
    /// <summary>Client to server: file uploads and DeleteOnServer. Pull is server-authoritative.</summary>
    public static bool ClientToServer(SyncMode mode) => mode != SyncMode.Pull;

    /// <summary>Server to client: file downloads and DeleteOnClient. Push is client-authoritative.</summary>
    public static bool ServerToClient(SyncMode mode) => mode != SyncMode.Push;
}
```

**3b — `SyncClient.cs:73`.** Replace exactly:

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

**3c — `SyncClient.cs:119-120`.** Replace exactly:

```csharp
            var mode = $"{(_options.Bidirectional ? "bidi" : "uni")}+delete";
            sessionId = _db.StartSession(mode, _options.Folder, _options.Host!, _options.Port);
```

with:

```csharp
            // The session label is what the review report and the session history render, so it
            // must name the real mode: "uni" covered Push and Pull alike, making a run that
            // deleted local files indistinguishable from one that only uploaded.
            var sessionMode = $"{_options.Mode.ToString().ToLowerInvariant()}+delete"
                            + (_options.MirrorDeletes ? "+mirror" : "");
            sessionId = _db.StartSession(sessionMode, _options.Folder, _options.Host!, _options.Port);
```

**3d — `SyncClient.cs:259-261`.** Replace exactly:

```csharp
        // 7. Send files to server (SendToServer + ClientOnly)
        var toSend = syncPlan.Where(p =>
            p.Action == SyncActionType.SendToServer || p.Action == SyncActionType.ClientOnly).ToList();
```

with:

```csharp
        // 7. Send files to server (SendToServer + ClientOnly). Pull never uploads: the server is
        // authoritative, so a stale client copy must not be pushed back over it. Gated here as
        // well as in the planner because the plan travels to the peer, which sizes its receive
        // loop from it — both halves must be gated on the same predicate.
        var toSend = ModeGate.ClientToServer(_options.Mode)
            ? syncPlan.Where(p =>
                p.Action == SyncActionType.SendToServer || p.Action == SyncActionType.ClientOnly).ToList()
            : new List<SyncPlanEntry>();
```

**3e — `SyncClient.cs:325-326`.** Replace exactly:

```csharp
        // 8. Deletion Phase (Server): Send DeleteFile for DeleteOnServer actions
        if (_options.DeleteEnabled)
```

with:

```csharp
        // 8. Deletion Phase (Server): Send DeleteFile for DeleteOnServer actions. Pull never
        // deletes on the server; the server's matching receive loop is gated identically, so a
        // plan that somehow carried DeleteOnServer in Pull mode is dropped by both peers rather
        // than by one of them.
        if (_options.DeleteEnabled && ModeGate.ClientToServer(_options.Mode))
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
        // frame that never arrives until the session times out.
        if (ModeGate.ServerToClient(_options.Mode))
```

**3g — `SyncClient.cs:404-405`.** Replace exactly:

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
        if (_options.DeleteEnabled && ModeGate.ServerToClient(_options.Mode))
```

**3h — `SyncServer.cs:180-182`.** Replace exactly:

```csharp
        // 6. Receive files from client (SendToServer + ClientOnly)
        var toReceive = syncPlan.Where(p =>
            p.Action == SyncActionType.SendToServer || p.Action == SyncActionType.ClientOnly).ToList();
```

with:

```csharp
        // 6. Receive files from client (SendToServer + ClientOnly). Mirror of the client's send
        // gate: the peer is unauthenticated, so the plan it sent is not trusted to be internally
        // consistent with the mode it declared in the handshake.
        var toReceive = ModeGate.ClientToServer(mode)
            ? syncPlan.Where(p =>
                p.Action == SyncActionType.SendToServer || p.Action == SyncActionType.ClientOnly).ToList()
            : new List<SyncPlanEntry>();
```

**3i — `SyncServer.cs`, server step 8.** Anchor on the text **Phase 3 step 3e** left behind (not the `main` text, which no longer exists). Replace exactly:

```csharp
        // 8. Send files to client (SendToClient + ServerOnly). Two-way only at this stage; the
        // mode-dispatch phase widens the condition to admit Pull, which also writes to the
        // client. Behaviour is unchanged here — this is the rename forced by dropping the
        // `bidirectional` local in favour of `mode`.
        if (mode == SyncMode.TwoWay)
```

with:

```csharp
        // 8. Send files to client (SendToClient + ServerOnly). Mirrors the client's receive gate;
        // both sides must derive it from `mode` or the frame counts diverge.
        if (ModeGate.ServerToClient(mode))
```

**3j — `SyncServer.cs`, server step 9.** Anchor on the text **Phase 3 step 3f** left behind. Phase 3 rewrote only the `if` line and left the step-9 comment above it untouched, so the anchor is the two lines below. Replace exactly:

```csharp
        // 9. Deletion Phase (Client): Send DeleteFile for DeleteOnClient actions
        if (deleteEnabled && mode == SyncMode.TwoWay)
```

with:

```csharp
        // 9. Deletion Phase (Client): Send DeleteFile for DeleteOnClient actions. Must use the
        // identical predicate to the client's receive gate, or one side blocks on the other.
        if (deleteEnabled && ModeGate.ServerToClient(mode))
```

(The server's step-7 receive gate is rewritten in Task 8.2, which also removes the guard nested inside it.)

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ModeGateTests"`
Expected: PASS — `EachMode_PermitsExactlyTheDirectionsItsNameClaims` (all three `InlineData` rows) and `Pull_PermitsTheDownwardDirection_WhichTheBidirectionalPredicateDenied`.

**What this task changes that no test here can prove.** That a real Pull session now downloads, refuses to upload, and applies `DeleteOnClient` end to end is only observable with two live peers. CONTRACT.md assigns every file under `tests/**/Integration/` to Phase 10, so Phase 10 owns that proof and must add: a Pull run that leaves a client-only file un-uploaded; a Pull run that deletes a client file the server no longer has; and a Push run that writes nothing to the client. Without those, this task's behaviour is covered only by the build.

---

### Task 8.2: `DeleteBudget`, both delete guards rebuilt on destination-side counts, and every other percentage bound routed through the same helper

Two guards, both inert exactly when they matter. The client divided by `_db.GetAllTrackedFiles().Count(f => f.Status == "exists")` — **0** on a wiped or never-built database, and `0 >= MinTrackedFilesForDeleteGuard` is false, so the whole check was skipped in the one situation it exists for. The server counted only `DeleteOnServer` — **0** in Pull mode, where every deletion is a `DeleteOnClient` the server itself originates, so nothing bounded them at all.

- [ ] **Step 1: Write the failing test**

Create `tests/RemoteFileSync.Tests/Sync/DeleteBudgetTests.cs`:

```csharp
using RemoteFileSync.Models;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

/// <summary>
/// The blast-radius bound for propagated deletions. Shared by both peers so they cannot
/// disagree about what is acceptable.
/// </summary>
public class DeleteBudgetTests
{
    [Fact]
    public void ZeroDestinationCount_RefusesRatherThanDisarming()
    {
        // The old client guard divided by the tracked-row count and skipped itself when that was
        // below the floor. A wiped database has zero rows, so the guard went inert precisely
        // when state loss had made every peer-only file look like a deletion.
        Assert.False(DeleteBudget.Within(deletes: 20, destinationCount: 0, maxDeletePercent: 25));
    }

    [Fact]
    public void NoDeletes_IsAlwaysWithinBudget()
    {
        Assert.True(DeleteBudget.Within(deletes: 0, destinationCount: 0, maxDeletePercent: 0));
        Assert.True(DeleteBudget.Within(deletes: 0, destinationCount: 5000, maxDeletePercent: 0));
    }

    [Fact]
    public void BelowTheFloor_ThePercentageIsNoiseAndTheGuardIsExempt()
    {
        int belowFloor = SyncOptions.MinTrackedFilesForDeleteGuard - 1;
        Assert.True(DeleteBudget.Within(belowFloor, belowFloor, maxDeletePercent: 25));
    }

    [Fact]
    public void AtTheFloor_AWholesaleDeletionIsRefused()
    {
        int atFloor = SyncOptions.MinTrackedFilesForDeleteGuard;
        Assert.False(DeleteBudget.Within(atFloor, atFloor, maxDeletePercent: 25));
    }

    [Theory]
    [InlineData(2, 20, 25, true)]    // 10% — ordinary
    [InlineData(5, 20, 25, true)]    // exactly at the limit — allowed
    [InlineData(6, 20, 25, false)]   // 30% — over
    [InlineData(20, 20, 100, true)]  // 100 disables the guard
    public void PercentageIsBoundedByTheDestinationPopulation(
        int deletes, int destinationCount, int maxDeletePercent, bool expected)
    {
        Assert.Equal(expected, DeleteBudget.Within(deletes, destinationCount, maxDeletePercent));
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~DeleteBudgetTests"`
Expected: FAIL — the test project does not compile: `error CS0103: The name 'DeleteBudget' does not exist in the current context`.

- [ ] **Step 3: Implement**

**3a — create `src/RemoteFileSync/Sync/DeleteBudget.cs`:**

```csharp
using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

/// <summary>
/// Blast-radius bound for propagated deletions, expressed once so the two peers cannot apply
/// different rules to the same plan.
/// </summary>
public static class DeleteBudget
{
    /// <summary>
    /// True when <paramref name="deletes"/> is an acceptable share of
    /// <paramref name="destinationCount"/> — the live file count on the side being deleted FROM,
    /// which is the population actually at risk.
    /// </summary>
    public static bool Within(int deletes, int destinationCount, int maxDeletePercent)
    {
        if (deletes <= 0) return true;

        // A destination we cannot count is not a destination we may empty. Deleting N files from
        // a side that reports zero files is arithmetically impossible, so a zero here means the
        // count is missing or the peer is lying — never that the deletion is small.
        if (destinationCount <= 0) return false;

        // Below the floor the percentage is noise: 1 of 2 files is 50% but entirely ordinary,
        // and a guard that fires on ordinary edits trains users into --force-delete by reflex,
        // disabling it for the run that actually needed it.
        if (destinationCount < SyncOptions.MinTrackedFilesForDeleteGuard) return true;

        return deletes * 100.0 / destinationCount <= maxDeletePercent;
    }
}
```

**3b — `SyncClient.cs:233-256`.** Replace exactly:

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
                // Bound each direction separately against the manifest of the side being deleted
                // FROM. The old denominator was the tracked-row count, which is 0 on a wiped or
                // never-built database — the guard then divided into nothing and skipped itself
                // in exactly the situation state loss creates, where every peer-only file is
                // indistinguishable from one the peer deleted.
                //
                // Each peer is authoritative for the deletions applied to itself: clientManifest
                // is this client's own scan, so an inflated peer manifest cannot relax the
                // DeleteOnClient bound. serverManifest arrived over the wire and is advisory —
                // the server re-checks DeleteOnServer against its own scan before applying it.
                int serverDeletes = syncPlan.Count(p => p.Action == SyncActionType.DeleteOnServer);
                int clientDeletes = syncPlan.Count(p => p.Action == SyncActionType.DeleteOnClient);

                if (!WithinDeleteBudget(serverDeletes, serverManifest.Count, "server")) return 4;
                if (!WithinDeleteBudget(clientDeletes, clientManifest.Count, "client")) return 4;
            }
```

Both `return 4` statements are inside the existing `try`, so the `finally` still calls `CompleteSession` and no session row is leaked.

**3c — `SyncClient.cs`, new private method immediately before the closing brace of the class.** Replace exactly:

```csharp
                _logger.Debug($"Sync session {sessionId} completed (exit code {finalExitCode})");
            }
        }
    }
}
```

with:

```csharp
                _logger.Debug($"Sync session {sessionId} completed (exit code {finalExitCode})");
            }
        }
    }

    /// <summary>
    /// Percentage bound for one direction, plus the operator-facing message. The arithmetic
    /// lives in <see cref="DeleteBudget"/> so this peer and its peer apply the same rule.
    /// </summary>
    private bool WithinDeleteBudget(int deletes, int destinationCount, string destinationLabel)
    {
        if (DeleteBudget.Within(deletes, destinationCount, _options.MaxDeletePercent)) return true;

        var msg = $"Refusing to sync: {deletes} of {destinationCount} file(s) on the " +
                  $"{destinationLabel} would be deleted, exceeding --max-delete-percent " +
                  $"{_options.MaxDeletePercent}. Check that --folder on both sides points where " +
                  "you expect, and that the sync database was not moved or deleted. If this is " +
                  "intentional, re-run with --force-delete on BOTH sides — the peer enforces its " +
                  "own copy of this bound.";
        _logger.Error(msg);
        _progress.WriteError(msg, fatal: true);
        return false;
    }
}
```

**3d — `SyncServer.cs:168-171`.** The server's guard moves ahead of the receive loop, so a refusal costs no writes on either side. Replace exactly:

```csharp
        // 5. Receive sync plan
        var (pType, pData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var syncPlan = ProtocolHandler.DeserializeSyncPlan(pData);
        _logger.Info($"Sync plan: {syncPlan.Count} actions");
```

with:

```csharp
        // 5. Receive sync plan
        var (pType, pData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var syncPlan = ProtocolHandler.DeserializeSyncPlan(pData);
        _logger.Info($"Sync plan: {syncPlan.Count} actions");

        // The plan arrives from a peer we do not authenticate, so the server enforces its own
        // bound instead of trusting the client's. BOTH directions are bounded: in Pull mode every
        // deletion is a DeleteOnClient the server itself originates, and the previous guard
        // counted only DeleteOnServer, so nothing checked those at all. Checked here, before the
        // receive loop, so a refusal happens before any file is written or removed.
        if (deleteEnabled && !_options.ForceDelete)
        {
            int plannedServerDeletes = syncPlan.Count(p => p.Action == SyncActionType.DeleteOnServer);
            int plannedClientDeletes = syncPlan.Count(p => p.Action == SyncActionType.DeleteOnClient);
            if (!WithinDeleteBudget(plannedServerDeletes, serverManifest.Count, "server")) return 4;
            if (!WithinDeleteBudget(plannedClientDeletes, clientManifest.Count, "client")) return 4;
        }
```

`serverManifest` (this server's own scan, authoritative for `DeleteOnServer`) is in scope from `SyncServer.cs:162`; `clientManifest` (peer-supplied, advisory for `DeleteOnClient` — the client re-checks it against its own scan) from `:156`. Both precede line 171.

**3e — `SyncServer.cs:221-241`.** Delete the old one-directional guard and gate the loop on mode. Replace exactly:

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
        // 7. Deletion Phase (Server): Receive DeleteFile from client for DeleteOnServer actions.
        // The bound now lives above, before the receive loop. Gated on the same predicate as the
        // client's matching send loop.
        if (deleteEnabled && ModeGate.ClientToServer(mode))
        {

```

**3f — `SyncServer.cs`, new private method immediately before the closing brace of the class.** Replace exactly:

```csharp
        var deletedSummary = filesDeleted > 0 ? $", {filesDeleted} deleted" : "";
        _logger.Summary($"Sync complete: {filesTransferred} files transferred{deletedSummary}, {bytesTransferred / (1024.0 * 1024.0):F1} MB, {sw.ElapsedMilliseconds}ms");
        return exitCode;
    }
}
```

with:

```csharp
        var deletedSummary = filesDeleted > 0 ? $", {filesDeleted} deleted" : "";
        _logger.Summary($"Sync complete: {filesTransferred} files transferred{deletedSummary}, {bytesTransferred / (1024.0 * 1024.0):F1} MB, {sw.ElapsedMilliseconds}ms");
        return exitCode;
    }

    /// <summary>
    /// Percentage bound for one direction. <paramref name="destinationCount"/> is the file count
    /// on the side being deleted from; for the client that is the manifest it just sent us, which
    /// is the only view of the client's population this server has.
    /// </summary>
    private bool WithinDeleteBudget(int deletes, int destinationCount, string destinationLabel)
    {
        if (DeleteBudget.Within(deletes, destinationCount, _options.MaxDeletePercent)) return true;

        var msg = $"Rejecting sync plan: it would delete {deletes} of {destinationCount} file(s) " +
                  $"on the {destinationLabel}, exceeding this server's --max-delete-percent " +
                  $"{_options.MaxDeletePercent}. The server enforces this independently of the " +
                  "client; an intentional bulk deletion needs --force-delete on both sides.";
        _logger.Error(msg);
        _progress.WriteError(msg, fatal: true);
        return false;
    }
}
```

**3g — `SyncServer.cs`, the conflict-squatter percentage guard introduced by Phase 7.** Phase 7 hand-rolled this arithmetic because `DeleteBudget` did not exist when it landed; routing it through the helper is what makes "one bound, one rule" true rather than aspirational, and it fixes the same two defects here — a zero `serverManifest.Count` currently *passes* the guard (`0 >= MinTrackedFilesForDeleteGuard` is false, so the whole block is skipped), and the boundary was `>` on the percentage rather than the shared `<=`. Anchor on Phase 7's post-edit text. Replace exactly:

```csharp
            if (occupied > 0 && !_options.ForceDelete
                && serverManifest.Count >= SyncOptions.MinTrackedFilesForDeleteGuard)
            {
                double pct = occupied * 100.0 / serverManifest.Count;
                if (pct > _options.MaxDeletePercent)
                {
                    var msg = $"Rejecting sync plan: peer's conflict names would replace {occupied} of " +
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
            // Same bound, same arithmetic, same zero-denominator rule as an outright deletion —
            // landing a conflict name on an occupied path destroys the file that was there, so it
            // must not be cheaper than asking for a DeleteFile. DeleteBudget is called directly
            // rather than through WithinDeleteBudget because the operator needs to be told this
            // was a conflict-rename collision, not a planned deletion.
            if (!_options.ForceDelete
                && !DeleteBudget.Within(occupied, serverManifest.Count, _options.MaxDeletePercent))
            {
                var msg = $"Rejecting sync plan: peer's conflict names would replace {occupied} of " +
                          $"{serverManifest.Count} local file(s), exceeding this server's " +
                          $"--max-delete-percent {_options.MaxDeletePercent}.";
                _logger.Error(msg);
                _progress.WriteError(msg, fatal: true);
                return 4;
            }
```

The `occupied > 0` precondition is dropped from the `if` because `DeleteBudget.Within` already returns `true` for zero deletes; keeping it as well would restore the very short-circuit that made the old guard skippable.

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~DeleteBudgetTests"`
Expected: PASS — `ZeroDestinationCount_RefusesRatherThanDisarming`, `NoDeletes_IsAlwaysWithinBudget`, `BelowTheFloor_ThePercentageIsNoiseAndTheGuardIsExempt`, `AtTheFloor_AWholesaleDeletionIsRefused`, `PercentageIsBoundedByTheDestinationPopulation` (all rows).

**Consequence of dropping `previousState` from the guard (deliberate, not silent).** Phase 6 already retired the `SyncStateManager` binary-state table as an ancestor source when it collapsed the two `ComputePlan` overloads into one. This edit removes its last remaining influence: the guard no longer falls back to `previousState?.Manifest.Count`. After both phases, `previousState` is loaded at `SyncClient.cs:126-133` and read only by the legacy `SaveState` call at `:483-488` — it can no longer cause or bound a deletion. The four `DeleteSyncTests` cases that depend on the binary-state deletion path (`DeleteSync_Case1_PropagatesDeletion`, `DeleteSync_BidiSymmetric`, `DeleteSync_SecondRun_DetectsDeletions`, `DeleteSync_UniDirectional_ServerDeletionIgnored`) break at Phase 6, not here, and belong to Phase 10 to migrate onto `SyncDatabase`/`UpsertSynced` or retire. This phase must not be read as having preserved them.

---

### Task 8.3: The no-ancestor safety gate, inside `SyncClient.RunAsync`

CONTRACT.md's state table:

| sync.db | pair.marker | behaviour |
|---|---|---|
| absent | absent | first run: additive only, build the table, write the marker on success |
| absent | present | exit 4, "sync state lost"; only `--mirror` proceeds |
| unreadable | present | exit 4, same |
| present | present | normal ancestor merge |

The condition is **exactly** that: marker present AND database absent or unreadable. It is *not* "the ancestor table is empty" — a legitimately empty tree would trip that and refuse a perfectly ordinary run.

- [ ] **Step 1: Write the failing test**

Create `tests/RemoteFileSync.Tests/Network/SyncClientGateTests.cs`:

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.Network;

/// <summary>
/// The no-ancestor gate. It lives in SyncClient.RunAsync rather than Program.Main so it is
/// reachable without a live socket, and so it runs before anything opens (and therefore
/// creates) the database whose absence it is testing for.
/// </summary>
public class SyncClientGateTests : IDisposable
{
    private readonly string _root;
    private readonly string _folder;
    private readonly string _dbPath;

    public SyncClientGateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rfs_gate_{Guid.NewGuid()}");
        _folder = Path.Combine(_root, "sync");
        Directory.CreateDirectory(_folder);
        _dbPath = Path.Combine(_root, "state", "sync.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A port bound just long enough to learn it is free, then released.</summary>
    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private SyncOptions ClientOptions(bool mirrorDeletes = false) => new()
    {
        IsServer = false,
        Host = "127.0.0.1",
        Port = ClosedPort(),
        Folder = _folder,
        Mode = SyncMode.Pull,
        DeleteEnabled = true,
        MirrorDeletes = mirrorDeletes,
        BackupFolder = Path.Combine(_root, "backup"),
        ArchiveFolder = Path.Combine(_root, "archive"),
    };

    [Fact]
    public async Task MarkerWithoutDatabase_AbortsWithExitFourBeforeConnecting()
    {
        PairMarker.Write(_dbPath);              // this pair has synced before
        Assert.False(File.Exists(_dbPath));     // ...and its ancestor table is gone

        using var logger = new SyncLogger(false, null);
        var client = new SyncClient(ClientOptions(), logger, dbPath: _dbPath);

        var sw = Stopwatch.StartNew();
        var exit = await client.RunAsync(CancellationToken.None);
        sw.Stop();

        Assert.Equal(4, exit);
        // Before the socket: three refused connects cost ~4s of retry backoff and return 2.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"the gate ran after the connection attempt ({sw.Elapsed})");
        // ...and before anything created the database it just refused to run without.
        Assert.False(File.Exists(_dbPath));
    }

    [Fact]
    public async Task UnreadableDatabase_WithMarker_AbortsWithoutConsumingTheEvidence()
    {
        const string junk = "not a sqlite database";
        File.WriteAllText(_dbPath, junk);
        PairMarker.Write(_dbPath);

        using var logger = new SyncLogger(false, null);
        var client = new SyncClient(ClientOptions(), logger, dbPath: _dbPath);

        Assert.Equal(4, await client.RunAsync(CancellationToken.None));

        // The probe must not rewrite, truncate or delete the file it inspected: a user restoring
        // from backup needs whatever is there, and a probe that mutates its subject is not one.
        Assert.Equal(junk, File.ReadAllText(_dbPath));
        // ...and must not leave a handle open. This throws IOException if it did.
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task MarkerWithoutDatabase_WithMirror_IsNotRefused()
    {
        PairMarker.Write(_dbPath);

        using var logger = new SyncLogger(false, null);
        var client = new SyncClient(ClientOptions(mirrorDeletes: true), logger, dbPath: _dbPath);

        // --mirror is the documented escape: the operator has accepted that the destination is
        // overwritten to match the source, so a missing ancestor table is not fatal. Reaching
        // the connect retries and failing with 2 is the proof that the gate did not fire.
        Assert.Equal(2, await client.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NoMarker_IsAGenuineFirstRunAndTheClientOpensItsOwnDatabase()
    {
        Assert.False(PairMarker.Exists(_dbPath));

        using var logger = new SyncLogger(false, null);
        var client = new SyncClient(ClientOptions(), logger, dbPath: _dbPath);

        Assert.Equal(2, await client.RunAsync(CancellationToken.None));
        // Program no longer opens the database; the client does, after the gate has passed.
        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public void PairStateLost_FollowsTheStateTableExactly()
    {
        // A live database is kept in its own directory: PairMarker.PathFor is per-directory, so
        // two databases under one directory would share a marker.
        var livePath = Path.Combine(_root, "live", "sync.db");

        // neither: a genuine first run, additive and safe.
        Assert.False(SyncClient.PairStateLost(_dbPath));

        // database, no marker: still a first run — the marker is only written on a clean exit.
        using (var db = new SyncDatabase(livePath)) { }
        Assert.False(SyncClient.PairStateLost(livePath));

        // database + marker: the normal steady state.
        PairMarker.Write(livePath);
        Assert.False(SyncClient.PairStateLost(livePath));

        // marker without database: state loss, not a first run. Every one-sided file would
        // otherwise resolve to a deletion.
        PairMarker.Write(_dbPath);
        Assert.True(SyncClient.PairStateLost(_dbPath));

        // unreadable counts the same as absent: a foreign file yields no ancestor rows.
        File.WriteAllText(_dbPath, "not a sqlite database");
        Assert.True(SyncClient.PairStateLost(_dbPath));

        // a zero-length file is the same case, and is what a half-finished restore leaves.
        File.WriteAllText(_dbPath, "");
        Assert.True(SyncClient.PairStateLost(_dbPath));
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncClientGateTests"`
Expected: FAIL — the test project does not compile:
`error CS1739: The best overload for 'SyncClient' does not have a parameter named 'dbPath'`
`error CS0117: 'SyncClient' does not contain a definition for 'PairStateLost'`

- [ ] **Step 3: Implement**

**3a — `SyncClient.cs:21`.** Replace exactly:

```csharp
    private readonly SyncDatabase? _db;
```

with:

```csharp
    // Not readonly: when the caller supplies a path instead of an instance, RunAsync opens the
    // database itself — and it must not do so until the no-ancestor gate has run, because
    // `new SyncDatabase(path)` creates the file whose absence the gate is looking for.
    // RunAsync clears this again in a finally when it was the opener, so the field never
    // outlives the instance it points at; see the at-most-once note on RunAsync.
    private SyncDatabase? _db;
    private readonly string? _dbPath;
```

**3b — `SyncClient.cs:23-35`.** Replace exactly:

```csharp
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

with:

```csharp
    /// <param name="db">
    /// An already-open database owned by the caller. Never disposed here.
    /// </param>
    /// <param name="dbPath">
    /// Where the ancestor database lives. Supplying this instead of <paramref name="db"/> lets
    /// RunAsync evaluate the no-ancestor gate against the on-disk state before anything opens
    /// (and thereby creates) the file, and lets it write pair.marker on a clean exit.
    /// </param>
    public SyncClient(SyncOptions options, SyncLogger logger,
                      SyncStateManager? stateManager = null,
                      JsonProgressWriter? progressWriter = null,
                      StdinCommandReader? stdinReader = null,
                      SyncDatabase? db = null,
                      string? dbPath = null)
    {
        _options = options;
        _logger = logger;
        _stateManager = stateManager;
        _progress = progressWriter ?? JsonProgressWriter.Null;
        _stdinReader = stdinReader ?? StdinCommandReader.Null;
        _db = db;
        _dbPath = dbPath;
    }

    /// <summary>
    /// True when this pair has synced before but its ancestor database is gone or unusable.
    /// An absent database on its own is indistinguishable from one deleted after a hundred
    /// successful syncs, so pair.marker is the only thing separating a safe additive first run
    /// from a destructive one.
    /// </summary>
    public static bool PairStateLost(string dbPath)
    {
        if (!PairMarker.Exists(dbPath)) return false;
        if (!File.Exists(dbPath)) return true;

        // A header probe, not an open. `new SyncDatabase(path)` would create the file when it is
        // missing and run migrations when it is not, and Microsoft.Data.Sqlite pools the
        // connection so the handle outlives the `using` — a probe that mutates, locks or removes
        // its subject is not a probe. Sixteen bytes is enough: a truncated, empty or foreign file
        // fails the magic and carries no ancestor rows either way.
        try
        {
            using var fs = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[SqliteFileMagic.Length];
            int read = fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            return read < header.Length || !header.SequenceEqual(SqliteFileMagic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable for any reason means we cannot prove the ancestor table is there. Fail
            // closed: refusing a run is recoverable, propagating deletions computed without an
            // ancestor table is not.
            return true;
        }
    }

    /// <summary>The SQLite file header, "SQLite format 3\0".</summary>
    private static readonly byte[] SqliteFileMagic =
        System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");
```

**3c — `SyncClient.cs:37-40`.** Replace exactly:

```csharp
    public async Task<int> RunAsync(CancellationToken ct)
    {
        int retries = 3;
        TcpClient? tcp = null;
```

with:

```csharp
    /// <summary>
    /// Runs one sync session. <b>May be called at most once per instance.</b> When the caller
    /// supplied <c>dbPath</c> rather than an open <c>db</c>, this method opens the ancestor
    /// database, publishes it to <c>_db</c> for the duration of the session, and disposes it on
    /// the way out — so a second call would find a disposed instance. Construct a new
    /// <see cref="SyncClient"/> per session. (An instance handed in by the caller is never
    /// disposed here and belongs to the caller.)
    /// </summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        // No-ancestor safety gate. Before the socket, before the database is opened, before any
        // state is written — nothing at all must happen on a refusal. --mirror is the documented
        // escape: it means "make the destination match the source", which needs no ancestor.
        if (_dbPath != null && _options.DeleteEnabled && !_options.MirrorDeletes
            && PairStateLost(_dbPath))
        {
            var msg = "Sync state lost: this pair has synced before (pair.marker is present) but " +
                      $"its database at '{_dbPath}' is missing or unreadable. Without it, every " +
                      "file present on only one side is indistinguishable from one the peer " +
                      "deleted. Restore the database from backup, or re-run with --mirror to " +
                      "accept the destination being overwritten to match the source.";
            _logger.Error(msg);
            _progress.WriteError(msg, fatal: true);
            return 4;
        }

        // Only now open the ancestor database.
        SyncDatabase? opened = null;
        if (_db == null && _dbPath != null && _options.DeleteEnabled)
        {
            var binPath = Path.Combine(Path.GetDirectoryName(_dbPath)!, "sync-state.bin");
            SyncDatabase.MigrateFromBinary(binPath, _dbPath);
            opened = new SyncDatabase(_dbPath);
            _db = opened;
        }

        try
        {
            return await RunSessionAsync(ct);
        }
        finally
        {
            // Only what this method opened is disposed — a caller-supplied instance is not ours.
            // The field is cleared *before* the dispose so `_db` can never name a disposed
            // object: a `using` declaration would dispose it and leave the field pointing at the
            // corpse, and the next RunAsync call would hand HandleConnectionAsync that.
            if (opened != null)
            {
                _db = null;
                opened.Dispose();
            }
        }
    }

    /// <summary>
    /// The connect-retry loop and the session itself. Split out of <see cref="RunAsync"/> only so
    /// the database-ownership try/finally above can wrap every exit path, including the early
    /// returns from failed connects.
    /// </summary>
    private async Task<int> RunSessionAsync(CancellationToken ct)
    {
        int retries = 3;
        TcpClient? tcp = null;
```

Everything below the replaced header — the whole existing body of `RunAsync`, up to and including its closing brace — is now the body of `RunSessionAsync`, unmoved and otherwise unedited. Step 3d below edits its tail.

**3d — `SyncClient.cs:79-81`.** Replace exactly:

```csharp
        using var stream = owned.GetStream();
        return await HandleConnectionAsync(stream, ct);
    }
```

with:

```csharp
        using var stream = owned.GetStream();
        var exit = await HandleConnectionAsync(stream, ct);

        // Arm the gate only after a clean session. A partial run leaves a database that was never
        // finished being built, and arming on it turns the next perfectly ordinary run into a
        // hard refusal.
        if (exit == 0 && _dbPath != null && _options.DeleteEnabled)
            PairMarker.Write(_dbPath);

        return exit;
    }
```

**3e — `Program.cs:55-78`.** `Program` now only surfaces the exit code; it must not open the database, because doing so would create the file and permanently disarm the gate. Replace exactly:

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
                // Hand over the path, not an open database. SyncClient runs the no-ancestor gate
                // before opening it, and `new SyncDatabase(path)` creates the file — opening it
                // here would mean the gate never sees an absent database and never fires. The
                // binary-state migration moved with it, for the same reason.
                string? dbPath = options.DeleteEnabled
                    ? SyncDatabase.GetDbPath(SyncDatabase.DefaultBaseDir, options.Folder,
                                             options.Host!, options.Port)
                    : null;

                var client = new Network.SyncClient(options, logger, dbPath: dbPath,
                    progressWriter: progressWriter, stdinReader: stdinReader);
                return await client.RunAsync(cts.Token);
            }
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~SyncClientGateTests"`
Expected: PASS — `MarkerWithoutDatabase_AbortsWithExitFourBeforeConnecting`, `UnreadableDatabase_WithMarker_AbortsWithoutConsumingTheEvidence`, `MarkerWithoutDatabase_WithMirror_IsNotRefused`, `NoMarker_IsAGenuineFirstRunAndTheClientOpensItsOwnDatabase`, `PairStateLost_FollowsTheStateTableExactly`. Two of these deliberately fall through to the connect retries and take a few seconds each.

---

### Behavioural impact on existing tests

**`tests/RemoteFileSync.Tests/Integration/DeleteThresholdTests.cs` — changed by this phase, and it does not pass unchanged.** This phase does not edit it: CONTRACT.md assigns integration test files to Phase 10. Phase 10 must apply both of the following.

1. **`ForceDelete_OverridesTheThreshold` breaks.** The server's guard now bounds `DeleteOnClient` as well as `DeleteOnServer`. `RunClientAsync` (`tests/RemoteFileSync.Tests/Integration/DeleteThresholdTests.cs:46-67`) sets `ForceDelete` on the *client* options only (`:50-54`); the server options are built on `:49` and carry no `ForceDelete`, so `_options.ForceDelete` is false there. With 20 planned `DeleteOnClient` against a 20-file client manifest, the server refuses at 100% and returns 4 immediately after receiving the plan. The client, having skipped its own guard, then reaches step 10 and blocks on `ProtocolHandler.ReadMessageAsync` at `SyncClient.cs:411` — a read that is *outside* the surrounding `try` — so the closed connection throws out of `RunAsync` and the test errors rather than failing an assertion.

   **Required Phase 10 fix, applicable verbatim.** In `tests/RemoteFileSync.Tests/Integration/DeleteThresholdTests.cs`, replace line 49 exactly:

   ```csharp
           var serverOpts = new SyncOptions { IsServer = true, Once = true, Port = port, Folder = _serverDir };
   ```

   with:

   ```csharp
           // The server enforces its own delete bound, independently of the client's, so an
           // intentional bulk deletion needs --force-delete on BOTH sides. Without this the
           // server refuses the plan and the client blocks on a read from a closed socket.
           var serverOpts = new SyncOptions
           {
               IsServer = true, Once = true, Port = port, Folder = _serverDir,
               ForceDelete = forceDelete,
           };
   ```

   Nothing else on `:46-67` changes for this item; `DeleteEnabled` is not added to the server options because the server takes that flag from the handshake, not from its own options. This is the correct semantics, not a workaround — both guard messages now tell the operator exactly this.
2. **`SeedTrackedFiles` (`:73-85`) must be re-seeded.** `db.MarkSynced(name, 9, DateTime.UtcNow.AddDays(-1), session, "to_server")` records an mtime a day older than the file that was just written, which `ChangeDetector` reads as "the client changed since the last sync". Under Phase 6's TwoWay table that yields `SendToServer` plus a resurrection, not `DeleteOnClient`, so the plan contains zero deletions and `EmptyPeerFolder_AbortsInsteadOfMassDeleting` fails with `Expected: 4 / Actual: 0` — a Phase 6 consequence, not a Phase 8 one, but it must be fixed before either threshold test means anything. The row needs the file's real `FileInfo.Length` and `LastWriteTimeUtc.Ticks` through `UpsertSynced`, and the session label should become `"two-way+delete"` to match the new `sessionMode` format.

With both applied, `EmptyPeerFolder_AbortsInsteadOfMassDeleting` passes on the *client's* guard (20 of 20 client files, 100% > 25), which returns 4 before reading anything further from the peer; the server independently returns 4 from its own guard, and the test's existing `try { await serverTask; } catch { }` absorbs that. `SmallPopulations_AreExemptFromThePercentageGuard` asserts only the constant and is unaffected.

**`DatabaseDeleteSyncTests`, `DeleteSyncTests`, `EndToEndTests`** — all operate on two- and three-file trees, below `MinTrackedFilesForDeleteGuard`, so neither new guard fires. `DeleteSyncTests` is nonetheless already broken by Phase 6's retirement of the binary-state ancestor path (see the note at the end of Task 8.2); this phase neither repairs nor worsens it.

---

### Phase 8 commit

```bash
git add src/RemoteFileSync/Sync/ModeGate.cs \
        src/RemoteFileSync/Sync/DeleteBudget.cs \
        src/RemoteFileSync/Network/SyncClient.cs \
        src/RemoteFileSync/Network/SyncServer.cs \
        src/RemoteFileSync/Program.cs \
        tests/RemoteFileSync.Tests/Sync/ModeGateTests.cs \
        tests/RemoteFileSync.Tests/Sync/DeleteBudgetTests.cs \
        tests/RemoteFileSync.Tests/Network/SyncClientGateTests.cs
git commit -m "feat: dispatch on SyncMode and rework the deletion safety gates

Push and Pull were both flattened to 'not bidirectional', so Pull planned
DeleteOnClient, the server sent DeleteFile for each, and a client gate
keyed on Bidirectional dropped every one of them while the server waited
for confirms that never came. Both peers now derive all four loop
predicates from a single shared ModeGate, so a client loop and its
matching server loop cannot disagree about which frames are on the wire.

Both delete guards used denominators that vanish when it matters: the
client divided by the tracked-row count (0 on a wiped database, and the
below-floor test then skipped the guard entirely) and the server bounded
only DeleteOnServer (0 in Pull mode, where every deletion is one the
server itself originates). Each direction is now bounded against the
manifest of the side being deleted from, via a shared DeleteBudget that
refuses outright when that count is zero. The conflict-rename squatter
guard is routed through the same helper, so no percentage expression
survives outside it.

Move the no-ancestor gate into SyncClient.RunAsync, where it returns 4
before the socket opens and before anything creates the database whose
absence it tests for; Program hands over a path instead of an open
database and only surfaces the code. The readability probe reads the
SQLite header and neither locks, mutates nor removes the file.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
git push -u origin feat/deletion-sync-ancestor-merge
```

**Verification before commit:**

```bash
dotnet build -c Release
dotnet test -c Release --filter "FullyQualifiedName~ModeGateTests"
dotnet test -c Release --filter "FullyQualifiedName~DeleteBudgetTests"
dotnet test -c Release --filter "FullyQualifiedName~SyncClientGateTests"
dotnet test -c Release
git grep -n "Bidirectional" -- src/
git grep -n "bidirectional" -- src/
git grep -n "DatabasePath\|ExistedBeforeOpen" -- src/ tests/
git grep -n "MaxDeletePercent" -- src/
```

Expected: `dotnet build -c Release` reports 0 errors. The three filtered runs are green. The first `git grep` over `src/` returns only the `SyncOptions.Bidirectional` declaration itself — this phase removes the last four reads of it from production code — and the second returns nothing at all (Phase 3 deleted the `bidirectional` local outright). The third grep returns **nothing**: `SyncDatabase.DatabasePath` and `SyncDatabase.ExistedBeforeOpen` do not exist, and the no-ancestor seam is the `dbPath` constructor parameter plus `SyncClient.PairStateLost` alone. The fourth returns `SyncOptions.MaxDeletePercent`'s declaration, the argument passed into `DeleteBudget.Within` from `DeleteBudget.cs`'s two callers-of-record (`SyncClient.WithinDeleteBudget`, `SyncServer.WithinDeleteBudget`), the squatter guard's direct `DeleteBudget.Within` call, and the message strings — **no surviving `* 100.0 /` percentage expression outside `DeleteBudget.cs`**; if one appears, a guard has been left with its own copy of the arithmetic and the zero-denominator defect with it. The full `dotnet test` run is **not** expected to be green until Phase 10 applies the two `DeleteThresholdTests` changes documented above; `ForceDelete_OverridesTheThreshold` will error and `EmptyPeerFolder_AbortsInsteadOfMassDeleting` will fail with `Expected: 4 / Actual: 0` until then, and no other test regresses.

---

## Phase 9: End-of-sync review report for conflicts and resurrections

**Goal:** After the `Sync complete:` summary line, print a review section listing every conflict and every resurrection the session recorded, with both sides' size and mtime, read back from the database through the two separate readers `GetSessionConflicts` / `GetSessionResurrections`, and mirror each item as one flat `review` JSON progress event so ExecRFS can list them.

**Files:**
- Create: `src/RemoteFileSync/Sync/ReviewReport.cs`
- Modify: `src/RemoteFileSync/Progress/JsonProgressWriter.cs` (append `WriteReview` between `WriteComplete` at `:60-63` and `WriteError` at `:65-68`)
- Modify: `src/ExecRFS/Models/ProgressEvent.cs` (append properties between `:34` and `:36`)
- Modify: `src/RemoteFileSync/Network/SyncClient.cs` (one inserted call after the `Sync complete:` summary — currently `:480`)
- Test (create): `tests/RemoteFileSync.Tests/Sync/ReviewReportTests.cs`
- Test (modify, append-only): `tests/RemoteFileSync.Tests/Progress/JsonProgressWriterTests.cs:105-111`
- Test (modify, append-only): `tests/ExecRFS.Tests/Models/ProgressEventTests.cs:73-80`

**Line numbers are as they stand on `main` today** (verified by reading each file). Phases 1–8 shift them. Every "Replace exactly" block below is anchored on **unique text that no lower-numbered phase edits** — match the text, not the number.

### Interfaces

**Consumes — from lower-numbered phases, as already applied. This phase edits none of it.**

| Symbol | Owner | Form consumed |
|---|---|---|
| `ConflictDetail` record + `Encode()` / `static ConflictDetail? Decode(string?)` | Phase 4 | `src/RemoteFileSync/State/ConflictDetail.cs`, `namespace RemoteFileSync.State`. Fields `long ClientSize, long ClientMtimeTicks, long ServerSize, long ServerMtimeTicks, string? RenamedTo`. `Decode` returns `null` on anything unparsable. |
| `record ConflictEntry(string Path, string Detail, DateTime Timestamp)` | Phase 4 | declared inside `SyncDatabase.cs` |
| `SyncDatabase.LogConflict(string path, long sessionId, string detail)` | Phase 4 | writes `action='conflict'` unconditionally |
| `SyncDatabase.LogResurrection(string path, long sessionId, string detail)` | Phase 4 | writes `action='resurrected'` unconditionally. Sanctioned by CONTRACT.md correction #2 ("`LogConflict` and `LogResurrection` are separate methods"); it is not in the frozen type block, so its signature is taken as the exact mirror of `LogConflict`. |
| `SyncDatabase.GetSessionConflicts(long)` / `GetSessionResurrections(long)` | Phase 4 | two independent readers filtering on the `action` column |
| `PlanResult` type (`Entries` / `Conflicts` / `Resurrections`) | Phase 2 | `src/RemoteFileSync/Sync/PlanResult.cs`; this phase never names the type, it only reads the rows the drain produced |
| `planResult.Conflicts` **and** `planResult.Resurrections` drained into `LogConflict` / `LogResurrection` inside `SyncClient` after the transfer phase succeeds | **Phase 7** | Phase 7 owns **both** drains, written in one edit block at the same anchor (above the `// 11. Exchange SyncComplete` landmark, `SyncClient.cs:472-474`). This phase **reads** those rows back via `GetSessionConflicts` / `GetSessionResurrections` and **never writes** either table. |
| `SyncDatabase.StartSession(...)` → `sessionId` local in `SyncClient.HandleConnectionAsync` (`SyncClient.cs:116-122`) | existing | reused, **not redeclared** |
| `_db`, `_progress`, `_logger` fields (`SyncClient.cs:17,19,21`) | existing | reused |
| `SyncLogger.Summary(string)` (`SyncLogger.cs:41`) | existing | prints to console *and* log file |

**Explicitly NOT consumed / NOT touched:**
- The `archive` local (Phase 5), `mode` / `skew` locals (Phase 3), `planResult` (the `PlanResult` local Phase 6 introduces; the type itself is Phase 2's) and `syncPlan` (still a `List<SyncPlanEntry>`, reassigned by Phase 6 from `planResult.Entries`) — this phase references none of them, so there is no CS0128 risk and no dependence on Phase 6's rewrite of `syncPlan.Count(...)` at `SyncClient.cs:485,499`.
- `SyncDatabase.cs`, `ConflictDetail.cs`, `SyncEngine.cs`, `SyncOptions.cs`, `Program.cs` — owned by Phases 4, 4, 6, 1, 1.
- `tests/RemoteFileSync.Tests/Integration/**` — owned by **Phase 10**. This phase adds no integration test (see Task 9.4).

**Produces — new, and NOT in CONTRACT.md. Stated here rather than invented silently:**

```csharp
// src/RemoteFileSync/Sync/ReviewReport.cs
namespace RemoteFileSync.Sync;
public static class ReviewReport
{
    public static IReadOnlyList<string> BuildLines(
        IReadOnlyList<ConflictEntry> conflicts,
        IReadOnlyList<ConflictEntry> resurrections);
    public static void Emit(SyncDatabase? db, long sessionId, SyncLogger logger, JsonProgressWriter progress);
}

// src/RemoteFileSync/Progress/JsonProgressWriter.cs
public void WriteReview(string kind, string path,
                        long client_size, string client_mtime,
                        long server_size, string server_mtime,
                        string? renamed_to = null);

// src/ExecRFS/Models/ProgressEvent.cs
public string? Kind;  public long? ClientSize;  public string? ClientMtime;
public long? ServerSize;  public string? ServerMtime;  public string? RenamedTo;
```

**Design decisions this phase settles:**

1. **There is no prefix-sniffing anywhere.** The `'conflict'` / `'resurrected'` discriminator lives in the `file_versions.action` column, written by two separate methods and read by two separate readers. `ConflictDetail` carries render data only; it has no resurrection flag and no magic prefix. Nothing in this phase inspects the leading characters of `detail`.
2. **Decoding is `ConflictDetail.Decode`, and `null` is expected, not exceptional.** A row written by an older build, or hand-edited, decodes to `null`; the item is still listed with its raw detail printed verbatim, because dropping the row would hide exactly the case the review exists to surface.
3. **Does `src/ExecRFS/Models/ProgressEvent.cs` need a parallel change? Yes.** `ProgressEvent` is a flat bag of nullables deserialized by `[JsonPropertyName]` (`ProgressEvent.cs:8-34`). Without new properties a `review` line parses successfully but silently yields `Event="review"` with every review-specific field lost. `path` already exists at `ProgressEvent.cs:20` and is **reused** — only `kind`, `client_size`, `client_mtime`, `server_size`, `server_mtime`, `renamed_to` are added.
4. **The wire shape matches `JsonProgressWriter`'s existing optional-field idiom exactly.** `WriteFileEnd` (`:46-51`) and `WriteDelete` (`:53-58`) build a `Dictionary<string, object>` with literal snake_case keys and add the optional key only when non-null; `WriteLine` (`:70-79`) sets `PropertyNamingPolicy` but **not** `DictionaryKeyPolicy`, so dictionary keys are emitted verbatim. `WriteReview` follows that idiom so `renamed_to` is absent (not `null`) on a resurrection.

---

### Task 9.1: `JsonProgressWriter.WriteReview`

- [ ] **Step 1: Write the failing test**

Exact current text at `tests/RemoteFileSync.Tests/Progress/JsonProgressWriterTests.cs:105-111`:

```csharp
    [Fact]
    public void NullWriter_NoOutput()
    {
        var writer = JsonProgressWriter.Null;
        writer.WriteStatus("connecting");
        writer.WriteComplete(0, 0, 0, 0, 0);
    }
```

Replace exactly with:

```csharp
    [Fact]
    public void WriteReview_Conflict_EmitsBothSidesAndTheRenamedCopy()
    {
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);
        writer.WriteReview("conflict", "docs/report.docx",
            2100000, "2026-07-20T14:30:52.0000000Z",
            2050112, "2026-07-20T14:31:10.0000000Z",
            renamed_to: "docs/report.conflict-20260720-143052-server.docx");
        var doc = JsonDocument.Parse(sw.ToString().Trim());
        Assert.Equal("review", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("conflict", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("docs/report.docx", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal(2100000, doc.RootElement.GetProperty("client_size").GetInt64());
        Assert.Equal("2026-07-20T14:30:52.0000000Z", doc.RootElement.GetProperty("client_mtime").GetString());
        Assert.Equal(2050112, doc.RootElement.GetProperty("server_size").GetInt64());
        Assert.Equal("2026-07-20T14:31:10.0000000Z", doc.RootElement.GetProperty("server_mtime").GetString());
        Assert.Equal("docs/report.conflict-20260720-143052-server.docx",
            doc.RootElement.GetProperty("renamed_to").GetString());
    }

    [Fact]
    public void WriteReview_NoRename_OmitsTheKeyEntirely()
    {
        // A resurrection renames nothing. Emitting renamed_to:null would make the GUI render an
        // empty "kept as" row for every resurrected file.
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);
        writer.WriteReview("resurrection", "notes/todo.txt",
            1024, "2026-07-20T09:15:00.0000000Z",
            900, "2026-07-19T17:00:00.0000000Z");
        var doc = JsonDocument.Parse(sw.ToString().Trim());
        Assert.Equal("resurrection", doc.RootElement.GetProperty("kind").GetString());
        Assert.False(doc.RootElement.TryGetProperty("renamed_to", out _));
    }

    [Fact]
    public void WriteReview_EmitsOneSelfContainedLinePerItem()
    {
        // The GUI parses this stream line by line; a multi-line or batched payload would be
        // dropped by ProgressEvent.TryParse.
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);
        writer.WriteReview("conflict", "a.docx", 1, "2026-07-20T09:15:00.0000000Z", 2, "2026-07-19T17:00:00.0000000Z");
        writer.WriteReview("resurrection", "b.txt", 3, "2026-07-20T09:15:00.0000000Z", 4, "2026-07-19T17:00:00.0000000Z");

        var lines = sw.ToString().Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("conflict", JsonDocument.Parse(lines[0]).RootElement.GetProperty("kind").GetString());
        Assert.Equal("resurrection", JsonDocument.Parse(lines[1]).RootElement.GetProperty("kind").GetString());
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

`using System.Text.Json;` is already present at `JsonProgressWriterTests.cs:1`; no new using is needed. `NullWriter_NoOutput` gains one call and loses no assertion.

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~JsonProgressWriterTests"`

Expected: FAIL — `error CS1061: 'JsonProgressWriter' does not contain a definition for 'WriteReview' and no accessible extension method 'WriteReview' accepting a first argument of type 'JsonProgressWriter' could be found`.

- [ ] **Step 3: Implement**

Exact current text at `src/RemoteFileSync/Progress/JsonProgressWriter.cs:60-68`:

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

Replace exactly with:

```csharp
    public void WriteComplete(int files_transferred, int files_deleted, long bytes, long elapsed_ms, int exit_code)
    {
        WriteLine(new { @event = "complete", files_transferred, files_deleted, bytes, elapsed_ms, exit_code });
    }

    // One line per reviewed item, like file_end and delete, because ProgressEvent is a flat bag
    // of nullables and cannot carry a nested array. kind is "conflict" or "resurrection".
    // A size of -1 paired with an empty mtime means the stored detail could not be decoded, so
    // the GUI must show "unknown" rather than render it as a 0-byte file.
    // renamed_to is omitted (not null) when nothing was renamed: a null would make the GUI draw
    // an empty "kept as" row for every resurrection.
    public void WriteReview(string kind, string path,
                            long client_size, string client_mtime,
                            long server_size, string server_mtime,
                            string? renamed_to = null)
    {
        var obj = new Dictionary<string, object>
        {
            ["event"] = "review",
            ["kind"] = kind,
            ["path"] = path,
            ["client_size"] = client_size,
            ["client_mtime"] = client_mtime,
            ["server_size"] = server_size,
            ["server_mtime"] = server_mtime,
        };
        if (renamed_to != null) obj["renamed_to"] = renamed_to;
        WriteLine(obj);
    }

    public void WriteError(string message, bool fatal)
    {
        WriteLine(new { @event = "error", message, fatal });
    }
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~JsonProgressWriterTests"`

Expected: PASS. Green: `WriteReview_Conflict_EmitsBothSidesAndTheRenamedCopy`, `WriteReview_NoRename_OmitsTheKeyEntirely`, `WriteReview_EmitsOneSelfContainedLinePerItem`, `NullWriter_NoOutput`, plus the seven pre-existing `Write*_EmitsValidJson` tests unchanged.

---

### Task 9.2: `ProgressEvent` carries the review fields

- [ ] **Step 1: Write the failing test**

Exact current text at `tests/ExecRFS.Tests/Models/ProgressEventTests.cs:73-80`:

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

Replace exactly with:

```csharp
    [Fact]
    public void TryParse_ErrorEvent()
    {
        var evt = ProgressEvent.TryParse(@"{""event"":""error"",""message"":""Connection refused"",""fatal"":true}");
        Assert.NotNull(evt);
        Assert.True(evt.Fatal);
    }

    [Fact]
    public void TryParse_ReviewConflictEvent_CarriesBothSidesAndTheRenamedCopy()
    {
        var evt = ProgressEvent.TryParse(
            @"{""event"":""review"",""kind"":""conflict"",""path"":""docs/report.docx""," +
            @"""client_size"":2100000,""client_mtime"":""2026-07-20T14:30:52.0000000Z""," +
            @"""server_size"":2050112,""server_mtime"":""2026-07-20T14:31:10.0000000Z""," +
            @"""renamed_to"":""docs/report.conflict-20260720-143052-server.docx""}");
        Assert.NotNull(evt);
        Assert.Equal("review", evt.Event);
        Assert.Equal("conflict", evt.Kind);
        Assert.Equal("docs/report.docx", evt.Path);
        Assert.Equal(2100000, evt.ClientSize);
        Assert.Equal("2026-07-20T14:30:52.0000000Z", evt.ClientMtime);
        Assert.Equal(2050112, evt.ServerSize);
        Assert.Equal("2026-07-20T14:31:10.0000000Z", evt.ServerMtime);
        Assert.Equal("docs/report.conflict-20260720-143052-server.docx", evt.RenamedTo);
    }

    [Fact]
    public void TryParse_ReviewResurrectionEvent_HasNoRenameAndKeepsUnknownSizesNegative()
    {
        // -1 is the CLI's "detail could not be decoded" sentinel. If it arrived as 0 the GUI
        // would render a real file as empty; if RenamedTo defaulted to "" it would draw a blank
        // "kept as" row for a file that was never renamed.
        var evt = ProgressEvent.TryParse(
            @"{""event"":""review"",""kind"":""resurrection"",""path"":""notes/todo.txt""," +
            @"""client_size"":-1,""client_mtime"":"""",""server_size"":-1,""server_mtime"":""""}");
        Assert.NotNull(evt);
        Assert.Equal("resurrection", evt.Kind);
        Assert.Equal(-1, evt.ClientSize);
        Assert.Equal(-1, evt.ServerSize);
        Assert.Equal("", evt.ServerMtime);
        Assert.Null(evt.RenamedTo);
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ProgressEventTests"`

Expected: FAIL — `error CS1061: 'ProgressEvent' does not contain a definition for 'Kind' and no accessible extension method 'Kind' accepting a first argument of type 'ProgressEvent' could be found` (and the same for `ClientSize`, `ClientMtime`, `ServerSize`, `ServerMtime`, `RenamedTo`).

- [ ] **Step 3: Implement**

Exact current text at `src/ExecRFS/Models/ProgressEvent.cs:34-36`:

```csharp
    [JsonPropertyName("error")] public string? Error { get; set; }

    public static ProgressEvent? TryParse(string line)
```

Replace exactly with:

```csharp
    [JsonPropertyName("error")] public string? Error { get; set; }

    // "review" event: one per conflict or resurrection, emitted after "complete".
    // Kind is "conflict" or "resurrection"; the path reuses the existing Path property above.
    // A size of -1 with an empty mtime means the CLI could not decode the stored ConflictDetail,
    // so the GUI must show "unknown" rather than treat it as a 0-byte file.
    // RenamedTo is absent from the JSON when nothing was renamed, so it stays null here.
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("client_size")] public long? ClientSize { get; set; }
    [JsonPropertyName("client_mtime")] public string? ClientMtime { get; set; }
    [JsonPropertyName("server_size")] public long? ServerSize { get; set; }
    [JsonPropertyName("server_mtime")] public string? ServerMtime { get; set; }
    [JsonPropertyName("renamed_to")] public string? RenamedTo { get; set; }

    public static ProgressEvent? TryParse(string line)
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ProgressEventTests"`

Expected: PASS. Green: `TryParse_ReviewConflictEvent_CarriesBothSidesAndTheRenamedCopy`, `TryParse_ReviewResurrectionEvent_HasNoRenameAndKeepsUnknownSizesNegative`, plus all seven pre-existing `TryParse_*` tests unchanged.

---

### Task 9.3: `ReviewReport` — build the lines and emit them from the two readers

`BuildLines` and `Emit` land in one task deliberately. Splitting them would leave `Emit`'s tests green the moment `BuildLines` compiled, which is a test without teeth.

- [ ] **Step 1: Write the failing test**

Create `tests/RemoteFileSync.Tests/Sync/ReviewReportTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RemoteFileSync.Logging;
using RemoteFileSync.Progress;
using RemoteFileSync.State;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public sealed class ReviewReportTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly string _logPath;

    public ReviewReportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rfs_review_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "sync.db");
        _logPath = Path.Combine(_tempDir, "sync.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly DateTime ClientMtime = new(2026, 7, 20, 14, 30, 52, DateTimeKind.Utc);
    private static readonly DateTime ServerMtime = new(2026, 7, 20, 14, 31, 10, DateTimeKind.Utc);
    private static readonly DateTime ResClientMtime = new(2026, 7, 20, 9, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime ResServerMtime = new(2026, 7, 19, 17, 0, 0, DateTimeKind.Utc);

    private static string ConflictDetailText() => new ConflictDetail(
        ClientSize: 2100000, ClientMtimeTicks: ClientMtime.Ticks,
        ServerSize: 2050112, ServerMtimeTicks: ServerMtime.Ticks,
        RenamedTo: "docs/report.conflict-20260720-143052-server.docx").Encode();

    private static string ResurrectionDetailText() => new ConflictDetail(
        ClientSize: 1024, ClientMtimeTicks: ResClientMtime.Ticks,
        ServerSize: 900, ServerMtimeTicks: ResServerMtime.Ticks,
        RenamedTo: null).Encode();

    private static ConflictEntry Conflict(string path) =>
        new(path, ConflictDetailText(), new DateTime(2026, 7, 20, 14, 31, 11, DateTimeKind.Utc));

    private static ConflictEntry Resurrection(string path) =>
        new(path, ResurrectionDetailText(), new DateTime(2026, 7, 20, 9, 16, 0, DateTimeKind.Utc));

    // ---- BuildLines ----

    [Fact]
    public void BuildLines_NothingToReview_ReturnsEmpty()
    {
        Assert.Empty(ReviewReport.BuildLines(Array.Empty<ConflictEntry>(), Array.Empty<ConflictEntry>()));
    }

    [Fact]
    public void BuildLines_Conflict_ShowsBothSidesAndTheRenamedCopy()
    {
        var text = string.Join("\n", ReviewReport.BuildLines(
            new[] { Conflict("docs/report.docx") }, Array.Empty<ConflictEntry>()));

        Assert.Contains("[CONFLICT] docs/report.docx", text);
        Assert.Contains("client: 2100000 bytes  2026-07-20 14:30:52Z", text);
        Assert.Contains("server: 2050112 bytes  2026-07-20 14:31:10Z", text);
        Assert.Contains("kept as: docs/report.conflict-20260720-143052-server.docx", text);
        Assert.Contains("both copies kept", text);
    }

    [Fact]
    public void BuildLines_Resurrection_ShowsBothSidesAndNoRenameLine()
    {
        var text = string.Join("\n", ReviewReport.BuildLines(
            Array.Empty<ConflictEntry>(), new[] { Resurrection("notes/todo.txt") }));

        Assert.Contains("[RESURRECTED] notes/todo.txt", text);
        Assert.Contains("client: 1024 bytes  2026-07-20 09:15:00Z", text);
        Assert.Contains("server: 900 bytes  2026-07-19 17:00:00Z", text);
        Assert.Contains("kept: modified after the peer deleted it", text);
        Assert.DoesNotContain("kept as:", text);
    }

    [Fact]
    public void BuildLines_HeaderCountsBothKinds()
    {
        var lines = ReviewReport.BuildLines(
            new[] { Conflict("a.docx"), Conflict("b.docx") },
            new[] { Resurrection("c.txt") });

        Assert.Equal("Review: 3 item(s) need attention", lines[0]);
    }

    [Fact]
    public void BuildLines_UndecodableDetail_StillListsTheFileAndPrintsTheRawText()
    {
        // A row written by a build that predates ConflictDetail decodes to null. Dropping it
        // would hide the exact case the review exists to surface, so it is listed verbatim.
        var entry = new ConflictEntry("legacy.docx", "both sides changed; kept both copies",
            new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc));

        var text = string.Join("\n", ReviewReport.BuildLines(new[] { entry }, Array.Empty<ConflictEntry>()));

        Assert.Contains("[CONFLICT] legacy.docx", text);
        Assert.Contains("detail: both sides changed; kept both copies", text);
        Assert.Contains("both copies kept", text);
    }

    [Fact]
    public void BuildLines_OutOfRangeTicks_PrintUnknownInsteadOfThrowing()
    {
        // long.MaxValue is not a valid DateTime tick count. new DateTime(ticks) would throw
        // ArgumentOutOfRangeException and take down the whole review over one corrupt row.
        var detail = new ConflictDetail(
            ClientSize: 5, ClientMtimeTicks: long.MaxValue,
            ServerSize: 6, ServerMtimeTicks: -1,
            RenamedTo: null).Encode();
        var entry = new ConflictEntry("corrupt.bin", detail, new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc));

        var text = string.Join("\n", ReviewReport.BuildLines(new[] { entry }, Array.Empty<ConflictEntry>()));

        Assert.Contains("[CONFLICT] corrupt.bin", text);
        Assert.Contains("client: 5 bytes  unknown", text);
        Assert.Contains("server: 6 bytes  unknown", text);
    }

    // ---- Emit ----

    [Fact]
    public void Emit_ReadsTheTwoActionsThroughTheirOwnReaders()
    {
        // LogConflict and LogResurrection are separate writers over the action column; nothing
        // inspects the detail string. This pins that the report never conflates the two.
        using var db = new SyncDatabase(_dbPath);
        var sessionId = db.StartSession("two-way+delete", _tempDir, "localhost", 15782);
        db.LogConflict("docs/report.docx", sessionId, ConflictDetailText());
        db.LogResurrection("notes/todo.txt", sessionId, ResurrectionDetailText());

        Assert.Equal("docs/report.docx", Assert.Single(db.GetSessionConflicts(sessionId)).Path);
        Assert.Equal("notes/todo.txt", Assert.Single(db.GetSessionResurrections(sessionId)).Path);
    }

    [Fact]
    public void Emit_LogsBothSectionsAndEmitsOneJsonEventPerItem()
    {
        using var sw = new StringWriter();
        var progress = new JsonProgressWriter(sw);

        using (var db = new SyncDatabase(_dbPath))
        using (var logger = new SyncLogger(verbose: false, logFile: _logPath, suppressConsole: true))
        {
            var sessionId = db.StartSession("two-way+delete", _tempDir, "localhost", 15782);
            db.LogConflict("docs/report.docx", sessionId, ConflictDetailText());
            db.LogResurrection("notes/todo.txt", sessionId, ResurrectionDetailText());

            ReviewReport.Emit(db, sessionId, logger, progress);
        }

        var log = File.ReadAllText(_logPath);
        Assert.Contains("Review: 2 item(s) need attention", log);
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
        Assert.Equal("docs/report.docx", first.GetProperty("path").GetString());
        Assert.Equal(2100000, first.GetProperty("client_size").GetInt64());
        Assert.Equal("docs/report.conflict-20260720-143052-server.docx",
            first.GetProperty("renamed_to").GetString());
        var second = JsonDocument.Parse(events[1]).RootElement;
        Assert.Equal("resurrection", second.GetProperty("kind").GetString());
        Assert.Equal("notes/todo.txt", second.GetProperty("path").GetString());
        Assert.Equal(1024, second.GetProperty("client_size").GetInt64());
        Assert.False(second.TryGetProperty("renamed_to", out _));
    }

    [Fact]
    public void Emit_UndecodableDetail_SendsTheSentinelInsteadOfAFabricatedSize()
    {
        using var sw = new StringWriter();
        var progress = new JsonProgressWriter(sw);

        using (var db = new SyncDatabase(_dbPath))
        using (var logger = new SyncLogger(verbose: false, logFile: _logPath, suppressConsole: true))
        {
            var sessionId = db.StartSession("two-way+delete", _tempDir, "localhost", 15782);
            db.LogConflict("legacy.docx", sessionId, "both sides changed; kept both copies");
            ReviewReport.Emit(db, sessionId, logger, progress);
        }

        var evt = JsonDocument.Parse(sw.ToString().Trim()).RootElement;
        Assert.Equal("legacy.docx", evt.GetProperty("path").GetString());
        Assert.Equal(-1, evt.GetProperty("client_size").GetInt64());
        Assert.Equal("", evt.GetProperty("client_mtime").GetString());
        Assert.Equal(-1, evt.GetProperty("server_size").GetInt64());
        Assert.False(evt.TryGetProperty("renamed_to", out _));
    }

    [Fact]
    public void Emit_CleanSession_PrintsAndEmitsNothing()
    {
        // A quiet sync must stay quiet. An empty "Review" header on every run trains the
        // operator to skip the section on the run that matters.
        using var sw = new StringWriter();
        var progress = new JsonProgressWriter(sw);

        using (var db = new SyncDatabase(_dbPath))
        using (var logger = new SyncLogger(verbose: false, logFile: _logPath, suppressConsole: true))
        {
            var sessionId = db.StartSession("two-way+delete", _tempDir, "localhost", 15782);
            ReviewReport.Emit(db, sessionId, logger, progress);
        }

        Assert.DoesNotContain("Review:", File.ReadAllText(_logPath));
        Assert.Equal("", sw.ToString());
    }

    [Fact]
    public void Emit_NullDatabaseOrNoSession_DoesNothing()
    {
        // SyncClient runs with _db == null on the binary-state fallback path, and leaves
        // sessionId at 0 whenever --delete is off (SyncClient.cs:116-122) — in both cases
        // nothing was ever logged, so there is nothing to read back.
        using var sw = new StringWriter();
        var progress = new JsonProgressWriter(sw);
        using var logger = new SyncLogger(verbose: false, logFile: _logPath, suppressConsole: true);

        ReviewReport.Emit(null, 1, logger, progress);
        using (var db = new SyncDatabase(_dbPath))
            ReviewReport.Emit(db, 0, logger, progress);

        Assert.Equal("", sw.ToString());
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ReviewReportTests"`

Expected: FAIL — `error CS0103: The name 'ReviewReport' does not exist in the current context` at every `ReviewReport.BuildLines` / `ReviewReport.Emit` call site.

- [ ] **Step 3: Implement**

Create `src/RemoteFileSync/Sync/ReviewReport.cs`:

```csharp
using System.Globalization;
using RemoteFileSync.Logging;
using RemoteFileSync.Progress;
using RemoteFileSync.State;

namespace RemoteFileSync.Sync;

/// <summary>
/// The end-of-sync review. Everything the sync could not decide on the operator's behalf — a
/// two-sided conflict where both copies were kept, and a file that survived the peer's deletion
/// because this side had edited it — is listed here, after the summary, so it is the last thing
/// on screen instead of one INF line buried in a thousand.
/// </summary>
public static class ReviewReport
{
    private const string ConflictTag      = "CONFLICT";
    private const string ResurrectionTag  = "RESURRECTED";
    private const string ConflictNote     = "both copies kept";
    private const string ResurrectionNote = "kept: modified after the peer deleted it";

    // The wire value of ProgressEvent.Kind. Deliberately not the same strings as the log tags:
    // the log is for humans, these are parsed by ExecRFS.
    private const string ConflictKind     = "conflict";
    private const string ResurrectionKind = "resurrection";

    /// <summary>Sentinel size for a row whose detail could not be decoded. The GUI renders this
    /// as "unknown"; a 0 would be indistinguishable from a genuinely empty file.</summary>
    private const long UnknownSize = -1;

    public static IReadOnlyList<string> BuildLines(
        IReadOnlyList<ConflictEntry> conflicts,
        IReadOnlyList<ConflictEntry> resurrections)
    {
        var lines = new List<string>();
        var total = conflicts.Count + resurrections.Count;
        if (total == 0) return lines;

        lines.Add($"Review: {total} item(s) need attention");
        foreach (var entry in conflicts)
            AppendItem(lines, ConflictTag, entry, ConflictNote);
        foreach (var entry in resurrections)
            AppendItem(lines, ResurrectionTag, entry, ResurrectionNote);
        return lines;
    }

    /// <summary>
    /// Reads the session's conflicts and resurrections back through their own readers and
    /// renders them. The two kinds are distinguished by the file_versions.action column that
    /// LogConflict and LogResurrection wrote — never by inspecting the detail string.
    /// </summary>
    public static void Emit(SyncDatabase? db, long sessionId, SyncLogger logger, JsonProgressWriter progress)
    {
        // No database, or no session, means no conflict row was ever written: SyncClient only
        // calls StartSession when a database is present and --delete is on (SyncClient.cs:116-122).
        if (db == null || sessionId <= 0) return;

        var conflicts = db.GetSessionConflicts(sessionId);
        var resurrections = db.GetSessionResurrections(sessionId);
        if (conflicts.Count == 0 && resurrections.Count == 0) return;

        foreach (var line in BuildLines(conflicts, resurrections))
            logger.Summary(line);

        foreach (var entry in conflicts)
            WriteEvent(progress, ConflictKind, entry);
        foreach (var entry in resurrections)
            WriteEvent(progress, ResurrectionKind, entry);
    }

    private static void AppendItem(List<string> lines, string tag, ConflictEntry entry, string note)
    {
        lines.Add($"  [{tag}] {entry.Path}");

        var detail = ConflictDetail.Decode(entry.Detail);
        if (detail == null)
        {
            // Written by a build that predates ConflictDetail, or hand-edited. Print it raw:
            // a dropped row hides precisely the case this report exists to surface.
            lines.Add($"      detail: {entry.Detail}");
        }
        else
        {
            lines.Add($"      client: {detail.ClientSize} bytes  {Stamp(detail.ClientMtimeTicks)}");
            lines.Add($"      server: {detail.ServerSize} bytes  {Stamp(detail.ServerMtimeTicks)}");
            if (detail.RenamedTo != null)
                lines.Add($"      kept as: {detail.RenamedTo}");
        }

        lines.Add($"      {note}");
    }

    private static void WriteEvent(JsonProgressWriter progress, string kind, ConflictEntry entry)
    {
        var detail = ConflictDetail.Decode(entry.Detail);
        if (detail == null)
        {
            progress.WriteReview(kind, entry.Path, UnknownSize, string.Empty, UnknownSize, string.Empty);
            return;
        }

        progress.WriteReview(kind, entry.Path,
            detail.ClientSize, Iso(detail.ClientMtimeTicks),
            detail.ServerSize, Iso(detail.ServerMtimeTicks),
            detail.RenamedTo);
    }

    // A tick count outside DateTime's range would make new DateTime(ticks) throw
    // ArgumentOutOfRangeException and abort the entire review over one corrupt row.
    private static bool TryUtc(long ticks, out DateTime utc)
    {
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            utc = default;
            return false;
        }
        utc = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    // InvariantCulture because ':' is the culture's time separator inside a custom format
    // string — on a de-DE console this would otherwise print 14.30.52.
    private static string Stamp(long ticks) =>
        TryUtc(ticks, out var utc)
            ? utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z"
            : "unknown";

    // Empty string, not "unknown", so the GUI's -1 size sentinel and an empty mtime always
    // travel together and mean exactly one thing.
    private static string Iso(long ticks) =>
        TryUtc(ticks, out var utc) ? utc.ToString("O", CultureInfo.InvariantCulture) : string.Empty;
}
```

- [ ] **Step 4: Run the test and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~ReviewReportTests"`

Expected: PASS. Green: `BuildLines_NothingToReview_ReturnsEmpty`, `BuildLines_Conflict_ShowsBothSidesAndTheRenamedCopy`, `BuildLines_Resurrection_ShowsBothSidesAndNoRenameLine`, `BuildLines_HeaderCountsBothKinds`, `BuildLines_UndecodableDetail_StillListsTheFileAndPrintsTheRawText`, `BuildLines_OutOfRangeTicks_PrintUnknownInsteadOfThrowing`, `Emit_ReadsTheTwoActionsThroughTheirOwnReaders`, `Emit_LogsBothSectionsAndEmitsOneJsonEventPerItem`, `Emit_UndecodableDetail_SendsTheSentinelInsteadOfAFabricatedSize`, `Emit_CleanSession_PrintsAndEmitsNothing`, `Emit_NullDatabaseOrNoSession_DoesNothing`.

---

### Task 9.4: Wire `ReviewReport.Emit` into `SyncClient`

**This task adds no test, and that is deliberate.** Driving `SyncClient.RunAsync` requires a live socket, so the only test with teeth for this one line is an integration test — and CONTRACT.md's ownership table assigns `tests/.../Integration/` to **Phase 10**. Adding one here would be an unowned edit. The obligation is handed to Phase 10 explicitly below rather than left implicit; the tests in Task 9.3 cover `Emit`'s behaviour but say nothing about whether it is called.

- [ ] **Step 1: Implement**

Exact current text at `src/RemoteFileSync/Network/SyncClient.cs:478-482`. **Anchor on the text, not the line number** — Phases 3, 5, 6, 7 and 8 all insert lines above this point, so it will have drifted. No lower-numbered phase edits any of these five lines: the ownership table assigns Phase 3 lines 89-113, Phase 6 lines 150-152 and 185-206, Phase 5 lines 209/371/425 plus the `File.Delete` deletion branches, Phase 7 the combined conflict + resurrection drain block (inserted above the `// 11. Exchange SyncComplete` landmark at `:472-474`, i.e. between the transfer phase and the summary line anchored below) together with the relocated ancestor-write block, and Phase 8 the delete guards at 233-256, the no-ancestor gate (`PairStateLost`) and the transfer-loop mode gating — all strictly above the `Sync complete:` summary.

```csharp
        var (scType, scData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var deletedLabel = filesDeleted > 0 ? $", {filesDeleted} deleted" : "";
        _logger.Summary($"Sync complete: {filesTransferred} files transferred{deletedLabel}, {bytesTransferred / (1024.0 * 1024.0):F1} MB, {sw.ElapsedMilliseconds}ms");

        // Fallback: save binary state when db is null (backward compat)
```

Replace exactly with:

```csharp
        var (scType, scData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var deletedLabel = filesDeleted > 0 ? $", {filesDeleted} deleted" : "";
        _logger.Summary($"Sync complete: {filesTransferred} files transferred{deletedLabel}, {bytesTransferred / (1024.0 * 1024.0):F1} MB, {sw.ElapsedMilliseconds}ms");

        // Placed after the summary so it is the last thing on screen. A conflict or a
        // resurrection is a decision the sync made on the operator's behalf, and a single INF
        // line among a thousand transfer lines is not a report — they will not see it.
        // Reads only rows Phase 7's drain already committed; it never writes.
        ReviewReport.Emit(_db, sessionId, _logger, _progress);

        // Fallback: save binary state when db is null (backward compat)
```

`SyncClient.cs:9` already has `using RemoteFileSync.Sync;`, so `ReviewReport` resolves without a new using. `_db` (`:21`), `sessionId` (`:116`), `_logger` (`:17`) and `_progress` (`:19`) are all existing locals/fields in scope at this point — **none is redeclared**.

The call sits above the `return exitCode;` at `:490` and therefore inside the `try` whose `finally` calls `CompleteSession` (`:492-502`), so a throw here still closes the session row. It runs for `exitCode == 1` (skipped files) as well as `0` — a run that skipped files is exactly the run whose conflicts most need showing.

- [ ] **Step 2: Verify the build and full suite**

Run: `dotnet build -c Release` then `dotnet test -c Release`

Expected: PASS, with no test newly red.

- [ ] **Step 3: Hand the wiring test to Phase 10 (record it, do not implement it here)**

Phase 10 must add, in `tests/RemoteFileSync.Tests/Integration/`, a two-way + `--delete` run in which both sides modify the same path so `planResult.Conflicts` is non-empty, asserting on the client log that:
- `Review: 1 item(s) need attention` appears, and
- its index in the log is **greater** than the index of `Sync complete:` (ordering is the point of the placement above), and
- the `[CONFLICT] <path>` line carries a real `client:`/`server:` size, not the `detail:` fallback — which is what proves **Phase 7's** drain encodes through `ConflictDetail.Encode()`.

Suggested name: `TwoWayConflict_ReviewSectionFollowsTheSummaryWithBothSides`.

Without it, this one-line call site ships unverified end to end.

---

### Phase 9 commit

```bash
git add src/RemoteFileSync/Sync/ReviewReport.cs \
        src/RemoteFileSync/Progress/JsonProgressWriter.cs \
        src/RemoteFileSync/Network/SyncClient.cs \
        src/ExecRFS/Models/ProgressEvent.cs \
        tests/RemoteFileSync.Tests/Sync/ReviewReportTests.cs \
        tests/RemoteFileSync.Tests/Progress/JsonProgressWriterTests.cs \
        tests/ExecRFS.Tests/Models/ProgressEventTests.cs

git commit -m "feat: end-of-sync review report for conflicts and resurrections

Every decision the sync made on the operator's behalf has to be shown, not
buried in a per-file INF line. Adds a review section printed after the
'Sync complete:' summary listing each conflict and each resurrection with
both sides' size and mtime, plus the conflict copy's new name.

The two kinds are read back through their own readers, GetSessionConflicts
and GetSessionResurrections, which filter on the file_versions.action column
written by LogConflict and LogResurrection. Nothing inspects the detail
string to decide which kind a row is. The detail itself is decoded with
ConflictDetail.Decode; a null return falls back to printing the stored text
verbatim, because dropping the row would hide exactly the case the report
exists to surface. Out-of-range tick counts render as 'unknown' rather than
throwing ArgumentOutOfRangeException and aborting the whole review.

Mirrored as one flat 'review' JSON progress event per item, like file_end and
delete, since ProgressEvent cannot carry a nested array; ProgressEvent gains
kind/client_size/client_mtime/server_size/server_mtime/renamed_to and reuses
the existing path property. renamed_to is omitted rather than null when
nothing was renamed, and a -1 size with an empty mtime is the 'could not
decode' sentinel.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"

git push -u origin feat/deletion-sync-ancestor-merge
```

**Verification before commit:**

```bash
dotnet build -c Release
dotnet test -c Release
dotnet test -c Release --filter "FullyQualifiedName~ReviewReportTests"
dotnet test -c Release --filter "FullyQualifiedName~JsonProgressWriterTests"
dotnet test -c Release --filter "FullyQualifiedName~ProgressEventTests"
```

Expected: 0 build errors, 0 warnings introduced, and no previously-green test turning red.

Knowing accepted changes to existing test files, both append-only:
- `JsonProgressWriterTests.NullWriter_NoOutput` gains one `writer.WriteReview(...)` call so the Null writer is proven to swallow the new event too. No existing assertion altered; the other seven tests in the file are untouched.
- `ProgressEventTests` gains two `[Fact]`s after `TryParse_ErrorEvent`. All eight existing tests untouched.

No production behaviour changes for a session with nothing to review: `Emit` returns before writing a byte when both readers come back empty, when `_db` is null, or when `sessionId` is 0.

---

## Phase 10: End-to-end acceptance tests and documentation

**Goal:** Prove the ancestor merge, mirror semantics, convergence and the no-ancestor safety gate over a real loopback socket; move the existing integration suite off the retired binary-state ancestor and onto `SyncDatabase` and the archive layout; and document `--mode`, `--mirror`, the archive and its retention, protocol v3, the ancestor model, the upgrade path and the revised known gaps.

**Files:**
- Create: `tests/RemoteFileSync.Tests/Integration/ArchiveAssertions.cs`
- Create: `tests/RemoteFileSync.Tests/Integration/TwoWayMergeE2ETests.cs`
- Rewrite whole file: `tests/RemoteFileSync.Tests/Integration/DeleteSyncTests.cs` (currently 220 lines)
- Modify: `tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs:1-5`, `:108-114`, `:142-144`, `:151`, `:158`, `:181`, `:201`, `:209`, `:230`, `:235-236`
- Modify: `tests/RemoteFileSync.Tests/Integration/DeleteThresholdTests.cs:49` (the server `SyncOptions` initialiser inside `RunClientAsync`), `:73-85` (`SeedTrackedFiles`)
- Modify: `README.md:35-41`, `:52`, `:56`, `:102-104`, `:108-115`, `:135-140`
- **Not modified:** `tests/RemoteFileSync.Tests/Integration/DatabaseDeleteSyncTests.cs`. Phase 1 already migrated its only `Bidirectional =` assignment (`:56`); it already drives a real `SyncDatabase`, its ancestor rows survive the schema-v2 migration, and its assertions describe file presence rather than backup paths. Phase 10 has no edit to make there.

---

### Interfaces

**Consumes — from lower-numbered phases, as those phases left the code. Phase 10 re-applies none of it.**

- **Phase 1:** `SyncMode.Push` / `SyncMode.Pull` / `SyncMode.TwoWay`; `SyncOptions.Mode`, `SyncOptions.MirrorDeletes`, `SyncOptions.Bidirectional` (get-only), `SyncOptions.MinTrackedFilesForDeleteGuard`. **Phase 1 owns every `Bidirectional =` assignment in `src/` and `tests/` and has already migrated all seven test call sites** (`EndToEndTests.cs:52`, `:86`, `:126`, `:158`, `DeleteSyncTests.cs:53`, `DatabaseDeleteSyncTests.cs:56`, `DeleteThresholdTests.cs:53`). Phase 10 quotes those lines in their post-Phase-1 form and never re-applies the migration. The suite therefore **compiles** at the start of this phase; it is red on assertions, not on CS0200.
- **Phase 2:** nothing directly. `ClockSkew` is exercised only through behaviour.
- **Phase 4:** `SyncDatabase.UpsertSynced(string, long, long, long, long, long, string)`, `GetRow(string)`, `GetRecentSessions(int)` (newest first — `ORDER BY id DESC`, `SyncDatabase.cs:158`), `StartSession(string, string, string, int)`, `CompleteSession(long, int, int, int, int)`, `GetSessionConflicts(long)`, `GetSessionResurrections(long)`; `AncestorRow.Status` / `AncestorRow.DeletedUtcTicks`; `ConflictEntry.Path`; `PairMarker.Exists(string)`.
- **Phase 5:** `ArchiveReason.Deleted` / `.Overwritten` / `.Conflict`, and the on-disk layout `<archiveRoot>/<yyyyMMdd-HHmmss>/<reason>/<relative path>`. Phase 5 owns the `archive` local in both `HandleConnectionAsync` methods; Phase 10 only observes its output on disk.
- **Phase 6:** `ComputePlan` returning `PlanResult`, including its `Resurrections` and `Conflicts` collections. Phase 6 computes them; it does not persist them.
- **Phase 7:** the conflict rename pass, **and the client's post-transfer drain of both `PlanResult.Conflicts` and `PlanResult.Resurrections` into `SyncDatabase.LogConflict` / `LogResurrection`** — one edit block at one anchor, owned by Phase 7. Task 10.4's and `DeleteSyncTests.DeleteSync_Case2_RestoresModifiedFile`'s resurrection assertions and Task 10.5's conflict assertion read *only* through `GetSessionResurrections` / `GetSessionConflicts`, so they are the acceptance check on that drain — and the resurrection assertions have a real writer precisely because Phase 7 owns it alongside the conflict drain.
- **Phase 8:** mode dispatch, the Push/Pull decision tables, the delete guards, **and the no-ancestor gate inside `SyncClient.RunAsync` returning exit 4 before the socket opens**, reached through the constructor's trailing `string? dbPath = null` parameter and the public predicate `SyncClient.PairStateLost(string)`, plus `PairMarker.Write(_dbPath)` on a clean exit. `SyncDatabase` gained no new members for this; there is no `DatabasePath` and no `ExistedBeforeOpen`, and nothing in this phase may reference either. Because `new SyncDatabase(path)` *creates* the file whose absence the gate tests for, **every helper in this phase hands the client a path (`dbPath: DbPath`) and never an open `SyncDatabase`**, and opens its own short-lived instance for post-run assertions only, after the client has disposed the one it owned. Task 10.9 is the only integration test that exercises the gate.

**Produces:** nothing consumed by a later phase. Phase 10 is terminal.

**Ownership note for `DeleteThresholdTests`.** The CONTRACT ownership table assigns integration test files under `tests/.../Integration/` to **Phase 10**, except the mechanical `Bidirectional`→`Mode` migration (Phase 1's) — and states that Phase 8 adds no integration tests. **Phase 10 is therefore the sole owner of both `DeleteThresholdTests.SeedTrackedFiles` and the server `SyncOptions` initialiser inside `RunClientAsync`**, and quotes both as they stand on `main` (Phase 1 touched only `:53`, the *client* options inside `RunClientAsync`). Phase 8 hands over the `ForceDelete = forceDelete` fix at the end of its own write-up but must not apply it; Phase 10 applies it in Step 5.

> **Stated deviation from strict TDD, and why.** Task 10.1 is genuinely red-first: after Phases 1-8 land, the migrated suite compiles but fails on assertions that describe the retired backup tree and the retired binary-state ancestor. Tasks 10.3-10.9 are *acceptance* tests over behaviour Phases 1-8 already implemented; making them red first would mean reverting those phases. They use a two-step rhythm — write, then run — and each Step 2 names the concrete diagnostic that identifies which earlier phase is wrong if it fails.

---

### Task 10.1: Run the migrated suite and see exactly what the redesign broke

- [ ] **Step 1: Run the existing integration suite and watch it fail**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Integration"`

Expected: **FAIL**, with these failures and no compile errors (Phase 1 restored compilation when it removed the `Bidirectional` setter):

| Test | Failure |
|---|---|
| `EndToEndTests.BiDirectional_BothSidesSync` | `Assert.True() Failure` on `.rfs-backups-server/<yyyyMMdd>/shared.txt` — Phase 5 writes `.rfs-archive-server/<yyyyMMdd-HHmmss>/overwritten/shared.txt` |
| `DeleteSyncTests.DeleteSync_Case1_PropagatesDeletion` | `Assert.False() Failure` on `_serverDir/to-delete.txt` — the `SyncStateManager` ancestor no longer feeds `ComputePlan`, so with `ancestor == null` the file is re-sent, not deleted |
| `DeleteSyncTests.DeleteSync_Case2_RestoresModifiedFile` | passes, but for the wrong reason (no-ancestor fallback, not rule [2]) |
| `DeleteSyncTests.DeleteSync_BidiSymmetric` | `Assert.False() Failure` on both files, same cause |
| `DeleteSyncTests.DeleteSync_SecondRun_DetectsDeletions` | `Assert.False() Failure` on `_serverDir/will-delete.txt`, same cause |
| `DeleteSyncTests.DeleteSync_UniDirectional_ServerDeletionIgnored` | `Assert.False() Failure` on `_serverDir/file.txt` — the Push table sends unconditionally on `client present, server absent` |
| `DeleteThresholdTests.EmptyPeerFolder_AbortsInsteadOfMassDeleting` | `Assert.Equal() Failure: Expected 4, Actual 0` — the seeded row's size (hardcoded `9`) and mtime (`UtcNow - 1 day`) do not match the file on disk, so `ChangeDetector.Unchanged` reports "client changed", no deletes are planned, and the threshold guard never fires |
| `DeleteThresholdTests.ForceDelete_OverridesTheThreshold` | **errors rather than fails** — an unhandled exception out of `RunAsync`. Phase 8's server guard now bounds `DeleteOnClient` too, but `RunClientAsync` sets `ForceDelete` on the *client* options only; the server refuses 20 of 20 at 100% and returns 4 straight after reading the plan. The client, having skipped its own guard, reaches step 10 and blocks on `ProtocolHandler.ReadMessageAsync` (`SyncClient.cs:411`, *outside* the surrounding `try`), so the closed connection throws out |

If Phase 4 removed the `MarkSynced` shim rather than keeping it, `DeleteThresholdTests` fails to compile instead. Either way it is red, and Steps 4 and 5 fix both.

- [ ] **Step 2: Add the shared archive assertions**

Create `tests/RemoteFileSync.Tests/Integration/ArchiveAssertions.cs`. One copy, used by all three suites — three byte-identical private helpers were how the previous draft let them drift.

```csharp
using RemoteFileSync.Backup;

namespace RemoteFileSync.Tests.Integration;

/// <summary>
/// Assertions over the archive tree laid out as
/// &lt;archiveRoot&gt;/&lt;yyyyMMdd-HHmmss&gt;/&lt;reason&gt;/&lt;relative path&gt;.
/// The session folder is stamped at sync start, so a test cannot spell it out and must
/// search for the file instead.
/// </summary>
internal static class ArchiveAssertions
{
    /// <summary>
    /// Asserts exactly one archived copy of <paramref name="fileName"/> exists under the
    /// expected reason folder, and returns its full path. Insisting on exactly one is the
    /// point: two copies mean a single run scattered itself across two session folders.
    /// </summary>
    public static string AssertArchived(string archiveRoot, ArchiveReason reason, string fileName)
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
    public static void AssertNothingArchived(string archiveRoot)
    {
        if (!Directory.Exists(archiveRoot)) return;
        Assert.Empty(Directory.GetFiles(archiveRoot, "*", SearchOption.AllDirectories));
    }
}
```

- [ ] **Step 3: Migrate `EndToEndTests.cs`**

`EndToEndTests.cs:1-5` — replace exactly:

```csharp
using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
```

with:

```csharp
using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Backup;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using static RemoteFileSync.Tests.Integration.ArchiveAssertions;
```

`EndToEndTests.cs:108-114` — replace exactly:

```csharp
        // Server's old shared.txt should be backed up — beside the sync folder, not inside it.
        var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
        var backupPath = Path.Combine(_testRoot, ".rfs-backups-server", dateStr, "shared.txt");
        Assert.True(File.Exists(backupPath), $"expected backup at {backupPath}");
        Assert.Equal("server older", File.ReadAllText(backupPath));
        // And nothing may have been written inside the sync folder itself.
        Assert.False(Directory.Exists(Path.Combine(_serverDir, dateStr)));
```

with:

```csharp
        // Server's old shared.txt must be archived before it is overwritten — beside the sync
        // folder, not inside it, or the archived copy is re-scanned as a new file next run.
        var archived = AssertArchived(Path.Combine(_testRoot, ".rfs-archive-server"),
            ArchiveReason.Overwritten, "shared.txt");
        Assert.Equal("server older", File.ReadAllText(archived));
        Assert.Empty(Directory.GetDirectories(_serverDir));
```

`EndToEndTests.cs:142-144` — replace exactly:

```csharp
        var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
        Assert.False(Directory.Exists(Path.Combine(_serverDir, dateStr)));
        Assert.False(Directory.Exists(Path.Combine(_clientDir, dateStr)));
```

with:

```csharp
        // Nothing was replaced, so nothing may be archived, and no session folder may appear
        // inside either sync folder.
        AssertNothingArchived(Path.Combine(_testRoot, ".rfs-archive-server"));
        AssertNothingArchived(Path.Combine(_testRoot, ".rfs-archive-client"));
        Assert.Empty(Directory.GetDirectories(_serverDir));
        Assert.Empty(Directory.GetDirectories(_clientDir));
```

`EndToEndTests.cs:151` — replace exactly:

```csharp
    private async Task<(int client, int server)> RunSyncAsync(bool bidirectional)
```

with:

```csharp
    private async Task<(int client, int server)> RunSyncAsync(SyncMode mode)
```

`EndToEndTests.cs:158` — Phase 1 rewrote this line; quote it in its post-Phase-1 form. Replace exactly:

```csharp
            Folder = _clientDir, Mode = bidirectional ? SyncMode.TwoWay : SyncMode.Push
```

with:

```csharp
            Folder = _clientDir, Mode = mode
```

`EndToEndTests.cs:181` — replace exactly `        await RunSyncAsync(bidirectional: false);` with `        await RunSyncAsync(SyncMode.Push);`

`EndToEndTests.cs:201` — replace exactly `        var first = await RunSyncAsync(bidirectional: true);` with `        var first = await RunSyncAsync(SyncMode.TwoWay);`

`EndToEndTests.cs:209` — replace exactly `        var second = await RunSyncAsync(bidirectional: true);` with `        var second = await RunSyncAsync(SyncMode.TwoWay);`

`EndToEndTests.cs:230` — replace exactly `        await RunSyncAsync(bidirectional: true);` with `        await RunSyncAsync(SyncMode.TwoWay);`

`EndToEndTests.cs:235-236` — replace exactly:

```csharp
        await RunSyncAsync(bidirectional: true);
        await RunSyncAsync(bidirectional: true);
```

with:

```csharp
        await RunSyncAsync(SyncMode.TwoWay);
        await RunSyncAsync(SyncMode.TwoWay);
```

- [ ] **Step 4: Fix `DeleteThresholdTests.SeedTrackedFiles`**

`DeleteThresholdTests.cs:73-85` — replace exactly:

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
            // The row must be read back from the file just written, never guessed. The old seed
            // hardcoded size 9 (wrong from file010.txt onward) and an mtime of UtcNow-1day, so
            // ChangeDetector.Unchanged reported "the client changed since the last sync" and the
            // plan came back with zero deletions — the threshold guard could not fire and this
            // suite passed 4-vs-0 for a reason unrelated to the guard it names.
            var fi = new FileInfo(full);
            db.UpsertSynced(name, fi.Length, fi.LastWriteTimeUtc.Ticks,
                                  fi.Length, fi.LastWriteTimeUtc.Ticks, session, "to_server");
        }
        db.CompleteSession(session, count, 0, 0, 0);
        return db;
    }
```

No `PairMarker.Write` here, and no new using: `using RemoteFileSync.State;` is already at `DeleteThresholdTests.cs:6`. `SyncClient.PairStateLost` returns true only when the marker exists **and** the database is absent or unreadable; `SeedTrackedFiles` leaves a fully populated database on disk and writes no marker, so the gate is not reachable in this suite in either direction. The previous draft's comment claiming the gate "would abort first" was inverted and is gone.

- [ ] **Step 5: Give the server `ForceDelete` in `DeleteThresholdTests.RunClientAsync`**

Phase 8's rebuilt server guard bounds `DeleteOnClient` as well as `DeleteOnServer`, and it reads `_options.ForceDelete` from the **server's** options. `RunClientAsync` passes `forceDelete` to the client only, so `ForceDelete_OverridesTheThreshold` errors out (see the table in Step 1). Phase 8 documents the fix and hands it here.

`DeleteThresholdTests.cs:49` — replace exactly:

```csharp
        var serverOpts = new SyncOptions { IsServer = true, Once = true, Port = port, Folder = _serverDir };
```

with:

```csharp
        // The server enforces its blast-radius bound independently of the client — it does not
        // trust a plan from an unauthenticated peer — so an intentional bulk deletion needs
        // --force-delete on BOTH sides, which is what both guard messages now tell the operator.
        // Without this the server returns 4 immediately after reading the plan, the client (which
        // skipped its own guard) blocks on a read outside its try block, and the test errors on a
        // torn socket instead of asserting anything.
        var serverOpts = new SyncOptions { IsServer = true, Once = true, Port = port, Folder = _serverDir, ForceDelete = forceDelete };
```

Re-derive the exact text with `git grep -n "IsServer = true" -- tests/RemoteFileSync.Tests/Integration/DeleteThresholdTests.cs` before applying; Phase 1 did not touch this line, but the `DeleteEnabled`/`MaxDeletePercent` members of the initialiser must be preserved verbatim if the file on `main` carries them.

This does not weaken `EmptyPeerFolder_AbortsInsteadOfMassDeleting`: that test passes `forceDelete: false`, so both guards stay armed. It passes on the *client's* guard (20 of 20 client files, 100% > 25) which returns 4 before reading anything further from the peer; the server independently returns 4 from its own guard, and the test's existing `try { await serverTask; } catch { }` absorbs that.

- [ ] **Step 6: Rewrite `DeleteSyncTests.cs`**

This suite seeds its ancestor through `SyncStateManager.SaveState`. Phase 6 replaced `ComputePlan`'s `previousState` parameter with `IReadOnlyDictionary<string, AncestorRow>? ancestor`, fed from `_db?.LoadAll()`, so `SyncStateManager` no longer influences any decision — every run in this file became the no-ancestor fallback and no deletion could propagate. Migrating the seeds to `SyncDatabase` is the only way to keep these cases testing what their names claim.

Replace the entire contents of `tests/RemoteFileSync.Tests/Integration/DeleteSyncTests.cs` with:

```csharp
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using RemoteFileSync.Backup;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.State;
using static RemoteFileSync.Tests.Integration.ArchiveAssertions;

namespace RemoteFileSync.Tests.Integration;

/// <summary>
/// Deletion propagation over a real socket, with the ancestor supplied by SyncDatabase.
/// These cases used to seed SyncStateManager; the binary-state ancestor was retired when
/// ComputePlan started taking an AncestorRow table, and a SyncStateManager seed now
/// influences nothing at all.
/// </summary>
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
        // SQLite keeps the file handle in a connection pool; without this the temp tree
        // cannot be deleted and every run leaks a directory.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }

    private string DbPath => Path.Combine(_stateDir, "sync.db");

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
    /// Seeds an ancestor row asserting both sides held <paramref name="relativePath"/> at
    /// <paramref name="mtime"/> with <paramref name="size"/> bytes when they last agreed.
    /// </summary>
    private void SeedAncestor(SyncDatabase db, string relativePath, long size, DateTime mtime)
    {
        var session = db.StartSession("two-way+delete", _clientDir, "127.0.0.1", 1234);
        db.UpsertSynced(relativePath, size, mtime.Ticks, size, mtime.Ticks, session, "to_server");
        db.CompleteSession(session, 1, 0, 0, 0);
    }

    /// <summary>
    /// One full client/server sync. Once=true on the server, or the test hangs waiting for a
    /// second connection that never arrives.
    ///
    /// The client is handed a database *path*, never an open SyncDatabase: `new SyncDatabase(p)`
    /// creates the file, and the no-ancestor gate in SyncClient.RunAsync fires on "pair.marker
    /// present, database absent". A test that opened the database around the run would create the
    /// very file the gate looks for and silently disarm it. Post-run assertions therefore open
    /// their own short-lived instance after this method returns.
    /// </summary>
    private async Task<(int clientResult, int serverResult)> RunSyncAsync(
        SyncMode mode, bool deleteEnabled)
    {
        int port = GetFreePort();
        var serverOpts = new SyncOptions { IsServer = true, Once = true, Port = port, Folder = _serverDir, DeleteEnabled = deleteEnabled };
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Mode = mode, DeleteEnabled = deleteEnabled };

        using var serverLogger = new SyncLogger(false, null);
        using var clientLogger = new SyncLogger(false, null);

        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger, dbPath: DbPath);

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
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "client-file.txt", "from client", ts);
        CreateFileWithTimestamp(_serverDir, "server-file.txt", "from server", ts);

        var (clientResult, serverResult) = await RunSyncAsync(SyncMode.TwoWay, deleteEnabled: true);
        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);

        Assert.True(File.Exists(Path.Combine(_serverDir, "client-file.txt")));
        Assert.True(File.Exists(Path.Combine(_clientDir, "server-file.txt")));
        // A clean first run claims the pairing. From here on, a missing database is evidence of
        // lost state rather than of a tree that was never synced.
        Assert.True(PairMarker.Exists(DbPath));
        AssertNothingArchived(Path.Combine(_testRoot, ".rfs-archive-server"));
        AssertNothingArchived(Path.Combine(_testRoot, ".rfs-archive-client"));
    }

    [Fact]
    public async Task DeleteSync_Case1_PropagatesDeletion()
    {
        var beforeSync = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_serverDir, "to-delete.txt", "will be deleted", beforeSync);

        using (var seed = new SyncDatabase(DbPath))
            SeedAncestor(seed, "to-delete.txt", 15, beforeSync);
        SqliteConnection.ClearAllPools();

        var (clientResult, serverResult) = await RunSyncAsync(SyncMode.TwoWay, deleteEnabled: true);

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        Assert.False(File.Exists(Path.Combine(_serverDir, "to-delete.txt")));
        AssertArchived(Path.Combine(_testRoot, ".rfs-archive-server"), ArchiveReason.Deleted, "to-delete.txt");

        // Reopened only now, after the client has disposed the database it opened for itself.
        using var db = new SyncDatabase(DbPath);
        Assert.Equal("deleted", db.GetRow("to-delete.txt")!.Status);
    }

    [Fact]
    public async Task DeleteSync_Case2_RestoresModifiedFile()
    {
        var beforeSync = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var afterSync = new DateTime(2026, 3, 27, 8, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_serverDir, "modified.txt", "modified content", afterSync);

        using (var seed = new SyncDatabase(DbPath))
            SeedAncestor(seed, "modified.txt", 16, beforeSync);
        SqliteConnection.ClearAllPools();

        var (clientResult, serverResult) = await RunSyncAsync(SyncMode.TwoWay, deleteEnabled: true);

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        // Rule [2]: an edit outranks a deletion. Deleting the peer's newer work because the
        // local copy vanished is the single most destructive outcome this design prevents.
        Assert.True(File.Exists(Path.Combine(_serverDir, "modified.txt")));
        Assert.True(File.Exists(Path.Combine(_clientDir, "modified.txt")));
        Assert.Equal("modified content", File.ReadAllText(Path.Combine(_clientDir, "modified.txt")));

        // Restoring a file the user deleted is surprising, so it must be reported, not silent.
        // The writer is Phase 7's drain of PlanResult.Resurrections into LogResurrection, in the
        // same edit block as its conflict drain; without it this row is never written.
        using var db = new SyncDatabase(DbPath);
        var sessionId = db.GetRecentSessions(1).First().Id;
        Assert.Contains(db.GetSessionResurrections(sessionId),
            r => r.Path.Equals("modified.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeleteSync_BidiSymmetric()
    {
        var beforeSync = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_serverDir, "client-deleted.txt", "from server", beforeSync);
        CreateFileWithTimestamp(_clientDir, "server-deleted.txt", "from client", beforeSync);

        using (var seed = new SyncDatabase(DbPath))
        {
            SeedAncestor(seed, "client-deleted.txt", 11, beforeSync);
            SeedAncestor(seed, "server-deleted.txt", 11, beforeSync);
        }
        SqliteConnection.ClearAllPools();

        var (clientResult, serverResult) = await RunSyncAsync(SyncMode.TwoWay, deleteEnabled: true);

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        Assert.False(File.Exists(Path.Combine(_serverDir, "client-deleted.txt")));
        Assert.False(File.Exists(Path.Combine(_clientDir, "server-deleted.txt")));
        // Each side archives what it destroys, into its own archive root.
        AssertArchived(Path.Combine(_testRoot, ".rfs-archive-server"), ArchiveReason.Deleted, "client-deleted.txt");
        AssertArchived(Path.Combine(_testRoot, ".rfs-archive-client"), ArchiveReason.Deleted, "server-deleted.txt");
    }

    [Fact]
    public async Task Push_ServerSideDeletion_IsReSentBecauseTheClientIsAuthoritative()
    {
        // Renamed from DeleteSync_UniDirectional_ServerDeletionIgnored, and the expectation is
        // inverted on purpose: in Push mode the server is made to match the client, so
        // "client present, server absent" is SendToServer with no ancestor consulted. The old
        // behaviour — leave the gap alone — meant a file the user deleted on the server stayed
        // deleted while the client still held it, and the pair never converged.
        var beforeSync = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "file.txt", "still here", beforeSync);

        using (var seed = new SyncDatabase(DbPath))
            SeedAncestor(seed, "file.txt", 10, beforeSync);
        SqliteConnection.ClearAllPools();

        var (clientResult, serverResult) = await RunSyncAsync(SyncMode.Push, deleteEnabled: true);

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        Assert.True(File.Exists(Path.Combine(_clientDir, "file.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "file.txt")));
        Assert.Equal("still here", File.ReadAllText(Path.Combine(_serverDir, "file.txt")));
    }

    [Fact]
    public async Task DeleteSync_SecondRun_DetectsDeletions()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "keep.txt", "keep this", ts);
        CreateFileWithTimestamp(_serverDir, "keep.txt", "keep this", ts);
        CreateFileWithTimestamp(_clientDir, "will-delete.txt", "will be deleted", ts);
        CreateFileWithTimestamp(_serverDir, "will-delete.txt", "will be deleted", ts);

        var (r1c, r1s) = await RunSyncAsync(SyncMode.TwoWay, deleteEnabled: true);
        Assert.Equal(0, r1c);
        Assert.Equal(0, r1s);

        File.Delete(Path.Combine(_clientDir, "will-delete.txt"));

        var (r2c, r2s) = await RunSyncAsync(SyncMode.TwoWay, deleteEnabled: true);
        Assert.Equal(0, r2c);
        Assert.Equal(0, r2s);

        // Run 1 wrote pair.marker on its clean exit, and run 2 found the database still there,
        // so the no-ancestor gate stayed silent and run 2 is a genuine ancestor merge.
        using (var db = new SyncDatabase(DbPath))
        {
            Assert.Equal("deleted", db.GetRow("will-delete.txt")!.Status);
            Assert.Equal("exists", db.GetRow("keep.txt")!.Status);
        }

        Assert.False(File.Exists(Path.Combine(_serverDir, "will-delete.txt")));
        Assert.True(File.Exists(Path.Combine(_clientDir, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "keep.txt")));
    }
}
```

- [ ] **Step 7: Run the migrated suite and watch it pass**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Integration"`
Expected: **PASS**. Every method in `EndToEndTests`, `DeleteSyncTests`, `DatabaseDeleteSyncTests` and `DeleteThresholdTests` must be green, in particular `EndToEndTests.BiDirectional_BothSidesSync`, `DeleteSyncTests.DeleteSync_Case1_PropagatesDeletion`, `DeleteSyncTests.DeleteSync_BidiSymmetric`, `DeleteSyncTests.DeleteSync_SecondRun_DetectsDeletions`, `DeleteThresholdTests.EmptyPeerFolder_AbortsInsteadOfMassDeleting` and `DeleteThresholdTests.ForceDelete_OverridesTheThreshold`.

#### Where assertion semantics change — audited case by case

The previous draft claimed "assertion semantics are preserved in every case". That was false. Re-audited against the seam Phase 8 actually built — the client is handed `dbPath:` and owns its database, so every seed and every read-back in `DeleteSyncTests` moves outside the run rather than wrapping it. That restructuring is mechanical and changes no expectation; the rows below list only what a reader would otherwise be surprised by.

| Test | Semantics | Why |
|---|---|---|
| `EndToEndTests.BiDirectional_BothSidesSync` | **changed** | Asserted a dated `.rfs-backups-server/<yyyyMMdd>/` path; now asserts one copy under `.rfs-archive-server/<session>/overwritten/`. Required by the contract's archive layout. Also strengthened: `Assert.Empty(GetDirectories(_serverDir))` forbids *any* directory in the sync folder, not just today's date stamp. |
| `EndToEndTests.IdenticalFiles_NothingTransferred` | **changed (stronger)** | Was two `Directory.Exists` checks against a date-stamped name; now asserts zero archived files under both roots plus zero subdirectories. |
| `EndToEndTests` — remaining four | preserved | `RunSyncAsync(bool)` → `RunSyncAsync(SyncMode)` is a rename of the same two behaviours. |
| `DeleteSyncTests.DeleteSync_FirstRun_NoState_AdditiveOnly` | **changed** | Asserted `File.Exists(stateManager.GetStatePath(...))`. `SyncStateManager` no longer carries the ancestor, so that assertion tested a dead artefact; it is replaced by `PairMarker.Exists(DbPath)`, which is the artefact the new first-run contract actually produces. The additive-only assertions are unchanged. |
| `DeleteSyncTests.DeleteSync_Case1_PropagatesDeletion` | preserved, seed changed | Same expectation (server copy deleted and archived). The ancestor moves from `SyncStateManager.SaveState` to `SyncDatabase.UpsertSynced`. Adds a tombstone assertion. |
| `DeleteSyncTests.DeleteSync_Case2_RestoresModifiedFile` | preserved, **strengthened** | Same file expectations, plus the resurrection must now appear in `GetSessionResurrections`. Under the old engine it was restored silently. The writer exists: Phase 7 owns the `PlanResult.Resurrections` → `LogResurrection` drain, at the same anchor and in the same edit block as its conflict drain, so this assertion is not aimed at a table nobody fills. |
| `DeleteSyncTests.DeleteSync_BidiSymmetric` | preserved, archive path changed | Both deletions still propagate; the backup path assertions become archive assertions. |
| `DeleteSyncTests.DeleteSync_UniDirectional_ServerDeletionIgnored` | **inverted, and renamed** to `Push_ServerSideDeletion_IsReSentBecauseTheClientIsAuthoritative` | The old test asserted `Assert.False(File.Exists(_serverDir/file.txt))` — a server-side deletion was neither propagated nor repaired. The Push table (CONTRACT: "client present, server absent -> SendToServer", unconditional) makes the file reappear on the server. This is a deliberate behaviour change, not a test fix, and the rename records it. |
| `DeleteSyncTests.DeleteSync_SecondRun_DetectsDeletions` | preserved, seed changed | Was omitted from the previous draft's change table entirely. Same two-run shape; the ancestor now comes from the database, ports are allocated per run because the state key no longer depends on the port, and it now also asserts the row's tombstone/`exists` status. |
| `DeleteSyncTests` — all six | harness only | `RunSyncAsync` drops its `SyncDatabase? db` parameter and hands the client `dbPath: DbPath`. Seeds move into a `using (var seed = …)` block *before* the run and read-backs into a fresh instance *after* it, because an instance held open across the run re-creates the database file and disarms the no-ancestor gate. No expectation changes. |
| `DeleteThresholdTests.SeedTrackedFiles` | helper only | No test assertion changes. The seed is corrected so the guard is what fires. |
| `DeleteThresholdTests.RunClientAsync` | helper only | Server options gain `ForceDelete = forceDelete`. No assertion changes: `ForceDelete_OverridesTheThreshold` still asserts exit `0`, and `EmptyPeerFolder_AbortsInsteadOfMassDeleting` still asserts exit `4` with both guards armed. The semantics *of the product* changed in Phase 8 — the server enforces its own bound — and the helper now expresses that. |
| `DatabaseDeleteSyncTests` | unchanged | Not edited by this phase. |

---

### Task 10.2: The shared two-way E2E harness

- [ ] **Step 1: Create the harness**

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
using static RemoteFileSync.Tests.Integration.ArchiveAssertions;

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
        // SQLite keeps the file handle in a connection pool; without this the temp tree
        // cannot be deleted and every run leaks a directory.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }

    private string DbPath => Path.Combine(_dbDir, "sync.db");

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
    /// One full client/server sync. Once=true on the server, or the test hangs waiting for a
    /// second connection that never arrives.
    ///
    /// The client is given a database *path* and opens the database itself, after its
    /// no-ancestor gate has run. Handing it an already-open SyncDatabase would mean the test
    /// created the file whose absence the gate keys on, disarming it for every case here —
    /// Task 10.9 most of all. Post-run assertions open their own instance once this returns.
    /// </summary>
    private async Task<(int clientResult, int serverResult)> RunSyncAsync(
        SyncMode mode, bool deleteEnabled = true, bool mirror = false)
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
        var client = new SyncClient(clientOpts, clientLogger, dbPath: DbPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(500);
        var clientResult = await client.RunAsync(cts.Token);

        // A safety guard aborts the client before or during the session, which tears the socket
        // down under the server too. The server's exit code is not the subject of those tests,
        // and letting the fault escape here would mask the client code the test is asserting on.
        int serverResult;
        try { serverResult = await serverTask; } catch { serverResult = -1; }
        return (clientResult, serverResult);
    }

    /// <summary>
    /// Runs one clean sync so the ancestor table and pair.marker exist. Every "peer-only file
    /// survives" test needs this: on a first run the additive-only rule suppresses all
    /// deletions before the Push/Pull table is ever consulted, so the assertion would pass
    /// even against a table that deletes unconditionally.
    /// </summary>
    private async Task PrimeAsync(SyncMode mode)
    {
        var (clientResult, _) = await RunSyncAsync(mode);
        Assert.Equal(0, clientResult);
        // Written by SyncClient itself on a clean exit — nothing in this file writes it, so this
        // assertion is also the check that the client arms the gate for the runs that follow.
        Assert.True(PairMarker.Exists(DbPath));
        // The client's own SyncDatabase is disposed by now, but Microsoft.Data.Sqlite pools the
        // handle. Release it so Task 10.9 can delete the file the way the field loses it.
        SqliteConnection.ClearAllPools();
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build -c Release`
Expected: 0 errors. The harness compiles against `SyncClient(SyncOptions, SyncLogger, SyncStateManager?, JsonProgressWriter?, StdinCommandReader?, SyncDatabase?, string? dbPath)` and `SyncServer(SyncOptions, SyncLogger)` as they stand after Phase 8. `dbPath` is the trailing optional parameter Phase 8 added and is always passed by name.

---

### Task 10.3: Two-way — a client delete removes the server copy and tombstones the row

- [ ] **Step 1: Write the test**

Insert before the closing brace of `TwoWayMergeE2ETests`:

```csharp
    [Fact]
    public async Task TwoWay_ClientDelete_RemovesServerCopyAndTombstonesRow()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "gone.txt", "bye", ts);
        CreateFileWithTimestamp(_clientDir, "stay.txt", "keep", ts);

        await PrimeAsync(SyncMode.TwoWay);
        Assert.True(File.Exists(Path.Combine(_serverDir, "gone.txt")));

        File.Delete(Path.Combine(_clientDir, "gone.txt"));

        var (clientResult, _) = await RunSyncAsync(SyncMode.TwoWay);
        Assert.Equal(0, clientResult);

        using (var db = new SyncDatabase(DbPath))
        {
            var row = db.GetRow("gone.txt");
            Assert.NotNull(row);
            Assert.Equal("deleted", row!.Status);
            // A tombstone with no timestamp can never be purged, so the table grows forever.
            Assert.NotNull(row.DeletedUtcTicks);
            Assert.Equal("exists", db.GetRow("stay.txt")!.Status);
        }

        Assert.False(File.Exists(Path.Combine(_serverDir, "gone.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "stay.txt")));
        AssertArchived(Path.Combine(_testRoot, ".rfs-archive-server"), ArchiveReason.Deleted, "gone.txt");
    }
```

- [ ] **Step 2: Run the test**

Run: `dotnet test -c Release --filter "FullyQualifiedName~TwoWay_ClientDelete_RemovesServerCopyAndTombstonesRow"`
Expected: **PASS**.

Triage if it fails:
- `Assert.False() Failure` on `_serverDir/gone.txt` — `ComputePlan` did not emit `DeleteOnServer` for `present/absent, C unchanged` (Phase 6, TwoWay table).
- `Expected "deleted", Actual "exists"` — the client applied the delete but never called `SyncDatabase.Tombstone` (Phase 6/8 DB-write block).
- `Assert.Single() Failure` inside `AssertArchived` — the server deleted without archiving, or archived into a second session folder (Phase 5).

---

### Task 10.4: Two-way — a client delete loses to a server edit (rule [2]) and is reported

- [ ] **Step 1: Write the test**

Insert before the closing brace of `TwoWayMergeE2ETests`:

```csharp
    [Fact]
    public async Task TwoWay_ClientDeleteVsServerEdit_RestoresFileAndLogsResurrection()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var later = new DateTime(2026, 3, 27, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "contested.txt", "original", ts);

        await PrimeAsync(SyncMode.TwoWay);

        // Rule [2]: an edit outranks a deletion. Deleting the peer's newer work because the
        // local copy vanished is the single most destructive outcome this design prevents.
        File.Delete(Path.Combine(_clientDir, "contested.txt"));
        CreateFileWithTimestamp(_serverDir, "contested.txt", "server edited it", later);

        var (clientResult, _) = await RunSyncAsync(SyncMode.TwoWay);
        Assert.Equal(0, clientResult);

        using (var db = new SyncDatabase(DbPath))
        {
            // The restore is surprising to the user, so it must surface in the review report
            // rather than happening silently. This is the only end-to-end check that the client
            // actually drains PlanResult.Resurrections into the database — a drain Phase 7 owns,
            // in the same edit block and at the same anchor as its conflict drain.
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
Expected: **PASS**.

Triage if it fails:
- `Assert.True() Failure` on `_clientDir/contested.txt` — `ComputePlan` emitted `DeleteOnServer` instead of `SendToClient` for `absent/present, S changed` (Phase 6).
- `Assert.Contains() Failure` on the resurrection list — the plan was right but `PlanResult.Resurrections` is never drained into `LogResurrection`, so the `[RESURRECTED]` section of the review report is permanently empty. That drain is Phase 7's, in the same edit block as its conflict drain; if `GetSessionConflicts` works in Task 10.5 and this does not, the block landed with only half its body.

---

### Task 10.5: Two-way — edits on both sides keep both copies, loser renamed

- [ ] **Step 1: Write the test**

Insert before the closing brace of `TwoWayMergeE2ETests`:

```csharp
    [Fact]
    public async Task TwoWay_EditBothSides_KeepsBothCopiesWithRenamedLoser()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "shared.txt", "original", ts);

        await PrimeAsync(SyncMode.TwoWay);

        CreateFileWithTimestamp(_clientDir, "shared.txt", "client edit",
            new DateTime(2026, 3, 27, 9, 0, 0, DateTimeKind.Utc));
        CreateFileWithTimestamp(_serverDir, "shared.txt", "server edit",
            new DateTime(2026, 3, 27, 11, 0, 0, DateTimeKind.Utc));

        var (clientResult, _) = await RunSyncAsync(SyncMode.TwoWay);
        Assert.Equal(0, clientResult);

        using (var db = new SyncDatabase(DbPath))
        {
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
Expected: **PASS**.

Triage if it fails:
- `Assert.Single() Failure: The collection was empty` on `shared.conflict-*.txt` — `ComputePlan` resolved `yes/yes` by newest-wins instead of `ConflictKeepBoth` (Phase 6).
- `Assert.Matches() Failure` — the rename does not follow `{name}.conflict-{yyyyMMdd-HHmmss}-{side}{ext}` (Phase 7).
- `Assert.Contains() Failure` on `contents` — the conflict file was written but one edit was still overwritten (Phase 7 apply order).
- `Assert.Contains() Failure` on `GetSessionConflicts` — the rename happened but `PlanResult.Conflicts` was not drained into `LogConflict` (Phase 7's drain).

---

### Task 10.6: Push mode — a server-only file survives without `--mirror`, dies with it

- [ ] **Step 1: Write the tests**

Insert before the closing brace of `TwoWayMergeE2ETests`:

```csharp
    [Fact]
    public async Task Push_ServerOnlyFile_SurvivesWithoutMirror()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "pushed.txt", "from client", ts);

        // The priming run is what gives this test teeth: without it the run is an
        // additive-only first run, deletions are suppressed before the Push table is
        // consulted, and the assertion would hold against a table that deletes blindly.
        await PrimeAsync(SyncMode.Push);
        CreateFileWithTimestamp(_serverDir, "server-only.txt", "server keeps this", ts);

        var (clientResult, _) = await RunSyncAsync(SyncMode.Push, deleteEnabled: true, mirror: false);

        Assert.Equal(0, clientResult);
        Assert.True(File.Exists(Path.Combine(_serverDir, "pushed.txt")));
        // No ancestor row ever said the client had this file, so its absence on the client is
        // not evidence of a deletion. Deleting it destroys files the client never knew about.
        Assert.True(File.Exists(Path.Combine(_serverDir, "server-only.txt")));
        AssertNothingArchived(Path.Combine(_testRoot, ".rfs-archive-server"));
    }

    [Fact]
    public async Task Push_Mirror_DeletesServerOnlyFile()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "pushed.txt", "from client", ts);

        await PrimeAsync(SyncMode.Push);
        CreateFileWithTimestamp(_serverDir, "server-only.txt", "server loses this", ts);

        var (clientResult, _) = await RunSyncAsync(SyncMode.Push, deleteEnabled: true, mirror: true);

        Assert.Equal(0, clientResult);
        Assert.True(File.Exists(Path.Combine(_serverDir, "pushed.txt")));
        // --mirror is the explicit "make the peer identical" opt-in: history stops mattering.
        Assert.False(File.Exists(Path.Combine(_serverDir, "server-only.txt")));
        AssertArchived(Path.Combine(_testRoot, ".rfs-archive-server"), ArchiveReason.Deleted, "server-only.txt");
    }
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Push_ServerOnlyFile_SurvivesWithoutMirror|FullyQualifiedName~Push_Mirror_DeletesServerOnlyFile"`
Expected: **PASS** — both `Push_ServerOnlyFile_SurvivesWithoutMirror` and `Push_Mirror_DeletesServerOnlyFile` green.

Triage: if `Push_ServerOnlyFile_SurvivesWithoutMirror` fails on `Assert.True`, the Push table deletes on `client absent, server present` without requiring an ancestor row — the exact bug `--mirror` exists to gate. If it fails inside `AssertNothingArchived`, something was archived without being deleted. If `Push_Mirror_DeletesServerOnlyFile` fails on `Assert.False`, `MirrorDeletes` is not reaching the plan — check handshake bit 3 (Phase 3) before suspecting Phase 6.

---

### Task 10.7: Pull mode — a client-only file survives without `--mirror`, dies with it

- [ ] **Step 1: Write the tests**

Insert before the closing brace of `TwoWayMergeE2ETests`:

```csharp
    [Fact]
    public async Task Pull_ClientOnlyFile_SurvivesWithoutMirror()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_serverDir, "pulled.txt", "from server", ts);

        await PrimeAsync(SyncMode.Pull);
        CreateFileWithTimestamp(_clientDir, "client-only.txt", "client keeps this", ts);

        var (clientResult, _) = await RunSyncAsync(SyncMode.Pull, deleteEnabled: true, mirror: false);

        Assert.Equal(0, clientResult);
        Assert.True(File.Exists(Path.Combine(_clientDir, "pulled.txt")));
        // Exact mirror of the Push case: no ancestor row, so no evidence of a deletion.
        Assert.True(File.Exists(Path.Combine(_clientDir, "client-only.txt")));
        AssertNothingArchived(Path.Combine(_testRoot, ".rfs-archive-client"));
    }

    [Fact]
    public async Task Pull_Mirror_DeletesClientOnlyFile()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_serverDir, "pulled.txt", "from server", ts);

        await PrimeAsync(SyncMode.Pull);
        CreateFileWithTimestamp(_clientDir, "client-only.txt", "client loses this", ts);

        var (clientResult, _) = await RunSyncAsync(SyncMode.Pull, deleteEnabled: true, mirror: true);

        Assert.Equal(0, clientResult);
        Assert.True(File.Exists(Path.Combine(_clientDir, "pulled.txt")));
        Assert.False(File.Exists(Path.Combine(_clientDir, "client-only.txt")));
        // The deleting side archives into its own root. Archiving under .rfs-archive-server
        // would mean the server is destroying files it does not own.
        AssertArchived(Path.Combine(_testRoot, ".rfs-archive-client"), ArchiveReason.Deleted, "client-only.txt");
        AssertNothingArchived(Path.Combine(_testRoot, ".rfs-archive-server"));
    }
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test -c Release --filter "FullyQualifiedName~Pull_ClientOnlyFile_SurvivesWithoutMirror|FullyQualifiedName~Pull_Mirror_DeletesClientOnlyFile"`
Expected: **PASS** — both `Pull_ClientOnlyFile_SurvivesWithoutMirror` and `Pull_Mirror_DeletesClientOnlyFile` green.

Triage: if `Pull_ClientOnlyFile_SurvivesWithoutMirror` fails, Pull is not the exact mirror of Push. If `Pull_Mirror_DeletesClientOnlyFile` fails inside `AssertNothingArchived` on the server root, the deleting side is archiving to the wrong root.

---

### Task 10.8: Convergence — runs 2 and 3 transfer nothing and delete nothing

- [ ] **Step 1: Write the test**

Insert before the closing brace of `TwoWayMergeE2ETests`:

```csharp
    [Fact]
    public async Task ThreeIdenticalRuns_Converge_NoTransfersOrDeletesAfterTheFirst()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "a.txt", "alpha", ts);
        CreateFileWithTimestamp(_clientDir, Path.Combine("sub", "b.txt"), "bravo", ts);
        CreateFileWithTimestamp(_serverDir, "c.txt", "charlie", ts);

        for (int i = 0; i < 3; i++)
        {
            var (clientResult, _) = await RunSyncAsync(SyncMode.TwoWay);
            Assert.Equal(0, clientResult);
        }

        // Opened after all three runs, so nothing here created the database ahead of the gate.
        using var db = new SyncDatabase(DbPath);
        // GetRecentSessions is newest-first (ORDER BY id DESC): [0] = run 3, [1] = run 2. A
        // merge that keeps re-sending or re-deleting the same files never settles, and that
        // ping-pong is invisible to a per-file assertion but obvious in the session counters.
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
Expected: **PASS**.

Triage: if `sessions[1].FilesTransferred` is non-zero, `UpsertSynced` recorded an mtime that `ChangeDetector.Unchanged` then rejects on the next run — usually receive-time instead of source-time, or ticks stored at the wrong precision. If `FilesDeleted` is non-zero, run 2 tombstoned rows for files still present on both sides. If `sessions.Count` is less than 3, sessions are not being opened — `StartSession` runs only when `DeleteEnabled` is set and the client has a database, which it opens for itself from the `dbPath` the harness passes once the no-ancestor gate has cleared.

---

### Task 10.9: The no-ancestor gate — a lost database with a surviving `pair.marker` aborts

- [ ] **Step 1: Write the tests**

Insert before the closing brace of `TwoWayMergeE2ETests`:

```csharp
    /// <summary>
    /// Deletes the database file and its WAL sidecars, leaving pair.marker in place. This is
    /// how the loss presents in the field: a restored profile, or a cleaned %LOCALAPPDATA%,
    /// takes sync.db but leaves the marker behind.
    ///
    /// Nothing may reopen the database between here and the run under test. `new SyncDatabase(p)`
    /// re-creates the file, so a test that wrapped its run in `using var db = new
    /// SyncDatabase(DbPath)` would put back the very file SyncClient.PairStateLost looks for and
    /// the gate could never fire — the test would pass 0 and prove nothing.
    /// </summary>
    private void LoseDatabaseKeepMarker()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(DbPath);
        foreach (var sidecar in Directory.GetFiles(_dbDir, "sync.db-*"))
            File.Delete(sidecar);
        Assert.False(File.Exists(DbPath));
        Assert.True(PairMarker.Exists(DbPath));
    }

    [Fact]
    public async Task LostDatabase_WithSurvivingPairMarker_AbortsWithoutDeleting()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "one.txt", "1", ts);
        CreateFileWithTimestamp(_clientDir, "two.txt", "2", ts);

        await PrimeAsync(SyncMode.TwoWay);
        LoseDatabaseKeepMarker();

        // No SyncDatabase is opened here, deliberately: the gate keys on the file being absent.
        var (clientResult, _) = await RunSyncAsync(SyncMode.TwoWay);
        // An absent database beside a marker a previous run wrote means "state lost", not
        // "nothing was ever synced". Treating it as a first run and rebuilding the ancestor
        // from the two live trees would resurrect everything either side deleted while the
        // database was gone.
        Assert.Equal(4, clientResult);
        // The refusal happens before anything opens the database, so it is still absent.
        Assert.False(File.Exists(DbPath));

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

        await PrimeAsync(SyncMode.TwoWay);
        LoseDatabaseKeepMarker();

        // --mirror is the documented escape hatch: the user has declared which side is
        // authoritative, so missing history is no longer a reason to refuse. Asserting the
        // success code rather than merely "not 4" — NotEqual(4) would also pass on a connection
        // failure (2) or a protocol abort (3), which is no proof at all. Again no SyncDatabase
        // is opened here: the client rebuilds it itself once the gate has waved the run through.
        var (clientResult, _) = await RunSyncAsync(SyncMode.TwoWay, mirror: true);
        Assert.Equal(0, clientResult);

        Assert.True(File.Exists(Path.Combine(_clientDir, "one.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "one.txt")));
    }
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test -c Release --filter "FullyQualifiedName~LostDatabase_WithSurvivingPairMarker_AbortsWithoutDeleting|FullyQualifiedName~LostDatabase_WithMirror_ProceedsInsteadOfAborting"`
Expected: **PASS** — both `LostDatabase_WithSurvivingPairMarker_AbortsWithoutDeleting` and `LostDatabase_WithMirror_ProceedsInsteadOfAborting` green.

Triage:
- `Assert.True() Failure` inside `PrimeAsync` on `PairMarker.Exists(DbPath)` — `SyncClient` is not calling `PairMarker.Write(_dbPath)` on a clean exit (Phase 8, Task 8.3 step 3d). The gate is inert until it does.
- `Assert.Equal() Failure: Expected 4, Actual 0` — the gate is not in `SyncClient.RunAsync`, or it is keyed on something other than `_dbPath != null && SyncClient.PairStateLost(_dbPath)`. A gate in `Program.Main` cannot fire here and never will: this test constructs `SyncClient` directly, which is precisely why Phase 8 relocated it. If `Assert.False(File.Exists(DbPath))` fails instead, something opened the database before the gate ran — check that no helper on the path from the test to `RunAsync` constructs a `SyncDatabase`.
- `Assert.Equal() Failure: Expected 0, Actual 4` in the mirror test — the gate is ignoring `MirrorDeletes`.

---

### Task 10.10: Document modes, mirror, archive, protocol v3, the ancestor model and the upgrade path

Phase 10 is the sole owner of `README.md`; no earlier phase edits it, so every quote below is from `main`.

- [ ] **Step 1: Rewrite the affected README sections**

`README.md:35-41` — replace exactly:

````markdown
On the other machine (the client):

```bash
RemoteFileSync.exe client --host 10.0.1.50 --folder "C:\Local" --bidirectional
```

Without `--bidirectional` the sync is one-way: the client pushes to the server.
````

with:

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

`README.md:52` — replace exactly:

```markdown
| `--bidirectional` | `-b` | off | Sync both directions rather than client → server only |
```

with:

```markdown
| `--mode <push\|pull\|two-way>` | — | `push` | Which side is authoritative — see Quick start |
| `--bidirectional` | `-b` | off | Deprecated alias for `--mode two-way` |
| `--mirror` | — | off | Let deletions propagate even for files with no sync history. Destructive — see Safety behaviour |
```

`README.md:56` — replace exactly:

```markdown
| `--backup-folder <path>` | — | `.rfs-backups-NAME` beside the sync folder | Where replaced and deleted files are kept. **Must be outside the sync folder** |
```

with:

```markdown
| `--backup-folder <path>` | — | `.rfs-backups-NAME` beside the sync folder | Legacy backup location, kept for existing scripts. **Must be outside the sync folder** |
| `--archive-folder <path>` | — | `.rfs-archive-NAME` beside the sync folder | Where replaced, deleted and conflicted files are kept. **Must be outside the sync folder** |
| `--archive-keep-days <n>` | — | `30` | Prune archive sessions older than this. `0` keeps them forever |
| `--archive-max-size <n>` | — | `0` (off) | Cap the total archive size; accepts `K`/`M`/`G` suffixes. Oldest sessions are pruned first |
```

`README.md:102-104` — replace exactly:

```markdown
- **Backups are copies.** Files replaced or deleted by a sync are copied into a dated backup
  tree first. The backup folder must live outside the sync folder, or backups would be
  re-synced to the peer and grow without bound.
```

with:

````markdown
- **Everything destroyed is archived first.** Files replaced, deleted, or displaced by a
  conflict are copied into the archive *before* the destructive step, under:

  ```
  <archive folder>/<yyyyMMdd-HHmmss of sync start>/<deleted|overwritten|conflict>/<original path>
  ```

  One folder per sync run, so a bad run is a single directory to restore from, and the reason
  level keeps a "what did this run delete" restore from sweeping up overwrite snapshots. The
  archive folder must live outside the sync folder, or archived copies would be re-scanned as
  new files, propagated to the peer, and grow without bound. `--archive-keep-days` and
  `--archive-max-size` prune the oldest sessions first; each is disabled by setting it to `0`.
- **`--mirror` is opt-in, and it is the dangerous one.** Without it, a file the peer has and
  you do not is deleted only when the ancestor table proves you *had* it and it was unchanged.
  With it, "the peer must match me" is taken literally and any unmatched file on the
  non-authoritative side is deleted. Use it for a genuine one-way mirror, never for a two-way
  pairing you care about.
- **Lost sync state never guesses.** A first run with no database is additive only: nothing is
  deleted, the ancestor table is built, and a `pair.marker` is written beside the database on
  success. If the database is later missing or unreadable *while that marker survives*, the run
  aborts with exit `4` before connecting, rather than treating a decade of synced files as
  never-seen. Only `--mirror` — where you have explicitly named the authoritative side —
  proceeds anyway.
````

`README.md:108-115` — replace exactly:

```markdown
## Protocol compatibility

The wire protocol is **version 2**. Both peers must run the same build — a mismatch is rejected
during the handshake rather than silently misparsed. Version 1 did not carry file timestamps,
so a mixed pair could never converge.

A single protocol frame is capped at 64 MB. Since the file manifest is sent as one frame, that
bounds a synced tree at roughly 1.3 million files.
```

with:

````markdown
## How two-way sync decides

Two-way sync keeps an **ancestor table**: for every path, the size and modification time each
side had at the end of the last successful sync. Comparing the two current states against that
common ancestor separates four cases a straight two-way comparison cannot:

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

The ancestor table lives on the **client only**. The server holds no sync state and simply
executes the plan the client sends.

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
3. **The default is still `push`.** A command line with no mode flag behaves as before, with
   one deliberate change: in `push` mode a file present on the client and missing on the server
   is now always re-sent, so the pair converges instead of leaving the gap open.
4. **The state database upgrades in place** to schema v2 on first open, splitting the single
   recorded size/mtime into per-side client and server columns. The first two-way run after the
   upgrade therefore treats both sides as matching the old record; verify a run with `--verbose`
   before enabling `--delete` on a large tree.
5. **Deletions and replacements now land in `.rfs-archive-NAME`**, not `.rfs-backups-NAME`, and
   are grouped per run rather than per day. Old backup folders are left alone; delete them by
   hand once you no longer need them.
````

`README.md:135-140` — replace exactly:

```markdown
## Known gaps

- `--max-threads` is parsed but transfers are sequential.
- Mid-transfer resume is not implemented; an interrupted sync restarts the affected file.
- Empty directories are not synced.
- No authentication or transport encryption (see the security notice above).
```

with:

```markdown
## Known gaps

- `--max-threads` is parsed but transfers are sequential. Ancestor rows are written from a
  single thread and `SyncDatabase` is not thread-safe, so parallel transfers need a write queue
  first.
- Mid-transfer resume is not implemented; an interrupted sync restarts the affected file.
- Empty directories are not synced. A directory left empty by a deletion stays behind on both
  sides.
- Renames are seen as a delete plus an add: the file is re-transferred, and the old name is
  archived rather than moved.
- Conflict resolution never merges file contents. Both copies are kept and the user reconciles
  them by hand.
- Change detection uses size and modification time, not content hashes. A file edited in place
  that preserves both its size and its mtime to within two seconds is seen as unchanged.
- The ancestor table lives only on the client. A server paired with two clients has no shared
  history between them, so a file one client deletes is deleted for the other as well once that
  client's own ancestor row agrees.
- Paths are compared case-insensitively. A peer holding both `Readme.md` and `README.md`
  collapses to a single row.
- No authentication or transport encryption (see the security notice above).
```

- [ ] **Step 2: Verify the documented flags match the code**

Run: `dotnet run --project src/RemoteFileSync -- --help`
Expected: every flag in the options table appears in the help output with the same default. Specifically `--mode`, `--mirror`, `--archive-folder`, `--archive-keep-days` and `--archive-max-size` are all listed, and `--bidirectional` is marked deprecated. Any divergence is a defect in Phase 1's `PrintUsage`, not in this README — Phase 1 owns that method; report it rather than editing it here.

---

### Phase 10 commit

```bash
git add tests/RemoteFileSync.Tests/Integration/ArchiveAssertions.cs \
        tests/RemoteFileSync.Tests/Integration/TwoWayMergeE2ETests.cs \
        tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs \
        tests/RemoteFileSync.Tests/Integration/DeleteSyncTests.cs \
        tests/RemoteFileSync.Tests/Integration/DeleteThresholdTests.cs \
        README.md
git commit -m "test: end-to-end acceptance tests for the ancestor merge, mirror and the no-ancestor gate

Covers over a real loopback socket: a client delete removes the server copy
and tombstones the ancestor row; a client delete losing to a server edit
restores the file and records the resurrection; simultaneous edits keep both
copies with the loser renamed; push and pull leave peer-only files alone
without --mirror and delete them with it, each verified after a priming run so
the additive-only first-run rule cannot mask the decision table; three
identical runs converge to zero transfers and zero deletes; and a database lost
beside a surviving pair.marker aborts with exit 4 without deleting anything.

Moves DeleteSyncTests off SyncStateManager, whose binary state stopped feeding
ComputePlan when the ancestor table replaced it, and onto SyncDatabase. Two
expectations change deliberately: a first run now proves itself by writing
pair.marker rather than a binary state file, and push mode re-sends a file the
server lost instead of leaving the pair divergent, so the old
DeleteSync_UniDirectional_ServerDeletionIgnored is renamed to say what it now
asserts. Corrects the DeleteThresholdTests seed to read size and mtime back
from the file it just wrote, so the blast-radius guard is what fires rather
than a spurious change detection, and gives that suite's server options
ForceDelete: the server enforces its bound independently of the client, so an
intentional bulk deletion needs --force-delete on both sides.

Every helper hands SyncClient a database path rather than an open
SyncDatabase. Opening one around a run creates the file whose absence the
no-ancestor gate keys on, which would have silently disarmed the gate the
LostDatabase tests exist to prove.

Documents --mode, --mirror, the archive layout and its retention flags,
protocol v3, the ancestor model, the upgrade path and the revised known gaps.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
git push -u origin feat/deletion-sync-ancestor-merge
```

**Verification before commit:**

```bash
dotnet build -c Release
dotnet test  -c Release
```

Expected: 0 build errors, 0 build warnings introduced by this phase, and the whole suite green.

Green-check the tests this phase is responsible for:

```bash
dotnet test -c Release --filter "FullyQualifiedName~TwoWayMergeE2ETests"
dotnet test -c Release --filter "FullyQualifiedName~Integration"
```

`TwoWayMergeE2ETests` must report all of `TwoWay_ClientDelete_RemovesServerCopyAndTombstonesRow`, `TwoWay_ClientDeleteVsServerEdit_RestoresFileAndLogsResurrection`, `TwoWay_EditBothSides_KeepsBothCopiesWithRenamedLoser`, `Push_ServerOnlyFile_SurvivesWithoutMirror`, `Push_Mirror_DeletesServerOnlyFile`, `Pull_ClientOnlyFile_SurvivesWithoutMirror`, `Pull_Mirror_DeletesClientOnlyFile`, `ThreeIdenticalRuns_Converge_NoTransfersOrDeletesAfterTheFirst`, `LostDatabase_WithSurvivingPairMarker_AbortsWithoutDeleting` and `LostDatabase_WithMirror_ProceedsInsteadOfAborting` as passing, with none skipped.

**Existing tests knowingly changed, and the only two whose intent changes:**
`DeleteSyncTests.DeleteSync_FirstRun_NoState_AdditiveOnly` (asserts `pair.marker` instead of the retired binary state file) and `DeleteSyncTests.DeleteSync_UniDirectional_ServerDeletionIgnored`, renamed to `Push_ServerSideDeletion_IsReSentBecauseTheClientIsAuthoritative` with its expectation inverted to match the Push decision table. Every other change is a seed migration, an archive-path assertion, a helper rewired onto Phase 8's `dbPath:` seam, or a strengthening — enumerated in the audit table under Task 10.1 Step 7.

**Leftover temp directories:** if a run is killed mid-test, `%TEMP%\rfs_merge_e2e_*`, `rfs_del_e2e_*` and `rfs_thresh_*` survive because SQLite still holds handles. `Dispose` calls `SqliteConnection.ClearAllPools()` first for exactly this reason; a leftover directory after a clean run means a `SyncDatabase` was constructed outside a `using`.

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
