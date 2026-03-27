# RemoteFileSync — Design Specification

**Date:** 2026-03-26
**Version:** 1.0
**Status:** Draft
**Platform:** Windows 10 / Windows 11
**Runtime:** .NET 10, C#

---

## 1. Overview

RemoteFileSync is a command-line file synchronization tool that transfers files between two Windows computers over a TCP network connection. It operates as a single self-contained executable with no configuration files — all settings are provided via command-line arguments.

### 1.1 Goals

- Synchronize files in a specified folder on one computer to a specified folder on another computer
- Support both uni-directional (client → server) and bi-directional sync modes
- Single executable, zero external dependencies, no installation required
- Run unattended (suitable for Windows Task Scheduler)
- Preserve data safety by backing up outdated files before overwriting

### 1.2 Non-Goals

- Real-time / continuous file watching (this is a one-shot sync tool)
- Encryption or authentication (designed for trusted networks / VPNs)
- Cross-platform support (Windows only)
- GUI interface

---

## 2. Architecture

### 2.1 Project Structure

```
RemoteFileSync/
├── Program.cs                  # Entry point + CLI parsing
├── Models/
│   ├── SyncOptions.cs          # CLI options model
│   ├── SyncAction.cs           # Sync action enum and plan entry
│   ├── FileManifest.cs         # File metadata collection
│   └── FileEntry.cs            # Single file metadata (path, size, timestamp)
├── Network/
│   ├── SyncServer.cs           # TCP listener, accepts connections
│   ├── SyncClient.cs           # TCP client, connects to server
│   └── ProtocolHandler.cs      # Binary protocol serialization/deserialization
├── Sync/
│   ├── FileScanner.cs          # Scans directories, builds manifests
│   ├── SyncEngine.cs           # Compares manifests, produces sync plan
│   └── ConflictResolver.cs     # Timestamp/size comparison logic
├── Transfer/
│   ├── FileTransfer.cs         # Sends/receives files over TCP
│   └── CompressionHelper.cs    # GZip compression + compressed-format detection
├── Backup/
│   └── BackupManager.cs        # Moves outdated files to dated folders
└── Logging/
    └── SyncLogger.cs           # Console + file logging with verbosity levels
```

### 2.2 Dependencies

**Zero external NuGet packages.** All functionality uses the .NET Base Class Library:

| Namespace | Usage |
|-----------|-------|
| `System.Net.Sockets` | TCP server and client |
| `System.IO.Compression` | GZip compression |
| `System.Security.Cryptography` | SHA256 checksum |
| `System.Threading` | `SemaphoreSlim`, async concurrency |

> **Note:** CLI argument parsing is implemented manually (simple `args[]` loop) to maintain zero NuGet dependencies. The option set is small and well-defined, making a custom parser straightforward.

### 2.3 Component Diagram

```
┌─────────────────────────────────────────────────┐
│                   Program.cs                     │
│              (CLI parsing, entry point)          │
└────────────┬───────────────────┬────────────────┘
             │                   │
     ┌───────▼───────┐   ┌──────▼────────┐
     │  SyncServer   │   │  SyncClient   │
     │  (TCP Listen) │   │  (TCP Connect)│
     └───────┬───────┘   └──────┬────────┘
             │                   │
             └─────────┬─────────┘
                       │
              ┌────────▼────────┐
              │ ProtocolHandler │
              │ (Binary Proto)  │
              └────────┬────────┘
                       │
          ┌────────────┼────────────┐
          │            │            │
  ┌───────▼───┐ ┌─────▼─────┐ ┌───▼──────────┐
  │FileScanner│ │SyncEngine  │ │FileTransfer  │
  │(Manifest) │ │(Diff/Plan) │ │(Send/Receive)│
  └───────────┘ └─────┬─────┘ └───┬──────────┘
                      │            │
               ┌──────▼──┐  ┌─────▼───────────┐
               │Conflict │  │CompressionHelper│
               │Resolver │  │(GZip + Detect)  │
               └─────────┘  └─────────────────┘
                                    │
                             ┌──────▼───────┐
                             │BackupManager │
                             │(Dated Backup)│
                             └──────────────┘
```

---

## 3. CLI Interface

### 3.1 Usage

