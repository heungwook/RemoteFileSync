# RemoteFileSync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a single-file .NET 10 CLI tool that synchronizes files between two Windows computers over raw TCP, supporting uni-directional and bi-directional modes with GZip compression and multi-threaded transfers.

**Architecture:** Layered design with Models at the base, Logging as cross-cutting, Sync engine for diffing, Network layer for TCP protocol, Transfer layer for file streaming with compression, and Backup for data safety. The client always computes the sync plan; both sides can send and receive files.

**Tech Stack:** .NET 10, C# 13, xUnit for testing, zero NuGet dependencies in the main project.

---

## File Structure

```
E:\RemoteFileSync\
├── RemoteFileSync.sln
├── src\
│   └── RemoteFileSync\
│       ├── RemoteFileSync.csproj
│       ├── Program.cs
│       ├── Models\
│       │   ├── SyncOptions.cs
│       │   ├── SyncAction.cs
│       │   ├── FileEntry.cs
│       │   └── FileManifest.cs
│       ├── Logging\
│       │   └── SyncLogger.cs
│       ├── Sync\
│       │   ├── FileScanner.cs
│       │   ├── ConflictResolver.cs
│       │   └── SyncEngine.cs
│       ├── Network\
│       │   ├── MessageType.cs
│       │   ├── ProtocolHandler.cs
│       │   ├── SyncServer.cs
│       │   └── SyncClient.cs
│       ├── Transfer\
│       │   ├── CompressionHelper.cs
│       │   └── FileTransfer.cs
│       └── Backup\
│           └── BackupManager.cs
└── tests\
    └── RemoteFileSync.Tests\
        ├── RemoteFileSync.Tests.csproj
        ├── Models\
        │   └── FileEntryTests.cs
        ├── Logging\
        │   └── SyncLoggerTests.cs
        ├── Sync\
        │   ├── FileScannerTests.cs
        │   ├── ConflictResolverTests.cs
        │   └── SyncEngineTests.cs
        ├── Network\
        │   └── ProtocolHandlerTests.cs
        ├── Transfer\
        │   ├── CompressionHelperTests.cs
        │   └── FileTransferTests.cs
        ├── Backup\
        │   └── BackupManagerTests.cs
        ├── Integration\
        │   └── EndToEndTests.cs
        └── CliParserTests.cs
```

---

## Task 1: Project Scaffolding

**Files:**
- Create: `RemoteFileSync.sln`
- Create: `src/RemoteFileSync/RemoteFileSync.csproj`
- Create: `src/RemoteFileSync/Program.cs`
- Create: `tests/RemoteFileSync.Tests/RemoteFileSync.Tests.csproj`

- [ ] **Step 1: Create the solution and project structure**

```bash
cd E:\RemoteFileSync
dotnet new sln --name RemoteFileSync
dotnet new console --name RemoteFileSync --output src/RemoteFileSync --framework net10.0
dotnet new xunit --name RemoteFileSync.Tests --output tests/RemoteFileSync.Tests --framework net10.0
dotnet sln add src/RemoteFileSync/RemoteFileSync.csproj
dotnet sln add tests/RemoteFileSync.Tests/RemoteFileSync.Tests.csproj
dotnet add tests/RemoteFileSync.Tests/RemoteFileSync.Tests.csproj reference src/RemoteFileSync/RemoteFileSync.csproj
```

- [ ] **Step 2: Configure the main project csproj for single-file publish**

Replace the contents of `src/RemoteFileSync/RemoteFileSync.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>RemoteFileSync</RootNamespace>
    <AssemblyName>RemoteFileSync</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>true</SelfContained>
    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Replace the default Program.cs with a placeholder**

Replace `src/RemoteFileSync/Program.cs` with:

```csharp
namespace RemoteFileSync;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("RemoteFileSync v1.0");
        return 0;
    }
}
```

- [ ] **Step 4: Verify the build succeeds**

```bash
cd E:\RemoteFileSync
dotnet build
```

Expected: Build succeeded with 0 errors.

- [ ] **Step 5: Verify tests run**

```bash
dotnet test
```

Expected: The default xUnit test passes (or 0 tests if template is empty).

- [ ] **Step 6: Commit**

```bash
git add src/ tests/ RemoteFileSync.sln
git commit -m "feat: scaffold .NET 10 solution with main project and test project"
git push
```

---

## Task 2: Models — FileEntry, FileManifest, SyncAction, SyncOptions

**Files:**
- Create: `src/RemoteFileSync/Models/FileEntry.cs`
- Create: `src/RemoteFileSync/Models/FileManifest.cs`
- Create: `src/RemoteFileSync/Models/SyncAction.cs`
- Create: `src/RemoteFileSync/Models/SyncOptions.cs`
- Create: `tests/RemoteFileSync.Tests/Models/FileEntryTests.cs`

- [ ] **Step 1: Write the failing tests for FileEntry**

Create `tests/RemoteFileSync.Tests/Models/FileEntryTests.cs`:

```csharp
using RemoteFileSync.Models;

namespace RemoteFileSync.Tests.Models;

public class FileEntryTests
{
    [Fact]
    public void Constructor_SetsPropertiesCorrectly()
    {
        var timestamp = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
        var entry = new FileEntry("docs/report.docx", 1024, timestamp);

        Assert.Equal("docs/report.docx", entry.RelativePath);
        Assert.Equal(1024, entry.FileSize);
        Assert.Equal(timestamp, entry.LastModifiedUtc);
    }

    [Fact]
    public void RelativePath_UsesForwardSlashes()
    {
        var entry = new FileEntry("docs\\sub\\file.txt", 100,
            DateTime.UtcNow);

        Assert.Equal("docs/sub/file.txt", entry.RelativePath);
    }

    [Fact]
    public void Equals_SamePathSizeTimestamp_ReturnsTrue()
    {
        var ts = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
        var a = new FileEntry("file.txt", 100, ts);
        var b = new FileEntry("file.txt", 100, ts);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Equals_DifferentSize_ReturnsFalse()
    {
        var ts = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
        var a = new FileEntry("file.txt", 100, ts);
        var b = new FileEntry("file.txt", 200, ts);

        Assert.NotEqual(a, b);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test --filter "FileEntryTests"
```

Expected: FAIL — `FileEntry` type does not exist.

- [ ] **Step 3: Implement FileEntry**

Create `src/RemoteFileSync/Models/FileEntry.cs`:

```csharp
namespace RemoteFileSync.Models;

public sealed class FileEntry : IEquatable<FileEntry>
{
    public string RelativePath { get; }
    public long FileSize { get; }
    public DateTime LastModifiedUtc { get; }

    public FileEntry(string relativePath, long fileSize, DateTime lastModifiedUtc)
    {
        RelativePath = relativePath.Replace('\\', '/');
        FileSize = fileSize;
        LastModifiedUtc = lastModifiedUtc;
    }

    public bool Equals(FileEntry? other)
    {
        if (other is null) return false;
        return RelativePath == other.RelativePath
            && FileSize == other.FileSize
            && LastModifiedUtc == other.LastModifiedUtc;
    }

    public override bool Equals(object? obj) => Equals(obj as FileEntry);

    public override int GetHashCode() =>
        HashCode.Combine(RelativePath, FileSize, LastModifiedUtc);

    public override string ToString() =>
        $"{RelativePath} ({FileSize} bytes, {LastModifiedUtc:u})";
}
```

- [ ] **Step 4: Implement FileManifest**

Create `src/RemoteFileSync/Models/FileManifest.cs`:

```csharp
namespace RemoteFileSync.Models;

public sealed class FileManifest
{
    private readonly Dictionary<string, FileEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<FileEntry> Entries => _entries.Values;
    public int Count => _entries.Count;

    public void Add(FileEntry entry)
    {
        _entries[entry.RelativePath] = entry;
    }

    public FileEntry? Get(string relativePath)
    {
        _entries.TryGetValue(relativePath.Replace('\\', '/'), out var entry);
        return entry;
    }

    public bool Contains(string relativePath) =>
        _entries.ContainsKey(relativePath.Replace('\\', '/'));

    public IEnumerable<string> AllPaths => _entries.Keys;
}
```

- [ ] **Step 5: Implement SyncAction**

Create `src/RemoteFileSync/Models/SyncAction.cs`:

```csharp
namespace RemoteFileSync.Models;

public enum SyncActionType : byte
{
    SendToServer = 0,
    SendToClient = 1,
    ClientOnly = 2,
    ServerOnly = 3,
    Skip = 4
}

public sealed class SyncPlanEntry
{
    public SyncActionType Action { get; }
    public string RelativePath { get; }

    public SyncPlanEntry(SyncActionType action, string relativePath)
    {
        Action = action;
        RelativePath = relativePath;
    }

    public override string ToString() => $"{Action}: {RelativePath}";
}
```

- [ ] **Step 6: Implement SyncOptions**

Create `src/RemoteFileSync/Models/SyncOptions.cs`:

```csharp
namespace RemoteFileSync.Models;

public sealed class SyncOptions
{
    public bool IsServer { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 15782;
    public string Folder { get; set; } = string.Empty;
    public bool Bidirectional { get; set; }
    public string? BackupFolder { get; set; }
    public List<string> IncludePatterns { get; set; } = new();
    public List<string> ExcludePatterns { get; set; } = new();
    public int BlockSize { get; set; } = 65536;
    public int MaxThreads { get; set; } = 1;
    public bool Verbose { get; set; }
    public string? LogFile { get; set; }

    public string EffectiveBackupFolder => BackupFolder ?? Folder;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Folder))
            throw new ArgumentException("--folder is required.");

        if (!Directory.Exists(Folder))
            throw new ArgumentException($"Folder does not exist: {Folder}");

        if (!IsServer && string.IsNullOrWhiteSpace(Host))
            throw new ArgumentException("--host is required in client mode.");

        if (Port < 1 || Port > 65535)
            throw new ArgumentException($"--port must be 1-65535, got {Port}.");

        const int minBlock = 4096;
        const int maxBlock = 4 * 1024 * 1024;
        if (BlockSize < minBlock)
        {
            Console.Error.WriteLine($"Warning: --block-size {BlockSize} clamped to minimum {minBlock}.");
            BlockSize = minBlock;
        }
        if (BlockSize > maxBlock)
        {
            Console.Error.WriteLine($"Warning: --block-size {BlockSize} clamped to maximum {maxBlock}.");
            BlockSize = maxBlock;
        }

        if (MaxThreads < 1)
            MaxThreads = 1;
    }
}
```

- [ ] **Step 7: Run all tests to verify they pass**

```bash
dotnet test
```

Expected: All `FileEntryTests` pass.

- [ ] **Step 8: Commit**

```bash
git add src/RemoteFileSync/Models/ tests/RemoteFileSync.Tests/Models/
git commit -m "feat: add core models — FileEntry, FileManifest, SyncAction, SyncOptions"
git push
```

---

## Task 3: SyncLogger — Logging Infrastructure

**Files:**
- Create: `src/RemoteFileSync/Logging/SyncLogger.cs`
- Create: `tests/RemoteFileSync.Tests/Logging/SyncLoggerTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/RemoteFileSync.Tests/Logging/SyncLoggerTests.cs`:

```csharp
using RemoteFileSync.Logging;

namespace RemoteFileSync.Tests.Logging;

public class SyncLoggerTests : IDisposable
{
    private readonly StringWriter _consoleOut = new();
    private readonly TextWriter _originalOut;

