# ExecRFS WPF Blazor Hybrid UI — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a WPF Blazor Hybrid desktop app (ExecRFS) for configuring, launching, and monitoring RemoteFileSync, plus add JSON progress output and stdin control commands to RemoteFileSync.exe.

**Architecture:** Two phases. Phase A adds `--json-progress` and stdin control (PAUSE/RESUME/STOP) to RemoteFileSync.exe. Phase B creates the ExecRFS WPF Blazor app with side-by-side server/client panels, process management, named profiles, live log viewer, and command generation.

**Tech Stack:** .NET 10, C# 13, WPF + `Microsoft.AspNetCore.Components.WebView.Wpf`, Blazor Razor components, xUnit for testing.

**Design Spec:** `Plans/2026-03-28-execrfs-wpf-blazor-design.md`

---

## File Structure

### Phase A — RemoteFileSync.exe Changes

```
src/RemoteFileSync/
├── Models/SyncOptions.cs              # MODIFY: add JsonProgress property
├── Program.cs                         # MODIFY: parse --json-progress, wire writer/reader
├── Progress/
│   ├── JsonProgressWriter.cs          # CREATE: JSON event serializer to stdout
│   └── StdinCommandReader.cs          # CREATE: stdin command parser with pause gate
├── Network/SyncClient.cs              # MODIFY: emit events + pause gate
└── Network/SyncServer.cs              # MODIFY: emit events + pause gate
```

### Phase B — ExecRFS New Project

```
src/ExecRFS/
├── ExecRFS.csproj
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / MainWindow.xaml.cs
├── wwwroot/
│   ├── index.html
│   └── css/app.css
├── Components/
│   ├── Layout/MainLayout.razor
│   ├── Panels/ServerPanel.razor
│   ├── Panels/ClientPanel.razor
│   ├── Shared/FolderPicker.razor
│   ├── Shared/PatternList.razor
│   ├── Shared/ProgressBar.razor
│   └── Shared/LogViewer.razor
├── Models/
│   ├── SyncProfile.cs
│   ├── SyncInstanceState.cs
│   └── ProgressEvent.cs
├── Services/
│   ├── SyncProcesses.cs               # Holder for server+client ProcessManager (DI-friendly)
│   ├── ProcessManager.cs
│   ├── ProfileService.cs
│   ├── CommandBuilder.cs
│   └── LogAggregator.cs
└── _Imports.razor

tests/ExecRFS.Tests/
├── ExecRFS.Tests.csproj
├── Services/
│   ├── CommandBuilderTests.cs
│   ├── ProfileServiceTests.cs
│   └── LogAggregatorTests.cs
└── Models/
    └── ProgressEventTests.cs
```

---

# Phase A — RemoteFileSync.exe: JSON Progress + Stdin Control

---

## Task 1: Models — Add JsonProgress Property

**Files:**
- Modify: `src/RemoteFileSync/Models/SyncOptions.cs`

- [ ] **Step 1: Add JsonProgress property to SyncOptions**

In `src/RemoteFileSync/Models/SyncOptions.cs`, add after `DeleteEnabled`:

```csharp
public bool JsonProgress { get; set; }
```

- [ ] **Step 2: Verify build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/RemoteFileSync/Models/SyncOptions.cs
git commit -m "feat: add JsonProgress option to SyncOptions"
```

---

## Task 2: Progress — JsonProgressWriter

**Files:**
- Create: `src/RemoteFileSync/Progress/JsonProgressWriter.cs`
- Create: `tests/RemoteFileSync.Tests/Progress/JsonProgressWriterTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/RemoteFileSync.Tests/Progress/JsonProgressWriterTests.cs`:

```csharp
using System.Text.Json;
using RemoteFileSync.Progress;

namespace RemoteFileSync.Tests.Progress;

public class JsonProgressWriterTests
{
    [Fact]
    public void WriteStatus_EmitsValidJson()
    {
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);

        writer.WriteStatus("connecting", host: "10.0.1.50", port: 15782);