```
RemoteFileSync.exe <mode> [options]
```

Where `<mode>` is either `server` or `client`.

### 3.2 Examples

```bash
# Server mode — listen for connections
RemoteFileSync.exe server --port 15782 --folder "C:\SyncFolder"

# Client mode — uni-directional (push to server)
RemoteFileSync.exe client --host 192.168.1.100 --port 15782 --folder "C:\SyncFolder"

# Client mode — bi-directional sync
RemoteFileSync.exe client --host 192.168.1.100 --port 15782 --folder "C:\SyncFolder" --bidirectional

# Full options example
RemoteFileSync.exe client --host 192.168.1.100 --port 15782 --folder "C:\SyncFolder" \
  --bidirectional --include "*.docx" --include "*.xlsx" --exclude "*.tmp" \
  --block-size 262144 --max-threads 4 --verbose --log "C:\Logs\sync.log"
```

### 3.3 Options Reference

| Option | Short | Required | Default | Description |
|--------|-------|----------|---------|-------------|
| `server` / `client` | — | Yes | — | Operating mode (positional, first argument) |
| `--host` | `-h` | Client only | — | Server hostname or IP address |
| `--port` | `-p` | No | `15782` | TCP port number |
| `--folder` | `-f` | Yes | — | Local sync folder path |
| `--bidirectional` | `-b` | No | `false` | Enable bi-directional sync |
| `--backup-folder` | — | No | Same as `--folder` | Root folder for outdated file backups |
| `--include` | — | No | `*` (all files) | Glob include pattern (repeatable) |
| `--exclude` | — | No | None | Glob exclude pattern (repeatable) |
| `--block-size` | `-bs` | No | `65536` (64 KB) | Transfer chunk size in bytes (min: 4 KB, max: 4 MB) |
| `--max-threads` | `-t` | No | `1` | Maximum concurrent file transfers |
| `--verbose` | `-v` | No | `false` | Enable verbose console output |
| `--log` | `-l` | No | None | Log file path (always verbose detail) |

### 3.4 Argument Validation

- `--folder` must exist and be accessible
- `--host` is required in client mode, ignored in server mode
- `--port` must be in range 1–65535
- `--block-size` clamped to range 4,096–4,194,304 bytes (with warning if adjusted)
- `--include` / `--exclude` support standard glob patterns (`*`, `?`, `**`)
- When both `--include` and `--exclude` are specified, include is applied first, then exclude filters the result

---

## 4. Network Protocol

### 4.1 Transport

Raw TCP sockets. No encryption. Designed for trusted LAN / VPN environments.

### 4.2 Message Frame

Every message uses a fixed header followed by variable-length payload:

```
┌──────────────┬────────────────┬──────────────────┐
│ MessageType  │ PayloadLength  │     Payload      │
│   (1 byte)   │  (4 bytes)     │   (N bytes)      │
└──────────────┴────────────────┴──────────────────┘
```

- `MessageType`: Unsigned byte identifying the message kind
- `PayloadLength`: Signed 32-bit integer (little-endian), length of `Payload` in bytes
- `Payload`: Variable-length data, format depends on `MessageType`

### 4.3 Message Types

| Code | Name | Direction | Payload Format |
|------|------|-----------|----------------|
| `0x01` | `Handshake` | Client → Server | Version (`byte`) + SyncMode (`byte`: 0=uni, 1=bidi) |
| `0x02` | `HandshakeAck` | Server → Client | Version (`byte`) + Status (`byte`: 0=OK, 1=Reject) |
| `0x03` | `Manifest` | Both | Serialized file manifest (see §4.4) |
| `0x04` | `SyncPlan` | Client → Server | Serialized list of sync actions (see §4.5) |
| `0x05` | `FileStart` | Sender → Receiver | FileId (`int16`) + RelativePath (`UTF-8`) + OriginalSize (`int64`) + IsCompressed (`byte`) + BlockSize (`int32`) |
| `0x06` | `FileChunk` | Sender → Receiver | FileId (`int16`) + ChunkIndex (`int32`) + ChunkData (`byte[]`) |
| `0x07` | `FileEnd` | Sender → Receiver | FileId (`int16`) + SHA256 checksum (`32 bytes`) |
| `0x08` | `BackupConfirm` | Receiver → Sender | RelativePath (`UTF-8`) + Success (`byte`) |
| `0x09` | `SyncComplete` | Both | FilesTransferred (`int32`) + BytesTransferred (`int64`) + ElapsedMs (`int64`) |
| `0xFF` | `Error` | Both | ErrorCode (`int32`) + Message (`UTF-8`) |

