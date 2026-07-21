# Phase 8 report — mode dispatch, Pull execution, reworked delete guards, no-ancestor gate

Branch `feat/deletion-sync-ancestor-merge`, based on `b7ebc8f`. Target `net10.0`.

**Status: complete.** Build 0 errors, 20 warnings (baseline 20 — no new warnings). Full suite
run twice on the final committed code, byte-identical results: **6 failed, 498 passed, 504
total**, and all six failures are the expected-red set owned by Phase 10.

---

## 1. Brief quotes that did NOT match, and what I anchored on instead

The brief was written against `main` and is the stalest of the run. Every anchor was re-derived
by reading the current files before applying. Two anchors did not match byte-for-byte:

### 1.1 Task 8.2 step 3c — `SyncClient`'s new private method

The brief anchors on the class ending immediately after the `finally` block:

```csharp
                _logger.Debug($"Sync session {sessionId} completed (exit code {finalExitCode})");
            }
        }
    }
}
```

**This no longer exists.** Phase 7 added `IsPeerDisconnect` after that block, so the class no
longer ends there. Applying the brief's quote verbatim would have inserted `WithinDeleteBudget`
*between* `HandleConnectionAsync` and `IsPeerDisconnect` — which happens to compile, so this is
exactly the class of silent stale-anchor application the instructions warned about.

**Anchored instead** on the real end of the class, the `IsPeerDisconnect` expression body plus
the closing brace, and appended `WithinDeleteBudget` after it. Final position:
`src/RemoteFileSync/Network/SyncClient.cs:867-885`.

### 1.2 Task 8.2 step 3g — the conflict-squatter guard

The brief instructs me to rewrite Phase 7's hand-rolled percentage arithmetic and quotes it:

```csharp
            if (occupied > 0 && !_options.ForceDelete
                && serverManifest.Count >= SyncOptions.MinTrackedFilesForDeleteGuard)
            {
                double pct = occupied * 100.0 / serverManifest.Count;
```

**This does not exist either.** Phase 7 already routes the squatter guard through
`DeleteBudget.Within` (`SyncServer.cs:279-288`), already drops the `occupied > 0`
short-circuit, and already carries the explanatory comment. **No edit applied** — re-applying
would have meant quoting text that is not there. Verified by reading, and confirmed by the
final `git grep "\* 100.0 /" -- src/` returning only `DeleteBudget.cs:30`.

### 1.3 Anchors that DID match exactly (verified, applied)

All other "replace exactly" blocks matched byte-for-byte despite the line numbers being wrong:
client `modeLabel`, the session-label block, steps 7/8/9/10, the old client delete guard, the
`_db` field, the constructor, the `RunAsync` header, the `GetStream()` tail, `Program.cs`'s
client branch, and server steps 5/6/7/8/9 plus the class tail.

### 1.4 Work the brief lists that was already done — not re-applied

Confirmed by reading, per the team lead's table: protocol-v3 handshake (Phase 3), the two
server `bidirectional` conditions already reading `mode == SyncMode.TwoWay` (Phase 3),
`BackupManager`→`ArchiveManager` (Phase 5), the `ComputePlan` call site (Phase 6), and
`DeleteBudget.cs` + the squatter guard routing (Phase 7). `bidirectional` as a local does not
exist in `SyncServer`; I never referenced it.

---

## 2. Task 8.1 — `ModeGate`, and Pull actually moving bytes

### Watch-it-fail (unit)

`tests/RemoteFileSync.Tests/Sync/ModeGateTests.cs` created, then:

```
ModeGateTests.cs(19,38): error CS0103: The name 'ModeGate' does not exist in the current context
ModeGateTests.cs(20,38): error CS0103: The name 'ModeGate' does not exist in the current context
ModeGateTests.cs(29,21): error CS0103: The name 'ModeGate' does not exist in the current context
```

Created `src/RemoteFileSync/Sync/ModeGate.cs`. 4/4 pass.

### Watch-it-fail (acceptance — the headline)

