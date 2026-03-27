# ExecRFS — WPF Blazor Hybrid UI Design Specification

**Date:** 2026-03-28
**Version:** 1.0
**Status:** Draft
**Platform:** Windows 10 / Windows 11
**Runtime:** .NET 10, C#, WPF + Blazor Hybrid
**Branch:** feature/execrfs-ui

---

## 1. Overview

ExecRFS is a WPF Blazor Hybrid desktop application that provides a graphical interface for configuring, launching, and monitoring RemoteFileSync operations. It replaces the need to manually construct complex CLI commands.

### 1.1 Goals

- Configure all RemoteFileSync CLI options through a visual interface
- Run both server and client instances simultaneously from one window
- Monitor real-time progress with per-file, per-thread detail
- Control execution: Start, Pause, Resume, Stop
- Save and load named sync profiles for reuse
- Generate CLI command strings for scripting and Task Scheduler use
- View live, color-coded, filterable log output

### 1.2 Non-Goals

- Replacing RemoteFileSync.exe (ExecRFS launches it as a child process)
- Cross-platform support (WPF is Windows-only, matching RemoteFileSync)
- Remote management (ExecRFS runs on the same machine as the sync process)
- Embedding the sync engine in-process (separate processes for crash isolation)

---

## 2. Architecture

### 2.1 Approach

Single WPF Blazor Hybrid project. WPF provides the window shell via `BlazorWebView`, all UI is built as Razor components. Communication with RemoteFileSync.exe is via child process stdout (JSON events) and stdin (control commands).

### 2.2 Project Structure

```
src/ExecRFS/
├── ExecRFS.csproj                 # WPF + Microsoft.AspNetCore.Components.WebView.Wpf
├── App.xaml / App.xaml.cs         # WPF Application entry
├── MainWindow.xaml / .cs          # WPF shell: BlazorWebView host
├── wwwroot/
│   ├── index.html                 # Blazor host page
│   └── css/
│       └── app.css                # Global styles (dark theme)
├── Components/
│   ├── Layout/
│   │   └── MainLayout.razor       # Top-level: toolbar + split panels + log
│   ├── Panels/
│   │   ├── ServerPanel.razor      # Server config + controls
│   │   └── ClientPanel.razor      # Client config + controls
│   ├── Shared/
│   │   ├── FolderPicker.razor     # Folder browse button + path input
│   │   ├── PatternList.razor      # Include/Exclude pattern editor (add/remove tags)
│   │   ├── ProgressBar.razor      # Per-instance progress display
│   │   └── LogViewer.razor        # Live scrolling log with color-coded levels
│   └── Dialogs/
│       ├── ProfileManager.razor   # Save/Load/Delete profiles
│       └── CommandPreview.razor   # Generated CLI command preview + copy
├── Models/
│   ├── SyncProfile.cs             # Serializable profile (all options for both roles)
│   ├── SyncInstanceState.cs       # Runtime state enum (Idle/Starting/Running/Paused/Stopping/Stopped/Error)
│   └── ProgressEvent.cs           # Deserialized JSON progress event from stdout
├── Services/
│   ├── ProcessManager.cs          # Launch/monitor/control RemoteFileSync.exe
│   ├── ProfileService.cs          # Load/Save/List profiles from disk
│   ├── CommandBuilder.cs          # Build CLI arg string from SyncProfile
│   └── LogAggregator.cs           # Merge server+client logs with timestamps
└── _Imports.razor                 # Shared using statements
```

### 2.3 Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.AspNetCore.Components.WebView.Wpf` | Blazor WebView for WPF |
| (none others) | All other functionality uses .NET BCL |

### 2.4 Component Diagram

```
┌─────────────────────────────────────────────────────┐
│                    ExecRFS (WPF Blazor)              │
│                                                      │
│  SyncProfile ──→ CommandBuilder ──→ CLI args string  │
│       │                                    │         │
│       │         ProcessManager (Server)    │         │
│       ├────────→ stdin: PAUSE/RESUME/STOP  │         │
│       │         stdout: JSON events ───────┤         │
│       │              │                     │         │
│       │         ProcessManager (Client)    │         │
│       ├────────→ stdin: PAUSE/RESUME/STOP  │         │
│       │         stdout: JSON events ───────┤         │
│       │              │                     │         │
│       │         LogAggregator              │         │
│       │         ← merges [SRV]+[CLI] logs  │         │
│       │              │                     │         │
│       ▼              ▼                     │         │
│  Blazor Components (reactive UI update)    │         │
│  ├── ServerPanel.razor                     │         │
│  ├── ClientPanel.razor                     │         │
│  ├── ProgressBar.razor                     │         │
│  └── LogViewer.razor                       │         │
└─────────────────────────────────────────────────────┘
         │                          │
         ▼                          ▼
   RemoteFileSync.exe         RemoteFileSync.exe
      (server mode)              (client mode)
```