### 4.4 Manifest Serialization (Binary)

```
┌────────────────┐
│ EntryCount     │  int32
├────────────────┤
│ Entry[0]       │
│  PathLength    │  int16
│  RelativePath  │  UTF-8 bytes
│  FileSize      │  int64
│  LastModUtc    │  int64 (ticks)
├────────────────┤
│ Entry[1]       │
│  ...           │
└────────────────┘
```

### 4.5 SyncPlan Serialization

```
┌────────────────┐
│ ActionCount    │  int32
├────────────────┤
│ Action[0]      │
│  ActionType    │  byte (0=SendToServer, 1=SendToClient, 2=ClientOnly, 3=ServerOnly, 4=Skip)
│  PathLength    │  int16
│  RelativePath  │  UTF-8 bytes
├────────────────┤
│ Action[1]      │
│  ...           │
└────────────────┘
```

### 4.6 Protocol Flow

```
CLIENT                                  SERVER
  │                                       │
  │─── 0x01 Handshake (v1, bidi) ───────>│
  │<── 0x02 HandshakeAck (OK) ──────────│
  │                                       │
  │─── 0x03 Manifest (client files) ────>│
  │<── 0x03 Manifest (server files) ─────│
  │                                       │
  │  [Client computes sync plan]          │
  │─── 0x04 SyncPlan ──────────────────>│
  │                                       │
  │  [Transfer phase: both directions]    │
  │─── 0x05/06/07 File ────────────────>│  (client → server)
  │<── 0x08 BackupConfirm ──────────────│
  │<── 0x05/06/07 File ─────────────────│  (server → client, if bidi)
  │─── 0x08 BackupConfirm ────────────>│
  │                                       │
  │─── 0x09 SyncComplete ──────────────>│
  │<── 0x09 SyncComplete ───────────────│
```

### 4.7 Connection Lifecycle

1. Server starts listening on specified port
2. Client connects via TCP
3. Handshake exchange (version check, sync mode agreement)
4. Both sides scan folders and exchange manifests
5. Client computes sync plan and sends to server
6. File transfer phase (potentially multi-threaded)
7. Both sides send `SyncComplete` with statistics
8. Connection closed gracefully

---

## 5. Sync Engine

### 5.1 Sync Actions

| Action | Meaning |
|--------|---------|
| `SendToServer` | Client file is newer → transfer to server, backup server's copy |
| `SendToClient` | Server file is newer → transfer to client, backup client's copy |
| `ClientOnly` | File exists only on client → copy to server |
| `ServerOnly` | File exists only on server → copy to client (bidi only) |
| `Skip` | Files are identical — no action needed |

### 5.2 Comparison Algorithm

For each file matched by relative path:

```
1. File exists on BOTH sides:
   a. Same size AND same timestamp (±2 sec tolerance) → Skip
   b. Different timestamp → newer timestamp wins
   c. Same timestamp, different size → larger size wins

2. File exists on ONE side only:
   a. Uni-directional mode: only ClientOnly actions (client → server)
   b. Bi-directional mode: ClientOnly or ServerOnly (copy to missing side)
```

**Timestamp tolerance:** ±2 seconds to account for FAT32/NTFS timestamp granularity differences and minor clock drift between machines.

### 5.3 Backup Path Formula

When a file is overwritten, the existing version is moved to a dated backup folder:

```
{BackupFolder}/{yyyyMMdd}/{RelativeDirectory}/{FileName}
```

**Example:**

| Item | Value |
|------|-------|
| Sync folder | `C:\SyncFolder` |
| Backup folder | `C:\SyncFolder` (default) |
| Original file | `C:\SyncFolder\docs\report.docx` |
| Backup destination | `C:\SyncFolder\20260326\docs\report.docx` |