`ModeGateTests` proves the predicate; it cannot prove the predicate is *wired in*. So I added
`tests/RemoteFileSync.Tests/Network/PullExecutionTests.cs`, which drives a real `SyncServer`
and a real `SyncClient` over loopback and asserts **file content on the receiving side**. This
is not under `tests/**/Integration/`, so it does not trespass on Phase 10.

With `ModeGate` created but the loops not yet gated:

```
RemoteFileSync.Tests.Network.PullExecutionTests.Pull_MovesTheServersBytesOntoTheClient [FAIL]
  Error Message:
   Pull planned the download but never executed it.
  Stack Trace:
     at ...PullExecutionTests.Pull_MovesTheServersBytesOntoTheClient() in PullExecutionTests.cs:line 95
Failed!  - Failed: 1, Passed: 6, Total: 7
```

That is the headline bug reproduced end to end: a Pull run planned `SendToClient`, exited 0,
and moved nothing. After widening all four gate pairs: **7/7 pass**.

### Honest note on the other two acceptance tests

`Pull_DoesNotPushTheClientsOwnFilesOverTheAuthoritativeServer` and `Push_WritesNothingToTheClient`
**passed before the fix as well as after.** They have no red phase and I am not claiming one.

The reason is worth recording, because it changes what the client→server gate actually is:
`ComputePlan` already never emits `SendToServer`/`ClientOnly` in Pull mode (contract Pull table:
client-only + no delete → `Skip`; both sides present and differing → `SendToClient`). So the
**upload gate is defence-in-depth against a malformed or hostile plan, not a live bug fix** —
unlike the download gate, which was a live, user-reachable data bug. I kept both tests as
regression guards on the widening (they would catch `ClientToServer` being loosened to admit
Pull, or `ServerToClient` being loosened to admit Push), but they should not be read as
evidence that this phase repaired an upload defect.

### Peer symmetry proof

Four gates on each side, same predicates, same order — the plan is serialised once and both
peers iterate it, so an asymmetric gate is a frame desync.

| direction | client | server |
|---|---|---|
| files up | `ModeGate.ClientToServer(_options.Mode)` `SyncClient.cs:544` (send) | `ModeGate.ClientToServer(mode)` `SyncServer.cs:329` (receive) |
| deletions on server | `DeleteEnabled && ClientToServer` `:630` (send) | `deleteEnabled && ClientToServer` `:388` (receive) |
| files down | `ModeGate.ServerToClient(_options.Mode)` `:663` (receive) | `ModeGate.ServerToClient(mode)` `:441` (send) |
| deletions on client | `DeleteEnabled && ServerToClient` `:730` (receive) | `deleteEnabled && ServerToClient` `:493` (send) |

`grep -n "mode == SyncMode\|Mode == SyncMode" src/RemoteFileSync/Network/*.cs` returns nothing:
no hand-written mode comparison survives in either session loop. (Phase 7's
`mode != SyncMode.TwoWay` conflict rejection is a different rule — not a transfer gate — and is
left untouched.)

---

## 3. Task 8.2 — both delete guards rebuilt

### `DeleteBudgetTests` — no red phase, teeth proven by mutation instead

`DeleteBudget` already existed (Phase 7), so `DeleteBudgetTests.cs` **passed 9/9 on its first
run**. There was no missing-implementation failure to observe and I am not manufacturing one.

Because a test that never failed proves nothing on its own, I proved the teeth by mutating
`DeleteBudget.cs` three ways and confirming each mutation is caught (file restored via
`git checkout` after each; final `git diff --stat` empty):

| mutation | result |
|---|---|
| zero denominator returns `true` (disarm) | **Failed: 2, Passed: 7** — caught |
| boundary `<=` → `<` | **Failed: 2, Passed: 7** — caught |
| below-floor exemption removed | **Failed: 1, Passed: 8** — caught |

Coverage requested by the team lead is present: zero denominator refuses, below-floor exemption,
exactly-at-threshold, above-threshold. I added one case beyond the brief —
`ANegativeDestinationCount_IsTreatedAsUncountable_NotAsRoomToDelete` — because the
zero-denominator rule exists to make an untrustworthy count fail closed, and a negative count is
the same class of nonsense; it is not reachable from a manifest today, which is why it is a
cheap guard against a future one.