---

## 3. UI Layout

### 3.1 Window Structure

Side-by-side split layout with four vertical zones:

```
┌─────────────────────────────────────────────────────┐
│ [Toolbar] Profile dropdown | Save | SaveAs | GenCMD  │
├────────────────────┬────────────────────────────────┤
│    SERVER PANEL     │        CLIENT PANEL            │
│                     │                                │
│  Folder [Browse]    │  Host: ___________             │
│  Port: ____         │  Folder [Browse]               │
│  Backup: [Browse]   │  Port: ____                    │
│  Block Size: ▾      │  Backup: [Browse]              │
│  Threads: ▾         │  [✓] Bidirectional             │
│  Include: [tags]    │  [✓] Delete Propagation        │
│  Exclude: [tags]    │  Block Size: ▾  Threads: ▾     │
│                     │  Include: [tags]               │
│  [● Running]        │  Exclude: [tags]               │
│  [Start] [Stop]     │                                │
│                     │  [● Syncing 5/12]              │
│                     │  [Pause] [Stop]                │
│                     │  ┌─ Thread Progress ─────────┐ │
│                     │  │ [T1] report.docx    67%   │ │
│                     │  │ [T2] export.csv     23%   │ │
│                     │  └───────────────────────────┘ │
├────────────────────┴────────────────────────────────┤
│ [Progress] 5/12 files | 23.4 MB / 89.7 MB | 01:23   │
│ ████████████░░░░░░░░░░░░░░░░░░░░░░░ 42%              │
├─────────────────────────────────────────────────────┤
│ [Live Log]  Auto-scroll | Clear | Filter ▾           │
│ [SRV] Listening on port 15782...                     │
│ [CLI] Connected. Bi-directional sync + delete.       │
│ [CLI] [→] docs/report.docx (2.1 MB, gzip)           │
│ [CLI] [DEL→] docs/old.docx (deleted on server)      │
│ [SRV] [←] docs/report.docx received OK              │
└─────────────────────────────────────────────────────┘
```

### 3.2 Panel Details

**Server Panel (left):**
- Folder path + Browse button (opens native folder dialog via WPF interop)
- Port number input
- Backup folder path (optional, placeholder shows "same as sync folder")
- Block size dropdown: 4 KB, 64 KB, 256 KB, 4 MB
- Max threads dropdown: 1, 2, 4, 8
- Include patterns: green tag chips with `+ Add` button and `×` remove
- Exclude patterns: red tag chips with `+ Add` button and `×` remove
- Status indicator (colored dot + text)
- Start / Stop buttons (context-sensitive: Start when idle, Stop when running)

**Client Panel (right):**
- All server options PLUS:
- Host input field (IP or hostname)
- Bidirectional checkbox
- Delete Propagation checkbox
- Per-thread transfer progress section (visible during sync)

**Progress Bar (shared):**
- File count: `N of M files`
- Byte count: `X MB / Y MB`
- Elapsed time
- Visual progress bar

**Log Viewer (shared):**
- Color-coded prefixes: `[SRV]` in blue, `[CLI]` in orange
- Color-coded actions: `[→]` send, `[←]` receive, `[DEL→]` delete in red
- Auto-scroll toggle (on by default)
- Clear button
- Filter dropdown: All, Server only, Client only, Errors only
- Circular buffer: last 5,000 lines

### 3.3 Theme

Dark theme matching the GitHub dark palette:
- Background: `#0d1117`
- Surface: `#161b22`
- Border: `#30363d`
- Text: `#c9d1d9`
- Muted: `#8b949e`
- Blue (server): `#4a9eff`
- Orange (client): `#e88a3a`
- Green (success): `#3fb950`
- Red (error/delete): `#f85149`
- Yellow (warning/syncing): `#d29922`

---

## 4. JSON Progress Protocol

### 4.1 New CLI Flag

Add `--json-progress` flag to RemoteFileSync.exe. When active:
- Structured JSON events written to stdout (one JSON object per line)
- Normal log output continues to `--log` file only (not stdout)
- Verbose mode is implied (all events emitted)