If the backup destination already exists (multiple syncs per day overwriting the same file), append a numeric suffix: `report_1.docx`, `report_2.docx`, etc.

### 5.4 Uni-directional Flow

1. Client scans its folder → sends manifest to server
2. Server scans its folder → sends manifest back
3. Client computes plan: only `SendToServer` and `ClientOnly` actions
4. Client sends files; server backs up outdated copies before overwriting

### 5.5 Bi-directional Flow

1. Both sides scan and exchange manifests
2. Client computes full plan: all action types
3. Client sends files to server (server backs up before overwriting)
4. Server sends files to client (client backs up before overwriting)
5. New files (exist on one side only) are copied without backup

---

## 6. File Transfer & Compression

### 6.1 Compression Strategy

- **Compression method:** GZip via `System.IO.Compression.GZipStream`
- **Decision per file:** Check file extension to determine if already compressed
- **Compressed = skip GZip:** Transfer raw bytes
- **Uncompressed = apply GZip:** Compress during transfer

### 6.2 Already-Compressed File Extensions

| Category | Extensions |
|----------|------------|
| Archives | `.zip`, `.gz`, `.7z`, `.rar`, `.tar.gz`, `.tgz`, `.bz2`, `.xz`, `.zst` |
| Images | `.jpg`, `.jpeg`, `.png`, `.gif`, `.webp`, `.heic`, `.avif` |
| Video | `.mp4`, `.avi`, `.mkv`, `.mov`, `.wmv`, `.webm` |
| Audio | `.mp3`, `.aac`, `.flac`, `.ogg`, `.wma`, `.m4a` |
| Documents | `.pdf`, `.docx`, `.xlsx`, `.pptx` (ZIP-based formats) |

### 6.3 Transfer Flow Per File

**Sender side:**

1. Open source file as read stream
2. Check extension → determine if already compressed
3. If not already compressed: pipe entire file through `GZipStream` → temp compressed file
4. Compute SHA256 of the original (uncompressed) file during the read pass (single pass using `CryptoStream` chained with the read)
5. Send `FileStart` with `FileId`, relative path, original size, `IsCompressed` flag (true = GZip applied), and block size
6. Read the transfer source (compressed temp file or original file) in `BlockSize` chunks
7. Send as `FileChunk` messages with sequential `ChunkIndex`
8. Send `FileEnd` with SHA256 of the original file
9. Clean up temp compressed file if created

**Receiver side:**

1. Receive `FileStart` → create temp file, note `IsCompressed` flag
2. Receive `FileChunk` messages → write to temp file
3. Receive `FileEnd` → finalize:
   a. If `IsCompressed` is true: decompress temp file via `GZipStream` → final file
   b. If `IsCompressed` is false: rename temp file → final file
4. Compute SHA256 of final (decompressed) file and compare with received checksum
5. If checksum matches: set `LastWriteTimeUtc` to original timestamp
6. If checksum fails: delete corrupted file, log error
7. Send `BackupConfirm` to sender

### 6.4 Multi-threaded Transfer

- The sync plan produces a queue of file transfer actions
- A `SemaphoreSlim(maxThreads)` controls concurrency
- Each file transfer is assigned a unique `FileId` (int16, 0–32767)
- Multiple `FileStart`/`FileChunk`/`FileEnd` sequences are interleaved on the same TCP connection
- Receiver demultiplexes chunks by `FileId` into separate temp file streams
- `BackupManager` operations are serialized via a lock to prevent conflicts on the dated backup folder

**Block size validation:** Values below 4 KB are clamped to 4 KB; values above 4 MB are clamped to 4 MB. A warning is logged if the value was adjusted.

---

## 7. Logging & Error Handling

### 7.1 Logging Levels

| Level | `--verbose` off | `--verbose` on | `--log` file |
|-------|-----------------|----------------|--------------|
| Error | Shown | Shown | Written |
| Warning | Shown | Shown | Written |
| Info | Summary only | Shown | Written |
| Debug | Hidden | Shown | Written |

### 7.2 Console Output Examples

**Default (quiet):**

```
[07:30:01] Connecting to 192.168.1.100:15782...
[07:30:01] Connected. Bi-directional sync.
[07:30:05] Sync complete: 12 files transferred, 3 backed up, 45.2 MB total.
```