### Client guard

Replaced the tracked-row denominator (`_db.GetAllTrackedFiles().Count(...)`, **0 on a wiped
database**, and `0 >= MinTrackedFilesForDeleteGuard` is false so the guard skipped itself
exactly when state loss made every peer-only file look like a deletion) with per-direction
bounds against the destination-side manifest, routed through `DeleteBudget.Within` via
`SyncClient.WithinDeleteBudget`. Both `return 4` sites remain inside the existing `try`, so the
`finally` still calls `CompleteSession` and no session row leaks.

### Server guard

Moved ahead of the receive loop (so a refusal costs no writes on either side) and widened to
bound **both** directions. Previously it counted only `DeleteOnServer` — **0 in Pull mode**,
where every deletion is a `DeleteOnClient` the server itself originates, so nothing bounded them
at all.

### Trust boundary — the anti-pattern flagged for this phase

The server's `DeleteOnClient` denominator is `clientManifest.Count`, which **arrived from the
peer**. I did not paper over this. The call-site comment (`SyncServer.cs:217-224`) states
explicitly that the two denominators are not equally trustworthy: `serverManifest.Count` is this
machine's own scan and is authoritative for the deletions applied here, while
`clientManifest.Count` can be inflated by a peer to buy itself a larger `DeleteOnClient` budget,
making that bound a **backstop, not the primary defence**. The primary defence is the client
enforcing the same bound against its own scan before applying any deletion — which is stated at
`SyncClient.cs:426-429`. No comment in this phase asserts a fact about the local filesystem that
is actually derived from something the peer sent.

---

## 4. Task 8.3 — the no-ancestor gate

### Watch-it-fail

```
SyncClientGateTests.cs(66,62): error CS1739: The best overload for 'SyncClient' does not have a parameter named 'dbPath'   (×4)
SyncClientGateTests.cs(134,33): error CS0117: 'SyncClient' does not contain a definition for 'PairStateLost'              (×6)
```

Exactly the two errors the brief predicts. After implementing: **5/5 pass** (20 s — two tests
deliberately fall through to the connect retries).

### What was built

The seam is exactly the two things specified and nothing more: a trailing
`string? dbPath = null` constructor parameter, and `public static bool PairStateLost(string)`.
**`SyncDatabase.DatabasePath` and `SyncDatabase.ExistedBeforeOpen` are not referenced anywhere** —
`git grep "DatabasePath\|ExistedBeforeOpen" -- src/ tests/` returns nothing.

The condition is the contract table exactly — `PairMarker.Exists(dbPath)` **AND** the database
is absent or unreadable. It is not an "ancestor table is empty" check.

The gate returns 4 **before the socket opens**; the test asserts `sw.Elapsed < 2s` against the
~4 s three-connect retry backoff, so a gate that ran after the connect attempt would fail on
timing rather than pass silently.

The readability probe is a 16-byte SQLite-header read on a `FileStream` with
`FileShare.ReadWrite`, never `new SyncDatabase(path)` (which would *create* the file it is
probing for, and whose pooled Microsoft.Data.Sqlite handle outlives the `using`). Tests assert
the probed file is byte-identical afterwards and that `File.Delete` succeeds — the latter throws
`IOException` if a handle was left open.

### `_db` lifetime

`RunAsync` is now a thin wrapper: gate → open (only if the caller gave a path, not an instance)
→ `try { RunSessionAsync } finally { clear field, then dispose }`. The field is nulled **before**
the dispose, so `_db` can never name a disposed object; a `using` declaration would have left the
field pointing at the corpse. A caller-supplied `db:` instance is never disposed here.
`RunAsync` is documented as **callable at most once per instance** in its XML doc.

---

## 5. The `SyncStateManager` consequence — stated explicitly, not left silent

Phase 6 removed the binary-state ancestor path when it collapsed the `ComputePlan` overloads;
`ComputePlan` now takes `IReadOnlyDictionary<string, AncestorRow>?` sourced from `_db.LoadAll()`.