    public SyncLoggerTests()
    {
        _originalOut = Console.Out;
        Console.SetOut(_consoleOut);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        _consoleOut.Dispose();
    }

    [Fact]
    public void Error_AlwaysShownOnConsole()
    {
        var logger = new SyncLogger(verbose: false, logFile: null);
        logger.Error("something broke");

        var output = _consoleOut.ToString();
        Assert.Contains("something broke", output);
    }

    [Fact]
    public void Info_HiddenWhenNotVerbose()
    {
        var logger = new SyncLogger(verbose: false, logFile: null);
        logger.Info("detailed info");

        var output = _consoleOut.ToString();
        Assert.DoesNotContain("detailed info", output);
    }

    [Fact]
    public void Info_ShownWhenVerbose()
    {
        var logger = new SyncLogger(verbose: true, logFile: null);
        logger.Info("detailed info");

        var output = _consoleOut.ToString();
        Assert.Contains("detailed info", output);
    }

    [Fact]
    public void Debug_HiddenWhenNotVerbose()
    {
        var logger = new SyncLogger(verbose: false, logFile: null);
        logger.Debug("debug stuff");

        var output = _consoleOut.ToString();
        Assert.DoesNotContain("debug stuff", output);
    }

    [Fact]
    public void Debug_ShownWhenVerbose()
    {
        var logger = new SyncLogger(verbose: true, logFile: null);
        logger.Debug("debug stuff");

        var output = _consoleOut.ToString();
        Assert.Contains("debug stuff", output);
    }

    [Fact]
    public void Summary_AlwaysShown()
    {
        var logger = new SyncLogger(verbose: false, logFile: null);
        logger.Summary("sync complete");

        var output = _consoleOut.ToString();
        Assert.Contains("sync complete", output);
    }