**Verbose:**

```
[07:30:01] Connecting to 192.168.1.100:15782...
[07:30:01] Connected. Bi-directional sync. Block: 64KB, Threads: 4
[07:30:01] Scanning local folder: C:\SyncFolder (include: *.docx, exclude: *.tmp)
[07:30:02] Local manifest: 156 files, 234.5 MB
[07:30:02] Receiving remote manifest...
[07:30:03] Remote manifest: 148 files, 210.1 MB
[07:30:03] Sync plan: 8 → server, 4 → client, 3 new, 141 skip
[07:30:03] [→] docs\report.docx (2.1 MB, gzip)
[07:30:03] [→] data\export.csv (15.3 MB, gzip)
[07:30:04] [←] images\logo.png (45 KB, raw)
[07:30:04] [backup] docs\report.docx → 20260326\docs\report.docx
[07:30:05] Sync complete: 12 files transferred, 3 backed up, 45.2 MB total.
```

### 7.3 Log File Behavior

- Always writes at full verbose detail regardless of `--verbose` flag
- Appends to existing file (does not overwrite)
- Each line prefixed with `[yyyy-MM-dd HH:mm:ss.fff]` timestamp

### 7.4 Error Handling

| Scenario | Behavior |
|----------|----------|
| Connection refused / timeout | Retry 3 times with 2-second delay, then exit with error |
| File locked on read | Skip file, log warning, continue sync |
| File locked on write | Skip file, log warning, continue sync |
| Checksum mismatch | Delete corrupted temp file, log error, continue |
| Disk full | Abort sync immediately, log error, exit |
| Network drop mid-transfer | Attempt reconnect once, resume from last chunk; if fails, abort |
| Backup folder creation fails | Log error, skip that file's overwrite (protect existing data) |
| Permission denied | Skip file, log warning, continue |

### 7.5 Exit Codes

| Code | Meaning |
|------|---------|
| `0` | Success — all files synced |
| `1` | Partial — sync completed but some files were skipped |
| `2` | Connection failure — could not reach server |
| `3` | Fatal error — disk full, unrecoverable |

---

## 8. Build & Publish

### 8.1 Project File

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>RemoteFileSync</RootNamespace>
    <AssemblyName>RemoteFileSync</AssemblyName>
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>true</SelfContained>
    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  </PropertyGroup>
</Project>
```

### 8.2 Publish Commands

```bash
# x64 (most common Windows machines)
dotnet publish -c Release -r win-x64

# ARM64 (Surface Pro X, Snapdragon laptops)
dotnet publish -c Release -r win-arm64
```

### 8.3 Compatibility

- **Target framework:** `net10.0`
- **Windows 10:** Version 1607 and later
- **Windows 11:** All versions
- **Self-contained:** Bundles the .NET runtime, so no .NET installation required on target machines

---

## 9. Design Decisions Summary

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Network protocol | Raw TCP sockets | Lightest weight, zero dependencies, full control |
| Encryption | None | Designed for trusted LAN/VPN environments |
| Conflict resolution | Newer timestamp wins; tie-break by larger size | Fully deterministic, no human intervention needed |
| File filtering | `--include` / `--exclude` glob patterns | User-controlled, no hardcoded assumptions |
| Logging | Quiet default, `--verbose`, `--log` | Clean for automation, detailed for troubleshooting |
| Compression | GZip, skip already-compressed extensions | Built-in .NET, no dependencies |
| Block size | Configurable `--block-size`, default 64 KB | Tunable for different network conditions |
| Concurrency | Configurable `--max-threads`, default 1 | Safe default, scalable for high-bandwidth links |
| Dependencies | Zero external NuGet packages | Single binary, no supply chain risk |
| Publish | Single-file, self-contained | No installation, no .NET prerequisite |

---

## 10. Future Enhancements (Out of Scope)

These are explicitly **not** part of the current design but noted for potential future work:

- TLS encryption via `SslStream` for untrusted networks
- Delta/differential sync (transfer only changed portions of files)
- File watching mode for continuous real-time sync
- Cross-platform support (Linux, macOS)
- Authentication with shared secret or certificate
- Resume interrupted syncs across sessions
- Web UI for monitoring sync status