**This phase removes `previousState`'s last influence on any destructive decision.** The old
client guard fell back to `previousState?.Manifest.Count ?? 0` as its denominator; that fallback
is gone. After Phase 6 and Phase 8, `previousState` is loaded in `HandleConnectionAsync` and read
only by the legacy `SaveState` fallback (the `_db == null` branch). It can no longer cause or
bound a deletion.

Consequently the four `DeleteSyncTests` cases that depend on the binary-state deletion path are
broken **by Phase 6, not by this phase**, and this phase neither repairs nor worsens them. They
belong to Phase 10 to migrate onto `SyncDatabase`/`UpsertSynced` or retire. This phase must not
be read as having preserved them.

---

## 6. Tests left red — exactly six, all pre-existing, all Phase 10's

Both final runs, identical:

```
Failed RemoteFileSync.Tests.Integration.DeleteSyncTests.DeleteSync_SecondRun_DetectsDeletions
Failed RemoteFileSync.Tests.Integration.DeleteThresholdTests.EmptyPeerFolder_AbortsInsteadOfMassDeleting
Failed RemoteFileSync.Tests.Integration.DeleteSyncTests.DeleteSync_UniDirectional_ServerDeletionIgnored
Failed RemoteFileSync.Tests.Integration.DeleteSyncTests.DeleteSync_BidiSymmetric
Failed RemoteFileSync.Tests.Integration.DeleteSyncTests.DeleteSync_Case1_PropagatesDeletion
Failed RemoteFileSync.Tests.Integration.EndToEndTests.BiDirectional_BothSidesSync
Failed!  - Failed: 6, Passed: 475, Skipped: 0, Total: 481  (RemoteFileSync.Tests)
Passed!  - Failed: 0, Passed:  23, Skipped: 0, Total:  23  (ExecRFS.Tests)
```

This is **exactly** the expected-red set named by the team lead. No test regressed, and no
failure exists that I have neither fixed nor declared. Phase 10 repairs all six.

Failure reasons (unchanged from baseline):
- the four `DeleteSyncTests` cases and `BiDirectional_BothSidesSync`: `Assert.False() Expected:
  False / Actual: True` — Phase 6's retirement of the binary-state ancestor path.
- `EmptyPeerFolder_AbortsInsteadOfMassDeleting`: `Assert.Equal() Expected: 4 / Actual: 0` —
  precisely the Phase-6 consequence the brief predicts, caused by `SeedTrackedFiles` recording an
  mtime a day older than the file it just wrote.

### Correction to the brief: `ForceDelete_OverridesTheThreshold` did NOT break

The brief predicts this test breaks under the server's widened guard and prescribes adding
`ForceDelete = forceDelete` to `serverOpts`. **It passes, in all four runs.**

Reason: `SeedTrackedFiles` writes `db.MarkSynced(name, 9, DateTime.UtcNow.AddDays(-1), ...)`,
an mtime a day older than the file just created, so `ChangeDetector` reads every file as
"client changed" and the plan contains **zero deletions**. With no deletions planned, no guard on
either side is ever reached, and `Assert.NotEqual(4, exit)` passes vacuously.

**This makes the brief's two Phase 10 fixes order-coupled, which the brief does not say.** Fix #2
(re-seed `SeedTrackedFiles` with the file's real `Length`/`LastWriteTimeUtc.Ticks` via
`UpsertSynced`) is what makes 20 `DeleteOnClient` entries appear. Only *then* does the server's
guard reject the plan for lack of server-side `ForceDelete`, and only then is fix #1 required —
at which point, without it, `ForceDelete_OverridesTheThreshold` goes from passing-vacuously to
**erroring** (the client blocks on `ProtocolHandler.ReadMessageAsync` outside the surrounding
`try` after the server closes the socket). **Phase 10 must apply #1 and #2 together in one
commit; applying #2 alone turns a green test red.**

I did not touch `DeleteThresholdTests.cs` — it is under `tests/**/Integration/` and is Phase 10's.

---

## 7. Verification greps

