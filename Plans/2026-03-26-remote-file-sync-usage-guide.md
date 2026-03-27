# RemoteFileSync Usage Guide

**Version:** 1.0
**Platform:** Windows 10 / Windows 11
**Runtime:** Self-contained (no .NET installation required)

---

## Quick Start

RemoteFileSync synchronizes files between two computers over TCP. One side runs as a **server** (waits for connections), the other as a **client** (initiates the connection).

```
RemoteFileSync.exe <server|client> [options]
```

---

## Options Reference

| Option | Short | Default | Description |
|--------|-------|---------|-------------|
| `--host` | `-h` | — | Server hostname or IP (client only, required) |
| `--port` | `-p` | `15782` | TCP port number |
| `--folder` | `-f` | — | Local sync folder path (required) |
| `--bidirectional` | `-b` | off | Enable bi-directional sync |
| `--delete` | `-d` | off | Enable deletion propagation (opt-in) |
| `--backup-folder` | — | same as `--folder` | Root folder for outdated file backups |
| `--include` | — | all files | Glob include pattern (repeatable) |
| `--exclude` | — | none | Glob exclude pattern (repeatable) |
| `--block-size` | `-bs` | `65536` (64 KB) | Transfer chunk size in bytes (4 KB–4 MB) |
| `--max-threads` | `-t` | `1` | Maximum concurrent file transfers |
| `--verbose` | `-v` | off | Verbose console output |
| `--log` | `-l` | none | Log file path (always full detail) |

---

## Exit Codes

| Code | Meaning |
|------|---------|
| `0` | Success — all files synced |
| `1` | Partial — sync completed but some files were skipped |
| `2` | Connection failure — could not reach server |
| `3` | Fatal error — invalid arguments, disk full, unrecoverable |

---

## Usage Scenarios

### 1. Basic Uni-Directional Sync (Client to Server)

Push files from a local workstation to a file server. Only files on the client are sent to the server. Server-only files are left untouched.

**On the server machine:**
```
RemoteFileSync.exe server --folder "D:\SharedFiles" --port 15782
```

**On the client machine:**
```
RemoteFileSync.exe client --host 192.168.1.100 --port 15782 --folder "C:\MyFiles"
```

---

### 2. Bi-Directional Sync Between Two Machines

Both sides exchange files. Newer files win (by timestamp, then by size). Outdated files are backed up to a dated subfolder before being overwritten.

**On Machine A (server):**
```
RemoteFileSync.exe server --folder "D:\ProjectFiles" --port 15782
```

**On Machine B (client):**
```
RemoteFileSync.exe client --host 192.168.1.100 --port 15782 --folder "E:\ProjectFiles" --bidirectional
```

After sync:
- Files only on A appear on B
- Files only on B appear on A
- If both sides have the same file but with different content, the newer version wins and the old version is backed up

---

### 3. Localhost Bi-Directional Sync (Two Local Folders)

Sync between two folders on the same machine, using the TCP protocol as if they were remote.

**Terminal 1 — Start the server:**
```
RemoteFileSync.exe server -f "H:\BK_20260320\NET10" -p 15782 -v
```

**Terminal 2 — Start the client:**
```
RemoteFileSync.exe client -h 127.0.0.1 -p 15782 -f "H:\SyncTest\NET10" -b -v
```

---

### 4. Selective Sync with Include/Exclude Filters

Sync only specific file types. Include patterns are applied first, then exclude patterns filter the result. Patterns match against the file name (not the full path).

**Sync only C# source files, excluding designer-generated files:**
```
RemoteFileSync.exe server -f "D:\Source" -p 15782

RemoteFileSync.exe client -h 192.168.1.100 -p 15782 -f "C:\Source" -b \
  --include "*.cs" --include "*.csproj" --include "*.slnx" \
  --exclude "*.Designer.cs" --exclude "*.g.cs"
```

**Sync documents only:**
```
RemoteFileSync.exe client -h 10.0.0.5 -p 15782 -f "C:\Docs" -b \
  --include "*.docx" --include "*.xlsx" --include "*.pdf" --include "*.pptx"
```

**Sync everything except temp and build artifacts:**
```
RemoteFileSync.exe client -h 10.0.0.5 -p 15782 -f "C:\Project" -b \
  --exclude "*.tmp" --exclude "*.bak" --exclude "*.obj" --exclude "*.pdb"
```

---

### 5. High-Performance Sync with Tuning Options

For large syncs over fast networks, increase block size and thread count.

**Server:**
```
RemoteFileSync.exe server -f "D:\LargeDataset" -p 15782 -v \
  --block-size 262144 --max-threads 4
```

