using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;

namespace RemoteFileSync.Tests.Integration;

/// <summary>
/// A per-file send failure that is NOT a peer disconnect — a locked or removed source file, a
/// compression or disk error — must be terminal for the transfer phase, not a silent skip.
/// Both peers size their receive loop positionally from the one shared plan, so skipping a file
/// the receiver still expects shifts every subsequent frame and confirm by one. The fix aborts
/// the phase (exit 3) exactly as a real desync or disconnect already did.
/// </summary>
public class SendFailureAbortsTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _serverDir;
    private readonly string _clientDir;

    public SendFailureAbortsTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"rfs_sendfail_{Guid.NewGuid()}");
        _serverDir = Path.Combine(_testRoot, "server");
        _clientDir = Path.Combine(_testRoot, "client");
        Directory.CreateDirectory(_serverDir);
        Directory.CreateDirectory(_clientDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }

    // A FileShare.None handle lets the scanner's metadata read (size + mtime) still succeed — so
    // the file enters the manifest and the plan — while SendFileAsync's compress/read throws a
    // sharing-violation IOException, the exact non-disconnect failure the fix must make terminal.
    private static FileStream LockExclusive(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.None);

    [Fact]
    public async Task ClientSendFailure_AbortsTransferPhaseWithExitThree()
    {
        var locked = Path.Combine(_clientDir, "locked.txt");
        File.WriteAllText(locked, "unreadable while exclusively locked");

        int port = GetFreePort();
        var serverOpts = new SyncOptions { IsServer = true, Once = true, Port = port, Folder = _serverDir };
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Mode = SyncMode.Push,
        };

        using var serverLogger = new SyncLogger(false, null, suppressConsole: true);
        using var clientLogger = new SyncLogger(false, null, suppressConsole: true);
        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int clientExit;
        using (LockExclusive(locked))
        {
            var serverTask = server.RunAsync(cts.Token);
            await Task.Delay(500);
            clientExit = await client.RunAsync(cts.Token);
            try { await serverTask; } catch { /* the server faults on the torn connection */ }
        }

        Assert.Equal(3, clientExit);
        // The abort must leave nothing partial or misattributed committed on the server.
        Assert.False(File.Exists(Path.Combine(_serverDir, "locked.txt")));
    }

    [Fact]
    public async Task ServerSendFailure_AbortsTransferPhaseWithExitThree()
    {
        var locked = Path.Combine(_serverDir, "locked.txt");
        File.WriteAllText(locked, "unreadable while exclusively locked");

        int port = GetFreePort();
        var serverOpts = new SyncOptions { IsServer = true, Once = true, Port = port, Folder = _serverDir };
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Mode = SyncMode.Pull,
        };

        using var serverLogger = new SyncLogger(false, null, suppressConsole: true);
        using var clientLogger = new SyncLogger(false, null, suppressConsole: true);
        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int serverExit;
        using (LockExclusive(locked))
        {
            var serverTask = server.RunAsync(cts.Token);
            await Task.Delay(500);
            var clientTask = client.RunAsync(cts.Token);
            serverExit = await serverTask;
            try { await clientTask; } catch { /* the client faults on the torn connection */ }
        }

        Assert.Equal(3, serverExit);
        Assert.False(File.Exists(Path.Combine(_clientDir, "locked.txt")));
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