```
git grep -n "Bidirectional" -- src/
  → src/RemoteFileSync/Models/SyncOptions.cs:16  (the getter-only shim declaration)
  → src/RemoteFileSync/Network/SyncClient.cs:727 (a comment, not a read)
  → 4 hits in src/ExecRFS/  (the GUI's own SyncProfile model + the --bidirectional flag it
    emits; a different project, and the contract keeps that alias accepted)
  No production READ of SyncOptions.Bidirectional survives in the sync engine.

git grep -n "bidirectional" -- src/
  → comments only, plus Program.cs's deprecated --bidirectional CLI alias (contract-mandated).
  No `bidirectional` local anywhere.

git grep -n "DatabasePath\|ExistedBeforeOpen" -- src/ tests/   → nothing.

git grep -n "\* 100.0 /" -- src/   → src/RemoteFileSync/Sync/DeleteBudget.cs:30 only.
  No percentage expression survives outside DeleteBudget; all five bounds route through it.
```

---

## 8. Anything beyond the brief, and why

1. **`tests/RemoteFileSync.Tests/Network/PullExecutionTests.cs`** (new, 3 tests). The brief
   assigns end-to-end Pull proof to Phase 10 and leaves this task covered "only by the build".
   The team lead required an acceptance test that asserts Pull moves bytes. Placed in `Network/`,
   not `Integration/`, so Phase 10's ownership is untouched. This is what produced the only
   genuine behavioural red in the phase.
2. **One extra `DeleteBudget` case** (negative denominator) — rationale in §3.
3. **The mutation-testing pass** on `DeleteBudget` — the only way to show teeth on tests that
   could not have a red phase.
4. **The trust-boundary comment at `SyncServer.cs:217-224`** distinguishing the provenance of the
   two denominators. The helper's XML doc already said it; the call site, where the destructive
   decision is actually made, did not.
5. **Anchor corrections** in §1.1 and §1.2 — the substantive deviations from the brief.
6. **One sentence removed from the brief's commit message.** The brief's second paragraph ends
   "The conflict-rename squatter guard is routed through the same helper, so no percentage
   expression survives outside it." Phase 7 already did that (§1.2), so this commit did not, and
   claiming it would put a false statement in the permanent history. The clause about no
   percentage expression surviving is still *true* of the tree — it is just not this commit's
   doing. Everything else in the message is verbatim from the brief.

Not done, deliberately: brief step 3g (already landed in Phase 7), and anything under
`tests/**/Integration/`.

---

## Fix pass (review findings)

Applied on top of `9f84ba8`, per-commit, not amended. Review returned Spec ✅ / Changes needed:
0 Critical, 3 Important, 8 Minor. Fixed all three Important plus M1, M2, M5 as adjudicated.

### I1 — delete guard bounded the COUNT, not the PATH (`SyncClient.cs`, `SyncServer.cs`)

Both peers' receive-delete loops (`SyncClient.cs` step 10, `SyncServer.cs` step 7) trusted
whatever path arrived in each `DeleteFile` frame. The budget only bounds how many frames are
expected — `clientDeletes`/`serverDeletes` — not which paths; a peer that sent the right COUNT of
frames but the wrong PATH in one of them got that arbitrary in-folder file deleted, with
`PathGuard` only keeping the result inside the sync root, not inside the approved set.

**Fix, both peers, identical shape:** after `DeserializeDeleteFile`, compare the wire path against
`del.RelativePath` (the plan entry this loop iteration is for) with `string.Equals(...,
StringComparison.Ordinal)`. On mismatch: log a protocol-mismatch message, `skippedFiles++`, send
back `DeleteConfirm(wirePath, success: false)` (so the frame count the sender is waiting on stays
aligned — no desync, just a refused deletion), and `continue` to the next planned entry. Never
call `Archive()` on the mismatched path.