### 4.2 Event Types

| Event | Fields | When |
|-------|--------|------|
| `status` | `state`, `host?`, `port?`, `mode?` | State changes (connecting, connected, paused, resumed, stopping) |
| `manifest` | `side` (local/remote), `files`, `bytes` | After scanning |
| `plan` | `transfers`, `deletes`, `skipped` | After plan computed |
| `file_start` | `action` (send/receive), `path`, `size`, `compressed`, `thread` | File transfer begins |
| `file_progress` | `path`, `bytes_sent`, `total_bytes`, `thread` | Per-block progress update |
| `file_end` | `path`, `success`, `error?`, `thread` | File transfer completes |
| `delete` | `path`, `backed_up`, `success`, `error?` | File deletion executed |
| `complete` | `files_transferred`, `files_deleted`, `bytes`, `elapsed_ms`, `exit_code` | Sync finished |
| `error` | `message`, `fatal` | Errors (fatal = process will exit) |

### 4.3 JSON Examples

```jsonl
{"event":"status","state":"connecting","host":"10.0.1.50","port":15782}
{"event":"status","state":"connected","mode":"bidi+delete"}
{"event":"manifest","side":"local","files":156,"bytes":234500000}
{"event":"manifest","side":"remote","files":148,"bytes":210100000}
{"event":"plan","transfers":10,"deletes":2,"skipped":141}
{"event":"file_start","action":"send","path":"docs/report.docx","size":2100000,"compressed":true,"thread":1}
{"event":"file_progress","path":"docs/report.docx","bytes_sent":1400000,"total_bytes":2100000,"thread":1}
{"event":"file_end","path":"docs/report.docx","success":true,"thread":1}
{"event":"delete","path":"docs/old.docx","backed_up":true,"success":true}
{"event":"complete","files_transferred":10,"files_deleted":2,"bytes":89700000,"elapsed_ms":5200,"exit_code":0}
```

### 4.4 Stdin Control Commands

The process reads stdin on a background thread. Commands are one per line:

| Command | Behavior |
|---------|----------|
| `PAUSE` | Finish current file transfer, then wait. Emit `{"event":"status","state":"paused"}` |
| `RESUME` | Continue from paused state. Emit `{"event":"status","state":"resumed"}` |
| `STOP` | Graceful shutdown: finish current file, skip remaining, exit with code 1. Emit `{"event":"status","state":"stopping"}` |

### 4.5 Implementation in RemoteFileSync.exe

New files:
- `src/RemoteFileSync/Progress/JsonProgressWriter.cs` — writes JSON events to stdout
- `src/RemoteFileSync/Progress/StdinCommandReader.cs` — reads control commands from stdin

Modified files:
- `src/RemoteFileSync/Models/SyncOptions.cs` — add `bool JsonProgress { get; set; }`
- `src/RemoteFileSync/Program.cs` — parse `--json-progress` flag
- `src/RemoteFileSync/Network/SyncClient.cs` — integrate progress writer + pause support
- `src/RemoteFileSync/Network/SyncServer.cs` — integrate progress writer + pause support

---

## 5. Process Management

### 5.1 ProcessManager Class

One `ProcessManager` instance per role (server/client). Manages the full lifecycle.

**State machine:**

```
Idle ──→ Starting ──→ Running ──→ Stopping ──→ Stopped
                         │                        ↑
                         ├──→ Paused ──→ Running   │
                         │                         │
                         └──→ Error ───────────────┘
```

**Public API:**

```csharp
public class ProcessManager : IDisposable
{
    public SyncInstanceState State { get; }
    public event Action<ProgressEvent>? OnProgress;
    public event Action<string>? OnLogLine;
    public event Action<int>? OnExited;

    public void Start(SyncProfile profile, bool isServer);
    public void Pause();
    public void Resume();
    public void Stop();
}
```

**Implementation details:**
- `Process.StartInfo`: `RedirectStandardOutput`, `RedirectStandardInput`, `RedirectStandardError`, `CreateNoWindow = true`
- `OutputDataReceived` event for async stdout capture
- Each line: try JSON parse → `ProgressEvent`; fallback → raw log line
- Stdin writes for PAUSE/RESUME/STOP
- Process exit detected via `Process.Exited` event + `Process.ExitCode`
- Events raised on `SynchronizationContext` for thread-safe Blazor updates

