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