        var json = sw.ToString().Trim();
        var doc = JsonDocument.Parse(json);
        Assert.Equal("status", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("connecting", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal("10.0.1.50", doc.RootElement.GetProperty("host").GetString());
        Assert.Equal(15782, doc.RootElement.GetProperty("port").GetInt32());
    }

    [Fact]
    public void WriteManifest_EmitsValidJson()
    {
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);

        writer.WriteManifest("local", 156, 234500000);

        var json = sw.ToString().Trim();
        var doc = JsonDocument.Parse(json);
        Assert.Equal("manifest", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("local", doc.RootElement.GetProperty("side").GetString());
        Assert.Equal(156, doc.RootElement.GetProperty("files").GetInt32());
    }

    [Fact]
    public void WritePlan_EmitsValidJson()
    {
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);

        writer.WritePlan(10, 2, 141);

        var json = sw.ToString().Trim();
        var doc = JsonDocument.Parse(json);
        Assert.Equal("plan", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal(10, doc.RootElement.GetProperty("transfers").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("deletes").GetInt32());
        Assert.Equal(141, doc.RootElement.GetProperty("skipped").GetInt32());
    }

    [Fact]
    public void WriteFileStart_EmitsValidJson()
    {
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);

        writer.WriteFileStart("send", "docs/report.docx", 2100000, true, 1);

        var json = sw.ToString().Trim();
        var doc = JsonDocument.Parse(json);
        Assert.Equal("file_start", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("send", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal("docs/report.docx", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal(2100000, doc.RootElement.GetProperty("size").GetInt64());
        Assert.True(doc.RootElement.GetProperty("compressed").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("thread").GetInt32());
    }

    [Fact]
    public void WriteFileProgress_EmitsValidJson()
    {
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);

        writer.WriteFileProgress("docs/report.docx", 1400000, 2100000, 1);

        var json = sw.ToString().Trim();
        var doc = JsonDocument.Parse(json);
        Assert.Equal("file_progress", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal(1400000, doc.RootElement.GetProperty("bytes_sent").GetInt64());
        Assert.Equal(2100000, doc.RootElement.GetProperty("total_bytes").GetInt64());
    }

    [Fact]
    public void WriteComplete_EmitsValidJson()
    {
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);

        writer.WriteComplete(10, 2, 89700000, 5200, 0);

        var json = sw.ToString().Trim();
        var doc = JsonDocument.Parse(json);
        Assert.Equal("complete", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal(10, doc.RootElement.GetProperty("files_transferred").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("files_deleted").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public void WriteError_EmitsValidJson()
    {
        using var sw = new StringWriter();
        var writer = new JsonProgressWriter(sw);

        writer.WriteError("Connection refused", fatal: true);

        var json = sw.ToString().Trim();
        var doc = JsonDocument.Parse(json);
        Assert.Equal("error", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("Connection refused", doc.RootElement.GetProperty("message").GetString());
        Assert.True(doc.RootElement.GetProperty("fatal").GetBoolean());
    }

    [Fact]
    public void NullWriter_NoOutput()
    {
        var writer = JsonProgressWriter.Null;
        // Should not throw
        writer.WriteStatus("connecting");
        writer.WriteComplete(0, 0, 0, 0, 0);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "JsonProgressWriterTests"`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement JsonProgressWriter**

Create `src/RemoteFileSync/Progress/JsonProgressWriter.cs`:

```csharp
using System.Text.Json;

namespace RemoteFileSync.Progress;

public sealed class JsonProgressWriter
{
    private readonly TextWriter? _writer;
    private readonly object _lock = new();

    public static readonly JsonProgressWriter Null = new(null);

    public JsonProgressWriter(TextWriter? writer)
    {
        _writer = writer;
    }

    public void WriteStatus(string state, string? host = null, int? port = null, string? mode = null)
    {
        var obj = new Dictionary<string, object> { ["event"] = "status", ["state"] = state };
        if (host != null) obj["host"] = host;
        if (port != null) obj["port"] = port;
        if (mode != null) obj["mode"] = mode;
        WriteLine(obj);
    }

    public void WriteManifest(string side, int files, long bytes)
    {
        WriteLine(new { @event = "manifest", side, files, bytes });
    }

    public void WritePlan(int transfers, int deletes, int skipped)
    {
        WriteLine(new { @event = "plan", transfers, deletes, skipped });
    }

    public void WriteFileStart(string action, string path, long size, bool compressed, int thread)
    {
        WriteLine(new { @event = "file_start", action, path, size, compressed, thread });
    }

    public void WriteFileProgress(string path, long bytes_sent, long total_bytes, int thread)
    {
        WriteLine(new { @event = "file_progress", path, bytes_sent, total_bytes, thread });
    }

    public void WriteFileEnd(string path, bool success, string? error = null, int thread = 0)
    {
        var obj = new Dictionary<string, object> { ["event"] = "file_end", ["path"] = path, ["success"] = success, ["thread"] = thread };
        if (error != null) obj["error"] = error;
        WriteLine(obj);
    }

    public void WriteDelete(string path, bool backed_up, bool success, string? error = null)
    {
        var obj = new Dictionary<string, object> { ["event"] = "delete", ["path"] = path, ["backed_up"] = backed_up, ["success"] = success };
        if (error != null) obj["error"] = error;
        WriteLine(obj);
    }

    public void WriteComplete(int files_transferred, int files_deleted, long bytes, long elapsed_ms, int exit_code)
    {
        WriteLine(new { @event = "complete", files_transferred, files_deleted, bytes, elapsed_ms, exit_code });
    }

    public void WriteError(string message, bool fatal)
    {
        WriteLine(new { @event = "error", message, fatal });
    }

    private void WriteLine(object obj)
    {
        if (_writer == null) return;
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        lock (_lock)
        {
            _writer.WriteLine(json);
            _writer.Flush();
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "JsonProgressWriterTests"`
Expected: All 8 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteFileSync/Progress/JsonProgressWriter.cs tests/RemoteFileSync.Tests/Progress/JsonProgressWriterTests.cs
git commit -m "feat: add JsonProgressWriter for structured JSON event output"
```

---

## Task 3: Progress — StdinCommandReader

**Files:**
- Create: `src/RemoteFileSync/Progress/StdinCommandReader.cs`
- Create: `tests/RemoteFileSync.Tests/Progress/StdinCommandReaderTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/RemoteFileSync.Tests/Progress/StdinCommandReaderTests.cs`:

```csharp
using RemoteFileSync.Progress;

namespace RemoteFileSync.Tests.Progress;

public class StdinCommandReaderTests
{
    [Fact]
    public void PauseGate_InitiallyOpen()
    {
        using var reader = new StdinCommandReader(new StringReader(""));
        Assert.True(reader.PauseGate.IsSet);
    }

    [Fact]
    public void PauseCommand_ClosesGate()
    {
        var input = new StringReader("PAUSE\n");
        using var sw = new StringWriter();
        using var reader = new StdinCommandReader(input, sw);
        reader.Start();
        Thread.Sleep(200); // Allow background thread to process

        Assert.False(reader.PauseGate.IsSet);
    }

    [Fact]
    public void ResumeCommand_OpensGate()
    {
        var input = new StringReader("PAUSE\nRESUME\n");
        using var sw = new StringWriter();
        using var reader = new StdinCommandReader(input, sw);
        reader.Start();
        Thread.Sleep(200);

        Assert.True(reader.PauseGate.IsSet);
    }

    [Fact]
    public void StopCommand_CancelsToken()
    {
        var input = new StringReader("STOP\n");
        using var sw = new StringWriter();
        using var reader = new StdinCommandReader(input, sw);
        reader.Start();
        Thread.Sleep(200);

        Assert.True(reader.StopToken.IsCancellationRequested);
    }

    [Fact]
    public void PauseCommand_EmitsJsonStatus()
    {
        var input = new StringReader("PAUSE\n");
        using var sw = new StringWriter();
        using var reader = new StdinCommandReader(input, sw);
        reader.Start();
        Thread.Sleep(200);

        var output = sw.ToString();
        Assert.Contains("\"state\":\"paused\"", output);
    }

    [Fact]
    public void NullReader_NoOp()
    {
        using var reader = StdinCommandReader.Null;
        reader.Start(); // Should not throw
        Assert.True(reader.PauseGate.IsSet);
        Assert.False(reader.StopToken.IsCancellationRequested);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "StdinCommandReaderTests"`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement StdinCommandReader**

Create `src/RemoteFileSync/Progress/StdinCommandReader.cs`:

```csharp
using System.Text.Json;

namespace RemoteFileSync.Progress;

public sealed class StdinCommandReader : IDisposable
{
    private readonly TextReader? _input;
    private readonly TextWriter? _output;
    private Thread? _thread;

    public ManualResetEventSlim PauseGate { get; } = new(initialState: true);
    public CancellationTokenSource StopToken { get; } = new();

    public static readonly StdinCommandReader Null = new(null, null);

    public StdinCommandReader(TextReader? input, TextWriter? output = null)
    {
        _input = input;
        _output = output;
    }

    public void Start()
    {
        if (_input == null) return;
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "StdinCommandReader" };
        _thread.Start();
    }

    private void ReadLoop()
    {
        try
        {
            while (_input!.ReadLine() is { } line)
            {
                switch (line.Trim().ToUpperInvariant())
                {
                    case "PAUSE":
                        PauseGate.Reset();
                        WriteStatus("paused");
                        break;
                    case "RESUME":
                        PauseGate.Set();
                        WriteStatus("resumed");
                        break;
                    case "STOP":
                        StopToken.Cancel();
                        WriteStatus("stopping");
                        return;
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private void WriteStatus(string state)
    {
        if (_output == null) return;
        var json = JsonSerializer.Serialize(new { @event = "status", state });
        lock (this)
        {
            _output.WriteLine(json);
            _output.Flush();
        }
    }

    public void Dispose()
    {
        StopToken.Dispose();
        PauseGate.Dispose();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "StdinCommandReaderTests"`
Expected: All 6 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteFileSync/Progress/StdinCommandReader.cs tests/RemoteFileSync.Tests/Progress/StdinCommandReaderTests.cs
git commit -m "feat: add StdinCommandReader for PAUSE/RESUME/STOP control"
```

---

## Task 4: CLI — Parse --json-progress and Wire Up

**Files:**
- Modify: `src/RemoteFileSync/Program.cs`

- [ ] **Step 1: Add --json-progress to ParseArgs**

In `Program.cs`, add this case to the switch in `ParseArgs` (before `default:`):

```csharp
case "--json-progress":
    options.JsonProgress = true;
    break;
```

- [ ] **Step 2: Update Main to create JsonProgressWriter and StdinCommandReader**

In `Program.cs`, update the `Main` method. After creating the logger and before the try block, add:

```csharp
using var progressWriter = options.JsonProgress
    ? new Progress.JsonProgressWriter(Console.Out)
    : Progress.JsonProgressWriter.Null;
using var stdinReader = options.JsonProgress
    ? new Progress.StdinCommandReader(Console.In, Console.Out)
    : Progress.StdinCommandReader.Null;
if (options.JsonProgress)
    stdinReader.Start();
```

Update the server and client instantiation to pass the writer and reader. The `SyncServer` and `SyncClient` constructors will be updated in Task 5 to accept these parameters. For now, just add the `using RemoteFileSync.Progress;` import and parse the flag.

- [ ] **Step 3: Update PrintUsage**

Add this line after the `--delete` line:

```csharp
Console.Error.WriteLine("  --json-progress         JSON events to stdout (for UI integration)");
```

- [ ] **Step 4: Verify build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteFileSync/Program.cs
git commit -m "feat: parse --json-progress flag and create writer/reader instances"
```

---

## Task 5: Integration — Wire JsonProgressWriter + StdinCommandReader into SyncClient and SyncServer

**Files:**
- Modify: `src/RemoteFileSync/Network/SyncClient.cs`
- Modify: `src/RemoteFileSync/Network/SyncServer.cs`
- Modify: `src/RemoteFileSync/Program.cs`

This is the largest modification task. Both `SyncClient` and `SyncServer` need:
1. Constructor parameters for `JsonProgressWriter` and `StdinCommandReader`
2. JSON event emissions at key points
3. Pause gate checks between file transfers

- [ ] **Step 1: Update SyncClient constructor**

Add `JsonProgressWriter` and `StdinCommandReader` parameters:

```csharp
using RemoteFileSync.Progress;

// Updated constructor:
private readonly JsonProgressWriter _progress;
private readonly StdinCommandReader _stdinReader;

public SyncClient(SyncOptions options, SyncLogger logger,
                  SyncStateManager? stateManager = null,
                  JsonProgressWriter? progressWriter = null,
                  StdinCommandReader? stdinReader = null)
{
    _options = options;
    _logger = logger;
    _stateManager = stateManager;
    _progress = progressWriter ?? JsonProgressWriter.Null;
    _stdinReader = stdinReader ?? StdinCommandReader.Null;
}
```

- [ ] **Step 2: Add JSON event emissions to SyncClient.RunAsync and HandleConnectionAsync**

In `SyncClient.RunAsync`, after the retry loop succeeds (before the mode label log), add:

```csharp
_progress.WriteStatus("connecting", host: _options.Host, port: _options.Port);
```

After the `_logger.Summary($"Connected. {modeLabel}...")` line, add:

```csharp
_progress.WriteStatus("connected", mode: $"{modeLabel}{deleteLabel}");
```

In `HandleConnectionAsync`, after `var clientManifest = scanner.Scan();`, add:

```csharp
long localBytes = clientManifest.Entries.Sum(e => e.FileSize);
_progress.WriteManifest("local", clientManifest.Count, localBytes);
```

After `var serverManifest = ProtocolHandler.DeserializeManifest(mData);`, add:

```csharp
long remoteBytes = serverManifest.Entries.Sum(e => e.FileSize);
_progress.WriteManifest("remote", serverManifest.Count, remoteBytes);
```

After the sync plan logging (`_logger.Info($"Sync plan: {transferCount}..."`), add:

```csharp
_progress.WritePlan(transferCount, deleteCount, skipCount);
```

In the `foreach (var action in toSend)` loop, before `await sender.SendFileAsync(...)`, add:

```csharp
var fi = new FileInfo(Path.Combine(_options.Folder, action.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
_progress.WriteFileStart("send", action.RelativePath, fi.Exists ? fi.Length : 0, compressed: false, thread: filesTransferred % _options.MaxThreads);
```

After `filesTransferred++`, add:

```csharp
_progress.WriteFileEnd(action.RelativePath, success: true, thread: (filesTransferred - 1) % _options.MaxThreads);
```

In the catch block, add:

```csharp
_progress.WriteFileEnd(action.RelativePath, success: false, error: ex.Message);
```

Before the SyncComplete exchange, add:

```csharp
_progress.WriteComplete(filesTransferred, filesDeleted, bytesTransferred, sw.ElapsedMilliseconds, exitCode);
```

In the receive loop (`foreach (var action in toReceive)`), add similar `WriteFileStart`/`WriteFileEnd` calls with `action: "receive"`.

For deletion phases, after each successful deletion add:

```csharp
_progress.WriteDelete(del.RelativePath, backed_up: true, success: true);
```

And for failed deletions:

```csharp
_progress.WriteDelete(del.RelativePath, backed_up: false, success: false, error: "Server failed");
```

- [ ] **Step 3: Add pause gate checks to SyncClient**

At the start of the `foreach (var action in toSend)` loop body, before `try {`, add:

```csharp
_stdinReader.PauseGate.Wait();
if (_stdinReader.StopToken.IsCancellationRequested)
{
    _logger.Warning("Stop requested. Finishing current operations...");
    _progress.WriteStatus("stopping");
    break;
}
```

Add the identical pattern at the start of the receive loop body and the deletion loop body.

- [ ] **Step 4: Update SyncServer constructor and add events + pause gate**

Add the same fields and constructor pattern to `SyncServer`:

```csharp
using RemoteFileSync.Progress;

private readonly JsonProgressWriter _progress;
private readonly StdinCommandReader _stdinReader;

public SyncServer(SyncOptions options, SyncLogger logger,
                  JsonProgressWriter? progressWriter = null,
                  StdinCommandReader? stdinReader = null)
{
    _options = options;
    _logger = logger;
    _progress = progressWriter ?? JsonProgressWriter.Null;
    _stdinReader = stdinReader ?? StdinCommandReader.Null;
}
```

Add event emissions at these specific points in `SyncServer`:

After `listener.Start()`: `_progress.WriteStatus("listening", port: _options.Port);`
After `AcceptTcpClientAsync`: `_progress.WriteStatus("connected");`
After server manifest scan: `_progress.WriteManifest("local", serverManifest.Count, serverBytes);`
After receiving client manifest: `_progress.WriteManifest("remote", clientManifest.Count, clientBytes);`
Before each file receive: `_progress.WriteFileStart("receive", ...);`
After each file receive: `_progress.WriteFileEnd(...);`
Before SyncComplete: `_progress.WriteComplete(filesTransferred, filesDeleted, bytesTransferred, sw.ElapsedMilliseconds, exitCode);`

Add pause gate checks at the start of each file loop body (same pattern as SyncClient Step 3).

- [ ] **Step 5: Update Program.cs to pass writer/reader to constructors**

```csharp
if (options.IsServer)
{
    var server = new Network.SyncServer(options, logger, progressWriter, stdinReader);
    return await server.RunAsync(cts.Token);
}
else
{
    SyncStateManager? stateManager = null;
    if (options.DeleteEnabled)
        stateManager = new SyncStateManager(SyncStateManager.DefaultBaseDir);
    var client = new Network.SyncClient(options, logger, stateManager, progressWriter, stdinReader);
    return await client.RunAsync(cts.Token);
}
```

- [ ] **Step 6: Verify build and run full test suite**

Run: `dotnet build && dotnet test`
Expected: Build succeeded, all existing tests pass (new optional params default to null).

- [ ] **Step 7: Commit**

```bash
git add src/RemoteFileSync/Network/SyncClient.cs src/RemoteFileSync/Network/SyncServer.cs src/RemoteFileSync/Program.cs
git commit -m "feat: wire JSON progress writer and stdin control into sync engine"
```

---

# Phase B — ExecRFS WPF Blazor Hybrid App

---

## Task 6: Project Scaffolding

**Files:**
- Create: `src/ExecRFS/ExecRFS.csproj`
- Create: `src/ExecRFS/App.xaml` + `App.xaml.cs`
- Create: `src/ExecRFS/MainWindow.xaml` + `MainWindow.xaml.cs`
- Create: `src/ExecRFS/wwwroot/index.html`
- Create: `src/ExecRFS/_Imports.razor`
- Modify: `RemoteFileSync.slnx`

- [ ] **Step 1: Create the WPF Blazor project**

```bash
cd E:\RemoteFileSync
mkdir -p src/ExecRFS
```

Create `src/ExecRFS/ExecRFS.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <RootNamespace>ExecRFS</RootNamespace>
    <AssemblyName>ExecRFS</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Components.WebView.Wpf" Version="10.0.*" />
  </ItemGroup>
  <ItemGroup>
    <Content Include="wwwroot\**" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create App.xaml and App.xaml.cs**

Create `src/ExecRFS/App.xaml`:

```xml
<Application x:Class="ExecRFS.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="MainWindow.xaml">
    <Application.Resources>
    </Application.Resources>
</Application>
```

Create `src/ExecRFS/App.xaml.cs`:

```csharp
using System.Windows;

namespace ExecRFS;

public partial class App : Application
{
}
```

- [ ] **Step 3: Create MainWindow.xaml and MainWindow.xaml.cs**

Create `src/ExecRFS/MainWindow.xaml`:

```xml
<Window x:Class="ExecRFS.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:blazor="clr-namespace:Microsoft.AspNetCore.Components.WebView.Wpf;assembly=Microsoft.AspNetCore.Components.WebView.Wpf"
        xmlns:layout="clr-namespace:ExecRFS.Components.Layout"
        Title="ExecRFS — RemoteFileSync Manager"
        Width="1200" Height="800" MinWidth="900" MinHeight="600"
        WindowStartupLocation="CenterScreen"
        Background="#0d1117">
    <blazor:BlazorWebView x:Name="blazorWebView" HostPage="wwwroot/index.html">
        <blazor:BlazorWebView.RootComponents>
            <blazor:RootComponent Selector="#app" ComponentType="{x:Type layout:MainLayout}" />
        </blazor:BlazorWebView.RootComponents>
    </blazor:BlazorWebView>
</Window>
```

Create `src/ExecRFS/MainWindow.xaml.cs`:

```csharp
using System.Windows;
using ExecRFS.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ExecRFS;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddSingleton<ProfileService>();
        services.AddSingleton<LogAggregator>();
        services.AddSingleton(new SyncProcesses(
            new ProcessManager("server"),
            new ProcessManager("client")));

        var sp = services.BuildServiceProvider();
        blazorWebView.Services = sp;

        Closing += (_, _) =>
        {
            sp.GetService<ProfileService>()?.AutoSave();
            var procs = sp.GetService<SyncProcesses>();
            procs?.Server.Dispose();
            procs?.Client.Dispose();
        };
    }
}
```

Also create `src/ExecRFS/Services/SyncProcesses.cs` — a simple holder for the two `ProcessManager` instances so DI can resolve them as a single service:

```csharp
namespace ExecRFS.Services;

/// <summary>
/// Holds the server and client ProcessManager instances.
/// Registered as a singleton in DI so Blazor components can inject it.
/// </summary>
public sealed class SyncProcesses
{
    public ProcessManager Server { get; }
    public ProcessManager Client { get; }

    public SyncProcesses(ProcessManager server, ProcessManager client)
    {
        Server = server;
        Client = client;
    }
}
```

- [ ] **Step 4: Create wwwroot/index.html**

Create `src/ExecRFS/wwwroot/index.html`:

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <title>ExecRFS</title>
    <link href="css/app.css" rel="stylesheet" />
</head>
<body>
    <div id="app">Loading...</div>
    <script>
        window.scrollToBottom = function(el) {
            if (el) { el.scrollTop = el.scrollHeight; }
        };
        window.copyToClipboard = function(text) {
            navigator.clipboard.writeText(text);
        };
    </script>
    <script src="_framework/blazor.webview.js"></script>
</body>
</html>
```

- [ ] **Step 5: Create _Imports.razor**

Create `src/ExecRFS/_Imports.razor`:

```razor
@using Microsoft.AspNetCore.Components
@using Microsoft.AspNetCore.Components.Web
@using Microsoft.JSInterop
@using ExecRFS.Models
@using ExecRFS.Services
@using ExecRFS.Components.Shared
```

- [ ] **Step 6: Add project to solution**

```bash
dotnet sln RemoteFileSync.slnx add src/ExecRFS/ExecRFS.csproj
```

- [ ] **Step 7: Verify build**

Run: `dotnet build src/ExecRFS/ExecRFS.csproj`
Expected: Build succeeded (may have warnings about missing components — those come in later tasks).

- [ ] **Step 8: Commit**

```bash
git add src/ExecRFS/ RemoteFileSync.slnx
git commit -m "feat: scaffold ExecRFS WPF Blazor Hybrid project"
```

---

## Task 7: Models — SyncProfile, SyncInstanceState, ProgressEvent

**Files:**
- Create: `src/ExecRFS/Models/SyncProfile.cs`
- Create: `src/ExecRFS/Models/SyncInstanceState.cs`
- Create: `src/ExecRFS/Models/ProgressEvent.cs`

- [ ] **Step 1: Create SyncProfile**

Create `src/ExecRFS/Models/SyncProfile.cs`:

```csharp
namespace ExecRFS.Models;

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

- [ ] **Step 2: Create SyncInstanceState**

Create `src/ExecRFS/Models/SyncInstanceState.cs`:

```csharp
namespace ExecRFS.Models;

public enum SyncInstanceState
{
    Idle,
    Starting,
    Running,
    Paused,
    Stopping,
    Stopped,
    Error
}
```

- [ ] **Step 3: Create ProgressEvent**

Create `src/ExecRFS/Models/ProgressEvent.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExecRFS.Models;

public class ProgressEvent
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = "";

    // status fields
    [JsonPropertyName("state")]
    public string? State { get; set; }
    [JsonPropertyName("host")]
    public string? Host { get; set; }
    [JsonPropertyName("port")]
    public int? Port { get; set; }
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    // manifest fields
    [JsonPropertyName("side")]
    public string? Side { get; set; }
    [JsonPropertyName("files")]
    public int? Files { get; set; }
    [JsonPropertyName("bytes")]
    public long? Bytes { get; set; }

    // plan fields
    [JsonPropertyName("transfers")]
    public int? Transfers { get; set; }
    [JsonPropertyName("deletes")]
    public int? Deletes { get; set; }
    [JsonPropertyName("skipped")]
    public int? Skipped { get; set; }

    // file fields
    [JsonPropertyName("action")]
    public string? Action { get; set; }
    [JsonPropertyName("path")]
    public string? Path { get; set; }
    [JsonPropertyName("size")]
    public long? Size { get; set; }
    [JsonPropertyName("compressed")]
    public bool? Compressed { get; set; }
    [JsonPropertyName("thread")]
    public int? Thread { get; set; }
    [JsonPropertyName("bytes_sent")]
    public long? BytesSent { get; set; }
    [JsonPropertyName("total_bytes")]
    public long? TotalBytes { get; set; }
    [JsonPropertyName("success")]
    public bool? Success { get; set; }

    // delete fields
    [JsonPropertyName("backed_up")]
    public bool? BackedUp { get; set; }

    // complete fields
    [JsonPropertyName("files_transferred")]
    public int? FilesTransferred { get; set; }
    [JsonPropertyName("files_deleted")]
    public int? FilesDeleted { get; set; }
    [JsonPropertyName("elapsed_ms")]
    public long? ElapsedMs { get; set; }
    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; set; }

    // error fields
    [JsonPropertyName("message")]
    public string? Message { get; set; }
    [JsonPropertyName("fatal")]
    public bool? Fatal { get; set; }

    // error fields (also reused by file_end)
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    public static ProgressEvent? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<ProgressEvent>(line);
        }
        catch
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: Verify build**

Run: `dotnet build src/ExecRFS/ExecRFS.csproj`

- [ ] **Step 5: Commit**

```bash
git add src/ExecRFS/Models/
git commit -m "feat: add ExecRFS models — SyncProfile, SyncInstanceState, ProgressEvent"
```

---

## Task 8: Services — CommandBuilder and ProfileService

**Files:**
- Create: `src/ExecRFS/Services/CommandBuilder.cs`
- Create: `src/ExecRFS/Services/ProfileService.cs`

- [ ] **Step 1: Create CommandBuilder**

Create `src/ExecRFS/Services/CommandBuilder.cs`:

```csharp
using System.Text;
using ExecRFS.Models;

namespace ExecRFS.Services;

public static class CommandBuilder
{
    public static string Build(SyncProfile profile, bool isServer)
    {
        var sb = new StringBuilder("RemoteFileSync.exe ");
        sb.Append(isServer ? "server" : "client");

        if (!isServer)
            sb.Append($" --host \"{profile.ClientHost}\"");

        sb.Append($" --folder \"{(isServer ? profile.ServerFolder : profile.ClientFolder)}\"");
        sb.Append($" --port {(isServer ? profile.ServerPort : profile.ClientPort)}");

        var backupFolder = isServer ? profile.ServerBackupFolder : profile.ClientBackupFolder;
        if (!string.IsNullOrWhiteSpace(backupFolder))
            sb.Append($" --backup-folder \"{backupFolder}\"");

        if (!isServer && profile.Bidirectional)
            sb.Append(" --bidirectional");
        if (!isServer && profile.DeleteEnabled)
            sb.Append(" --delete");

        var blockSize = isServer ? profile.ServerBlockSize : profile.ClientBlockSize;
        if (blockSize != 65536)
            sb.Append($" --block-size {blockSize}");

        var maxThreads = isServer ? profile.ServerMaxThreads : profile.ClientMaxThreads;
        if (maxThreads > 1)
            sb.Append($" --max-threads {maxThreads}");

        foreach (var pattern in profile.IncludePatterns)
            sb.Append($" --include \"{pattern}\"");
        foreach (var pattern in profile.ExcludePatterns)
            sb.Append($" --exclude \"{pattern}\"");

        sb.Append(" --verbose");

        var logFile = isServer ? profile.ServerLogFile : profile.ClientLogFile;
        if (!string.IsNullOrWhiteSpace(logFile))
            sb.Append($" --log \"{logFile}\"");

        return sb.ToString();
    }

    public static string BuildForProcess(SyncProfile profile, bool isServer)
    {
        return Build(profile, isServer) + " --json-progress";
    }

    public static string BuildBoth(SyncProfile profile)
    {
        var server = Build(profile, isServer: true);
        var client = Build(profile, isServer: false);
        return $"REM === Server Command ===\n{server}\n\nREM === Client Command ===\n{client}";
    }
}
```

- [ ] **Step 2: Create ProfileService**

Create `src/ExecRFS/Services/ProfileService.cs`:

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using ExecRFS.Models;

namespace ExecRFS.Services;

public partial class ProfileService
{
    private readonly string _profileDir;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public SyncProfile CurrentProfile { get; set; } = new();

    public ProfileService()
    {
        _profileDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteFileSync", "profiles");
        Directory.CreateDirectory(_profileDir);
    }

    public ProfileService(string profileDir)
    {
        _profileDir = profileDir;
        Directory.CreateDirectory(_profileDir);
    }

    public List<string> ListProfiles()
    {
        return Directory.GetFiles(_profileDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != null && n != "_last-session")
            .Cast<string>()
            .OrderBy(n => n)
            .ToList();
    }

    public SyncProfile Load(string name)
    {
        var path = Path.Combine(_profileDir, SanitizeFileName(name) + ".json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Profile not found: {name}");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SyncProfile>(json, JsonOpts) ?? new SyncProfile();
    }

    public void Save(SyncProfile profile)
    {
        var path = Path.Combine(_profileDir, SanitizeFileName(profile.Name) + ".json");
        var json = JsonSerializer.Serialize(profile, JsonOpts);
        File.WriteAllText(path, json);
    }

    public void Delete(string name)
    {
        var path = Path.Combine(_profileDir, SanitizeFileName(name) + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    public void AutoSave()
    {
        var path = Path.Combine(_profileDir, "_last-session.json");
        var json = JsonSerializer.Serialize(CurrentProfile, JsonOpts);
        File.WriteAllText(path, json);
    }

    public SyncProfile LoadLastSession()
    {
        var path = Path.Combine(_profileDir, "_last-session.json");
        if (!File.Exists(path)) return new SyncProfile();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SyncProfile>(json, JsonOpts) ?? new SyncProfile();
        }
        catch { return new SyncProfile(); }
    }

    private static string SanitizeFileName(string name)
    {
        var sanitized = InvalidCharsRegex().Replace(name.ToLowerInvariant().Replace(' ', '-'), "");
        return string.IsNullOrWhiteSpace(sanitized) ? "untitled" : sanitized;
    }

    [GeneratedRegex("[^a-z0-9\\-_]")]
    private static partial Regex InvalidCharsRegex();
}
```

- [ ] **Step 3: Verify build**

Run: `dotnet build src/ExecRFS/ExecRFS.csproj`

- [ ] **Step 4: Commit**

```bash
git add src/ExecRFS/Services/CommandBuilder.cs src/ExecRFS/Services/ProfileService.cs
git commit -m "feat: add CommandBuilder and ProfileService"
```

---

## Task 9: Services — ProcessManager and LogAggregator

**Files:**
- Create: `src/ExecRFS/Services/ProcessManager.cs`
- Create: `src/ExecRFS/Services/LogAggregator.cs`

- [ ] **Step 1: Create ProcessManager**

Create `src/ExecRFS/Services/ProcessManager.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json;
using ExecRFS.Models;

namespace ExecRFS.Services;

public sealed class ProcessManager : IDisposable
{
    private readonly string _role; // "server" or "client"
    private Process? _process;
    private SyncInstanceState _state = SyncInstanceState.Idle;

    public SyncInstanceState State
    {
        get => _state;
        private set
        {
            _state = value;
            OnStateChanged?.Invoke(value);
        }
    }

    public event Action<ProgressEvent>? OnProgress;
    public event Action<string>? OnLogLine;
    public event Action<SyncInstanceState>? OnStateChanged;
    public event Action<int>? OnExited;

    public ProcessManager(string role)
    {
        _role = role;
    }

    public void Start(SyncProfile profile, string? exePath = null)
    {
        if (_process != null && !_process.HasExited)
            return;

        State = SyncInstanceState.Starting;

        var resolvedExe = exePath ?? ResolveExePath();
        var args = CommandBuilder.BuildForProcess(profile, _role == "server");
        // Strip "RemoteFileSync.exe " prefix since we pass it as the exe
        args = args.Substring(args.IndexOf(' ') + 1);

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = resolvedExe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            OnLogLine?.Invoke(e.Data);

            var evt = ProgressEvent.TryParse(e.Data);
            if (evt != null)
            {
                OnProgress?.Invoke(evt);
                if (evt.Event == "status" && evt.State == "paused")
                    State = SyncInstanceState.Paused;
                else if (evt.Event == "status" && evt.State == "resumed")
                    State = SyncInstanceState.Running;
                else if (evt.Event == "complete")
                    State = SyncInstanceState.Stopped;
                else if (evt.Event == "error" && evt.Fatal == true)
                    State = SyncInstanceState.Error;
            }
        };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) OnLogLine?.Invoke($"[STDERR] {e.Data}");
        };

        _process.Exited += (_, _) =>
        {
            var exitCode = _process.ExitCode;
            if (State != SyncInstanceState.Error)
                State = SyncInstanceState.Stopped;
            OnExited?.Invoke(exitCode);
        };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        State = SyncInstanceState.Running;
    }

    public void Pause()
    {
        if (State != SyncInstanceState.Running) return;
        WriteStdin("PAUSE");
    }

    public void Resume()
    {
        if (State != SyncInstanceState.Paused) return;
        WriteStdin("RESUME");
    }

    public void Stop()
    {
        if (_process == null || _process.HasExited) return;
        State = SyncInstanceState.Stopping;
        WriteStdin("STOP");

        // Give it 5 seconds to exit gracefully
        Task.Run(async () =>
        {
            await Task.Delay(5000);
            if (_process != null && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                State = SyncInstanceState.Stopped;
            }
        });
    }

    private void WriteStdin(string command)
    {
        try
        {
            _process?.StandardInput.WriteLine(command);
            _process?.StandardInput.Flush();
        }
        catch (Exception) { /* process may have already exited */ }
    }

    private static string ResolveExePath()
    {
        // 1. Same directory as ExecRFS.exe
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var local = Path.Combine(appDir, "RemoteFileSync.exe");
        if (File.Exists(local)) return local;

        // 2. PATH
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, "RemoteFileSync.exe");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            "RemoteFileSync.exe not found. Place it in the same directory as ExecRFS.exe or add it to PATH.");
    }

    public void Dispose()
    {
        if (_process != null && !_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
        _process?.Dispose();
    }
}
```

- [ ] **Step 2: Create LogAggregator**

Create `src/ExecRFS/Services/LogAggregator.cs`:

```csharp
namespace ExecRFS.Services;

public sealed class LogAggregator
{
    private readonly List<LogEntry> _entries = new();
    private readonly object _lock = new();
    private const int MaxEntries = 5000;

    public event Action<LogEntry>? OnEntry;

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_lock) return _entries.ToList();
        }
    }

    public void AddLine(string source, string line)
    {
        var entry = new LogEntry(DateTime.Now, source, ParseLevel(line), line);
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveAt(0);
        }
        OnEntry?.Invoke(entry);
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }

