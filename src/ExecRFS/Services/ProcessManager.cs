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
        if (_process != null && !_process.HasExited) return;
        State = SyncInstanceState.Starting;
        var resolvedExe = exePath ?? ResolveExePath();
        var fullCmd = CommandBuilder.BuildForProcess(profile, _role == "server");
        var args = fullCmd.Substring(fullCmd.IndexOf(' ') + 1);

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = resolvedExe, Arguments = args,
                RedirectStandardOutput = true, RedirectStandardInput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
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
                if (evt.Event == "status" && evt.State == "paused") State = SyncInstanceState.Paused;
                else if (evt.Event == "status" && evt.State == "resumed") State = SyncInstanceState.Running;
                else if (evt.Event == "complete") State = SyncInstanceState.Stopped;
                else if (evt.Event == "error" && evt.Fatal == true) State = SyncInstanceState.Error;
            }
        };
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) OnLogLine?.Invoke($"[STDERR] {e.Data}"); };
        _process.Exited += (_, _) =>
        {
            var code = _process.ExitCode;
            if (State != SyncInstanceState.Error) State = SyncInstanceState.Stopped;
            OnExited?.Invoke(code);
        };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        State = SyncInstanceState.Running;
    }

    public void Pause() { if (State == SyncInstanceState.Running) WriteStdin("PAUSE"); }
    public void Resume() { if (State == SyncInstanceState.Paused) WriteStdin("RESUME"); }
    public void Stop()
    {
        if (_process == null || _process.HasExited) return;
        State = SyncInstanceState.Stopping;
        WriteStdin("STOP");
        Task.Run(async () => {
            await Task.Delay(5000);
            if (_process != null && !_process.HasExited) { _process.Kill(entireProcessTree: true); State = SyncInstanceState.Stopped; }
        });
    }

    private void WriteStdin(string cmd)
    {
        try { _process?.StandardInput.WriteLine(cmd); _process?.StandardInput.Flush(); }
        catch { }
    }

    private static string ResolveExePath()
    {
        var local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RemoteFileSync.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [])
        {
            var candidate = Path.Combine(dir, "RemoteFileSync.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("RemoteFileSync.exe not found. Place it alongside ExecRFS.exe or add to PATH.");
    }

    public void Dispose()
    {
        if (_process != null && !_process.HasExited) _process.Kill(entireProcessTree: true);
        _process?.Dispose();
    }
}