**Client:**
```
RemoteFileSync.exe client -h 192.168.1.100 -p 15782 -f "E:\LargeDataset" -b -v \
  --block-size 262144 --max-threads 4
```

| Block Size | Best For |
|------------|----------|
| `4096` (4 KB) | Many small files, slow networks |
| `65536` (64 KB) | General purpose (default) |
| `262144` (256 KB) | Large files, fast LAN |
| `4194304` (4 MB) | Very large files, 10 Gbps networks |

---

### 6. Custom Backup Folder

By default, outdated files are backed up inside the sync folder under a dated subfolder (`yyyyMMdd/`). To keep backups separate:

```
RemoteFileSync.exe server -f "D:\Production" -p 15782 \
  --backup-folder "D:\SyncBackups"

RemoteFileSync.exe client -h 192.168.1.100 -p 15782 -f "C:\Production" -b \
  --backup-folder "C:\SyncBackups"
```

**Backup path structure:**
```
D:\SyncBackups\
  20260326\
    docs\report.docx         <- backed up before overwrite
    docs\report_1.docx       <- second overwrite same day
    src\Program.cs
  20260327\
    docs\report.docx         <- next day's backup
```

---

### 7. Logging for Troubleshooting and Auditing

The `--log` flag writes full verbose detail to a file regardless of the `--verbose` flag. Useful for scheduled tasks and auditing.

**Quiet console, full log file:**
```
RemoteFileSync.exe client -h 192.168.1.100 -p 15782 -f "C:\Data" -b \
  --log "C:\Logs\sync.log"
```

**Verbose console AND log file:**
```
RemoteFileSync.exe client -h 192.168.1.100 -p 15782 -f "C:\Data" -b -v \
  --log "C:\Logs\sync.log"
```

**Log file sample:**
```
[2026-03-26 07:30:01.123] [INF] RemoteFileSync v1.0 — Client mode
[2026-03-26 07:30:01.456] [INF] Connecting to 192.168.1.100:15782...
[2026-03-26 07:30:01.789] [INF] Connected. Bi-directional sync. Block: 64KB, Threads: 1
[2026-03-26 07:30:02.100] [INF] Local manifest: 156 files
[2026-03-26 07:30:02.500] [INF] Remote manifest: 148 files
[2026-03-26 07:30:02.510] [INF] Sync plan: 12 transfers, 141 skipped
[2026-03-26 07:30:02.600] [INF] [→] docs/report.docx
[2026-03-26 07:30:03.100] [INF] [←] images/logo.png
[2026-03-26 07:30:05.200] [INF] Sync complete: 12 files, 45.2 MB, 3200ms
```

---

### 8. Scheduled Sync with Windows Task Scheduler

Run sync on a schedule without user interaction. Use the published single-file executable and exit codes for monitoring.

**Create a batch script `sync.bat`:**
```bat
@echo off
set LOG=C:\Logs\sync_%date:~0,4%%date:~5,2%%date:~8,2%.log

C:\Tools\RemoteFileSync.exe client -h 192.168.1.100 -p 15782 ^
  -f "C:\SyncFolder" -b ^
  --log "%LOG%"

if %ERRORLEVEL% EQU 0 (
  echo Sync successful >> "%LOG%"
) else if %ERRORLEVEL% EQU 1 (
  echo Sync partial - some files skipped >> "%LOG%"
) else (
  echo Sync FAILED with code %ERRORLEVEL% >> "%LOG%"
)
```

**Note:** The server must be running before the client connects. Start the server as a background service or in a separate scheduled task.

---

### 9. Different Port (Firewall / Multiple Instances)

Run multiple sync instances simultaneously using different ports.

**Instance 1 — Sync source code:**
```
RemoteFileSync.exe server -f "D:\SourceCode" -p 20001
RemoteFileSync.exe client -h 10.0.0.5 -p 20001 -f "C:\SourceCode" -b
```

**Instance 2 — Sync documents (same server machine, different port):**
```
RemoteFileSync.exe server -f "D:\Documents" -p 20002
RemoteFileSync.exe client -h 10.0.0.5 -p 20002 -f "C:\Documents" -b
```

---

### 10. Full Production Example

A complete bi-directional sync with all recommended production options.

**Server (data center machine, 10.0.1.50):**
```
RemoteFileSync.exe server \
  --folder "D:\Production\AppData" \
  --port 15782 \
  --backup-folder "D:\Backups\SyncBackups" \
  --block-size 262144 \
  --max-threads 4 \
  --verbose \
  --log "D:\Logs\sync-server.log"
```