### 5.2 RemoteFileSync.exe Path Resolution

Search order:
1. Same directory as ExecRFS.exe
2. System `PATH`
3. User-configured path (stored in `%LOCALAPPDATA%\RemoteFileSync\settings.json`)

If not found, show an error dialog on startup with a file picker.

---

## 6. Profile Management

### 6.1 Storage

```
%LOCALAPPDATA%\RemoteFileSync\profiles\
  ├── work-source-code.json
  ├── documents-backup.json
  └── _last-session.json          # auto-saved on window close
```

### 6.2 SyncProfile Model

```csharp
public class SyncProfile
{
    public string Name { get; set; } = "Untitled";

    // Server settings
    public string ServerFolder { get; set; } = "";
    public int ServerPort { get; set; } = 15782;
    public string? ServerBackupFolder { get; set; }
    public int ServerBlockSize { get; set; } = 65536;
    public int ServerMaxThreads { get; set; } = 1;

    // Client settings
    public string ClientHost { get; set; } = "";
    public string ClientFolder { get; set; } = "";
    public int ClientPort { get; set; } = 15782;
    public string? ClientBackupFolder { get; set; }
    public bool Bidirectional { get; set; }
    public bool DeleteEnabled { get; set; }
    public int ClientBlockSize { get; set; } = 65536;
    public int ClientMaxThreads { get; set; } = 1;

    // Shared filter settings
    public List<string> IncludePatterns { get; set; } = new();
    public List<string> ExcludePatterns { get; set; } = new();

    // Log settings
    public string? ServerLogFile { get; set; }
    public string? ClientLogFile { get; set; }
}
```

### 6.3 ProfileService API

```csharp
public class ProfileService
{
    public List<string> ListProfiles();
    public SyncProfile Load(string name);
    public void Save(SyncProfile profile);
    public void Delete(string name);
    public SyncProfile CreateDefault();
}
```

Filename derivation: `name.ToLowerInvariant().Replace(' ', '-')` + `.json`, invalid path chars stripped.

### 6.4 Auto-Save Behavior

- On window close: save current state as `_last-session.json`
- On startup: load `_last-session.json` if it exists (restores where user left off)
- Explicit Save/Save As: writes to named profile file

---

## 7. Command Builder

### 7.1 CommandBuilder API

```csharp
public static class CommandBuilder
{
    public static string Build(SyncProfile profile, bool isServer);
    public static string BuildBoth(SyncProfile profile);  // returns both commands
}
```

### 7.2 Output Format

```bash
# Server command:
RemoteFileSync.exe server --folder "D:\Production\AppData" --port 15782 --backup-folder "D:\Backups" --block-size 262144 --max-threads 4 --verbose --log "D:\Logs\sync-server.log"

# Client command:
RemoteFileSync.exe client --host 10.0.1.50 --port 15782 --folder "C:\Production\AppData" --bidirectional --delete --backup-folder "C:\Backups" --block-size 262144 --max-threads 4 --verbose --log "C:\Logs\sync-client.log"
```

### 7.3 Generate CMD Dialog

- Shows both server and client commands in a preview panel
- "Copy Server" button → copies server command to clipboard
- "Copy Client" button → copies client command to clipboard
- "Copy Both" button → copies both with a separator
- "Save as .bat" button → writes to a `.bat` file via save dialog

---

## 8. Log Aggregator

### 8.1 LogAggregator Class

```csharp
public class LogAggregator
{
    public event Action<LogEntry>? OnEntry;
    public IReadOnlyList<LogEntry> Entries { get; }

    public void AddServerLine(string line);
    public void AddClientLine(string line);
    public void Clear();
}

public record LogEntry(DateTime Timestamp, string Source, string Level, string Message);
```

### 8.2 Behavior

- Receives raw log lines from both `ProcessManager` instances
- Prepends `[SRV]` or `[CLI]` source prefix
- Parses log level from JSON events or line format (`[ERR]`, `[WRN]`, `[INF]`, `[DBG]`)
- Maintains circular buffer of 5,000 entries (configurable)
- Fires `OnEntry` for each new line → LogViewer re-renders

### 8.3 LogViewer Features

- Auto-scroll to bottom (toggle)
- Clear all entries
- Filter dropdown: All / Server / Client / Errors
- Text search (Ctrl+F style input)
- Color coding: errors in red, warnings in yellow, file actions in green/blue

---

## 9. WPF Shell Integration

### 9.1 Native Features via WPF Interop

