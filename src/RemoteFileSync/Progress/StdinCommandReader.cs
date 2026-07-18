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
                        // Release anyone blocked in WaitWhilePaused: this loop is about to
                        // exit, so RESUME can never arrive and a PAUSE+STOP would otherwise
                        // block the sync thread forever.
                        PauseGate.Set();
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

    /// <summary>
    /// Blocks while paused. Honours both the caller's token (Ctrl+C) and STOP.
    /// Returns false if the sync should stop.
    /// </summary>
    public bool WaitWhilePaused(CancellationToken ct)
    {
        PauseGate.Wait(ct);
        return !StopToken.IsCancellationRequested;
    }

    public void Dispose()
    {
        if (_input == null) return; // Null instance is a shared singleton — never dispose it
        // Never tear down primitives with a waiter still blocked on them.
        PauseGate.Set();
        if (!StopToken.IsCancellationRequested) StopToken.Cancel();
        StopToken.Dispose();
        PauseGate.Dispose();
    }
}
