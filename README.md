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
RemoteFileSync.exe client --host 10.0.1.50 --folder "C:\Local" --bidirectional
```

Without `--bidirectional` the sync is one-way: the client pushes to the server.

## Options

| Flag | Short | Default | Description |
|---|---|---|---|
| `--folder <path>` | `-f` | *(required)* | Local sync folder |
| `--host <addr>` | `-h` | — | Server hostname/IP (client mode only) |
| `--port <n>` | `-p` | `15782` | TCP port |
| `--bind <ip>` | — | `127.0.0.1` | Server bind address. `0.0.0.0` exposes it on all interfaces — see the security notice |
| `--once` | — | off | Server: handle one connection then exit, instead of listening continuously |
| `--bidirectional` | `-b` | off | Sync both directions rather than client → server only |
| `--delete` | `-d` | off | Propagate deletions (opt-in) |
| `--max-delete-percent <n>` | — | `25` | Abort if deletions exceed this share of tracked files |
| `--force-delete` | — | off | Bypass the deletion threshold |
| `--backup-folder <path>` | — | `.rfs-backups-NAME` beside the sync folder | Where replaced and deleted files are kept. **Must be outside the sync folder** |
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
  `--max-delete-percent` of tracked files. This catches the common disaster of pointing
  `--folder` at the wrong or an empty directory. It applies only once at least 10 files are
  tracked, since a percentage is meaningless on tiny sets. Override with `--force-delete`.
- **Incomplete scans never delete.** If any directory could not be read, deletion propagation is
  refused — an unreadable file is indistinguishable from a deleted one.
- **Atomic receives.** Incoming files are staged beside their destination, checksum-verified,
  and committed with a single rename. An interrupted or corrupt transfer leaves the existing
  file untouched.
- **Backups are copies.** Files replaced or deleted by a sync are copied into a dated backup
  tree first. The backup folder must live outside the sync folder, or backups would be
  re-synced to the peer and grow without bound.
- **Path containment.** Every path received over the network is validated to resolve inside the
  sync folder, including a check for junctions and symlinks that would otherwise escape it.

## Protocol compatibility

The wire protocol is **version 2**. Both peers must run the same build — a mismatch is rejected
during the handshake rather than silently misparsed. Version 1 did not carry file timestamps,
so a mixed pair could never converge.

A single protocol frame is capped at 64 MB. Since the file manifest is sent as one frame, that
bounds a synced tree at roughly 1.3 million files.

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

- `--max-threads` is parsed but transfers are sequential.
- Mid-transfer resume is not implemented; an interrupted sync restarts the affected file.
- Empty directories are not synced.
- No authentication or transport encryption (see the security notice above).