Since Blazor runs in a WebView, native Windows features require WPF interop:

| Feature | Approach |
|---------|----------|
| Folder picker dialog | `FolderBrowserDialog` invoked from WPF, result passed to Blazor via JS interop or injected service |
| Clipboard access | `Clipboard.SetText()` via injected `IClipboardService` |
| Window title | `MainWindow.Title` updated via service binding |
| Window close handling | `Window.Closing` event → auto-save profile |
| Taskbar progress | `TaskbarItemInfo.ProgressValue` bound to sync progress |

### 9.2 MainWindow.xaml

```xml
<Window x:Class="ExecRFS.MainWindow"
        Title="ExecRFS — RemoteFileSync Manager"
        Width="1200" Height="800" MinWidth="900" MinHeight="600"
        WindowStartupLocation="CenterScreen">
    <blazor:BlazorWebView HostPage="wwwroot/index.html" Services="{DynamicResource services}">
        <blazor:BlazorWebView.RootComponents>
            <blazor:RootComponent Selector="#app" ComponentType="{x:Type local:Components.Layout.MainLayout}" />
        </blazor:BlazorWebView.RootComponents>
    </blazor:BlazorWebView>
</Window>
```

---

## 10. Changes to RemoteFileSync.exe

### 10.1 Summary of Modifications

| File | Change |
|------|--------|
| `Models/SyncOptions.cs` | Add `bool JsonProgress` property |
| `Program.cs` | Parse `--json-progress` flag, wire up `JsonProgressWriter` and `StdinCommandReader` |
| `Network/SyncClient.cs` | Emit JSON events at key points, check pause flag between file transfers |
| `Network/SyncServer.cs` | Emit JSON events at key points, check pause flag between file transfers |

### 10.2 New Files

| File | Purpose |
|------|---------|
| `Progress/JsonProgressWriter.cs` | Writes JSON events to stdout using `System.Text.Json` |
| `Progress/StdinCommandReader.cs` | Background thread reading stdin lines, sets `ManualResetEventSlim` for pause/resume |

### 10.3 Pause Mechanism

```csharp
public class StdinCommandReader : IDisposable
{
    public ManualResetEventSlim PauseGate { get; } = new(initialState: true);
    public CancellationTokenSource StopToken { get; } = new();

    // Background thread reads stdin, sets PauseGate/StopToken accordingly
}
```

Integration in `SyncClient`/`SyncServer`: between file transfers, call `PauseGate.Wait()`. If paused, this blocks until `RESUME` is received. If `STOP` is received, the `StopToken` cancels the sync loop.

---

## 11. Affected Files Summary

### 11.1 New Project: ExecRFS

| File | Purpose |
|------|---------|
| `src/ExecRFS/ExecRFS.csproj` | WPF Blazor Hybrid project file |
| `src/ExecRFS/App.xaml` + `.cs` | WPF application entry |
| `src/ExecRFS/MainWindow.xaml` + `.cs` | WPF shell with BlazorWebView |
| `src/ExecRFS/wwwroot/index.html` | Blazor host page |
| `src/ExecRFS/wwwroot/css/app.css` | Dark theme styles |
| `src/ExecRFS/Components/Layout/MainLayout.razor` | Top-level layout |
| `src/ExecRFS/Components/Panels/ServerPanel.razor` | Server config + controls |
| `src/ExecRFS/Components/Panels/ClientPanel.razor` | Client config + controls |
| `src/ExecRFS/Components/Shared/FolderPicker.razor` | Native folder dialog wrapper |
| `src/ExecRFS/Components/Shared/PatternList.razor` | Tag-based pattern editor |
| `src/ExecRFS/Components/Shared/ProgressBar.razor` | Overall + per-thread progress |
| `src/ExecRFS/Components/Shared/LogViewer.razor` | Live log viewer |
| `src/ExecRFS/Components/Dialogs/ProfileManager.razor` | Profile save/load UI |
| `src/ExecRFS/Components/Dialogs/CommandPreview.razor` | Generated command preview |
| `src/ExecRFS/Models/SyncProfile.cs` | Profile data model |
| `src/ExecRFS/Models/SyncInstanceState.cs` | Runtime state enum |
| `src/ExecRFS/Models/ProgressEvent.cs` | JSON event deserialization |
| `src/ExecRFS/Services/ProcessManager.cs` | Process lifecycle management |
| `src/ExecRFS/Services/ProfileService.cs` | Profile CRUD |
| `src/ExecRFS/Services/CommandBuilder.cs` | CLI command generation |
| `src/ExecRFS/Services/LogAggregator.cs` | Log merging + buffering |
| `src/ExecRFS/_Imports.razor` | Shared usings |