**Client (office workstation):**
```
RemoteFileSync.exe client \
  --host 10.0.1.50 \
  --port 15782 \
  --folder "C:\Production\AppData" \
  --bidirectional \
  --backup-folder "C:\Backups\SyncBackups" \
  --include "*.cs" --include "*.csproj" --include "*.json" --include "*.xml" \
  --exclude "*.tmp" --exclude "*.bak" --exclude "*.log" \
  --block-size 262144 \
  --max-threads 4 \
  --verbose \
  --log "C:\Logs\sync-client.log"
```

---

### 11. Deletion Propagation with `--delete`

When `--delete` is enabled, files deleted on one side since the last sync are detected and handled:

- **Case 1:** Deleted on Side-A, untouched on Side-B → delete propagated to Side-B (backed up first)
- **Case 2:** Deleted on Side-A, modified on Side-B → restored from Side-B to Side-A

The first sync with `--delete` establishes a state baseline (no deletions occur). Subsequent syncs compare against this baseline to detect deletions.

**Bi-directional sync with deletion propagation:**
```
RemoteFileSync.exe server -f "D:\SharedFiles" -p 15782

RemoteFileSync.exe client -h 192.168.1.100 -p 15782 -f "C:\SharedFiles" -b -d
```

**Uni-directional sync with deletion (client is source of truth):**
```
RemoteFileSync.exe server -f "D:\Mirror" -p 15782

RemoteFileSync.exe client -h 192.168.1.100 -p 15782 -f "C:\Source" -d
```

In uni-directional mode, only client-side deletions propagate to the server. Server-side deletions are ignored (server is not authoritative).

**State file location:**
```
%LOCALAPPDATA%\RemoteFileSync\{pairId}\sync-state.bin
```

State is saved only after a fully successful sync (exit code 0). If a sync is partial or fails, the state file is not updated.

**Verbose output with deletions:**
```
[07:30:03] Sync plan: 6 transfers, 2 delete, 141 skipped
[07:30:04] [DEL→] docs/old-report.docx (deleted on server)
[07:30:04] [←] data/updated.csv (deleted on client, modified on server → restore)
[07:30:05] Sync complete: 8 files transferred, 2 deleted, 45.2 MB total.
```

---

## How Conflict Resolution Works

When the same file exists on both sides with different content:

| Scenario | Winner | Rule |
|----------|--------|------|
| Client timestamp newer | Client | Newer timestamp wins |
| Server timestamp newer | Server | Newer timestamp wins |
| Same timestamp, client file larger | Client | Larger size wins |
| Same timestamp, server file larger | Server | Larger size wins |
| Same timestamp, same size (within ±2 sec) | Skip | Files considered identical |

The losing file is moved to the backup folder before being overwritten:
```
{backup-folder}/{yyyyMMdd}/{relative-path}/{filename}
```

If the same file is backed up multiple times in one day, numeric suffixes are appended:
`report.docx`, `report_1.docx`, `report_2.docx`, ...

---

## Compression Behavior

Files are GZip-compressed during transfer to reduce bandwidth usage. Files that are already in a compressed format are transferred as-is (raw bytes) since re-compressing them would waste CPU with no size benefit.

**Already-compressed formats (transferred raw):**

| Category | Extensions |
|----------|------------|
| Archives | `.zip`, `.gz`, `.7z`, `.rar`, `.tgz`, `.bz2`, `.xz`, `.zst` |
| Images | `.jpg`, `.jpeg`, `.png`, `.gif`, `.webp`, `.heic`, `.avif` |
| Video | `.mp4`, `.avi`, `.mkv`, `.mov`, `.wmv`, `.webm` |
| Audio | `.mp3`, `.aac`, `.flac`, `.ogg`, `.wma`, `.m4a` |
| Documents | `.pdf`, `.docx`, `.xlsx`, `.pptx` |

All other file types (`.txt`, `.cs`, `.json`, `.xml`, `.csv`, `.html`, etc.) are GZip-compressed during transfer and decompressed on arrival.

---

## Troubleshooting

| Problem | Solution |
|---------|----------|
| "Connection refused" | Verify server is running and port is open in firewall |
| Files not syncing | Use `--verbose` to see sync plan; check include/exclude patterns |
| Identical files being transferred | Ensure system clocks are synchronized (NTP); timestamps must match within ±2 seconds |
| "Disk full" error | Free disk space on receiving side; check backup folder size |
| Slow transfers | Increase `--block-size` and `--max-threads`; check network bandwidth |
| Permission denied | Run as administrator or check folder permissions |
| Deleted files reappearing | Enable `--delete` flag; without it, deleted files are copied back as "new" |
| `--delete` not deleting on first run | Expected: the first sync with `--delete` only establishes state; deletions propagate from the second run onward |
| Wrong files deleted | Check system clocks (NTP); large clock skew can cause false "modified" detection |