Chose "skip and continue" over "abort the session" (`desynced = true; return 3`): a wrong path in
one frame does not corrupt the wire framing (it is still a well-formed `DeleteFile` frame, so a
`DeleteConfirm` per iteration keeps both sides' frame counts in lockstep) — only its content
diverges from the plan. Aborting the whole session over one bad frame would let a single lie
degrade an otherwise-valid sync from "declined one deletion" to "moved nothing at all", which is
a worse outcome for the honest paths in the same plan.

**Covering test:** `tests/RemoteFileSync.Tests/Network/DeletePathGuardTests.cs` (new),
`DeleteFrame_NamesAPathThePlanDidNotApprove_UnlistedFileSurvives`. Server side, in the
`ConflictGuardTests` raw-protocol style: a hand-scripted "client" (`TcpClient`) sends a plan
approving `DeleteOnServer` for `planned.txt`, then a `DeleteFile` frame naming `untouched.txt`
instead. Asserts the confirm reports `success: false`, exit is 1 (not 4, not a crash), and BOTH
`untouched.txt` (the unlisted path the frame named) and `planned.txt` (the approved path whose
frame never arrived) survive on disk. Both files are kept well under
`MinTrackedFilesForDeleteGuard` so the percentage bound is exempt and cannot be what saves them —
only the path check can. Only the server side has a dedicated test (task said "client or server");
the client-side loop got the byte-identical fix and is otherwise covered by the full-suite runs
below showing no regression.

### I2 — marker armed only on exit 0, so a habitually-exit-1 pair never disarms the gate (`SyncClient.cs`)

`RunSessionAsync` wrote `pair.marker` only when `exit == 0`. Exit 1 ("completed with skipped
files") also leaves a fully-built ancestor table behind — the ancestor rows are written before the
transfer loops and regardless of `skippedFiles` — so a pair with one permanently-locked file
(which exits 1 on every run) never armed the marker. If that pair's database were later wiped, the
no-ancestor gate — whose entire job is to catch exactly that — would never fire.

**Fix:** `if ((exit == 0 || exit == 1) && _dbPath != null && _options.DeleteEnabled)
PairMarker.Write(_dbPath);`. Exit codes 2 (connection failure), 3 (protocol/fatal) and 4 (safety
abort) are excluded: none of them leave a completed ancestor-write pass behind. Updated the
in-code comment to say what the marker actually means — "an ancestor table was built" — not "the
run was perfect".

**M1 (bundled here, same method's XML doc):** `RunAsync`'s doc claimed a second call "would find a
disposed instance". False — the `finally` nulls `_db` before disposing, so a second call re-enters
the open branch and works. Rewrote the doc to say what's actually true (safe re-entry via
`dbPath`, not exercised or supported, but not broken) instead of dropping the claim silently.

**Covering test:** `tests/RemoteFileSync.Tests/Network/MarkerArmingTests.cs` (new),
`ExitOne_OneSkippedFile_StillArmsTheMarker`. Drives a real `SyncClient` (TwoWay + delete) against a
hand-scripted raw "server", with an ancestor row pre-seeded via `SyncDatabase.UpsertSynced` so the
plan computes a real `DeleteOnClient` for `ghost.txt`. The script deletes the client's own file out
from under the delete phase (not a lock/timing race — deterministic, since the whole exchange is
paced by the test) so `Archive()` reports `NothingToArchive` for a path the wire named correctly —
proving this is the ordinary "one skipped file" path, not the I1 mismatch guard. Asserts exit == 1
and `PairMarker.Exists(dbPath)` is true afterward.

### I3 — server delete-budget counts ungated on mode (`SyncServer.cs`)

The pre-receive-loop budget check counted `DeleteOnServer` and `DeleteOnClient` unconditionally. In
Push mode `ModeGate.ServerToClient` is false, so no `DeleteOnClient` frame is ever sent — but a
plan carrying them (malformed or hostile) still made the server count them, `return 4`, and close
the socket, and the client then blocked on `ReadMessageAsync` and threw out of `RunAsync` instead
of getting a clean refusal.

**Fix:** gated each count on the same `ModeGate` predicate the matching loop below uses —
`plannedServerDeletes` only when `ModeGate.ClientToServer(mode)`, `plannedClientDeletes` only when
`ModeGate.ServerToClient(mode)`; otherwise 0. A deletion the mode will never execute can no longer
abort the session.

No new dedicated test: the existing `ConflictGuardTests` and `PullExecutionTests` already drive
Push/Pull/TwoWay sessions through this exact code path with real and hand-crafted plans, and the
full-suite runs below confirm no regression from the added gating.

### M2 — `ModeGate` failed OPEN on an undefined `SyncMode` (`ModeGate.cs`)

`mode != SyncMode.Pull` / `mode != SyncMode.Push` both returned `true` for `(SyncMode)0`, which is
not a defined enum member. Unreachable today (the server clamps the wire byte before casting;
the client only ever parses a real enum literal from its own CLI), but this is a shared safety
predicate and must not rely on every caller having already validated its input.

**Fix:** rewrote both as a whitelist `switch` — `ClientToServer` returns true only for
`Push`/`TwoWay`, `ServerToClient` only for `Pull`/`TwoWay`, `false` for everything else including
any undefined value.

**Covering test:** `tests/RemoteFileSync.Tests/Sync/ModeGateTests.cs`,
`UndefinedMode_FailsClosedInBothDirections` (new `[Fact]`) — asserts `(SyncMode)0` returns `false`
from both predicates.

### M5 — `DeleteBudgetTests` missing the `maxDeletePercent: 0` boundary

`100` (disable the guard) was covered; `0` (allow nothing) — the opposite boundary — was not.

**Fix:** added `[InlineData(1, 20, 0, false)]` to `PercentageIsBoundedByTheDestinationPopulation` —
one delete out of 20, `maxDeletePercent: 0`, refused.

### Build and full-suite verification

`dotnet build -c Release` (clean rebuild): **0 errors, 20 warnings** — identical to the Phase 8
baseline, no new warnings.

Full suite run twice, byte-identical both times:

```
Failed RemoteFileSync.Tests.Integration.DeleteSyncTests.DeleteSync_SecondRun_DetectsDeletions
Failed RemoteFileSync.Tests.Integration.DeleteThresholdTests.EmptyPeerFolder_AbortsInsteadOfMassDeleting
Failed RemoteFileSync.Tests.Integration.DeleteSyncTests.DeleteSync_UniDirectional_ServerDeletionIgnored
Failed RemoteFileSync.Tests.Integration.DeleteSyncTests.DeleteSync_BidiSymmetric
Failed RemoteFileSync.Tests.Integration.DeleteSyncTests.DeleteSync_Case1_PropagatesDeletion
Failed RemoteFileSync.Tests.Integration.EndToEndTests.BiDirectional_BothSidesSync
Failed!  - Failed: 6, Passed: 479, Skipped: 0, Total: 485  (RemoteFileSync.Tests)
Passed!  - Failed: 0, Passed:  23, Skipped: 0, Total:  23  (ExecRFS.Tests)
```

Exactly the six Phase-10-owned failures named by the team lead, unchanged from the Phase 8
baseline. Total grew from 481 to 485 (four new test cases: `DeletePathGuardTests` × 1,
`MarkerArmingTests` × 1, `ModeGateTests` × 1, one added `InlineData` row on `DeleteBudgetTests`);
passed grew from 475 to 479 — the same delta, so nothing regressed and nothing new is red. Neither
`PartialSync_NoResurrection` nor `NoMarker_IsAGenuineFirstRunAndTheClientOpensItsOwnDatabase` (the
two known PORT-RACE flakes) failed in either run.

### Files touched

`src/RemoteFileSync/Network/SyncClient.cs`, `src/RemoteFileSync/Network/SyncServer.cs`,
`src/RemoteFileSync/Sync/ModeGate.cs`, `tests/RemoteFileSync.Tests/Sync/DeleteBudgetTests.cs`,
`tests/RemoteFileSync.Tests/Sync/ModeGateTests.cs`,
`tests/RemoteFileSync.Tests/Network/DeletePathGuardTests.cs` (new),
`tests/RemoteFileSync.Tests/Network/MarkerArmingTests.cs` (new). Nothing under
`tests/**/Integration/` touched; `SyncDatabase.cs`, `ArchiveManager.cs`, `FileTransfer.cs`,
`SyncEngine.cs`, `ConflictKeepBothExecutor.cs`, `ConflictNamer.cs` untouched (used read-only by the
new tests, per the ownership boundary). Left alone: the uncommitted `.csproj` changes and
`Plans/_Prompts.md`.