### 11.2 Modified: RemoteFileSync.exe

| File | Change |
|------|--------|
| `src/RemoteFileSync/Models/SyncOptions.cs` | Add `bool JsonProgress` property |
| `src/RemoteFileSync/Program.cs` | Parse `--json-progress`, wire up writer/reader |
| `src/RemoteFileSync/Network/SyncClient.cs` | Emit JSON events, pause gate integration |
| `src/RemoteFileSync/Network/SyncServer.cs` | Emit JSON events, pause gate integration |
| `src/RemoteFileSync/Progress/JsonProgressWriter.cs` | NEW: JSON event serializer |
| `src/RemoteFileSync/Progress/StdinCommandReader.cs` | NEW: Stdin command handler |

### 11.3 Solution File

Add `src/ExecRFS/ExecRFS.csproj` to `RemoteFileSync.slnx`.

---

## 12. Testing Strategy

### 12.1 Unit Tests

| Test | Verifies |
|------|----------|
| `CommandBuilder_ServerMode_GeneratesCorrectArgs` | Server command string |
| `CommandBuilder_ClientMode_AllOptions_GeneratesCorrectArgs` | Client command with all flags |
| `CommandBuilder_DefaultValues_Omitted` | Default values not included in command |
| `ProfileService_SaveAndLoad_RoundTrips` | JSON serialization integrity |
| `ProfileService_ListProfiles_ReturnsNames` | Directory enumeration |
| `ProfileService_Delete_RemovesFile` | File deletion |
| `ProgressEvent_Deserialize_AllEventTypes` | JSON parsing for each event type |
| `LogAggregator_MergesSourcesChronologically` | Log ordering |
| `LogAggregator_CircularBuffer_Bounds` | Memory bounded at 5000 entries |
| `JsonProgressWriter_AllEvents_ValidJson` | Output is valid JSON lines |
| `StdinCommandReader_Pause_Resume_Stop` | Command parsing and gate control |

### 12.2 Integration Tests

| Test | Verifies |
|------|----------|
| `ProcessManager_LaunchAndCapture_ReceivesEvents` | Full process lifecycle |
| `ProcessManager_PauseResume_ControlsExecution` | Stdin commands work |
| `ProcessManager_Stop_GracefulShutdown` | Process exits cleanly |
| `JsonProgress_FullSync_EmitsAllEvents` | End-to-end event stream |

---

## 13. Build & Publish

### 13.1 Project File

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <RootNamespace>ExecRFS</RootNamespace>
    <AssemblyName>ExecRFS</AssemblyName>
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>true</SelfContained>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Components.WebView.Wpf" Version="10.0.*" />
  </ItemGroup>
</Project>
```

### 13.2 Publish

```bash
dotnet publish src/ExecRFS -c Release -r win-x64
```

Output: single `ExecRFS.exe`. Place alongside `RemoteFileSync.exe` for automatic discovery.

---

## 14. Design Decisions Summary

| Decision | Choice | Rationale |
|----------|--------|-----------|
| UI framework | WPF Blazor Hybrid | Modern Razor components with native WPF shell features (folder dialogs, taskbar) |
| Layout | Side-by-side split | See both server + client simultaneously; ideal for localhost sync |
| Process communication | JSON stdout + stdin commands | Rich progress data, crash isolation, minimal changes to existing CLI |
| Profiles | Named JSON files in %LOCALAPPDATA% | Persistent, shareable, human-readable |
| Pause mechanism | Stdin PAUSE/RESUME/STOP | Safe (completes current file), clean protocol |
| Theme | Dark (GitHub palette) | Developer-focused tool, easy on eyes |
| Project structure | Single WPF Blazor project | Simple, fast to build, one consumer |
| Command generation | Preview dialog + clipboard | Bridges GUI and CLI workflows (Task Scheduler, scripts) |

---

## 15. Future Enhancements (Out of Scope)

- Drag-and-drop folder paths
- System tray minimize with background sync
- Scheduled sync (built-in cron within ExecRFS)
- Multi-session tabs (run multiple independent sync pairs)
- Remote ExecRFS management (control sync on another machine)
- Notification system (Windows toast on sync complete/error)