    [Fact]
    public void LogFile_WritesAllLevels()
    {
        var logPath = Path.Combine(Path.GetTempPath(),
            $"synclogger_test_{Guid.NewGuid()}.log");
        try
        {
            var logger = new SyncLogger(verbose: false, logFile: logPath);
            logger.Info("info line");
            logger.Debug("debug line");
            logger.Error("error line");
            logger.Dispose();

            var content = File.ReadAllText(logPath);
            Assert.Contains("info line", content);
            Assert.Contains("debug line", content);
            Assert.Contains("error line", content);
        }
        finally
        {
            if (File.Exists(logPath)) File.Delete(logPath);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test --filter "SyncLoggerTests"
```

Expected: FAIL — `SyncLogger` type does not exist.

- [ ] **Step 3: Implement SyncLogger**

Create `src/RemoteFileSync/Logging/SyncLogger.cs`:

```csharp
namespace RemoteFileSync.Logging;

public sealed class SyncLogger : IDisposable
{
    private readonly bool _verbose;
    private readonly StreamWriter? _logWriter;
    private readonly object _lock = new();

    public SyncLogger(bool verbose, string? logFile)
    {
        _verbose = verbose;
        if (!string.IsNullOrWhiteSpace(logFile))
        {
            var dir = Path.GetDirectoryName(logFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            _logWriter = new StreamWriter(logFile, append: true) { AutoFlush = true };
        }
    }

    public void Error(string message) => Log("ERR", message, consoleAlways: true);
    public void Warning(string message) => Log("WRN", message, consoleAlways: true);
    public void Info(string message) => Log("INF", message, consoleAlways: false);
    public void Debug(string message) => Log("DBG", message, consoleAlways: false);
    public void Summary(string message) => Log("INF", message, consoleAlways: true);

    private void Log(string level, string message, bool consoleAlways)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var fullTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var consoleLine = $"[{timestamp}] {message}";
        var fileLine = $"[{fullTimestamp}] [{level}] {message}";

        lock (_lock)
        {
            if (consoleAlways || _verbose)
                Console.WriteLine(consoleLine);

            _logWriter?.WriteLine(fileLine);
        }
    }

    public void Dispose()
    {
        _logWriter?.Flush();
        _logWriter?.Dispose();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test --filter "SyncLoggerTests"
```

Expected: All 7 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteFileSync/Logging/ tests/RemoteFileSync.Tests/Logging/
git commit -m "feat: add SyncLogger with console and file output"
git push
```

---

## Task 4: CLI Argument Parser

**Files:**
- Modify: `src/RemoteFileSync/Program.cs`
- Create: `tests/RemoteFileSync.Tests/CliParserTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/RemoteFileSync.Tests/CliParserTests.cs`:

```csharp
using RemoteFileSync.Models;

namespace RemoteFileSync.Tests;

public class CliParserTests
{
    [Fact]
    public void ParseArgs_ServerMode_MinimalArgs()
    {
        var args = new[] { "server", "--folder", @"C:\Sync" };
        var result = Program.ParseArgs(args);

        Assert.True(result.IsServer);
        Assert.Equal(@"C:\Sync", result.Folder);
        Assert.Equal(15782, result.Port);
    }

    [Fact]
    public void ParseArgs_ClientMode_AllOptions()
    {
        var args = new[]
        {
            "client", "--host", "192.168.1.100", "--port", "9999",
            "--folder", @"C:\Sync", "--bidirectional",
            "--include", "*.docx", "--include", "*.xlsx",
            "--exclude", "*.tmp",
            "--block-size", "131072", "--max-threads", "4",
            "--verbose", "--log", @"C:\Logs\sync.log",
            "--backup-folder", @"C:\Backup"
        };
        var result = Program.ParseArgs(args);

        Assert.False(result.IsServer);
        Assert.Equal("192.168.1.100", result.Host);
        Assert.Equal(9999, result.Port);
        Assert.Equal(@"C:\Sync", result.Folder);
        Assert.True(result.Bidirectional);
        Assert.Equal(new[] { "*.docx", "*.xlsx" }, result.IncludePatterns);
        Assert.Equal(new[] { "*.tmp" }, result.ExcludePatterns);
        Assert.Equal(131072, result.BlockSize);
        Assert.Equal(4, result.MaxThreads);
        Assert.True(result.Verbose);
        Assert.Equal(@"C:\Logs\sync.log", result.LogFile);
        Assert.Equal(@"C:\Backup", result.BackupFolder);
    }

    [Fact]
    public void ParseArgs_ShortFlags()
    {
        var args = new[] { "client", "-h", "10.0.0.1", "-p", "8080",
            "-f", @"C:\Data", "-b", "-v", "-t", "2", "-bs", "4096" };
        var result = Program.ParseArgs(args);

        Assert.Equal("10.0.0.1", result.Host);
        Assert.Equal(8080, result.Port);
        Assert.Equal(@"C:\Data", result.Folder);
        Assert.True(result.Bidirectional);
        Assert.True(result.Verbose);
        Assert.Equal(2, result.MaxThreads);
        Assert.Equal(4096, result.BlockSize);
    }

    [Fact]
    public void ParseArgs_NoArgs_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Program.ParseArgs(Array.Empty<string>()));
    }

    [Fact]
    public void ParseArgs_InvalidMode_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Program.ParseArgs(new[] { "watch" }));
    }

    [Fact]
    public void ParseArgs_ServerMode_IgnoresHost()
    {
        var args = new[] { "server", "--folder", @"C:\Sync", "--host", "ignored" };
        var result = Program.ParseArgs(args);

        Assert.True(result.IsServer);
        Assert.Equal("ignored", result.Host); // stored but not validated in ParseArgs
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test --filter "CliParserTests"
```

Expected: FAIL — `Program.ParseArgs` method does not exist.

- [ ] **Step 3: Implement the CLI parser in Program.cs**

Replace `src/RemoteFileSync/Program.cs` with:

```csharp
using RemoteFileSync.Models;
using RemoteFileSync.Logging;

namespace RemoteFileSync;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        SyncOptions options;
        try
        {
            options = ParseArgs(args);
            options.Validate();
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            PrintUsage();
            return 3;
        }

        using var logger = new SyncLogger(options.Verbose, options.LogFile);
        logger.Summary($"RemoteFileSync v1.0 — {(options.IsServer ? "Server" : "Client")} mode");

        // TODO: Wire up server/client orchestration in a later task
        return 0;
    }

    public static SyncOptions ParseArgs(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("No arguments provided. Use 'server' or 'client' as the first argument.");

        var mode = args[0].ToLowerInvariant();
        if (mode != "server" && mode != "client")
            throw new ArgumentException($"Invalid mode '{args[0]}'. Use 'server' or 'client'.");

        var options = new SyncOptions { IsServer = mode == "server" };

        for (int i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--host" or "-h":
                    options.Host = args[++i];
                    break;
                case "--port" or "-p":
                    options.Port = int.Parse(args[++i]);
                    break;
                case "--folder" or "-f":
                    options.Folder = args[++i];
                    break;
                case "--bidirectional" or "-b":
                    options.Bidirectional = true;
                    break;
                case "--backup-folder":
                    options.BackupFolder = args[++i];
                    break;
                case "--include":
                    options.IncludePatterns.Add(args[++i]);
                    break;
                case "--exclude":
                    options.ExcludePatterns.Add(args[++i]);
                    break;
                case "--block-size" or "-bs":
                    options.BlockSize = int.Parse(args[++i]);
                    break;
                case "--max-threads" or "-t":
                    options.MaxThreads = int.Parse(args[++i]);
                    break;
                case "--verbose" or "-v":
                    options.Verbose = true;
                    break;
                case "--log" or "-l":
                    options.LogFile = args[++i];
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {arg}");
            }
        }

        return options;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("Usage: RemoteFileSync.exe <server|client> [options]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Options:");
        Console.Error.WriteLine("  --host, -h <addr>       Server hostname/IP (client only)");
        Console.Error.WriteLine("  --port, -p <port>       TCP port (default: 15782)");
        Console.Error.WriteLine("  --folder, -f <path>     Local sync folder (required)");
        Console.Error.WriteLine("  --bidirectional, -b     Enable bi-directional sync");
        Console.Error.WriteLine("  --backup-folder <path>  Backup folder (default: sync folder)");
        Console.Error.WriteLine("  --include <pattern>     Glob include pattern (repeatable)");
        Console.Error.WriteLine("  --exclude <pattern>     Glob exclude pattern (repeatable)");
        Console.Error.WriteLine("  --block-size, -bs <n>   Transfer block size in bytes (default: 65536)");
        Console.Error.WriteLine("  --max-threads, -t <n>   Max concurrent transfers (default: 1)");
        Console.Error.WriteLine("  --verbose, -v           Verbose console output");
        Console.Error.WriteLine("  --log, -l <path>        Log file path");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test --filter "CliParserTests"
```

Expected: All 6 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteFileSync/Program.cs tests/RemoteFileSync.Tests/CliParserTests.cs
git commit -m "feat: add CLI argument parser with short and long option support"
git push
```

---

## Task 5: FileScanner — Directory Scanning with Glob Filtering

**Files:**
- Create: `src/RemoteFileSync/Sync/FileScanner.cs`
- Create: `tests/RemoteFileSync.Tests/Sync/FileScannerTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/RemoteFileSync.Tests/Sync/FileScannerTests.cs`:

```csharp
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class FileScannerTests : IDisposable
{
    private readonly string _testDir;

    public FileScannerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"rfs_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    private void CreateFile(string relativePath, string content = "hello")
    {
        var fullPath = Path.Combine(_testDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    [Fact]
    public void Scan_EmptyFolder_ReturnsEmptyManifest()
    {
        var scanner = new FileScanner(_testDir, new(), new());
        var manifest = scanner.Scan();

        Assert.Equal(0, manifest.Count);
    }

    [Fact]
    public void Scan_SingleFile_ReturnsOneEntry()
    {
        CreateFile("file.txt");

        var scanner = new FileScanner(_testDir, new(), new());
        var manifest = scanner.Scan();

        Assert.Equal(1, manifest.Count);
        Assert.True(manifest.Contains("file.txt"));
    }

    [Fact]
    public void Scan_NestedFolders_ReturnsAllFiles()
    {
        CreateFile("a.txt");
        CreateFile("sub/b.txt");
        CreateFile("sub/deep/c.txt");

        var scanner = new FileScanner(_testDir, new(), new());
        var manifest = scanner.Scan();

        Assert.Equal(3, manifest.Count);
        Assert.True(manifest.Contains("sub/b.txt"));
        Assert.True(manifest.Contains("sub/deep/c.txt"));
    }

    [Fact]
    public void Scan_IncludePattern_FiltersCorrectly()
    {
        CreateFile("doc.docx");
        CreateFile("image.png");
        CreateFile("data.csv");

        var scanner = new FileScanner(_testDir,
            include: new List<string> { "*.docx" },
            exclude: new());
        var manifest = scanner.Scan();

        Assert.Equal(1, manifest.Count);
        Assert.True(manifest.Contains("doc.docx"));
    }

    [Fact]
    public void Scan_ExcludePattern_FiltersCorrectly()
    {
        CreateFile("doc.docx");
        CreateFile("temp.tmp");
        CreateFile("backup.bak");

        var scanner = new FileScanner(_testDir,
            include: new(),
            exclude: new List<string> { "*.tmp", "*.bak" });
        var manifest = scanner.Scan();

        Assert.Equal(1, manifest.Count);
        Assert.True(manifest.Contains("doc.docx"));
    }

    [Fact]
    public void Scan_IncludeAndExclude_IncludeFirst()
    {
        CreateFile("report.docx");
        CreateFile("draft.docx");
        CreateFile("image.png");

        var scanner = new FileScanner(_testDir,
            include: new List<string> { "*.docx" },
            exclude: new List<string> { "draft*" });
        var manifest = scanner.Scan();

        Assert.Equal(1, manifest.Count);
        Assert.True(manifest.Contains("report.docx"));
    }

    [Fact]
    public void Scan_FileEntry_HasCorrectSize()
    {
        CreateFile("sized.txt", "12345");

        var scanner = new FileScanner(_testDir, new(), new());
        var manifest = scanner.Scan();
        var entry = manifest.Get("sized.txt");

        Assert.NotNull(entry);
        Assert.Equal(5, entry.FileSize);
    }

    [Fact]
    public void Scan_FileEntry_HasUtcTimestamp()
    {
        CreateFile("ts.txt");

        var scanner = new FileScanner(_testDir, new(), new());
        var manifest = scanner.Scan();
        var entry = manifest.Get("ts.txt");

        Assert.NotNull(entry);
        Assert.Equal(DateTimeKind.Utc, entry.LastModifiedUtc.Kind);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test --filter "FileScannerTests"
```

Expected: FAIL — `FileScanner` type does not exist.

- [ ] **Step 3: Implement FileScanner**

Create `src/RemoteFileSync/Sync/FileScanner.cs`:

```csharp
using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

public sealed class FileScanner
{
    private readonly string _rootPath;
    private readonly List<string> _includePatterns;
    private readonly List<string> _excludePatterns;

    public FileScanner(string rootPath, List<string> include, List<string> exclude)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _includePatterns = include;
        _excludePatterns = exclude;
    }

    public FileManifest Scan()
    {
        var manifest = new FileManifest();
        if (!Directory.Exists(_rootPath))
            return manifest;

        foreach (var fullPath in Directory.EnumerateFiles(_rootPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(_rootPath, fullPath).Replace('\\', '/');

            if (!MatchesFilters(relativePath))
                continue;

            var info = new FileInfo(fullPath);
            var entry = new FileEntry(
                relativePath,
                info.Length,
                info.LastWriteTimeUtc);

            manifest.Add(entry);
        }

        return manifest;
    }

    private bool MatchesFilters(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);

        // If include patterns specified, file must match at least one
        if (_includePatterns.Count > 0)
        {
            bool included = false;
            foreach (var pattern in _includePatterns)
            {
                if (GlobMatch(fileName, pattern))
                {
                    included = true;
                    break;
                }
            }
            if (!included) return false;
        }

        // If exclude patterns specified, file must not match any
        foreach (var pattern in _excludePatterns)
        {
            if (GlobMatch(fileName, pattern))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Simple glob matching supporting * and ? wildcards.
    /// </summary>
    public static bool GlobMatch(string input, string pattern)
    {
        int i = 0, p = 0;
        int starI = -1, starP = -1;

        while (i < input.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(input[i])))
            {
                i++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starI = i;
                starP = p;
                p++;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                starI++;
                i = starI;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;

        return p == pattern.Length;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test --filter "FileScannerTests"
```

Expected: All 8 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteFileSync/Sync/FileScanner.cs tests/RemoteFileSync.Tests/Sync/FileScannerTests.cs
git commit -m "feat: add FileScanner with glob include/exclude filtering"
git push
```

---

## Task 6: ConflictResolver — Timestamp & Size Comparison

**Files:**
- Create: `src/RemoteFileSync/Sync/ConflictResolver.cs`
- Create: `tests/RemoteFileSync.Tests/Sync/ConflictResolverTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/RemoteFileSync.Tests/Sync/ConflictResolverTests.cs`:

```csharp
using RemoteFileSync.Models;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class ConflictResolverTests
{
    private static readonly DateTime BaseTime =
        new(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SameTimestampAndSize_ReturnsSkip()
    {
        var client = new FileEntry("f.txt", 100, BaseTime);
        var server = new FileEntry("f.txt", 100, BaseTime);

        var result = ConflictResolver.Resolve(client, server);
        Assert.Equal(SyncActionType.Skip, result);
    }

    [Fact]
    public void TimestampWithin2Seconds_SameSize_ReturnsSkip()
    {
        var client = new FileEntry("f.txt", 100, BaseTime);
        var server = new FileEntry("f.txt", 100, BaseTime.AddSeconds(1.5));

        var result = ConflictResolver.Resolve(client, server);
        Assert.Equal(SyncActionType.Skip, result);
    }

    [Fact]
    public void ClientNewer_ReturnsSendToServer()
    {
        var client = new FileEntry("f.txt", 100, BaseTime.AddMinutes(5));
        var server = new FileEntry("f.txt", 100, BaseTime);

        var result = ConflictResolver.Resolve(client, server);
        Assert.Equal(SyncActionType.SendToServer, result);
    }

    [Fact]
    public void ServerNewer_ReturnsSendToClient()
    {
        var client = new FileEntry("f.txt", 100, BaseTime);
        var server = new FileEntry("f.txt", 100, BaseTime.AddMinutes(5));

        var result = ConflictResolver.Resolve(client, server);
        Assert.Equal(SyncActionType.SendToClient, result);
    }

    [Fact]
    public void SameTimestamp_LargerClient_ReturnsSendToServer()
    {
        var client = new FileEntry("f.txt", 200, BaseTime);
        var server = new FileEntry("f.txt", 100, BaseTime);

        var result = ConflictResolver.Resolve(client, server);
        Assert.Equal(SyncActionType.SendToServer, result);
    }

    [Fact]
    public void SameTimestamp_LargerServer_ReturnsSendToClient()
    {
        var client = new FileEntry("f.txt", 100, BaseTime);
        var server = new FileEntry("f.txt", 200, BaseTime);

        var result = ConflictResolver.Resolve(client, server);
        Assert.Equal(SyncActionType.SendToClient, result);
    }

    [Fact]
    public void Tolerance_JustOver2Seconds_NotSkipped()
    {
        var client = new FileEntry("f.txt", 100, BaseTime);
        var server = new FileEntry("f.txt", 100, BaseTime.AddSeconds(2.5));

        var result = ConflictResolver.Resolve(client, server);
        // Server is newer by 2.5 seconds, outside tolerance
        Assert.Equal(SyncActionType.SendToClient, result);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test --filter "ConflictResolverTests"
```

Expected: FAIL — `ConflictResolver` type does not exist.

- [ ] **Step 3: Implement ConflictResolver**

Create `src/RemoteFileSync/Sync/ConflictResolver.cs`:

```csharp
using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

public static class ConflictResolver
{
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    public static SyncActionType Resolve(FileEntry clientEntry, FileEntry serverEntry)
    {
        var timeDiff = clientEntry.LastModifiedUtc - serverEntry.LastModifiedUtc;

        // Same timestamp (within tolerance) and same size → skip
        if (Math.Abs(timeDiff.TotalSeconds) <= TimestampTolerance.TotalSeconds
            && clientEntry.FileSize == serverEntry.FileSize)
        {
            return SyncActionType.Skip;
        }

        // Different timestamps → newer wins
        if (Math.Abs(timeDiff.TotalSeconds) > TimestampTolerance.TotalSeconds)
        {
            return timeDiff.TotalSeconds > 0
                ? SyncActionType.SendToServer   // client is newer
                : SyncActionType.SendToClient;  // server is newer
        }

        // Same timestamp (within tolerance), different size → larger wins
        if (clientEntry.FileSize > serverEntry.FileSize)
            return SyncActionType.SendToServer;
        if (serverEntry.FileSize > clientEntry.FileSize)
            return SyncActionType.SendToClient;

        return SyncActionType.Skip;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test --filter "ConflictResolverTests"
```

Expected: All 7 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteFileSync/Sync/ConflictResolver.cs tests/RemoteFileSync.Tests/Sync/ConflictResolverTests.cs
git commit -m "feat: add ConflictResolver with timestamp tolerance and size tie-break"
git push
```

---

## Task 7: SyncEngine — Manifest Diffing and Plan Generation

**Files:**
- Create: `src/RemoteFileSync/Sync/SyncEngine.cs`
- Create: `tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs`:

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

    [Fact]
    public void BothEmpty_EmptyPlan()
    {
        var plan = SyncEngine.ComputePlan(new FileManifest(), new FileManifest(),
            bidirectional: true);
        Assert.Empty(plan);
    }

    [Fact]
    public void IdenticalFiles_AllSkipped()
    {
        var client = MakeManifest(new FileEntry("a.txt", 100, T1));
        var server = MakeManifest(new FileEntry("a.txt", 100, T1));

        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true);
        Assert.All(plan, p => Assert.Equal(SyncActionType.Skip, p.Action));
    }

    [Fact]
    public void ClientOnly_Unidirectional_ProducesClientOnlyAction()
    {
        var client = MakeManifest(new FileEntry("new.txt", 50, T1));
        var server = new FileManifest();

        var plan = SyncEngine.ComputePlan(client, server, bidirectional: false);

        Assert.Single(plan);
        Assert.Equal(SyncActionType.ClientOnly, plan[0].Action);
        Assert.Equal("new.txt", plan[0].RelativePath);
    }

    [Fact]
    public void ServerOnly_Unidirectional_Ignored()
    {
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("only-server.txt", 50, T1));

        var plan = SyncEngine.ComputePlan(client, server, bidirectional: false);

        Assert.Empty(plan.Where(p => p.Action != SyncActionType.Skip));
    }

    [Fact]
    public void ServerOnly_Bidirectional_ProducesServerOnlyAction()
    {
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("only-server.txt", 50, T1));

        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true);

        Assert.Single(plan);
        Assert.Equal(SyncActionType.ServerOnly, plan[0].Action);
    }

    [Fact]
    public void ClientNewer_SendToServer()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T2));
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));

        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true);

        Assert.Single(plan);
        Assert.Equal(SyncActionType.SendToServer, plan[0].Action);
    }

    [Fact]
    public void ServerNewer_SendToClient()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = MakeManifest(new FileEntry("f.txt", 100, T2));

        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true);

        Assert.Single(plan);
        Assert.Equal(SyncActionType.SendToClient, plan[0].Action);
    }

    [Fact]
    public void MixedScenario_CorrectPlan()
    {
        var client = MakeManifest(
            new FileEntry("same.txt", 100, T1),
            new FileEntry("client-newer.txt", 100, T2),
            new FileEntry("client-only.txt", 50, T1));
        var server = MakeManifest(
            new FileEntry("same.txt", 100, T1),
            new FileEntry("client-newer.txt", 100, T1),
            new FileEntry("server-only.txt", 50, T1));

        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true);

        var actions = plan.ToDictionary(p => p.RelativePath, p => p.Action);
        Assert.Equal(SyncActionType.Skip, actions["same.txt"]);
        Assert.Equal(SyncActionType.SendToServer, actions["client-newer.txt"]);
        Assert.Equal(SyncActionType.ClientOnly, actions["client-only.txt"]);
        Assert.Equal(SyncActionType.ServerOnly, actions["server-only.txt"]);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test --filter "SyncEngineTests"
```

Expected: FAIL — `SyncEngine` type does not exist.

- [ ] **Step 3: Implement SyncEngine**

Create `src/RemoteFileSync/Sync/SyncEngine.cs`:

```csharp
using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

public static class SyncEngine
{
    public static List<SyncPlanEntry> ComputePlan(
        FileManifest clientManifest,
        FileManifest serverManifest,
        bool bidirectional)
    {
        var plan = new List<SyncPlanEntry>();
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in clientManifest.AllPaths)
            allPaths.Add(path);
        foreach (var path in serverManifest.AllPaths)
            allPaths.Add(path);

        foreach (var path in allPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var clientEntry = clientManifest.Get(path);
            var serverEntry = serverManifest.Get(path);

            if (clientEntry != null && serverEntry != null)
            {
                // Both sides have the file
                var action = ConflictResolver.Resolve(clientEntry, serverEntry);
                plan.Add(new SyncPlanEntry(action, path));
            }
            else if (clientEntry != null && serverEntry == null)
            {
                // Client only
                plan.Add(new SyncPlanEntry(SyncActionType.ClientOnly, path));
            }
            else if (clientEntry == null && serverEntry != null)
            {
                // Server only
                if (bidirectional)
                    plan.Add(new SyncPlanEntry(SyncActionType.ServerOnly, path));
                // In uni-directional mode, server-only files are ignored
            }
        }

        return plan;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test --filter "SyncEngineTests"
```

Expected: All 8 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteFileSync/Sync/SyncEngine.cs tests/RemoteFileSync.Tests/Sync/SyncEngineTests.cs
git commit -m "feat: add SyncEngine for manifest diffing and sync plan generation"
git push
```

---

## Task 8: CompressionHelper — GZip and Compressed-Format Detection

**Files:**
- Create: `src/RemoteFileSync/Transfer/CompressionHelper.cs`
- Create: `tests/RemoteFileSync.Tests/Transfer/CompressionHelperTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/RemoteFileSync.Tests/Transfer/CompressionHelperTests.cs`:

```csharp
using RemoteFileSync.Transfer;

namespace RemoteFileSync.Tests.Transfer;

public class CompressionHelperTests : IDisposable
{
    private readonly string _tempDir;

    public CompressionHelperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rfs_comp_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Theory]
    [InlineData(".zip", true)]
    [InlineData(".gz", true)]
    [InlineData(".7z", true)]
    [InlineData(".jpg", true)]
    [InlineData(".png", true)]
    [InlineData(".mp4", true)]
    [InlineData(".mp3", true)]
    [InlineData(".docx", true)]
    [InlineData(".pdf", true)]
    [InlineData(".txt", false)]
    [InlineData(".csv", false)]
    [InlineData(".xml", false)]
    [InlineData(".cs", false)]
    [InlineData(".html", false)]
    [InlineData("", false)]
    public void IsAlreadyCompressed_DetectsCorrectly(string extension, bool expected)
    {
        Assert.Equal(expected, CompressionHelper.IsAlreadyCompressed(extension));
    }

    [Fact]
    public void CompressFile_ProducesSmallerOutput_ForTextFile()
    {
        var source = Path.Combine(_tempDir, "source.txt");
        var compressed = Path.Combine(_tempDir, "source.txt.gz");

        // Write a repetitive string that compresses well
        File.WriteAllText(source, new string('A', 10000));

        CompressionHelper.CompressFile(source, compressed);

        Assert.True(File.Exists(compressed));
        Assert.True(new FileInfo(compressed).Length < new FileInfo(source).Length);
    }

    [Fact]
    public void DecompressFile_RestoresOriginalContent()
    {
        var source = Path.Combine(_tempDir, "original.txt");
        var compressed = Path.Combine(_tempDir, "compressed.gz");
        var decompressed = Path.Combine(_tempDir, "restored.txt");

        var content = "The quick brown fox jumps over the lazy dog. " +
                      string.Join("", Enumerable.Range(0, 100).Select(i => $"Line {i}\n"));
        File.WriteAllText(source, content);

        CompressionHelper.CompressFile(source, compressed);
        CompressionHelper.DecompressFile(compressed, decompressed);

        Assert.Equal(content, File.ReadAllText(decompressed));
    }

    [Fact]
    public void ComputeSha256_ReturnsConsistentHash()
    {
        var file = Path.Combine(_tempDir, "hashme.txt");
        File.WriteAllText(file, "deterministic content");

        var hash1 = CompressionHelper.ComputeSha256(file);
        var hash2 = CompressionHelper.ComputeSha256(file);

        Assert.Equal(32, hash1.Length); // SHA256 = 32 bytes
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeSha256_DifferentContent_DifferentHash()
    {
        var file1 = Path.Combine(_tempDir, "a.txt");
        var file2 = Path.Combine(_tempDir, "b.txt");
        File.WriteAllText(file1, "content A");
        File.WriteAllText(file2, "content B");

        var hash1 = CompressionHelper.ComputeSha256(file1);
        var hash2 = CompressionHelper.ComputeSha256(file2);

        Assert.NotEqual(hash1, hash2);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test --filter "CompressionHelperTests"
```

Expected: FAIL — `CompressionHelper` type does not exist.

- [ ] **Step 3: Implement CompressionHelper**

Create `src/RemoteFileSync/Transfer/CompressionHelper.cs`:

```csharp
using System.IO.Compression;
using System.Security.Cryptography;

namespace RemoteFileSync.Transfer;

public static class CompressionHelper
{
    private static readonly HashSet<string> CompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Archives
        ".zip", ".gz", ".7z", ".rar", ".tgz", ".bz2", ".xz", ".zst",
        // Images
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".avif",
        // Video
        ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".webm",
        // Audio
        ".mp3", ".aac", ".flac", ".ogg", ".wma", ".m4a",
        // Documents (ZIP-based)
        ".pdf", ".docx", ".xlsx", ".pptx"
    };

    public static bool IsAlreadyCompressed(string extensionOrPath)
    {
        var ext = extensionOrPath.StartsWith('.')
            ? extensionOrPath
            : Path.GetExtension(extensionOrPath);
        return CompressedExtensions.Contains(ext);
    }

    public static void CompressFile(string sourcePath, string compressedPath)
    {
        using var sourceStream = File.OpenRead(sourcePath);
        using var destStream = File.Create(compressedPath);
        using var gzipStream = new GZipStream(destStream, CompressionLevel.Optimal);
        sourceStream.CopyTo(gzipStream);
    }

    public static void DecompressFile(string compressedPath, string destPath)
    {
        using var sourceStream = File.OpenRead(compressedPath);
        using var gzipStream = new GZipStream(sourceStream, CompressionMode.Decompress);
        using var destStream = File.Create(destPath);
        gzipStream.CopyTo(destStream);
    }

    public static byte[] ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return SHA256.HashData(stream);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test --filter "CompressionHelperTests"
```

Expected: All 5 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteFileSync/Transfer/CompressionHelper.cs tests/RemoteFileSync.Tests/Transfer/CompressionHelperTests.cs
git commit -m "feat: add CompressionHelper with GZip, SHA256, and compressed-format detection"
git push
```

---

## Task 9: BackupManager — Dated Folder Backup

**Files:**
- Create: `src/RemoteFileSync/Backup/BackupManager.cs`
- Create: `tests/RemoteFileSync.Tests/Backup/BackupManagerTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/RemoteFileSync.Tests/Backup/BackupManagerTests.cs`:

```csharp
using RemoteFileSync.Backup;

namespace RemoteFileSync.Tests.Backup;

public class BackupManagerTests : IDisposable
{
    private readonly string _syncDir;
    private readonly string _backupDir;

    public BackupManagerTests()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rfs_bak_{Guid.NewGuid()}");
        _syncDir = Path.Combine(root, "sync");
        _backupDir = Path.Combine(root, "backup");
        Directory.CreateDirectory(_syncDir);
        Directory.CreateDirectory(_backupDir);
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_syncDir)!;
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private void CreateSyncFile(string relativePath, string content = "original")
    {
        var full = Path.Combine(_syncDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void BackupFile_MovesToDatedFolder()
    {
        CreateSyncFile("report.docx");
        var mgr = new BackupManager(_syncDir, _backupDir);

        var result = mgr.BackupFile("report.docx");

        Assert.True(result);
        Assert.False(File.Exists(Path.Combine(_syncDir, "report.docx")));

        var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
        var backupPath = Path.Combine(_backupDir, dateStr, "report.docx");
        Assert.True(File.Exists(backupPath));
        Assert.Equal("original", File.ReadAllText(backupPath));
    }

    [Fact]
    public void BackupFile_PreservesSubdirectoryStructure()
    {
        CreateSyncFile("docs/sub/file.txt");
        var mgr = new BackupManager(_syncDir, _backupDir);

        mgr.BackupFile("docs/sub/file.txt");

        var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
        var backupPath = Path.Combine(_backupDir, dateStr, "docs", "sub", "file.txt");
        Assert.True(File.Exists(backupPath));
    }

    [Fact]
    public void BackupFile_DuplicateSameDay_AppendsNumericSuffix()
    {
        CreateSyncFile("report.docx", "version1");
        var mgr = new BackupManager(_syncDir, _backupDir);
        mgr.BackupFile("report.docx");

        // Create the file again and back it up again
        CreateSyncFile("report.docx", "version2");
        mgr.BackupFile("report.docx");

        var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
        var backup1 = Path.Combine(_backupDir, dateStr, "report.docx");
        var backup2 = Path.Combine(_backupDir, dateStr, "report_1.docx");
        Assert.True(File.Exists(backup1));
        Assert.True(File.Exists(backup2));
        Assert.Equal("version1", File.ReadAllText(backup1));
        Assert.Equal("version2", File.ReadAllText(backup2));
    }

    [Fact]
    public void BackupFile_FileDoesNotExist_ReturnsFalse()
    {
        var mgr = new BackupManager(_syncDir, _backupDir);
        var result = mgr.BackupFile("nonexistent.txt");

        Assert.False(result);
    }

    [Fact]
    public void BackupFile_ThreadSafe_NoCrash()
    {
        for (int i = 0; i < 10; i++)
            CreateSyncFile($"file{i}.txt", $"content{i}");

        var mgr = new BackupManager(_syncDir, _backupDir);
        var tasks = Enumerable.Range(0, 10)
            .Select(i => Task.Run(() => mgr.BackupFile($"file{i}.txt")))
            .ToArray();

        Task.WaitAll(tasks);

        Assert.All(tasks, t => Assert.True(t.Result));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test --filter "BackupManagerTests"
```

Expected: FAIL — `BackupManager` type does not exist.

- [ ] **Step 3: Implement BackupManager**

Create `src/RemoteFileSync/Backup/BackupManager.cs`:

```csharp
namespace RemoteFileSync.Backup;

public sealed class BackupManager
{
    private readonly string _syncFolder;
    private readonly string _backupFolder;
    private readonly object _lock = new();

    public BackupManager(string syncFolder, string backupFolder)
    {
        _syncFolder = Path.GetFullPath(syncFolder);
        _backupFolder = Path.GetFullPath(backupFolder);
    }

    public bool BackupFile(string relativePath)
    {
        var sourcePath = Path.Combine(_syncFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(sourcePath))
            return false;

        lock (_lock)
        {
            var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
            var backupDir = Path.Combine(_backupFolder, dateStr,
                Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? "");
            Directory.CreateDirectory(backupDir);

            var fileName = Path.GetFileNameWithoutExtension(relativePath);
            var ext = Path.GetExtension(relativePath);
            var destPath = Path.Combine(backupDir, Path.GetFileName(relativePath));

            // If destination already exists, add numeric suffix
            int suffix = 1;
            while (File.Exists(destPath))
            {
                destPath = Path.Combine(backupDir, $"{fileName}_{suffix}{ext}");
                suffix++;
            }

            File.Move(sourcePath, destPath);
            return true;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test --filter "BackupManagerTests"
```

Expected: All 5 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteFileSync/Backup/ tests/RemoteFileSync.Tests/Backup/
git commit -m "feat: add BackupManager with dated folders and numeric suffix dedup"
git push
```

---

## Task 10: ProtocolHandler — Binary Message Serialization

**Files:**
- Create: `src/RemoteFileSync/Network/MessageType.cs`
- Create: `src/RemoteFileSync/Network/ProtocolHandler.cs`
- Create: `tests/RemoteFileSync.Tests/Network/ProtocolHandlerTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/RemoteFileSync.Tests/Network/ProtocolHandlerTests.cs`:

```csharp
using RemoteFileSync.Models;
using RemoteFileSync.Network;

namespace RemoteFileSync.Tests.Network;

public class ProtocolHandlerTests
{
    [Fact]
    public async Task WriteAndReadMessage_RoundTrips()
    {
        using var stream = new MemoryStream();
        var payload = new byte[] { 0x01, 0x02, 0x03 };

        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Handshake, payload);

        stream.Position = 0;
        var (type, data) = await ProtocolHandler.ReadMessageAsync(stream);

        Assert.Equal(MessageType.Handshake, type);
        Assert.Equal(payload, data);
    }

    [Fact]
    public async Task SerializeManifest_RoundTrips()
    {
        var manifest = new FileManifest();
        manifest.Add(new FileEntry("docs/a.txt", 1024,
            new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc)));
        manifest.Add(new FileEntry("b.csv", 2048,
            new DateTime(2026, 3, 25, 8, 30, 0, DateTimeKind.Utc)));

        var bytes = ProtocolHandler.SerializeManifest(manifest);
        var restored = ProtocolHandler.DeserializeManifest(bytes);

        Assert.Equal(2, restored.Count);

        var a = restored.Get("docs/a.txt");
        Assert.NotNull(a);
        Assert.Equal(1024, a.FileSize);
        Assert.Equal(new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc), a.LastModifiedUtc);

        var b = restored.Get("b.csv");
        Assert.NotNull(b);
        Assert.Equal(2048, b.FileSize);
    }

    [Fact]
    public async Task SerializeSyncPlan_RoundTrips()
    {
        var plan = new List<SyncPlanEntry>
        {
            new(SyncActionType.SendToServer, "a.txt"),
            new(SyncActionType.SendToClient, "b.txt"),
            new(SyncActionType.ClientOnly, "c.txt"),
            new(SyncActionType.Skip, "d.txt"),
        };

        var bytes = ProtocolHandler.SerializeSyncPlan(plan);
        var restored = ProtocolHandler.DeserializeSyncPlan(bytes);

        Assert.Equal(4, restored.Count);
        Assert.Equal(SyncActionType.SendToServer, restored[0].Action);
        Assert.Equal("a.txt", restored[0].RelativePath);
        Assert.Equal(SyncActionType.SendToClient, restored[1].Action);
        Assert.Equal(SyncActionType.ClientOnly, restored[2].Action);
        Assert.Equal(SyncActionType.Skip, restored[3].Action);
    }

    [Fact]
    public void SerializeHandshake_CorrectBytes()
    {
        var bytes = ProtocolHandler.SerializeHandshake(version: 1, bidirectional: true);
        Assert.Equal(2, bytes.Length);
        Assert.Equal(1, bytes[0]);  // version
        Assert.Equal(1, bytes[1]);  // bidi = 1
    }

    [Fact]
    public void DeserializeHandshake_ParsesCorrectly()
    {
        var bytes = new byte[] { 1, 0 }; // version 1, uni
        var (version, bidi) = ProtocolHandler.DeserializeHandshake(bytes);

        Assert.Equal(1, version);
        Assert.False(bidi);
    }

    [Fact]
    public async Task EmptyManifest_RoundTrips()
    {
        var manifest = new FileManifest();
        var bytes = ProtocolHandler.SerializeManifest(manifest);
        var restored = ProtocolHandler.DeserializeManifest(bytes);

        Assert.Equal(0, restored.Count);
    }

    [Fact]
    public async Task WriteMessage_LargePayload_Works()
    {
        using var stream = new MemoryStream();
        var payload = new byte[100_000];
        Random.Shared.NextBytes(payload);

        await ProtocolHandler.WriteMessageAsync(stream, MessageType.FileChunk, payload);
        stream.Position = 0;
        var (type, data) = await ProtocolHandler.ReadMessageAsync(stream);

        Assert.Equal(MessageType.FileChunk, type);
        Assert.Equal(payload, data);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test --filter "ProtocolHandlerTests"
```

Expected: FAIL — `ProtocolHandler` and `MessageType` types do not exist.

- [ ] **Step 3: Implement MessageType enum**

Create `src/RemoteFileSync/Network/MessageType.cs`:

```csharp
namespace RemoteFileSync.Network;

public enum MessageType : byte
{
    Handshake = 0x01,
    HandshakeAck = 0x02,
    Manifest = 0x03,
    SyncPlan = 0x04,
    FileStart = 0x05,
    FileChunk = 0x06,
    FileEnd = 0x07,
    BackupConfirm = 0x08,
    SyncComplete = 0x09,
    Error = 0xFF
}
```

- [ ] **Step 4: Implement ProtocolHandler**

Create `src/RemoteFileSync/Network/ProtocolHandler.cs`:

```csharp
using System.Text;
using RemoteFileSync.Models;

namespace RemoteFileSync.Network;

public static class ProtocolHandler
{
    // --- Message framing ---

    public static async Task WriteMessageAsync(Stream stream, MessageType type, byte[] payload,
        CancellationToken ct = default)
    {
        var header = new byte[5];
        header[0] = (byte)type;
        BitConverter.TryWriteBytes(header.AsSpan(1), payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<(MessageType type, byte[] payload)> ReadMessageAsync(Stream stream,
        CancellationToken ct = default)
    {
        var header = new byte[5];
        await ReadExactAsync(stream, header, ct);

        var type = (MessageType)header[0];
        var length = BitConverter.ToInt32(header, 1);
        var payload = new byte[length];

        if (length > 0)
            await ReadExactAsync(stream, payload, ct);

        return (type, payload);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer,
        CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0)
                throw new EndOfStreamException("Connection closed unexpectedly.");
            offset += read;
        }
    }

    // --- Handshake ---

    public static byte[] SerializeHandshake(byte version, bool bidirectional)
    {
        return new[] { version, (byte)(bidirectional ? 1 : 0) };
    }

    public static (byte version, bool bidirectional) DeserializeHandshake(byte[] data)
    {
        return (data[0], data[1] == 1);
    }

    public static byte[] SerializeHandshakeAck(byte version, bool accepted)
    {
        return new[] { version, (byte)(accepted ? 0 : 1) };
    }

    public static (byte version, bool accepted) DeserializeHandshakeAck(byte[] data)
    {
        return (data[0], data[1] == 0);
    }

    // --- Manifest ---

    public static byte[] SerializeManifest(FileManifest manifest)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        writer.Write(manifest.Count);
        foreach (var entry in manifest.Entries)
        {
            var pathBytes = Encoding.UTF8.GetBytes(entry.RelativePath);
            writer.Write((short)pathBytes.Length);
            writer.Write(pathBytes);
            writer.Write(entry.FileSize);
            writer.Write(entry.LastModifiedUtc.Ticks);
        }

        writer.Flush();
        return ms.ToArray();
    }

    public static FileManifest DeserializeManifest(byte[] data)
    {
        var manifest = new FileManifest();
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms, Encoding.UTF8);

        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            short pathLen = reader.ReadInt16();
            var pathBytes = reader.ReadBytes(pathLen);
            var path = Encoding.UTF8.GetString(pathBytes);
            long size = reader.ReadInt64();
            long ticks = reader.ReadInt64();

            manifest.Add(new FileEntry(path, size,
                new DateTime(ticks, DateTimeKind.Utc)));
        }

        return manifest;
    }

    // --- SyncPlan ---

    public static byte[] SerializeSyncPlan(List<SyncPlanEntry> plan)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        writer.Write(plan.Count);
        foreach (var entry in plan)
        {
            writer.Write((byte)entry.Action);
            var pathBytes = Encoding.UTF8.GetBytes(entry.RelativePath);
            writer.Write((short)pathBytes.Length);
            writer.Write(pathBytes);
        }

        writer.Flush();
        return ms.ToArray();
    }

    public static List<SyncPlanEntry> DeserializeSyncPlan(byte[] data)
    {
        var plan = new List<SyncPlanEntry>();
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms, Encoding.UTF8);

        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            var action = (SyncActionType)reader.ReadByte();
            short pathLen = reader.ReadInt16();
            var pathBytes = reader.ReadBytes(pathLen);
            var path = Encoding.UTF8.GetString(pathBytes);
            plan.Add(new SyncPlanEntry(action, path));
        }

        return plan;
    }

    // --- FileStart ---

    public static byte[] SerializeFileStart(short fileId, string relativePath,
        long originalSize, bool isCompressed, int blockSize)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        writer.Write(fileId);
        var pathBytes = Encoding.UTF8.GetBytes(relativePath);
        writer.Write((short)pathBytes.Length);
        writer.Write(pathBytes);
        writer.Write(originalSize);
        writer.Write((byte)(isCompressed ? 1 : 0));
        writer.Write(blockSize);

        writer.Flush();
        return ms.ToArray();
    }

    public static (short fileId, string relativePath, long originalSize, bool isCompressed, int blockSize)
        DeserializeFileStart(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms, Encoding.UTF8);

        short fileId = reader.ReadInt16();
        short pathLen = reader.ReadInt16();
        var pathBytes = reader.ReadBytes(pathLen);
        string path = Encoding.UTF8.GetString(pathBytes);
        long originalSize = reader.ReadInt64();
        bool isCompressed = reader.ReadByte() == 1;
        int blockSize = reader.ReadInt32();

        return (fileId, path, originalSize, isCompressed, blockSize);
    }

    // --- FileChunk ---

    public static byte[] SerializeFileChunk(short fileId, int chunkIndex, byte[] chunkData)
    {
        var result = new byte[6 + chunkData.Length];
        BitConverter.TryWriteBytes(result.AsSpan(0), fileId);
        BitConverter.TryWriteBytes(result.AsSpan(2), chunkIndex);
        chunkData.CopyTo(result, 6);
        return result;
    }

    public static (short fileId, int chunkIndex, byte[] chunkData) DeserializeFileChunk(byte[] data)
    {
        short fileId = BitConverter.ToInt16(data, 0);
        int chunkIndex = BitConverter.ToInt32(data, 2);
        var chunkData = new byte[data.Length - 6];
        Array.Copy(data, 6, chunkData, 0, chunkData.Length);
        return (fileId, chunkIndex, chunkData);
    }

    // --- FileEnd ---

    public static byte[] SerializeFileEnd(short fileId, byte[] sha256Hash)
    {
        var result = new byte[2 + 32];
        BitConverter.TryWriteBytes(result.AsSpan(0), fileId);
        sha256Hash.CopyTo(result, 2);
        return result;
    }

    public static (short fileId, byte[] sha256Hash) DeserializeFileEnd(byte[] data)
    {
        short fileId = BitConverter.ToInt16(data, 0);
        var hash = new byte[32];
        Array.Copy(data, 2, hash, 0, 32);
        return (fileId, hash);
    }

    // --- SyncComplete ---

    public static byte[] SerializeSyncComplete(int filesTransferred, long bytesTransferred, long elapsedMs)
    {
        var result = new byte[20];
        BitConverter.TryWriteBytes(result.AsSpan(0), filesTransferred);
        BitConverter.TryWriteBytes(result.AsSpan(4), bytesTransferred);
        BitConverter.TryWriteBytes(result.AsSpan(12), elapsedMs);
        return result;
    }

    public static (int filesTransferred, long bytesTransferred, long elapsedMs)
        DeserializeSyncComplete(byte[] data)
    {
        return (
            BitConverter.ToInt32(data, 0),
            BitConverter.ToInt64(data, 4),
            BitConverter.ToInt64(data, 12)
        );
    }

    // --- Error ---

    public static byte[] SerializeError(int errorCode, string message)
    {
        var msgBytes = Encoding.UTF8.GetBytes(message);
        var result = new byte[4 + msgBytes.Length];
        BitConverter.TryWriteBytes(result.AsSpan(0), errorCode);
        msgBytes.CopyTo(result, 4);
        return result;
    }

    public static (int errorCode, string message) DeserializeError(byte[] data)
    {
        int code = BitConverter.ToInt32(data, 0);
        string msg = Encoding.UTF8.GetString(data, 4, data.Length - 4);
        return (code, msg);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test --filter "ProtocolHandlerTests"
```

Expected: All 7 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/RemoteFileSync/Network/ tests/RemoteFileSync.Tests/Network/
git commit -m "feat: add binary protocol handler with message framing and serialization"
git push
```

---

## Task 11: FileTransfer — Send and Receive Files Over Stream

**Files:**
- Create: `src/RemoteFileSync/Transfer/FileTransfer.cs`
- Create: `tests/RemoteFileSync.Tests/Transfer/FileTransferTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/RemoteFileSync.Tests/Transfer/FileTransferTests.cs`:

```csharp
using RemoteFileSync.Network;
using RemoteFileSync.Transfer;

namespace RemoteFileSync.Tests.Transfer;

public class FileTransferTests : IDisposable
{
    private readonly string _tempDir;

    public FileTransferTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rfs_xfer_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task SendAndReceive_TextFile_RoundTrips()
    {
        var sourceDir = Path.Combine(_tempDir, "source");
        var destDir = Path.Combine(_tempDir, "dest");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);

        var content = "Hello, world! " + new string('X', 5000);
        File.WriteAllText(Path.Combine(sourceDir, "test.txt"), content);

        using var pipeStream = new MemoryStream();
        var sender = new FileTransferSender(sourceDir, blockSize: 1024);
        var receiver = new FileTransferReceiver(destDir);

        // Send
        await sender.SendFileAsync(pipeStream, fileId: 1, relativePath: "test.txt",
            CancellationToken.None);

        // Rewind stream for receiver
        pipeStream.Position = 0;

        // Receive
        var result = await receiver.ReceiveFileAsync(pipeStream, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("test.txt", result.RelativePath);

        var received = File.ReadAllText(Path.Combine(destDir, "test.txt"));
        Assert.Equal(content, received);
    }

    [Fact]
    public async Task SendAndReceive_AlreadyCompressedFile_NoGzip()
    {
        var sourceDir = Path.Combine(_tempDir, "source2");
        var destDir = Path.Combine(_tempDir, "dest2");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);

        // Create a fake .jpg file (already "compressed")
        var data = new byte[2048];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(Path.Combine(sourceDir, "photo.jpg"), data);

        using var pipeStream = new MemoryStream();
        var sender = new FileTransferSender(sourceDir, blockSize: 512);
        var receiver = new FileTransferReceiver(destDir);

        await sender.SendFileAsync(pipeStream, fileId: 2, relativePath: "photo.jpg",
            CancellationToken.None);
        pipeStream.Position = 0;

        var result = await receiver.ReceiveFileAsync(pipeStream, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(destDir, "photo.jpg")));
    }

    [Fact]
    public async Task SendAndReceive_SubdirectoryFile_CreatesPath()
    {
        var sourceDir = Path.Combine(_tempDir, "source3");
        var destDir = Path.Combine(_tempDir, "dest3");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);
        Directory.CreateDirectory(Path.Combine(sourceDir, "sub", "deep"));
        File.WriteAllText(Path.Combine(sourceDir, "sub", "deep", "nested.txt"), "deep content");

        using var pipeStream = new MemoryStream();
        var sender = new FileTransferSender(sourceDir, blockSize: 4096);
        var receiver = new FileTransferReceiver(destDir);

        await sender.SendFileAsync(pipeStream, fileId: 3, relativePath: "sub/deep/nested.txt",
            CancellationToken.None);
        pipeStream.Position = 0;

        var result = await receiver.ReceiveFileAsync(pipeStream, CancellationToken.None);

        Assert.True(result.Success);
        var destFile = Path.Combine(destDir, "sub", "deep", "nested.txt");
        Assert.True(File.Exists(destFile));
        Assert.Equal("deep content", File.ReadAllText(destFile));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test --filter "FileTransferTests"
```

Expected: FAIL — `FileTransferSender` and `FileTransferReceiver` types do not exist.

- [ ] **Step 3: Implement FileTransfer**

Create `src/RemoteFileSync/Transfer/FileTransfer.cs`:

```csharp
using RemoteFileSync.Network;

namespace RemoteFileSync.Transfer;

public sealed class FileTransferSender
{
    private readonly string _rootFolder;
    private readonly int _blockSize;

    public FileTransferSender(string rootFolder, int blockSize)
    {
        _rootFolder = Path.GetFullPath(rootFolder);
        _blockSize = blockSize;
    }

    public async Task SendFileAsync(Stream networkStream, short fileId,
        string relativePath, CancellationToken ct)
    {
        var sourcePath = Path.Combine(_rootFolder,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        var sourceInfo = new FileInfo(sourcePath);
        var extension = Path.GetExtension(relativePath);
        bool alreadyCompressed = CompressionHelper.IsAlreadyCompressed(extension);

        string transferSource;
        string? tempCompressed = null;

        if (!alreadyCompressed)
        {
            tempCompressed = Path.Combine(Path.GetTempPath(),
                $"rfs_gz_{Guid.NewGuid()}.tmp");
            CompressionHelper.CompressFile(sourcePath, tempCompressed);
            transferSource = tempCompressed;
        }
        else
        {
            transferSource = sourcePath;
        }

        try
        {
            var sha256 = CompressionHelper.ComputeSha256(sourcePath);

            // Send FileStart
            var startPayload = ProtocolHandler.SerializeFileStart(
                fileId, relativePath, sourceInfo.Length,
                isCompressed: !alreadyCompressed, _blockSize);
            await ProtocolHandler.WriteMessageAsync(networkStream,
                MessageType.FileStart, startPayload, ct);

            // Send FileChunks
            using var fileStream = File.OpenRead(transferSource);
            var buffer = new byte[_blockSize];
            int chunkIndex = 0;
            int bytesRead;

            while ((bytesRead = await fileStream.ReadAsync(buffer, ct)) > 0)
            {
                var chunkData = bytesRead == buffer.Length
                    ? buffer
                    : buffer[..bytesRead];
                var chunkPayload = ProtocolHandler.SerializeFileChunk(
                    fileId, chunkIndex, chunkData);
                await ProtocolHandler.WriteMessageAsync(networkStream,
                    MessageType.FileChunk, chunkPayload, ct);
                chunkIndex++;
            }

            // Send FileEnd
            var endPayload = ProtocolHandler.SerializeFileEnd(fileId, sha256);
            await ProtocolHandler.WriteMessageAsync(networkStream,
                MessageType.FileEnd, endPayload, ct);
        }
        finally
        {
            if (tempCompressed != null && File.Exists(tempCompressed))
                File.Delete(tempCompressed);
        }
    }
}

public record FileReceiveResult(bool Success, string RelativePath, string? ErrorMessage = null);

public sealed class FileTransferReceiver
{
    private readonly string _rootFolder;

    public FileTransferReceiver(string rootFolder)
    {
        _rootFolder = Path.GetFullPath(rootFolder);
    }

    public async Task<FileReceiveResult> ReceiveFileAsync(Stream networkStream,
        CancellationToken ct)
    {
        // Read FileStart
        var (startType, startData) = await ProtocolHandler.ReadMessageAsync(networkStream, ct);
        if (startType != MessageType.FileStart)
            return new FileReceiveResult(false, "", $"Expected FileStart, got {startType}");

        var (fileId, relativePath, originalSize, isCompressed, blockSize) =
            ProtocolHandler.DeserializeFileStart(startData);

        var tempPath = Path.Combine(Path.GetTempPath(), $"rfs_recv_{Guid.NewGuid()}.tmp");

        try
        {
            // Receive FileChunks into temp file
            using (var tempStream = File.Create(tempPath))
            {
                while (true)
                {
                    var (msgType, msgData) = await ProtocolHandler.ReadMessageAsync(networkStream, ct);

                    if (msgType == MessageType.FileChunk)
                    {
                        var (_, _, chunkData) = ProtocolHandler.DeserializeFileChunk(msgData);
                        await tempStream.WriteAsync(chunkData, ct);
                    }
                    else if (msgType == MessageType.FileEnd)
                    {
                        var (_, expectedHash) = ProtocolHandler.DeserializeFileEnd(msgData);

                        // Close temp stream before decompressing
                        await tempStream.FlushAsync(ct);
                        tempStream.Close();

                        // Finalize file
                        var destPath = Path.Combine(_rootFolder,
                            relativePath.Replace('/', Path.DirectorySeparatorChar));
                        var destDir = Path.GetDirectoryName(destPath)!;
                        Directory.CreateDirectory(destDir);

                        if (isCompressed)
                        {
                            CompressionHelper.DecompressFile(tempPath, destPath);
                        }
                        else
                        {
                            File.Copy(tempPath, destPath, overwrite: true);
                        }

                        // Verify checksum
                        var actualHash = CompressionHelper.ComputeSha256(destPath);
                        if (!actualHash.SequenceEqual(expectedHash))
                        {
                            File.Delete(destPath);
                            return new FileReceiveResult(false, relativePath,
                                "Checksum mismatch");
                        }

                        return new FileReceiveResult(true, relativePath);
                    }
                    else
                    {
                        return new FileReceiveResult(false, relativePath,
                            $"Unexpected message type: {msgType}");
                    }
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test --filter "FileTransferTests"
```

Expected: All 3 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteFileSync/Transfer/FileTransfer.cs tests/RemoteFileSync.Tests/Transfer/FileTransferTests.cs
git commit -m "feat: add FileTransfer sender/receiver with GZip and SHA256 verification"
git push
```

---

## Task 12: SyncServer and SyncClient — TCP Networking and Orchestration

**Files:**
- Create: `src/RemoteFileSync/Network/SyncServer.cs`
- Create: `src/RemoteFileSync/Network/SyncClient.cs`

- [ ] **Step 1: Implement SyncServer**

Create `src/RemoteFileSync/Network/SyncServer.cs`:

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Backup;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Sync;
using RemoteFileSync.Transfer;

namespace RemoteFileSync.Network;

public sealed class SyncServer
{
    private readonly SyncOptions _options;
    private readonly SyncLogger _logger;

    public SyncServer(SyncOptions options, SyncLogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, _options.Port);
        listener.Start();
        _logger.Summary($"Listening on port {_options.Port}...");

        try
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            _logger.Summary("Client connected.");
            using var stream = client.GetStream();

            return await HandleConnectionAsync(stream, ct);
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<int> HandleConnectionAsync(NetworkStream stream, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int skippedFiles = 0;

        // 1. Receive handshake
        var (hsType, hsData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        if (hsType != MessageType.Handshake)
        {
            _logger.Error($"Expected Handshake, got {hsType}");
            return 3;
        }
        var (version, bidirectional) = ProtocolHandler.DeserializeHandshake(hsData);
        _logger.Info($"Handshake: v{version}, {(bidirectional ? "bidirectional" : "unidirectional")}");

        // 2. Send HandshakeAck
        var ackPayload = ProtocolHandler.SerializeHandshakeAck(1, accepted: true);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.HandshakeAck, ackPayload, ct);

        // 3. Receive client manifest
        var (mType, mData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var clientManifest = ProtocolHandler.DeserializeManifest(mData);
        _logger.Info($"Client manifest: {clientManifest.Count} files");

        // 4. Scan local folder and send server manifest
        var scanner = new FileScanner(_options.Folder,
            _options.IncludePatterns, _options.ExcludePatterns);
        var serverManifest = scanner.Scan();
        _logger.Info($"Local manifest: {serverManifest.Count} files");
        var serverManifestBytes = ProtocolHandler.SerializeManifest(serverManifest);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Manifest,
            serverManifestBytes, ct);

        // 5. Receive sync plan
        var (pType, pData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var syncPlan = ProtocolHandler.DeserializeSyncPlan(pData);
        _logger.Info($"Sync plan: {syncPlan.Count} actions");

        var backup = new BackupManager(_options.Folder, _options.EffectiveBackupFolder);
        var receiver = new FileTransferReceiver(_options.Folder);
        var sender = new FileTransferSender(_options.Folder, _options.BlockSize);
        int filesTransferred = 0;
        long bytesTransferred = 0;

        // 6. Receive files from client (SendToServer + ClientOnly)
        var toReceive = syncPlan.Where(p =>
            p.Action == SyncActionType.SendToServer ||
            p.Action == SyncActionType.ClientOnly).ToList();

        foreach (var action in toReceive)
        {
            if (action.Action == SyncActionType.SendToServer)
            {
                if (!backup.BackupFile(action.RelativePath))
                    _logger.Debug($"No existing file to backup: {action.RelativePath}");
            }

            var result = await receiver.ReceiveFileAsync(stream, ct);
            if (result.Success)
            {
                _logger.Info($"[←] {result.RelativePath}");
                filesTransferred++;

                var fi = new FileInfo(Path.Combine(_options.Folder,
                    result.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                bytesTransferred += fi.Length;
            }
            else
            {
                _logger.Error($"Failed to receive {action.RelativePath}: {result.ErrorMessage}");
                skippedFiles++;
            }

            // Send BackupConfirm
            var confirmPayload = System.Text.Encoding.UTF8.GetBytes(action.RelativePath);
            var confirm = new byte[confirmPayload.Length + 1];
            confirmPayload.CopyTo(confirm, 0);
            confirm[^1] = (byte)(result.Success ? 1 : 0);
            await ProtocolHandler.WriteMessageAsync(stream, MessageType.BackupConfirm, confirm, ct);
        }

        // 7. Send files to client (SendToClient + ServerOnly)
        if (bidirectional)
        {
            var toSend = syncPlan.Where(p =>
                p.Action == SyncActionType.SendToClient ||
                p.Action == SyncActionType.ServerOnly).ToList();

            foreach (var action in toSend)
            {
                try
                {
                    short fileId = (short)(filesTransferred % short.MaxValue);
                    await sender.SendFileAsync(stream, fileId, action.RelativePath, ct);
                    _logger.Info($"[→] {action.RelativePath}");
                    filesTransferred++;

                    // Wait for BackupConfirm
                    var (cType, _) = await ProtocolHandler.ReadMessageAsync(stream, ct);
                    if (cType != MessageType.BackupConfirm)
                        _logger.Warning($"Expected BackupConfirm, got {cType}");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to send {action.RelativePath}: {ex.Message}");
                    skippedFiles++;
                }
            }
        }

        // 8. Exchange SyncComplete
        sw.Stop();
        var completePayload = ProtocolHandler.SerializeSyncComplete(
            filesTransferred, bytesTransferred, sw.ElapsedMilliseconds);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncComplete,
            completePayload, ct);

        var (scType, scData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var (clientFiles, clientBytes, clientMs) =
            ProtocolHandler.DeserializeSyncComplete(scData);

        _logger.Summary($"Sync complete: {filesTransferred} files, " +
            $"{bytesTransferred / (1024.0 * 1024.0):F1} MB, " +
            $"{sw.ElapsedMilliseconds}ms");

        return skippedFiles > 0 ? 1 : 0;
    }
}
```

- [ ] **Step 2: Implement SyncClient**

Create `src/RemoteFileSync/Network/SyncClient.cs`:

```csharp
using System.Diagnostics;
using System.Net.Sockets;
using RemoteFileSync.Backup;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Sync;
using RemoteFileSync.Transfer;

namespace RemoteFileSync.Network;

public sealed class SyncClient
{
    private readonly SyncOptions _options;
    private readonly SyncLogger _logger;

    public SyncClient(SyncOptions options, SyncLogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        using var tcp = new TcpClient();
        int retries = 3;

        for (int attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
                _logger.Summary($"Connecting to {_options.Host}:{_options.Port}...");
                await tcp.ConnectAsync(_options.Host!, _options.Port, ct);
                break;
            }
            catch (SocketException) when (attempt < retries)
            {
                _logger.Warning($"Connection attempt {attempt} failed. Retrying in 2s...");
                await Task.Delay(2000, ct);
            }
            catch (SocketException ex)
            {
                _logger.Error($"Connection failed after {retries} attempts: {ex.Message}");
                return 2;
            }
        }

        _logger.Summary($"Connected. {(_options.Bidirectional ? "Bi-directional" : "Uni-directional")} sync." +
            (_options.Verbose ? $" Block: {_options.BlockSize / 1024}KB, Threads: {_options.MaxThreads}" : ""));

        using var stream = tcp.GetStream();
        return await HandleConnectionAsync(stream, ct);
    }

    private async Task<int> HandleConnectionAsync(NetworkStream stream, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int skippedFiles = 0;

        // 1. Send handshake
        var hsPayload = ProtocolHandler.SerializeHandshake(1, _options.Bidirectional);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Handshake, hsPayload, ct);

        // 2. Receive HandshakeAck
        var (ackType, ackData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        if (ackType != MessageType.HandshakeAck)
        {
            _logger.Error($"Expected HandshakeAck, got {ackType}");
            return 3;
        }
        var (_, accepted) = ProtocolHandler.DeserializeHandshakeAck(ackData);
        if (!accepted)
        {
            _logger.Error("Server rejected the connection.");
            return 2;
        }

        // 3. Scan local folder and send client manifest
        var scanner = new FileScanner(_options.Folder,
            _options.IncludePatterns, _options.ExcludePatterns);
        var clientManifest = scanner.Scan();
        _logger.Info($"Local manifest: {clientManifest.Count} files");
        var clientManifestBytes = ProtocolHandler.SerializeManifest(clientManifest);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Manifest,
            clientManifestBytes, ct);

        // 4. Receive server manifest
        var (mType, mData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var serverManifest = ProtocolHandler.DeserializeManifest(mData);
        _logger.Info($"Remote manifest: {serverManifest.Count} files");

        // 5. Compute sync plan and send to server
        var syncPlan = SyncEngine.ComputePlan(clientManifest, serverManifest,
            _options.Bidirectional);
        var actionable = syncPlan.Where(p => p.Action != SyncActionType.Skip).ToList();
        _logger.Info($"Sync plan: {actionable.Count} transfers, " +
            $"{syncPlan.Count(p => p.Action == SyncActionType.Skip)} skipped");

        var planBytes = ProtocolHandler.SerializeSyncPlan(syncPlan);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncPlan, planBytes, ct);

        var backup = new BackupManager(_options.Folder, _options.EffectiveBackupFolder);
        var sender = new FileTransferSender(_options.Folder, _options.BlockSize);
        var receiver = new FileTransferReceiver(_options.Folder);
        int filesTransferred = 0;
        long bytesTransferred = 0;

        // 6. Send files to server (SendToServer + ClientOnly)
        var toSend = syncPlan.Where(p =>
            p.Action == SyncActionType.SendToServer ||
            p.Action == SyncActionType.ClientOnly).ToList();

        foreach (var action in toSend)
        {
            try
            {
                short fileId = (short)(filesTransferred % short.MaxValue);
                await sender.SendFileAsync(stream, fileId, action.RelativePath, ct);
                _logger.Info($"[→] {action.RelativePath}");
                filesTransferred++;

                var fi = new FileInfo(Path.Combine(_options.Folder,
                    action.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                bytesTransferred += fi.Length;

                // Wait for BackupConfirm
                var (cType, _) = await ProtocolHandler.ReadMessageAsync(stream, ct);
                if (cType != MessageType.BackupConfirm)
                    _logger.Warning($"Expected BackupConfirm, got {cType}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to send {action.RelativePath}: {ex.Message}");
                skippedFiles++;
            }
        }

        // 7. Receive files from server (SendToClient + ServerOnly)
        if (_options.Bidirectional)
        {
            var toReceive = syncPlan.Where(p =>
                p.Action == SyncActionType.SendToClient ||
                p.Action == SyncActionType.ServerOnly).ToList();

            foreach (var action in toReceive)
            {
                if (action.Action == SyncActionType.SendToClient)
                {
                    if (!backup.BackupFile(action.RelativePath))
                        _logger.Debug($"No existing file to backup: {action.RelativePath}");
                }

                var result = await receiver.ReceiveFileAsync(stream, ct);
                if (result.Success)
                {
                    _logger.Info($"[←] {result.RelativePath}");
                    filesTransferred++;

                    var fi = new FileInfo(Path.Combine(_options.Folder,
                        result.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                    bytesTransferred += fi.Length;
                }
                else
                {
                    _logger.Error($"Failed to receive {action.RelativePath}: {result.ErrorMessage}");
                    skippedFiles++;
                }

                // Send BackupConfirm
                var confirmPayload = System.Text.Encoding.UTF8.GetBytes(action.RelativePath);
                var confirm = new byte[confirmPayload.Length + 1];
                confirmPayload.CopyTo(confirm, 0);
                confirm[^1] = (byte)(result.Success ? 1 : 0);
                await ProtocolHandler.WriteMessageAsync(stream, MessageType.BackupConfirm,
                    confirm, ct);
            }
        }

        // 8. Exchange SyncComplete
        sw.Stop();
        var completePayload = ProtocolHandler.SerializeSyncComplete(
            filesTransferred, bytesTransferred, sw.ElapsedMilliseconds);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncComplete,
            completePayload, ct);

        var (scType, scData) = await ProtocolHandler.ReadMessageAsync(stream, ct);

        _logger.Summary($"Sync complete: {filesTransferred} files, " +
            $"{bytesTransferred / (1024.0 * 1024.0):F1} MB, " +
            $"{sw.ElapsedMilliseconds}ms");

        return skippedFiles > 0 ? 1 : 0;
    }
}
```

- [ ] **Step 3: Verify build succeeds**

```bash
dotnet build
```

Expected: Build succeeded with 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/RemoteFileSync/Network/SyncServer.cs src/RemoteFileSync/Network/SyncClient.cs
git commit -m "feat: add SyncServer and SyncClient with full protocol orchestration"
git push
```

---

## Task 13: Wire Up Program.cs — Main Orchestration

**Files:**
- Modify: `src/RemoteFileSync/Program.cs`

- [ ] **Step 1: Update Program.cs to wire server/client based on mode**

Update the `Main` method in `src/RemoteFileSync/Program.cs` — replace the TODO comment and the return statement after it:

Replace:

```csharp
        // TODO: Wire up server/client orchestration in a later task
        return 0;
```

With:

```csharp
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            logger.Warning("Cancellation requested...");
        };

        try
        {
            if (options.IsServer)
            {
                var server = new Network.SyncServer(options, logger);
                return await server.RunAsync(cts.Token);
            }
            else
            {
                var client = new Network.SyncClient(options, logger);
                return await client.RunAsync(cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            logger.Summary("Operation cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            logger.Error($"Fatal error: {ex.Message}");
            return 3;
        }
```

- [ ] **Step 2: Verify build succeeds**

```bash
dotnet build
```

Expected: Build succeeded with 0 errors.

- [ ] **Step 3: Run all tests**

```bash
dotnet test
```

Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/RemoteFileSync/Program.cs
git commit -m "feat: wire up Program.cs with server/client orchestration and Ctrl+C handling"
git push
```

---

## Task 14: Integration Test — End-to-End Sync

**Files:**
- Create: `tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs`

- [ ] **Step 1: Write the integration test**

Create `tests/RemoteFileSync.Tests/Integration/EndToEndTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Backup;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.Sync;
using RemoteFileSync.Transfer;

namespace RemoteFileSync.Tests.Integration;

public class EndToEndTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _serverDir;
    private readonly string _clientDir;

    public EndToEndTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"rfs_e2e_{Guid.NewGuid()}");
        _serverDir = Path.Combine(_testRoot, "server");
        _clientDir = Path.Combine(_testRoot, "client");
        Directory.CreateDirectory(_serverDir);
        Directory.CreateDirectory(_clientDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private void CreateFile(string baseDir, string relativePath, string content)
    {
        var fullPath = Path.Combine(baseDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private void CreateFileWithTimestamp(string baseDir, string relativePath,
        string content, DateTime utcTimestamp)
    {
        var fullPath = Path.Combine(baseDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        File.SetLastWriteTimeUtc(fullPath, utcTimestamp);
    }

    [Fact]
    public async Task UniDirectional_ClientPushesToServer()
    {
        // Client has files, server is empty
        CreateFile(_clientDir, "readme.txt", "Hello from client");
        CreateFile(_clientDir, "sub/data.csv", "col1,col2\n1,2");

        int port = GetFreePort();
        var serverOpts = new SyncOptions
        {
            IsServer = true, Port = port, Folder = _serverDir
        };
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port,
            Folder = _clientDir, Bidirectional = false
        };

        using var serverLogger = new SyncLogger(false, null);
        using var clientLogger = new SyncLogger(false, null);

        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);

        // Small delay to let server start
        await Task.Delay(200);
        var clientResult = await client.RunAsync(cts.Token);

        var serverResult = await serverTask;

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        Assert.True(File.Exists(Path.Combine(_serverDir, "readme.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "sub", "data.csv")));
        Assert.Equal("Hello from client",
            File.ReadAllText(Path.Combine(_serverDir, "readme.txt")));
    }

    [Fact]
    public async Task BiDirectional_BothSidesSync()
    {
        var older = new DateTime(2026, 3, 20, 12, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);

        // Shared file: client has newer version
        CreateFileWithTimestamp(_clientDir, "shared.txt", "client newer", newer);
        CreateFileWithTimestamp(_serverDir, "shared.txt", "server older", older);

        // Client-only file
        CreateFile(_clientDir, "client-only.txt", "only on client");

        // Server-only file
        CreateFile(_serverDir, "server-only.txt", "only on server");

        int port = GetFreePort();
        var serverOpts = new SyncOptions
        {
            IsServer = true, Port = port, Folder = _serverDir
        };
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port,
            Folder = _clientDir, Bidirectional = true
        };

        using var serverLogger = new SyncLogger(false, null);
        using var clientLogger = new SyncLogger(false, null);

        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(200);
        var clientResult = await client.RunAsync(cts.Token);
        var serverResult = await serverTask;

        // shared.txt: client was newer → server should have client's version
        Assert.Equal("client newer",
            File.ReadAllText(Path.Combine(_serverDir, "shared.txt")));

        // client-only.txt: should be on server now
        Assert.True(File.Exists(Path.Combine(_serverDir, "client-only.txt")));

        // server-only.txt: should be on client now (bidi)
        Assert.True(File.Exists(Path.Combine(_clientDir, "server-only.txt")));
        Assert.Equal("only on server",
            File.ReadAllText(Path.Combine(_clientDir, "server-only.txt")));

        // Server's old shared.txt should be backed up
        var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
        var backupPath = Path.Combine(_serverDir, dateStr, "shared.txt");
        Assert.True(File.Exists(backupPath));
        Assert.Equal("server older", File.ReadAllText(backupPath));
    }

    [Fact]
    public async Task IdenticalFiles_NothingTransferred()
    {
        var ts = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "same.txt", "identical", ts);
        CreateFileWithTimestamp(_serverDir, "same.txt", "identical", ts);

        int port = GetFreePort();
        var serverOpts = new SyncOptions
        {
            IsServer = true, Port = port, Folder = _serverDir
        };
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port,
            Folder = _clientDir, Bidirectional = true
        };

        using var serverLogger = new SyncLogger(false, null);
        using var clientLogger = new SyncLogger(false, null);

        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(200);
        var clientResult = await client.RunAsync(cts.Token);
        var serverResult = await serverTask;

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);

        // No backup folder should be created
        var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
        Assert.False(Directory.Exists(Path.Combine(_serverDir, dateStr)));
        Assert.False(Directory.Exists(Path.Combine(_clientDir, dateStr)));
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
```

- [ ] **Step 2: Run the integration tests**

```bash
dotnet test --filter "EndToEndTests" --timeout 60000
```

Expected: All 3 integration tests pass.

- [ ] **Step 3: Commit**

```bash
git add tests/RemoteFileSync.Tests/Integration/
git commit -m "test: add end-to-end integration tests for uni/bi-directional sync"
git push
```

---

## Task 15: Build Verification and Publish

**Files:**
- None new — verification only

- [ ] **Step 1: Run all tests**

```bash
dotnet test --verbosity normal
```

Expected: All tests pass (unit + integration).

- [ ] **Step 2: Build Release**

```bash
cd E:\RemoteFileSync
dotnet publish src/RemoteFileSync/RemoteFileSync.csproj -c Release -r win-x64
```

Expected: Produces a single `RemoteFileSync.exe` in `src/RemoteFileSync/bin/Release/net10.0/win-x64/publish/`.

- [ ] **Step 3: Verify the executable runs**

```bash
src/RemoteFileSync/bin/Release/net10.0/win-x64/publish/RemoteFileSync.exe --help 2>&1 || true
```

Expected: Shows usage text (exits with code 3 since no valid mode was given, which is expected).

- [ ] **Step 4: Verify server mode starts and stops**

```bash
timeout 3 src/RemoteFileSync/bin/Release/net10.0/win-x64/publish/RemoteFileSync.exe server --folder . --port 19999 2>&1 || true
```

Expected: "Listening on port 19999..." message before timeout.

- [ ] **Step 5: Add .gitignore and commit final state**

Create `E:\RemoteFileSync\.gitignore`:

```
bin/
obj/
*.user
*.suo
.vs/
*.tmp
```

```bash
git add .gitignore
git commit -m "chore: add .gitignore for build artifacts"
git push
```

- [ ] **Step 6: Final commit — tag the milestone**

```bash
git tag v0.1.0
git push origin v0.1.0
```

---

## Spec Coverage Verification

| Spec Requirement | Implementing Task |
|---|---|
| §1 Single executable, CLI-configured | Task 1 (csproj), Task 4 (CLI parser), Task 15 (publish) |
| §2 Project structure / zero NuGet | Task 1 (scaffolding), Task 2–11 (all source files) |
| §3 CLI options (all flags) | Task 2 (SyncOptions), Task 4 (parser) |
| §4 Binary TCP protocol | Task 10 (ProtocolHandler), Task 12 (Server/Client) |
| §5 Sync engine (conflict resolution) | Task 6 (ConflictResolver), Task 7 (SyncEngine) |
| §5.3 Backup to dated folders | Task 9 (BackupManager) |
| §6 GZip compression + detection | Task 8 (CompressionHelper) |
| §6.3 File transfer with SHA256 | Task 11 (FileTransfer) |
| §6.4 Multi-threaded (SemaphoreSlim) | Task 12 (infra present, default threads=1) |
| §7 Logging (verbose, log file) | Task 3 (SyncLogger) |
| §7.4 Error handling (retry, skip) | Task 12 (SyncClient retry logic) |
| §7.5 Exit codes | Task 12 (return values), Task 13 (Program.cs) |
| §8 Build & publish single file | Task 15 (publish verification) |
| §5.2 Timestamp ±2s tolerance | Task 6 (ConflictResolver) |
| §3.4 Block size clamping | Task 2 (SyncOptions.Validate) |
| Bi-directional sync | Task 7 (SyncEngine), Task 12 (both directions), Task 14 (E2E test) |
| Include/exclude glob filtering | Task 5 (FileScanner) |
