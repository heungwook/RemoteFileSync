# RemoteFileSync

A folder-synchronisation tool for Windows: a CLI (`RemoteFileSync`) that syncs a local folder
with a peer over TCP, and a WPF + Blazor GUI (`ExecRFS`) that drives it.

> ## ⚠ Security notice — read before exposing this to a network
>
> **The wire protocol has no authentication and no encryption.** Any peer that can reach the
> port can read, write, and delete files within the sync folder, and all traffic — including
> file contents — crosses the network in cleartext.
>
> The server therefore binds to **loopback (`127.0.0.1`) by default**. Use `--bind` to expose
> it only on a trusted network, or better, tunnel it over SSH or a VPN. Do not put it on the
> open internet.

---

## Building

Requires the .NET 10 SDK.

```bash
dotnet build RemoteFileSync.slnx
dotnet test  RemoteFileSync.slnx
```

## Quick start

On the machine holding the files (the server):

```bash
RemoteFileSync.exe server --folder "D:\Shared" --bind 0.0.0.0
```

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

## Options

| Flag | Short | Default | Description |
|---|---|---|---|
| `--folder <path>` | `-f` | *(required)* | Local sync folder |
| `--host <addr>` | `-h` | — | Server hostname/IP (client mode only) |
| `--port <n>` | `-p` | `15782` | TCP port |
| `--bind <ip>` | — | `127.0.0.1` | Server bind address. `0.0.0.0` exposes it on all interfaces — see the security notice |
| `--once` | — | off | Server: handle one connection then exit, instead of listening continuously |
| `--mode <push\|pull\|two-way>` | — | `push` | Which side is authoritative — see Quick start |
| `--bidirectional` | `-b` | off | Deprecated alias for `--mode two-way` |
| `--mirror` | — | off | Let deletions propagate even for files with no sync history. Destructive — see Safety behaviour |
| `--delete` | `-d` | off | Propagate deletions (opt-in) |
| `--max-delete-percent <n>` | — | `25` | Abort if deletions exceed this share of tracked files |
| `--force-delete` | — | off | Bypass the deletion threshold |
| `--backup-folder <path>` | — | `.rfs-backups-NAME` beside the sync folder | Accepted for CLI compatibility only — nothing is written there. Replaced, deleted and conflicted files go to `--archive-folder` instead. **Must be outside the sync folder** |
| `--archive-folder <path>` | — | `.rfs-archive-NAME` beside the sync folder | Where replaced, deleted and conflicted files are kept. **Must be outside the sync folder** |
| `--archive-keep-days <n>` | — | `30` | Prune archive sessions older than this. `0` keeps them forever |
| `--archive-max-size <n>` | — | `0` (off) | Cap the total archive size; accepts `K`/`M`/`G` suffixes. Oldest sessions are pruned first |
| `--include <glob>` | — | — | Include pattern, repeatable |
| `--exclude <glob>` | — | — | Exclude pattern, repeatable |
| `--block-size <n>` | `-bs` | `65536` | Transfer block size in bytes (clamped to 4 KB–4 MB) |
| `--max-threads <n>` | `-t` | `1` | Accepted but **not yet implemented** — transfers are sequential |
| `--verbose` | `-v` | off | Verbose console output |
| `--log <path>` | `-l` | — | Log file path |
| `--json-progress` | — | off | Emit JSON progress events on stdout, for the GUI |

### Glob patterns

A pattern containing a separator (`/` or `\`) matches against the **relative path**; otherwise
it matches the **file name** at any depth. `*` matches any characters including separators, so
`node_modules/*` covers nested entries.

```
--exclude "*.tmp"              # any .tmp file, at any depth
--exclude "node_modules/*"     # everything under node_modules
```

Paths excluded by a filter are invisible to the sync: never transferred, and never deleted on
the peer.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Completed, but some files were skipped, or the sync was stopped |
| `2` | Connection failure |
| `3` | Protocol or fatal error |
| `4` | Aborted by a safety guard (see below) |

## Safety behaviour

Several guards exist because sync bugs destroy data rather than merely misbehaving:

- **Deletion threshold.** With `--delete`, the sync aborts (exit `4`) if deletions would exceed
  `--max-delete-percent` of the files on the side being deleted from. This catches the common
  disaster of pointing `--folder` at the wrong or an empty directory. It applies only once at
  least 10 files exist on the side being deleted from, since a percentage is meaningless on tiny
  sets. Override with `--force-delete`.
- **Incomplete scans never delete.** If any directory could not be read, deletion propagation is
  refused — an unreadable file is indistinguishable from a deleted one.
- **Atomic receives.** Incoming files are staged beside their destination, checksum-verified,
  and committed with a single rename. An interrupted or corrupt transfer leaves the existing
  file untouched.
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
- **Path containment.** Every path received over the network is validated to resolve inside the
  sync folder, including a check for junctions and symlinks that would otherwise escape it.

## How two-way sync decides

Everything below requires `--delete` alongside `--mode two-way`. Without `--delete`, no ancestor
table is built at all, so two-way sync falls back to plain newest-wins for every path — there is
no edit-vs-delete discrimination, and a file deleted on one side is simply re-copied
(resurrected) from the other rather than tombstoned.

With `--delete`, two-way sync keeps an **ancestor table**: for every path, the size and
modification time each side had at the end of the last successful sync. Comparing the two
current states against that common ancestor separates four cases a straight two-way comparison
cannot:

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

## ExecRFS (GUI)

A WPF shell hosting a Blazor UI that builds command lines, launches the CLI as child processes,
and renders their JSON progress events. It manages a server and a client instance side by side,
with profile save/load, live logs, and per-file progress.

The GUI locates `RemoteFileSync.exe` in the sibling build output, then alongside `ExecRFS.exe`,
then on `PATH`.

## Repository layout

```
src/RemoteFileSync/   CLI: protocol, transfer, sync engine, SQLite state
src/ExecRFS/          WPF + Blazor GUI
tests/                xUnit test projects
Plans/                Design documents and remediation plans
```

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