    private static string ParseLevel(string line)
    {
        if (line.Contains("[ERR]") || line.Contains("\"fatal\":true")) return "error";
        if (line.Contains("[WRN]") || line.Contains("Failed")) return "warning";
        if (line.Contains("[DEL")) return "delete";
        return "info";
    }
}

public record LogEntry(DateTime Timestamp, string Source, string Level, string Message);
```

- [ ] **Step 3: Verify build**

Run: `dotnet build src/ExecRFS/ExecRFS.csproj`

- [ ] **Step 4: Commit**

```bash
git add src/ExecRFS/Services/ProcessManager.cs src/ExecRFS/Services/LogAggregator.cs
git commit -m "feat: add ProcessManager and LogAggregator services"
```

---

## Task 10: CSS Theme

**Files:**
- Create: `src/ExecRFS/wwwroot/css/app.css`

- [ ] **Step 1: Create the dark theme CSS**

Create `src/ExecRFS/wwwroot/css/app.css`:

```css
/* ExecRFS — Dark Theme (GitHub Dark palette) */
:root {
    --bg: #0d1117;
    --surface: #161b22;
    --border: #30363d;
    --text: #c9d1d9;
    --muted: #8b949e;
    --blue: #4a9eff;
    --orange: #e88a3a;
    --green: #3fb950;
    --red: #f85149;
    --yellow: #d29922;
}

