using System.Diagnostics;
using System.IO;
using ExecRFS.Models;

namespace ExecRFS.Services;

public sealed class ProcessManager : IDisposable
{
    private readonly string _role;
    private Process? _process;
    private SyncInstanceState _state = SyncInstanceState.Idle;

    public SyncInstanceState State
    {
        get => _state;
        private set { _state = value; OnStateChanged?.Invoke(value); }
    }

    public event Action<ProgressEvent>? OnProgress;
    public event Action<string>? OnLogLine;
    public event Action<SyncInstanceState>? OnStateChanged;
    public event Action<int>? OnExited;

    public ProcessManager(string role) { _role = role; }

    public void Start(SyncProfile profile, string? exePath = null)
    {
        if (_process != null && !HasExitedSafely(_process)) return;

        // Restarting without this leaks a handle and a stdin pipe on every run.
        _process?.Dispose();
        _process = null;

        State = SyncInstanceState.Starting;

        string resolvedExe;
        string args;
        try
        {
            resolvedExe = exePath ?? ResolveExePath();
            var fullCmd = CommandBuilder.BuildForProcess(profile, _role == "server");
            args = fullCmd.Substring(fullCmd.IndexOf(' ') + 1);
        }
        catch (Exception ex)
        {
            // Never throw into a Blazor event handler, and never leave the UI wedged at
            // Starting with no way back.
            State = SyncInstanceState.Error;
            OnLogLine?.Invoke($"[ERR] {ex.Message}");
            return;
        }

        // Every handler binds to this local, not the _process field: the field is nulled
        // above and reassigned below, so a field-bound handler would dereference null.
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = resolvedExe, Arguments = args,
                RedirectStandardOutput = true, RedirectStandardInput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            },
            EnableRaisingEvents = true   // load-bearing: without it Exited never fires
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            OnLogLine?.Invoke(e.Data);
            var evt = ProgressEvent.TryParse(e.Data);
            if (evt != null)
            {
                OnProgress?.Invoke(evt);
                if (evt.Event == "status" && evt.State == "paused") State = SyncInstanceState.Paused;
                else if (evt.Event == "status" && evt.State == "resumed") State = SyncInstanceState.Running;
                else if (evt.Event == "complete") State = SyncInstanceState.Stopped;
                else if (evt.Event == "error" && evt.Fatal == true) State = SyncInstanceState.Error;
            }
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) OnLogLine?.Invoke($"[STDERR] {e.Data}"); };
        process.Exited += (_, _) =>
        {
            var code = process.ExitCode;   // local capture: survives restart and dispose
            if (State != SyncInstanceState.Error) State = SyncInstanceState.Stopped;
            OnExited?.Invoke(code);
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            State = SyncInstanceState.Error;
            OnLogLine?.Invoke($"[ERR] Failed to start {resolvedExe}: {ex.Message}");
            process.Dispose();
            return;
        }

        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // A child that exits instantly (bad args) has already fired Exited; overwriting
        // Stopped/Error with Running would make Stop() no-op and wedge the UI.
        if (State == SyncInstanceState.Starting)
            State = SyncInstanceState.Running;
    }

    /// <summary>HasExited throws InvalidOperationException on a Process never started.</summary>
    private static bool HasExitedSafely(Process p)
    {
        try { return p.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    public void Pause() { if (State == SyncInstanceState.Running) WriteStdin("PAUSE"); }
    public void Resume() { if (State == SyncInstanceState.Paused) WriteStdin("RESUME"); }
    public void Stop()
    {
        var target = _process;
        if (target == null || HasExitedSafely(target)) return;
        State = SyncInstanceState.Stopping;
        WriteStdin("STOP");
        // Capture `target`: binding to the field killed a NEWLY RESTARTED process when a
        // stop/start cycle happened inside the 5s window.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(5000);
                if (!HasExitedSafely(target))
                {
                    target.Kill(entireProcessTree: true);
                    if (ReferenceEquals(target, _process)) State = SyncInstanceState.Stopped;
                }
            }
            catch (Exception ex) { OnLogLine?.Invoke($"[ERR] Kill failed: {ex.Message}"); }
        });
    }

    private void WriteStdin(string cmd)
    {
        try { _process?.StandardInput.WriteLine(cmd); _process?.StandardInput.Flush(); }
        catch { }
    }

    private static string ResolveExePath()
    {
        // 1. Development: sibling project build output (checked first — most common during dev)
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var devPaths = new[]
        {
            Path.GetFullPath(Path.Combine(appDir, @"..\..\..\..\RemoteFileSync\bin\Debug\net10.0\win-x64\RemoteFileSync.exe")),
            Path.GetFullPath(Path.Combine(appDir, @"..\..\..\..\RemoteFileSync\bin\Release\net10.0\win-x64\RemoteFileSync.exe")),
        };
        foreach (var devPath in devPaths)
        {
            if (IsValidExe(devPath)) return devPath;
        }

        // 2. Same directory as ExecRFS.exe (production: published single-file)
        var local = Path.Combine(appDir, "RemoteFileSync.exe");
        if (IsValidExe(local)) return local;

        // 3. PATH
        foreach (var dir in Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [])
        {
            var candidate = Path.Combine(dir, "RemoteFileSync.exe");
            if (IsValidExe(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            "RemoteFileSync.exe not found. Build RemoteFileSync first, or place it alongside ExecRFS.exe, or add to PATH.");
    }

    /// <summary>
    /// Checks that the exe exists and its companion .dll is also present
    /// (required for non-single-file builds from dotnet build).
    /// For published single-file builds, the dll is embedded so only the exe needs to exist.
    /// </summary>
    private static bool IsValidExe(string exePath)
    {
        if (!File.Exists(exePath)) return false;
        // Check for companion dll (non-single-file build)
        var dllPath = Path.ChangeExtension(exePath, ".dll");
        if (File.Exists(dllPath)) return true;
        // No dll = might be a published single-file exe — check size > 1MB as heuristic
        var fi = new FileInfo(exePath);
        return fi.Length > 1_000_000;
    }

    public void Dispose()
    {
        var p = _process;
        if (p == null) return;
        try { if (!HasExitedSafely(p)) p.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
        p.Dispose();
        _process = null;
    }
}
