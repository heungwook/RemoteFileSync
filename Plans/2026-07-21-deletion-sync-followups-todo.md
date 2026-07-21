# Deletion-Sync — Follow-Up To-Do List

> Created 2026-07-21, after the deletion-sync ancestor-merge redesign merged to `main`
> (merge commit `3eea545`; 22 commits, 10 phases; 554/554 tests green; final whole-branch
> review 0 Critical / 0 Major). These items were **consciously deferred at merge time** — none
> block anything already shipped; they are the tracked next work.

**Status legend:** `[ ]` open · `[~]` in progress · `[x]` done

> **Progress 2026-07-21:** §1 implemented on branch `feat/execrfs-gui-sync-options`
> (4 commits, 565 tests); §2 + §2c implemented on branch `fix/deletion-sync-robustness`
> (5 commits, 559 tests). Both branches adversarially reviewed; all confirmed findings fixed.
> §3 (destructive branch deletion + unrelated drift) intentionally left for the user to decide.

---

## 1. ExecRFS GUI feature completeness  — *branch `feat/execrfs-gui-sync-options`*

The CLI fully implements Pull mode, `--mirror`, the `--archive-*` options, and emits a `review`
progress event. The WPF/Blazor GUI does not yet surface any of it. (The branch's final commit
added GUI mockups for exactly this, so it was always intended follow-up.)

- [x] Add fields to `src/ExecRFS/Models/SyncProfile.cs`: `Mode` (push/pull/two-way),
      `MirrorDeletes`, `ArchiveFolder`, `ArchiveKeepDays`, `ArchiveMaxBytes`
      (currently only `Bidirectional` + `DeleteEnabled`). *(added `SyncMode` enum + `EffectiveMode`)*
- [x] Surface the new fields in the client panel UI (`src/ExecRFS/Components/Panels/ClientPanel.razor`).
      *(Bidirectional checkbox → Mode dropdown; Mirror checkbox; Archive folder/keep-days/max-size)*
- [x] Update `src/ExecRFS/Services/CommandBuilder.cs` to emit `--mode`, `--mirror`,
      `--archive-folder`, `--archive-keep-days`, `--archive-max-size`
      (currently emits only `--bidirectional`/`--delete`). Keep `--bidirectional` as the load
      path for old profiles. *(via `EffectiveMode` migration + `ProfileService` Mode stamping;
      archive flags emit on BOTH branches — the server is the archiving side in a push)*
- [x] Add a `review` case to the GUI `HandleProgress` switches
      (`ClientPanel.razor` and `Components/Shared/ProgressBar.razor`) so conflicts /
      resurrections / overwrites render in the UI instead of only as a raw log line.
      Read `Kind`/`ClientSize`/`ClientMtime`/`ServerSize`/`ServerMtime`/`RenamedTo` from
      `ProgressEvent`. *(honours the `-1`/empty "unknown" sentinel; consistent Starting-based reset)*
- [x] Tests for the new profile serialization + command-string generation.
      *(+legacy migration, out-of-range Mode fallback; 25 → 36 ExecRFS tests)*

---

## 2. Code hygiene + pre-existing robustness  — *branch `fix/deletion-sync-robustness`*

### 2a. Remove (or wire) the dead binary-state fallback
`SyncClient`'s `_stateManager` path is unreachable in the shipped CLI (`Program.cs` never passes
a `SyncStateManager`). Comments were corrected during the merge; the dead code was left in.

- [x] Decide: wire a real `SyncStateManager` through `Program` for genuine backward-compat, **or**
      delete the dead path. *(deleted — binary state already migrates to SQLite)*
- [x] If deleting: remove the `previousState` load, the `_stateManager != null` merged-manifest
      block, `SyncEngine.BuildMergedManifest`, and its now-defensive `ConflictKeepBoth` case;
      adjust `BuildMergedManifest_ConflictKeepBoth_KeepsClientEntry` accordingly. *(commit `c5e1e5b`)*

### 2b. Pre-existing robustness gaps (verified **not** regressions vs base `bf8a1fb`)

- [x] **Staging-sweep unrecoverable delete** — `FileScanner.SweepAbandonedStagingFiles` deletes any
      file whose name merely *contains* `.rfs-part-` and is >24h old, with no archive first. A
      legitimate user file matching that substring is destroyed unrecoverably. Fix: match the exact
      staging shape (suffix + 32-hex GUID + end-of-name), **or** archive before deleting.
      *(tightened matcher to `\.rfs-part-[0-9a-f]{32}$`; commit `fdacec0`)*
- [x] **Send-loop desync on a locked/removed source file** — a non-disconnect exception mid-send
      sends zero/partial frames while the receiver still expects the file → potential stream desync.
      Fix: make a per-file send failure terminal for the phase (treat like the `desynced` flag),
      **or** frame an explicit per-file skip the receiver consumes.
      *(chose terminal-abort in both send loops; excludes cancellation; commits `3c2248c`, `fa1d009`)*

### 2c. Non-blocking micro-item
- [x] *(Optional)* F1's corrupt-DB `return 4` sits above the try/finally that owns
      `CompleteSession`, so that rare path leaves an open `sync_sessions` row (**not** a regression —
      the pre-fix throw leaked it too). One-liner if audit-log completeness is wanted; note it would
      mean writing to a DB just declared unreadable.
      *(relocated `StartSession` inside the try — no leak, no write to the unreadable DB; commit `9ab0523`)*

---

## 3. Optional cleanups  — *left for the user (destructive / unrelated)*

- [ ] Delete fully-merged remote branches when no longer needed for reference:
      `origin/feat/deletion-sync-ancestor-merge`, `origin/feature/deletion-sync`.
      *(destructive + outward-facing — not done without explicit confirmation)*
- [ ] Resolve the two unrelated working-tree drift items (keep or discard):
      `Plans/_Prompts.md` (cosmetic markdown renumber) and untracked `.github/copilot-instructions.md`
      (Azure/Copilot auto-generated). Neither belongs to the deletion-sync work. *(left untouched)*

---

## Reference
- Implementation plan: `Plans/2026-07-20-deletion-sync-ancestor-merge-plan.md`
- Merge commit: `3eea545` on `main`
- Follow-up branches: `fix/deletion-sync-robustness`, `feat/execrfs-gui-sync-options`
