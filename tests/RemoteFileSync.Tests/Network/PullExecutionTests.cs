using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;

namespace RemoteFileSync.Tests.Network;

/// <summary>
/// Acceptance coverage for --mode pull, driven through a real SyncServer and a real SyncClient.
///
/// ModeGateTests proves the predicate; this proves the predicate is actually wired into both
/// session loops. That distinction is the whole point: before this phase a Pull run planned
/// SendToClient correctly and then dropped every entry, because the client's receive loop was
/// gated on `_options.Bidirectional` (false in Pull) and the server's send loop on
/// `mode == SyncMode.TwoWay`. The run exited 0 having moved nothing, which no plan-level
/// assertion would have caught. So these assert file CONTENT on the receiving side.
/// </summary>
public class PullExecutionTests : IDisposable
{
    private readonly string _root;
    private readonly string _clientDir;
    private readonly string _serverDir;

    public PullExecutionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rfs_pull_{Guid.NewGuid()}");
        _clientDir = Path.Combine(_root, "client");
        _serverDir = Path.Combine(_root, "server");
        Directory.CreateDirectory(_clientDir);
        Directory.CreateDirectory(_serverDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Runs one complete session between a real server and a real client in <paramref name="mode"/>,
    /// and returns the client's exit code.
    /// </summary>
    private async Task<int> RunSessionAsync(SyncMode mode)
    {
        int port = GetFreePort();

        var serverOpts = new SyncOptions
        {
            IsServer = true, Once = true, BindAddress = "127.0.0.1", Port = port,
            Folder = _serverDir,
            ArchiveFolder = Path.Combine(_root, "server-archive"),
            BackupFolder = Path.Combine(_root, "server-backup"),
        };
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port,
            Folder = _clientDir, Mode = mode,
            ArchiveFolder = Path.Combine(_root, "client-archive"),
            BackupFolder = Path.Combine(_root, "client-backup"),
        };

        using var serverLogger = new SyncLogger(verbose: false, logFile: null, suppressConsole: true);
        using var clientLogger = new SyncLogger(verbose: false, logFile: null, suppressConsole: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var serverTask = new SyncServer(serverOpts, serverLogger).RunAsync(cts.Token);

        // SyncClient retries a refused connect three times at 2s intervals, which covers the
        // window before RunAsync has bound the listener.
        int clientExit = await new SyncClient(clientOpts, clientLogger).RunAsync(cts.Token);
        await serverTask;
        return clientExit;
    }

    [Fact]
    public async Task Pull_MovesTheServersBytesOntoTheClient()
    {
        const string content = "the server is authoritative in pull mode";
        File.WriteAllText(Path.Combine(_serverDir, "report.txt"), content);

        Assert.Equal(0, await RunSessionAsync(SyncMode.Pull));

        // The assertion that matters: not that the plan contained SendToClient, but that the
        // bytes are on disk on the receiving side.
        var landed = Path.Combine(_clientDir, "report.txt");
        Assert.True(File.Exists(landed), "Pull planned the download but never executed it.");
        Assert.Equal(content, File.ReadAllText(landed));
    }

    [Fact]
    public async Task Pull_DoesNotPushTheClientsOwnFilesOverTheAuthoritativeServer()
    {
        // Pull means the server wins. Uploading here would be the inverse of what the user asked
        // for, and with the client's send loop ungated that is exactly what a Pull run did.
        File.WriteAllText(Path.Combine(_clientDir, "local-only.txt"), "client scratch");

        Assert.Equal(0, await RunSessionAsync(SyncMode.Pull));

        Assert.False(File.Exists(Path.Combine(_serverDir, "local-only.txt")),
            "Pull uploaded a client-only file to the authoritative server.");
        // ...and left the client's own copy alone: Pull without --delete is additive downward,
        // never a local wipe.
        Assert.True(File.Exists(Path.Combine(_clientDir, "local-only.txt")));
    }

    [Fact]
    public async Task Push_WritesNothingToTheClient()
    {
        // The mirror image, and the regression this phase's widening could plausibly introduce:
        // ServerToClient must stay false for Push, or the client starts blocking on a receive
        // loop for frames a Push server never sends.
        File.WriteAllText(Path.Combine(_serverDir, "server-only.txt"), "server scratch");
        File.WriteAllText(Path.Combine(_clientDir, "mine.txt"), "client content");

        Assert.Equal(0, await RunSessionAsync(SyncMode.Push));

        Assert.False(File.Exists(Path.Combine(_clientDir, "server-only.txt")),
            "Push wrote a server file onto the client.");
        Assert.Equal("client content", File.ReadAllText(Path.Combine(_serverDir, "mine.txt")));
    }
}