* { margin: 0; padding: 0; box-sizing: border-box; }

body {
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif;
    background: var(--bg);
    color: var(--text);
    font-size: 13px;
    overflow: hidden;
    height: 100vh;
}

.toolbar {
    display: flex; align-items: center; gap: 8px;
    padding: 8px 12px; background: var(--surface);
    border-bottom: 1px solid var(--border);
}
.toolbar .app-name { color: var(--blue); font-weight: bold; font-size: 14px; }

.btn {
    background: #21262d; border: 1px solid var(--border);
    padding: 4px 12px; border-radius: 4px; color: var(--text);
    cursor: pointer; font-size: 12px; transition: background 0.15s;
}
.btn:hover { background: #30363d; }
.btn-start { background: #238636; border-color: #2ea043; color: white; }
.btn-start:hover { background: #2ea043; }
.btn-pause { background: #8b5e34; border-color: #a06b3b; color: white; }
.btn-stop { background: #9a3434; border-color: #b04040; color: white; }
.btn-stop:hover { background: #b04040; }

.split-panels {
    display: flex; gap: 1px; background: var(--border);
    flex: 1; overflow: hidden;
}
.panel {
    flex: 1; background: var(--bg); padding: 12px;
    overflow-y: auto;
}
.panel-header {
    display: flex; align-items: center; justify-content: space-between;
    margin-bottom: 10px;
}
.panel-title { font-weight: bold; font-size: 14px; }
.panel-title.server { color: var(--blue); }
.panel-title.client { color: var(--orange); }

.status-dot {
    width: 8px; height: 8px; border-radius: 50%; display: inline-block;
}
.status-dot.idle { background: var(--muted); }
.status-dot.running { background: var(--green); }
.status-dot.paused { background: var(--yellow); }
.status-dot.error { background: var(--red); }
.status-dot.syncing { background: var(--yellow); animation: pulse 1.5s infinite; }

@keyframes pulse {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.4; }
}

.field { margin-bottom: 8px; }
.field-label {
    color: var(--muted); font-size: 10px;
    text-transform: uppercase; margin-bottom: 3px;
}
.field-row { display: flex; gap: 4px; }
.field-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; }

input[type="text"], input[type="number"], select {
    background: var(--surface); border: 1px solid var(--border);
    padding: 5px 8px; border-radius: 4px; color: var(--text);
    font-family: 'Cascadia Code', monospace; font-size: 11px;
    width: 100%;
}
input:focus, select:focus { outline: none; border-color: var(--blue); }

.checkbox-row {
    display: flex; gap: 12px; margin-bottom: 8px;
}
.checkbox-label {
    display: flex; align-items: center; gap: 4px; font-size: 12px; cursor: pointer;
}

.tag { padding: 2px 8px; border-radius: 10px; font-size: 10px; display: inline-flex; align-items: center; gap: 4px; }
.tag-include { background: #1f3a1f; border: 1px solid #2d5a2d; color: var(--green); }
.tag-exclude { background: #3a1f1f; border: 1px solid #5a2d2d; color: var(--red); }
.tag-remove { cursor: pointer; opacity: 0.7; }
.tag-remove:hover { opacity: 1; }
.tag-add { background: #21262d; border: 1px solid var(--border); color: var(--muted); cursor: pointer; }

.progress-section {
    padding: 8px 12px; background: var(--surface);
    border-top: 1px solid var(--border);
}
.progress-bar-track {
    background: var(--border); border-radius: 3px; height: 6px; margin-top: 4px;
}
.progress-bar-fill {
    background: linear-gradient(90deg, var(--blue), var(--green));
    height: 100%; border-radius: 3px; transition: width 0.3s;
}

.thread-progress {
    background: var(--surface); border: 1px solid var(--border);
    border-radius: 4px; padding: 6px; margin-top: 6px;
}
.thread-row { display: flex; justify-content: space-between; font-size: 10px; margin-bottom: 2px; }
.thread-bar { background: var(--border); border-radius: 2px; height: 3px; margin-bottom: 4px; }
.thread-bar-fill { background: var(--blue); height: 100%; border-radius: 2px; transition: width 0.2s; }

.log-viewer {
    background: #010409; padding: 8px 12px;
    font-family: 'Cascadia Code', 'Consolas', monospace; font-size: 11px;
    line-height: 1.6; overflow-y: auto; flex: 0 0 200px;
    border-top: 1px solid var(--border);
}
.log-toolbar {
    display: flex; align-items: center; gap: 4px; margin-bottom: 6px;
}
.log-line { white-space: pre-wrap; word-break: break-all; }
.log-srv { color: var(--blue); }
.log-cli { color: var(--orange); }
.log-error { color: var(--red); }
.log-warning { color: var(--yellow); }
.log-delete { color: var(--red); }

.main-layout {
    display: flex; flex-direction: column; height: 100vh;
}
```

- [ ] **Step 2: Commit**

```bash
git add src/ExecRFS/wwwroot/css/app.css
git commit -m "feat: add ExecRFS dark theme CSS"
```

---

## Task 11: Blazor Components — MainLayout

**Files:**
- Create: `src/ExecRFS/Components/Layout/MainLayout.razor`

- [ ] **Step 1: Create MainLayout**

Create `src/ExecRFS/Components/Layout/MainLayout.razor`:

```razor
@inject ProfileService ProfileService
@inject SyncProcesses SyncProcs
@inject LogAggregator LogAgg
@inject IJSRuntime JS
@implements IDisposable

<div class="main-layout">
    <!-- Toolbar -->
    <div class="toolbar">
        <span class="app-name">ExecRFS</span>

        <select class="btn" @onchange="OnProfileSelected">
            <option value="">-- Select Profile --</option>
            @foreach (var name in _profiles)
            {
                <option value="@name" selected="@(name == _currentProfileName)">@name</option>
            }
        </select>

        <button class="btn" @onclick="SaveProfile">Save</button>
        <button class="btn" @onclick="SaveProfileAs">Save As...</button>
        <button class="btn" style="color:var(--red);" @onclick="DeleteProfile"
                disabled="@(string.IsNullOrEmpty(_currentProfileName))">Delete</button>

        <div style="margin-left:auto;display:flex;gap:4px;">
            <button class="btn" @onclick="GenerateCommand">Generate CMD</button>
        </div>
    </div>

    <!-- Split Panels -->
    <div class="split-panels">
        <ServerPanel Profile="@ProfileService.CurrentProfile"
                     ProcessManager="@SyncProcs.Server" />
        <ClientPanel Profile="@ProfileService.CurrentProfile"
                     ProcessManager="@SyncProcs.Client" />
    </div>

    <!-- Progress Bar -->
    <ProgressBar ServerProcess="@SyncProcs.Server" ClientProcess="@SyncProcs.Client" />

    <!-- Log Viewer -->
    <LogViewer Aggregator="@LogAgg" />
</div>

@if (_showSaveDialog)
{
    <div style="position:fixed;top:0;left:0;right:0;bottom:0;background:rgba(0,0,0,0.6);display:flex;align-items:center;justify-content:center;z-index:100;">
        <div style="background:var(--surface);border:1px solid var(--border);border-radius:8px;padding:20px;width:400px;">
            <h3 style="margin-bottom:12px;">Save Profile As</h3>
            <input type="text" @bind="_saveAsName" placeholder="Profile name..." style="margin-bottom:12px;" />
            <div style="display:flex;gap:8px;justify-content:flex-end;">
                <button class="btn" @onclick="() => _showSaveDialog = false">Cancel</button>
                <button class="btn btn-start" @onclick="ConfirmSaveAs">Save</button>
            </div>
        </div>
    </div>
}

@if (_showCommandPreview)
{
    <div style="position:fixed;top:0;left:0;right:0;bottom:0;background:rgba(0,0,0,0.6);display:flex;align-items:center;justify-content:center;z-index:100;">
        <div style="background:var(--surface);border:1px solid var(--border);border-radius:8px;padding:20px;width:650px;">
            <h3 style="margin-bottom:12px;">Generated CLI Commands</h3>
            <div style="margin-bottom:8px;">
                <div class="field-label">Server Command</div>
                <pre style="background:var(--bg);padding:8px;border-radius:4px;font-size:11px;white-space:pre-wrap;">@_serverCmd</pre>
            </div>
            <div style="margin-bottom:12px;">
                <div class="field-label">Client Command</div>
                <pre style="background:var(--bg);padding:8px;border-radius:4px;font-size:11px;white-space:pre-wrap;">@_clientCmd</pre>
            </div>
            <div style="display:flex;gap:8px;justify-content:flex-end;">
                <button class="btn" @onclick="CopyServerCmd">Copy Server</button>
                <button class="btn" @onclick="CopyClientCmd">Copy Client</button>
                <button class="btn" @onclick="CopyBothCmds">Copy Both</button>
                <button class="btn" @onclick="() => _showCommandPreview = false">Close</button>
            </div>
        </div>
    </div>
}

@code {
    private List<string> _profiles = new();
    private string _currentProfileName = "";
    private bool _showSaveDialog;
    private string _saveAsName = "";
    private bool _showCommandPreview;
    private string _serverCmd = "";
    private string _clientCmd = "";

    protected override void OnInitialized()
    {
        SyncProcs.Server.OnLogLine += line => { LogAgg.AddLine("SRV", line); InvokeAsync(StateHasChanged); };
        SyncProcs.Client.OnLogLine += line => { LogAgg.AddLine("CLI", line); InvokeAsync(StateHasChanged); };
        SyncProcs.Server.OnStateChanged += _ => InvokeAsync(StateHasChanged);
        SyncProcs.Client.OnStateChanged += _ => InvokeAsync(StateHasChanged);

        ProfileService.CurrentProfile = ProfileService.LoadLastSession();
        _profiles = ProfileService.ListProfiles();
    }

    private void OnProfileSelected(ChangeEventArgs e)
    {
        var name = e.Value?.ToString();
        if (string.IsNullOrEmpty(name)) return;
        ProfileService.CurrentProfile = ProfileService.Load(name);
        _currentProfileName = name;
    }

    private void SaveProfile()
    {
        if (string.IsNullOrWhiteSpace(ProfileService.CurrentProfile.Name) || ProfileService.CurrentProfile.Name == "Untitled")
        { SaveProfileAs(); return; }
        ProfileService.Save(ProfileService.CurrentProfile);
        _profiles = ProfileService.ListProfiles();
    }

    private void SaveProfileAs() { _saveAsName = ProfileService.CurrentProfile.Name; _showSaveDialog = true; }

    private void ConfirmSaveAs()
    {
        if (string.IsNullOrWhiteSpace(_saveAsName)) return;
        ProfileService.CurrentProfile.Name = _saveAsName;
        ProfileService.Save(ProfileService.CurrentProfile);
        _currentProfileName = _saveAsName;
        _profiles = ProfileService.ListProfiles();
        _showSaveDialog = false;
    }

    private void DeleteProfile()
    {
        if (string.IsNullOrEmpty(_currentProfileName)) return;
        ProfileService.Delete(_currentProfileName);
        _currentProfileName = "";
        ProfileService.CurrentProfile = new SyncProfile();
        _profiles = ProfileService.ListProfiles();
    }

    private void GenerateCommand()
    {
        _serverCmd = CommandBuilder.Build(ProfileService.CurrentProfile, isServer: true);
        _clientCmd = CommandBuilder.Build(ProfileService.CurrentProfile, isServer: false);
        _showCommandPreview = true;
    }

    private async Task CopyServerCmd() => await JS.InvokeVoidAsync("copyToClipboard", _serverCmd);
    private async Task CopyClientCmd() => await JS.InvokeVoidAsync("copyToClipboard", _clientCmd);
    private async Task CopyBothCmds() => await JS.InvokeVoidAsync("copyToClipboard", $"{_serverCmd}\n\n{_clientCmd}");

    public void Dispose()
    {
        // ProcessManagers are disposed by MainWindow.Closing handler
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add src/ExecRFS/Components/Layout/MainLayout.razor
git commit -m "feat: add MainLayout with toolbar, split panels, dialogs"
```

---

## Task 12: Blazor Components — Shared Components

**Files:**
- Create: `src/ExecRFS/Components/Shared/FolderPicker.razor`
- Create: `src/ExecRFS/Components/Shared/PatternList.razor`
- Create: `src/ExecRFS/Components/Shared/ProgressBar.razor`
- Create: `src/ExecRFS/Components/Shared/LogViewer.razor`

- [ ] **Step 1: Create FolderPicker**

Create `src/ExecRFS/Components/Shared/FolderPicker.razor`:

```razor
<div class="field">
    <div class="field-label">@Label</div>
    <div class="field-row">
        <input type="text" value="@Value" @oninput="OnInput" placeholder="@Placeholder"
               style="flex:1;" />
        <button class="btn" @onclick="Browse">Browse</button>
    </div>
</div>

@code {
    [Parameter] public string Label { get; set; } = "Folder";
    [Parameter] public string? Value { get; set; }
    [Parameter] public EventCallback<string?> ValueChanged { get; set; }
    [Parameter] public string Placeholder { get; set; } = "";

    private async Task OnInput(ChangeEventArgs e)
    {
        Value = e.Value?.ToString();
        await ValueChanged.InvokeAsync(Value);
    }

    private async Task Browse()
    {
        var folder = await Task.Run(() =>
        {
            string? result = null;
            var thread = new Thread(() =>
            {
                var dialog = new System.Windows.Forms.FolderBrowserDialog();
                if (!string.IsNullOrEmpty(Value))
                    dialog.SelectedPath = Value;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    result = dialog.SelectedPath;
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return result;
        });

        if (folder != null)
        {
            Value = folder;
            await ValueChanged.InvokeAsync(folder);
        }
    }
}
```

- [ ] **Step 2: Create PatternList**

Create `src/ExecRFS/Components/Shared/PatternList.razor`:

```razor
<div class="field">
    <div class="field-label">@Label</div>
    <div style="display:flex;flex-wrap:wrap;gap:3px;">
        @foreach (var pattern in Patterns)
        {
            var p = pattern;
            <span class="tag @CssClass">
                @p
                <span class="tag-remove" @onclick="() => Remove(p)">&times;</span>
            </span>
        }
        @if (_adding)
        {
            <input type="text" @bind="_newPattern" @onkeydown="OnKeyDown"
                   @ref="_inputRef" style="width:100px;font-size:10px;padding:2px 6px;" />
        }
        else
        {
            <span class="tag tag-add" @onclick="StartAdding">+ Add</span>
        }
    </div>
</div>

@code {
    [Parameter] public string Label { get; set; } = "Patterns";
    [Parameter] public List<string> Patterns { get; set; } = new();
    [Parameter] public EventCallback<List<string>> PatternsChanged { get; set; }
    [Parameter] public string Type { get; set; } = "include"; // "include" or "exclude"

    private string CssClass => Type == "include" ? "tag-include" : "tag-exclude";
    private bool _adding;
    private string _newPattern = "";
    private ElementReference _inputRef;

    private void StartAdding() { _adding = true; _newPattern = ""; }

    private async Task OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !string.IsNullOrWhiteSpace(_newPattern))
        {
            var updated = new List<string>(Patterns) { _newPattern.Trim() };
            await PatternsChanged.InvokeAsync(updated);
            _newPattern = "";
            _adding = false;
        }
        else if (e.Key == "Escape")
        {
            _adding = false;
        }
    }

    private async Task Remove(string pattern)
    {
        var updated = new List<string>(Patterns);
        updated.Remove(pattern);
        await PatternsChanged.InvokeAsync(updated);
    }
}
```

- [ ] **Step 3: Create ProgressBar**

Create `src/ExecRFS/Components/Shared/ProgressBar.razor`:

```razor
@implements IDisposable

<div class="progress-section">
    <div style="display:flex;justify-content:space-between;font-size:11px;color:var(--muted);">
        <span>Progress: <strong style="color:var(--text);">@_filesCompleted of @_totalFiles files</strong></span>
        <span>@FormatBytes(TotalBytesCompleted) / @FormatBytes(_totalBytes)</span>
        <span style="color:var(--yellow);">Elapsed: @_elapsed</span>
    </div>
    <div class="progress-bar-track">
        <div class="progress-bar-fill" style="width:@_percent%"></div>
    </div>
</div>

@code {
    [Parameter] public ProcessManager ServerProcess { get; set; } = default!;
    [Parameter] public ProcessManager ClientProcess { get; set; } = default!;

    private int _filesCompleted;
    private int _totalFiles;
    private long _bytesForCompletedFiles;
    private long _bytesForCurrentFile;
    private long _totalBytes;
    private string _elapsed = "00:00:00";
    private int _percent;
    private DateTime _startTime;
    private System.Timers.Timer? _timer;

    private long TotalBytesCompleted => _bytesForCompletedFiles + _bytesForCurrentFile;

    protected override void OnInitialized()
    {
        ServerProcess.OnProgress += HandleProgress;
        ClientProcess.OnProgress += HandleProgress;
        _timer = new System.Timers.Timer(1000);
        _timer.Elapsed += (_, _) =>
        {
            if (ServerProcess.State == SyncInstanceState.Running || ClientProcess.State == SyncInstanceState.Running)
            {
                _elapsed = (DateTime.Now - _startTime).ToString(@"hh\:mm\:ss");
                InvokeAsync(StateHasChanged);
            }
        };
    }

    private void HandleProgress(ProgressEvent evt)
    {
        switch (evt.Event)
        {
            case "plan":
                _totalFiles = (evt.Transfers ?? 0) + (evt.Deletes ?? 0);
                _filesCompleted = 0;
                _bytesForCompletedFiles = 0;
                _bytesForCurrentFile = 0;
                _startTime = DateTime.Now;
                _timer?.Start();
                break;
            case "manifest":
                if (evt.Side == "local") _totalBytes += evt.Bytes ?? 0;
                break;
            case "file_start":
                _bytesForCurrentFile = 0;
                break;
            case "file_progress":
                _bytesForCurrentFile = evt.BytesSent ?? 0;
                break;
            case "file_end":
                if (evt.Success == true)
                {
                    _filesCompleted++;
                    _bytesForCompletedFiles += _bytesForCurrentFile;
                }
                _bytesForCurrentFile = 0;
                break;
            case "complete":
                _bytesForCompletedFiles = evt.Bytes ?? 0;
                _bytesForCurrentFile = 0;
                _totalBytes = evt.Bytes ?? _totalBytes;
                _filesCompleted = evt.FilesTransferred ?? 0;
                _timer?.Stop();
                break;
        }
        _percent = _totalFiles > 0 ? (int)(_filesCompleted * 100.0 / _totalFiles) : 0;
        InvokeAsync(StateHasChanged);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
    };

    public void Dispose()
    {
        _timer?.Stop();
        _timer?.Dispose();
    }
}
```

- [ ] **Step 4: Create LogViewer**

Create `src/ExecRFS/Components/Shared/LogViewer.razor`:

```razor
@inject IJSRuntime JS
@implements IDisposable

<div class="log-viewer" @ref="_logContainer">
    <div class="log-toolbar">
        <span style="color:var(--muted);font-size:10px;text-transform:uppercase;">Live Log</span>
        <div style="margin-left:auto;display:flex;gap:4px;">
            <button class="btn" style="font-size:10px;padding:1px 8px;" @onclick="ToggleAutoScroll">
                Auto-scroll: @(_autoScroll ? "ON" : "OFF")
            </button>
            <button class="btn" style="font-size:10px;padding:1px 8px;" @onclick="Clear">Clear</button>
            <select class="btn" style="font-size:10px;padding:1px 8px;" @onchange="OnFilterChanged">
                <option value="all">All</option>
                <option value="SRV">Server</option>
                <option value="CLI">Client</option>
                <option value="error">Errors</option>
            </select>
        </div>
    </div>
    @foreach (var entry in FilteredEntries)
    {
        <div class="log-line @GetLineClass(entry)">[@entry.Timestamp.ToString("HH:mm:ss")] <span class="@GetSourceClass(entry.Source)">[@entry.Source]</span> @entry.Message</div>
    }
</div>

@code {
    [Parameter] public LogAggregator Aggregator { get; set; } = default!;

    private ElementReference _logContainer;
    private bool _autoScroll = true;
    private string _filter = "all";
    private Action<LogEntry>? _entryHandler;

    private IEnumerable<LogEntry> FilteredEntries => _filter switch
    {
        "SRV" => Aggregator.Entries.Where(e => e.Source == "SRV"),
        "CLI" => Aggregator.Entries.Where(e => e.Source == "CLI"),
        "error" => Aggregator.Entries.Where(e => e.Level == "error"),
        _ => Aggregator.Entries
    };

    protected override void OnInitialized()
    {
        _entryHandler = _ => InvokeAsync(StateHasChanged);
        Aggregator.OnEntry += _entryHandler;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_autoScroll)
        {
            try { await JS.InvokeVoidAsync("scrollToBottom", _logContainer); }
            catch (ObjectDisposedException) { }
        }
    }

    private void ToggleAutoScroll() => _autoScroll = !_autoScroll;
    private void Clear() { Aggregator.Clear(); StateHasChanged(); }
    private void OnFilterChanged(ChangeEventArgs e) => _filter = e.Value?.ToString() ?? "all";

    private static string GetSourceClass(string source) => source switch
    {
        "SRV" => "log-srv",
        "CLI" => "log-cli",
        _ => ""
    };

    private static string GetLineClass(LogEntry entry) => entry.Level switch
    {
        "error" => "log-error",
        "warning" => "log-warning",
        "delete" => "log-delete",
        _ => ""
    };

    public void Dispose()
    {
        if (_entryHandler != null)
            Aggregator.OnEntry -= _entryHandler;
    }
}
```

- [ ] **Step 5: Commit**

```bash
git add src/ExecRFS/Components/Shared/
git commit -m "feat: add shared Blazor components — FolderPicker, PatternList, ProgressBar, LogViewer"
```

---

## Task 13: Blazor Components — ServerPanel and ClientPanel

**Files:**
- Create: `src/ExecRFS/Components/Panels/ServerPanel.razor`
- Create: `src/ExecRFS/Components/Panels/ClientPanel.razor`

- [ ] **Step 1: Create ServerPanel**

Create `src/ExecRFS/Components/Panels/ServerPanel.razor`:

```razor
<div class="panel">
    <div class="panel-header">
        <div style="display:flex;align-items:center;gap:6px;">
            <span class="status-dot @GetStatusDotClass()"></span>
            <span class="panel-title server">SERVER</span>
            <span style="font-size:11px;color:var(--muted);">@ProcessManager.State</span>
        </div>
        <div style="display:flex;gap:4px;">
            @if (ProcessManager.State == SyncInstanceState.Idle || ProcessManager.State == SyncInstanceState.Stopped || ProcessManager.State == SyncInstanceState.Error)
            {
                <button class="btn btn-start" @onclick="Start">Start</button>
            }
            @if (ProcessManager.State == SyncInstanceState.Running || ProcessManager.State == SyncInstanceState.Paused)
            {
                <button class="btn btn-stop" @onclick="Stop">Stop</button>
            }
        </div>
    </div>

    <FolderPicker Label="Sync Folder" @bind-Value="Profile.ServerFolder" />

    <div class="field-grid">
        <div class="field">
            <div class="field-label">Port</div>
            <input type="number" @bind="Profile.ServerPort" min="1" max="65535" />
        </div>
        <FolderPicker Label="Backup Folder" @bind-Value="Profile.ServerBackupFolder" Placeholder="(same as sync)" />
    </div>

    <div class="field-grid">
        <div class="field">
            <div class="field-label">Block Size</div>
            <select @bind="Profile.ServerBlockSize">
                <option value="4096">4 KB</option>
                <option value="65536">64 KB (default)</option>
                <option value="262144">256 KB</option>
                <option value="4194304">4 MB</option>
            </select>
        </div>
        <div class="field">
            <div class="field-label">Max Threads</div>
            <select @bind="Profile.ServerMaxThreads">
                <option value="1">1 (default)</option>
                <option value="2">2</option>
                <option value="4">4</option>
                <option value="8">8</option>
            </select>
        </div>
    </div>

    <PatternList Label="Include Patterns" @bind-Patterns="Profile.IncludePatterns" Type="include" />
    <PatternList Label="Exclude Patterns" @bind-Patterns="Profile.ExcludePatterns" Type="exclude" />
</div>

@code {
    [Parameter] public SyncProfile Profile { get; set; } = new();
    [Parameter] public ProcessManager ProcessManager { get; set; } = default!;

    private void Start() => ProcessManager.Start(Profile);
    private void Stop() => ProcessManager.Stop();

    private string GetStatusDotClass() => ProcessManager.State switch
    {
        SyncInstanceState.Running => "running",
        SyncInstanceState.Paused => "paused",
        SyncInstanceState.Error => "error",
        _ => "idle"
    };
}
```

- [ ] **Step 2: Create ClientPanel**

Create `src/ExecRFS/Components/Panels/ClientPanel.razor`:

```razor
<div class="panel">
    <div class="panel-header">
        <div style="display:flex;align-items:center;gap:6px;">
            <span class="status-dot @GetStatusDotClass()"></span>
            <span class="panel-title client">CLIENT</span>
            <span style="font-size:11px;color:var(--muted);">@GetStatusText()</span>
        </div>
        <div style="display:flex;gap:4px;">
            @if (ProcessManager.State == SyncInstanceState.Idle || ProcessManager.State == SyncInstanceState.Stopped || ProcessManager.State == SyncInstanceState.Error)
            {
                <button class="btn btn-start" @onclick="Start">Start</button>
            }
            @if (ProcessManager.State == SyncInstanceState.Running)
            {
                <button class="btn btn-pause" @onclick="Pause">Pause</button>
            }
            @if (ProcessManager.State == SyncInstanceState.Paused)
            {
                <button class="btn btn-start" @onclick="Resume">Resume</button>
            }
            @if (ProcessManager.State == SyncInstanceState.Running || ProcessManager.State == SyncInstanceState.Paused)
            {
                <button class="btn btn-stop" @onclick="Stop">Stop</button>
            }
        </div>
    </div>

    <div class="field">
        <div class="field-label">Server Host</div>
        <input type="text" @bind="Profile.ClientHost" placeholder="IP or hostname" />
    </div>

    <FolderPicker Label="Sync Folder" @bind-Value="Profile.ClientFolder" />

    <div class="field-grid">
        <div class="field">
            <div class="field-label">Port</div>
            <input type="number" @bind="Profile.ClientPort" min="1" max="65535" />
        </div>
        <FolderPicker Label="Backup Folder" @bind-Value="Profile.ClientBackupFolder" Placeholder="(same as sync)" />
    </div>

    <div class="checkbox-row">
        <label class="checkbox-label">
            <input type="checkbox" @bind="Profile.Bidirectional" />
            Bidirectional
        </label>
        <label class="checkbox-label">
            <input type="checkbox" @bind="Profile.DeleteEnabled" />
            Delete Propagation
        </label>
    </div>

    <div class="field-grid">
        <div class="field">
            <div class="field-label">Block Size</div>
            <select @bind="Profile.ClientBlockSize">
                <option value="4096">4 KB</option>
                <option value="65536">64 KB (default)</option>
                <option value="262144">256 KB</option>
                <option value="4194304">4 MB</option>
            </select>
        </div>
        <div class="field">
            <div class="field-label">Max Threads</div>
            <select @bind="Profile.ClientMaxThreads">
                <option value="1">1 (default)</option>
                <option value="2">2</option>
                <option value="4">4</option>
                <option value="8">8</option>
            </select>
        </div>
    </div>

    <PatternList Label="Include Patterns" @bind-Patterns="Profile.IncludePatterns" Type="include" />
    <PatternList Label="Exclude Patterns" @bind-Patterns="Profile.ExcludePatterns" Type="exclude" />

    @if (ProcessManager.State == SyncInstanceState.Running || ProcessManager.State == SyncInstanceState.Paused)
    {
        <div class="field" style="margin-top:8px;">
            <div class="field-label">Currently Transferring</div>
            <div class="thread-progress">
                @foreach (var tf in _activeTransfers.Values)
                {
                    var pct = tf.TotalBytes > 0 ? (int)(tf.BytesSent * 100 / tf.TotalBytes) : 0;
                    <div class="thread-row">
                        <span style="color:var(--orange);">[T@(tf.Thread)]</span>
                        <span>@tf.Path</span>
                        <span style="color:var(--blue);">@(pct)%</span>
                    </div>
                    <div class="thread-bar"><div class="thread-bar-fill" style="width:@(pct)%"></div></div>
                }
                @if (!_activeTransfers.Any())
                {
                    <div style="color:var(--muted);font-size:10px;">Waiting...</div>
                }
            </div>
        </div>
    }
</div>

@code {
    [Parameter] public SyncProfile Profile { get; set; } = new();
    [Parameter] public ProcessManager ProcessManager { get; set; } = default!;

    private readonly Dictionary<int, TransferInfo> _activeTransfers = new();

    private record TransferInfo(string Path, long BytesSent, long TotalBytes, int Thread);

    protected override void OnInitialized()
    {
        ProcessManager.OnProgress += HandleProgress;
    }

    private void HandleProgress(ProgressEvent evt)
    {
        switch (evt.Event)
        {
            case "file_start":
                var thread = evt.Thread ?? 0;
                _activeTransfers[thread] = new TransferInfo(evt.Path ?? "", 0, evt.Size ?? 0, thread);
                break;
            case "file_progress":
                var t = evt.Thread ?? 0;
                if (_activeTransfers.ContainsKey(t))
                    _activeTransfers[t] = _activeTransfers[t] with { BytesSent = evt.BytesSent ?? 0 };
                break;
            case "file_end":
                _activeTransfers.Remove(evt.Thread ?? 0);
                break;
            case "complete":
                _activeTransfers.Clear();
                break;
        }
        InvokeAsync(StateHasChanged);
    }

    private void Start() => ProcessManager.Start(Profile);
    private void Pause() => ProcessManager.Pause();
    private void Resume() => ProcessManager.Resume();
    private void Stop() => ProcessManager.Stop();

    private string GetStatusDotClass() => ProcessManager.State switch
    {
        SyncInstanceState.Running => "syncing",
        SyncInstanceState.Paused => "paused",
        SyncInstanceState.Error => "error",
        _ => "idle"
    };

    private string GetStatusText() => ProcessManager.State switch
    {
        SyncInstanceState.Running => $"Syncing",
        SyncInstanceState.Paused => "Paused",
        SyncInstanceState.Error => "Error",
        SyncInstanceState.Stopped => "Stopped",
        _ => "Idle"
    };
}
```

- [ ] **Step 3: Verify build**

Run: `dotnet build src/ExecRFS/ExecRFS.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/ExecRFS/Components/Panels/
git commit -m "feat: add ServerPanel and ClientPanel Blazor components"
```

---

## Task 14: ExecRFS Unit Tests

**Files:**
- Create: `tests/ExecRFS.Tests/ExecRFS.Tests.csproj`
- Create: `tests/ExecRFS.Tests/Services/CommandBuilderTests.cs`
- Create: `tests/ExecRFS.Tests/Services/ProfileServiceTests.cs`
- Create: `tests/ExecRFS.Tests/Services/LogAggregatorTests.cs`
- Create: `tests/ExecRFS.Tests/Models/ProgressEventTests.cs`

- [ ] **Step 1: Create test project**

```bash
cd E:\RemoteFileSync
dotnet new xunit --name ExecRFS.Tests --output tests/ExecRFS.Tests --framework net10.0
dotnet sln add tests/ExecRFS.Tests/ExecRFS.Tests.csproj
dotnet add tests/ExecRFS.Tests/ExecRFS.Tests.csproj reference src/ExecRFS/ExecRFS.csproj
```

- [ ] **Step 2: Create CommandBuilderTests**

Create `tests/ExecRFS.Tests/Services/CommandBuilderTests.cs`:

```csharp
using ExecRFS.Models;
using ExecRFS.Services;

namespace ExecRFS.Tests.Services;

public class CommandBuilderTests
{
    [Fact]
    public void Build_ServerMode_GeneratesCorrectArgs()
    {
        var profile = new SyncProfile { ServerFolder = @"D:\Sync", ServerPort = 15782 };
        var cmd = CommandBuilder.Build(profile, isServer: true);
        Assert.Contains("server", cmd);
        Assert.Contains("--folder \"D:\\Sync\"", cmd);
        Assert.Contains("--port 15782", cmd);
        Assert.DoesNotContain("--host", cmd);
        Assert.DoesNotContain("--bidirectional", cmd);
    }

    [Fact]
    public void Build_ClientMode_AllOptions_GeneratesCorrectArgs()
    {
        var profile = new SyncProfile
        {
            ClientHost = "10.0.1.50", ClientFolder = @"C:\Sync", ClientPort = 20000,
            Bidirectional = true, DeleteEnabled = true,
            ClientBlockSize = 262144, ClientMaxThreads = 4,
            ClientBackupFolder = @"C:\Backups",
            IncludePatterns = new() { "*.cs", "*.csproj" },
            ExcludePatterns = new() { "*.tmp" },
            ClientLogFile = @"C:\Logs\sync.log"
        };
        var cmd = CommandBuilder.Build(profile, isServer: false);
        Assert.Contains("client", cmd);
        Assert.Contains("--host \"10.0.1.50\"", cmd);
        Assert.Contains("--bidirectional", cmd);
        Assert.Contains("--delete", cmd);
        Assert.Contains("--block-size 262144", cmd);
        Assert.Contains("--max-threads 4", cmd);
        Assert.Contains("--backup-folder \"C:\\Backups\"", cmd);
        Assert.Contains("--include \"*.cs\"", cmd);
        Assert.Contains("--include \"*.csproj\"", cmd);
        Assert.Contains("--exclude \"*.tmp\"", cmd);
        Assert.Contains("--log \"C:\\Logs\\sync.log\"", cmd);
    }

    [Fact]
    public void Build_DefaultValues_Omitted()
    {
        var profile = new SyncProfile { ServerFolder = @"D:\Sync" };
        var cmd = CommandBuilder.Build(profile, isServer: true);
        Assert.DoesNotContain("--block-size", cmd); // 65536 is default, omitted
        Assert.DoesNotContain("--max-threads", cmd); // 1 is default, omitted
        Assert.DoesNotContain("--backup-folder", cmd); // null, omitted
    }

    [Fact]
    public void BuildForProcess_AppendsJsonProgress()
    {
        var profile = new SyncProfile { ServerFolder = @"D:\Sync" };
        var cmd = CommandBuilder.BuildForProcess(profile, isServer: true);
        Assert.Contains("--json-progress", cmd);
    }
}
```

- [ ] **Step 3: Create ProfileServiceTests**

Create `tests/ExecRFS.Tests/Services/ProfileServiceTests.cs`:

```csharp
using ExecRFS.Models;
using ExecRFS.Services;

namespace ExecRFS.Tests.Services;

public class ProfileServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ProfileService _service;

    public ProfileServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"execrfs_profile_test_{Guid.NewGuid()}");
        _service = new ProfileService(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var profile = new SyncProfile
        {
            Name = "Test Profile",
            ServerFolder = @"D:\Sync",
            ClientHost = "10.0.1.50",
            Bidirectional = true,
            IncludePatterns = new() { "*.cs", "*.csproj" }
        };
        _service.Save(profile);
        var loaded = _service.Load("Test Profile");
        Assert.Equal("Test Profile", loaded.Name);
        Assert.Equal(@"D:\Sync", loaded.ServerFolder);
        Assert.Equal("10.0.1.50", loaded.ClientHost);
        Assert.True(loaded.Bidirectional);
        Assert.Equal(2, loaded.IncludePatterns.Count);
    }

    [Fact]
    public void ListProfiles_ReturnsNames()
    {
        _service.Save(new SyncProfile { Name = "Alpha" });
        _service.Save(new SyncProfile { Name = "Beta" });
        var names = _service.ListProfiles();
        Assert.Equal(2, names.Count);
        Assert.Contains("alpha", names);
        Assert.Contains("beta", names);
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        _service.Save(new SyncProfile { Name = "ToDelete" });
        Assert.Single(_service.ListProfiles());
        _service.Delete("ToDelete");
        Assert.Empty(_service.ListProfiles());
    }

    [Fact]
    public void AutoSave_And_LoadLastSession()
    {
        _service.CurrentProfile = new SyncProfile { Name = "Current", ClientHost = "192.168.1.1" };
        _service.AutoSave();
        var loaded = _service.LoadLastSession();
        Assert.Equal("Current", loaded.Name);
        Assert.Equal("192.168.1.1", loaded.ClientHost);
    }
}
```

- [ ] **Step 4: Create LogAggregatorTests**

Create `tests/ExecRFS.Tests/Services/LogAggregatorTests.cs`:

```csharp
using ExecRFS.Services;

namespace ExecRFS.Tests.Services;

public class LogAggregatorTests
{
    [Fact]
    public void AddLine_MergesSourcesChronologically()
    {
        var agg = new LogAggregator();
        agg.AddLine("SRV", "Server started");
        agg.AddLine("CLI", "Client connected");
        agg.AddLine("SRV", "File received");

        Assert.Equal(3, agg.Entries.Count);
        Assert.Equal("SRV", agg.Entries[0].Source);
        Assert.Equal("CLI", agg.Entries[1].Source);
        Assert.Equal("SRV", agg.Entries[2].Source);
    }

    [Fact]
    public void CircularBuffer_BoundsAt5000()
    {
        var agg = new LogAggregator();
        for (int i = 0; i < 5100; i++)
            agg.AddLine("SRV", $"Line {i}");

        Assert.Equal(5000, agg.Entries.Count);
        Assert.Contains("Line 5099", agg.Entries[^1].Message);
        Assert.Contains("Line 100", agg.Entries[0].Message);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var agg = new LogAggregator();
        agg.AddLine("SRV", "test");
        agg.Clear();
        Assert.Empty(agg.Entries);
    }

    [Fact]
    public void ParseLevel_DetectsErrors()
    {
        var agg = new LogAggregator();
        agg.AddLine("SRV", "[ERR] Something failed");
        Assert.Equal("error", agg.Entries[0].Level);
    }

    [Fact]
    public void OnEntry_FiresForEachLine()
    {
        var agg = new LogAggregator();
        int count = 0;
        agg.OnEntry += _ => count++;
        agg.AddLine("SRV", "test1");
        agg.AddLine("CLI", "test2");
        Assert.Equal(2, count);
    }
}
```

- [ ] **Step 5: Create ProgressEventTests**

Create `tests/ExecRFS.Tests/Models/ProgressEventTests.cs`:

```csharp
using ExecRFS.Models;

namespace ExecRFS.Tests.Models;

public class ProgressEventTests
{
    [Fact]
    public void TryParse_StatusEvent()
    {
        var evt = ProgressEvent.TryParse("{\"event\":\"status\",\"state\":\"connecting\",\"host\":\"10.0.1.50\",\"port\":15782}");
        Assert.NotNull(evt);
        Assert.Equal("status", evt.Event);
        Assert.Equal("connecting", evt.State);
        Assert.Equal("10.0.1.50", evt.Host);
        Assert.Equal(15782, evt.Port);
    }

    [Fact]
    public void TryParse_FileProgressEvent()
    {
        var evt = ProgressEvent.TryParse("{\"event\":\"file_progress\",\"path\":\"docs/report.docx\",\"bytes_sent\":1400000,\"total_bytes\":2100000,\"thread\":1}");
        Assert.NotNull(evt);
        Assert.Equal("file_progress", evt.Event);
        Assert.Equal("docs/report.docx", evt.Path);
        Assert.Equal(1400000, evt.BytesSent);
        Assert.Equal(2100000, evt.TotalBytes);
        Assert.Equal(1, evt.Thread);
    }

    [Fact]
    public void TryParse_CompleteEvent()
    {
        var evt = ProgressEvent.TryParse("{\"event\":\"complete\",\"files_transferred\":10,\"files_deleted\":2,\"bytes\":89700000,\"elapsed_ms\":5200,\"exit_code\":0}");
        Assert.NotNull(evt);
        Assert.Equal(10, evt.FilesTransferred);
        Assert.Equal(2, evt.FilesDeleted);
        Assert.Equal(0, evt.ExitCode);
    }

    [Fact]
    public void TryParse_ErrorEvent()
    {
        var evt = ProgressEvent.TryParse("{\"event\":\"error\",\"message\":\"Connection refused\",\"fatal\":true}");
        Assert.NotNull(evt);
        Assert.Equal("error", evt.Event);
        Assert.Equal("Connection refused", evt.Message);
        Assert.True(evt.Fatal);
    }

    [Fact]
    public void TryParse_InvalidJson_ReturnsNull()
    {
        var evt = ProgressEvent.TryParse("not json at all");
        Assert.Null(evt);
    }

    [Fact]
    public void TryParse_DeleteEvent()
    {
        var evt = ProgressEvent.TryParse("{\"event\":\"delete\",\"path\":\"docs/old.docx\",\"backed_up\":true,\"success\":true}");
        Assert.NotNull(evt);
        Assert.Equal("delete", evt.Event);
        Assert.True(evt.BackedUp);
        Assert.True(evt.Success);
    }
}
```

- [ ] **Step 6: Verify all tests pass**

Run: `dotnet test`
Expected: All tests pass.

- [ ] **Step 7: Commit**

```bash
git add tests/ExecRFS.Tests/
git commit -m "test: add ExecRFS unit tests for CommandBuilder, ProfileService, LogAggregator, ProgressEvent"
```

---

## Task 15: Final Build Verification and Push

- [ ] **Step 1: Build entire solution**

```bash
cd E:\RemoteFileSync
dotnet build
```

Expected: Build succeeded for both RemoteFileSync and ExecRFS.

- [ ] **Step 2: Run all tests**

```bash
dotnet test
```

Expected: All existing tests pass + new JSON progress tests pass.

- [ ] **Step 3: Push the branch**

```bash
git push -u origin feature/execrfs-ui
```

---

## Self-Review Checklist

| Check | Result |
|-------|--------|
| All spec sections covered? | Yes — JSON protocol (T2-5), Scaffold (T6), Models (T7), Services (T8-9), CSS (T10), Layout (T11), Components (T12-13), Tests (T14) |
| No TBD/TODO? | Yes — all code is complete |
| Type names consistent? | `SyncProfile`, `SyncInstanceState`, `ProgressEvent`, `ProcessManager`, `SyncProcesses`, `ProfileService`, `CommandBuilder`, `LogAggregator` — consistent |
| Method signatures match? | `ProcessManager.Start(SyncProfile)` used in panels matches definition in T9. `SyncProcesses.Server`/`.Client` used in MainLayout matches T6 |
| DI consistent? | `SyncProcesses` registered once, injected via `@inject SyncProcesses` — no duplicate registrations |
| Spec requirements covered? | Start/Stop/Pause ✓, Generate CMD (with Copy Server/Client/Both) ✓, Live log (with JS auto-scroll) ✓, Per-thread progress ✓, Profiles (Save/Load/Delete) ✓, Dark theme ✓ |
| Build correctness? | csproj has UseWindowsForms ✓, xmlns:layout declared ✓, FolderPicker uses SelectedPath ✓, PatternList creates new list ✓ |
| Tests complete? | Phase A: JsonProgressWriter (T2), StdinCommandReader (T3). Phase B: CommandBuilder, ProfileService, LogAggregator, ProgressEvent (T14) |

## Review Fixes Applied

| Issue | Fix |
|-------|-----|
| Missing UseWindowsForms in csproj | Added `<UseWindowsForms>true</UseWindowsForms>` + explicit wwwroot content include |
| DI dual-singleton collision | Replaced with `SyncProcesses` holder class registered once |
| Missing xmlns:layout in XAML | Added `xmlns:layout="clr-namespace:ExecRFS.Components.Layout"` |
| Task 5 too vague | Added concrete code for every JSON event insertion point |
| 9 missing ExecRFS tests | Added Task 14 with full test project and 19 test methods |
| FolderBrowserDialog.InitialDirectory | Changed to `dialog.SelectedPath` |
| PatternList mutates [Parameter] | Creates new `List<string>` before invoking callback |
| LogViewer no auto-scroll | Added `IJSRuntime` injection + `scrollToBottom` JS interop in `OnAfterRenderAsync` |
| ProgressBar byte tracking | Added `_bytesForCompletedFiles` accumulator + `_bytesForCurrentFile` per-file |
| CommandPreview missing buttons | Added Copy Server / Copy Client / Copy Both with JS clipboard interop |
| ProfileManager missing Delete | Added Delete button to toolbar with confirmation |
| ProgressBar/LogViewer Dispose | Added `IDisposable` with timer disposal and event unsubscription |
