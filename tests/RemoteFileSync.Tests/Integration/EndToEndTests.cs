using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;

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
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }

    private void CreateFile(string baseDir, string relativePath, string content)
    {
        var fullPath = Path.Combine(baseDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private void CreateFileWithTimestamp(string baseDir, string relativePath, string content, DateTime utcTimestamp)
    {
        var fullPath = Path.Combine(baseDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        File.SetLastWriteTimeUtc(fullPath, utcTimestamp);
    }

    [Fact]
    public async Task UniDirectional_ClientPushesToServer()
    {
        CreateFile(_clientDir, "readme.txt", "Hello from client");
        CreateFile(_clientDir, Path.Combine("sub", "data.csv"), "col1,col2\n1,2");

        int port = GetFreePort();
        var serverOpts = new SyncOptions { IsServer = true, Port = port, Folder = _serverDir };
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Bidirectional = false };

        using var serverLogger = new SyncLogger(false, null);
        using var clientLogger = new SyncLogger(false, null);

        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(500);
        var clientResult = await client.RunAsync(cts.Token);
        var serverResult = await serverTask;

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        Assert.True(File.Exists(Path.Combine(_serverDir, "readme.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "sub", "data.csv")));
        Assert.Equal("Hello from client", File.ReadAllText(Path.Combine(_serverDir, "readme.txt")));
    }

    [Fact]
    public async Task BiDirectional_BothSidesSync()
    {
        var older = new DateTime(2026, 3, 20, 12, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);

        CreateFileWithTimestamp(_clientDir, "shared.txt", "client newer", newer);
        CreateFileWithTimestamp(_serverDir, "shared.txt", "server older", older);
        CreateFile(_clientDir, "client-only.txt", "only on client");
        CreateFile(_serverDir, "server-only.txt", "only on server");

        int port = GetFreePort();
        var serverOpts = new SyncOptions { IsServer = true, Port = port, Folder = _serverDir };
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Bidirectional = true };

        using var serverLogger = new SyncLogger(false, null);
        using var clientLogger = new SyncLogger(false, null);

        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(500);
        var clientResult = await client.RunAsync(cts.Token);
        var serverResult = await serverTask;

        // shared.txt: client newer -> server gets client version
        Assert.Equal("client newer", File.ReadAllText(Path.Combine(_serverDir, "shared.txt")));
        // client-only -> server
        Assert.True(File.Exists(Path.Combine(_serverDir, "client-only.txt")));
        // server-only -> client (bidi)
        Assert.True(File.Exists(Path.Combine(_clientDir, "server-only.txt")));
        Assert.Equal("only on server", File.ReadAllText(Path.Combine(_clientDir, "server-only.txt")));

        // Server's old shared.txt should be backed up — beside the sync folder, not inside it.
        var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
        var backupPath = Path.Combine(_testRoot, ".rfs-backups-server", dateStr, "shared.txt");
        Assert.True(File.Exists(backupPath), $"expected backup at {backupPath}");
        Assert.Equal("server older", File.ReadAllText(backupPath));
        // And nothing may have been written inside the sync folder itself.
        Assert.False(Directory.Exists(Path.Combine(_serverDir, dateStr)));
    }

    [Fact]
    public async Task IdenticalFiles_NothingTransferred()
    {
        var ts = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "same.txt", "identical", ts);
        CreateFileWithTimestamp(_serverDir, "same.txt", "identical", ts);

        int port = GetFreePort();
        var serverOpts = new SyncOptions { IsServer = true, Port = port, Folder = _serverDir };
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Bidirectional = true };

        using var serverLogger = new SyncLogger(false, null);
        using var clientLogger = new SyncLogger(false, null);

        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(500);
        var clientResult = await client.RunAsync(cts.Token);
        var serverResult = await serverTask;

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
        Assert.False(Directory.Exists(Path.Combine(_serverDir, dateStr)));
        Assert.False(Directory.Exists(Path.Combine(_clientDir, dateStr)));
    }

    /// <summary>
    /// Runs one full client/server sync against the shared test folders.
    /// The server is single-accept, so each sync needs a fresh instance.
    /// </summary>
    private async Task<(int client, int server)> RunSyncAsync(bool bidirectional)
    {
        int port = GetFreePort();
        var serverOpts = new SyncOptions { IsServer = true, Port = port, Folder = _serverDir };
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port,
            Folder = _clientDir, Bidirectional = bidirectional
        };

        using var serverLogger = new SyncLogger(false, null);
        using var clientLogger = new SyncLogger(false, null);

        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(500);
        var clientResult = await client.RunAsync(cts.Token);
        var serverResult = await serverTask;
        return (clientResult, serverResult);
    }

    [Fact]
    public async Task TransferredFile_HasSourceTimestamp()
    {
        var ts = new DateTime(2026, 3, 20, 9, 30, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "stamped.txt", "payload", ts);

        await RunSyncAsync(bidirectional: false);

        var dest = Path.Combine(_serverDir, "stamped.txt");
        Assert.True(File.Exists(dest));
        // Allow filesystem timestamp granularity, but not the multi-minute drift
        // that "receive time" would produce.
        Assert.True(Math.Abs((File.GetLastWriteTimeUtc(dest) - ts).TotalSeconds) < 2,
            $"Expected ~{ts:O} but got {File.GetLastWriteTimeUtc(dest):O}");
    }

    [Fact]
    public async Task SecondSync_TransfersNothing_WhenNothingChanged()
    {
        // Timestamps must be well in the past: if the source mtime were merely "now", a
        // receive-time stamp would land inside ConflictResolver's 2s tolerance and this
        // test would pass even with the bug present.
        var ts = new DateTime(2026, 3, 20, 12, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "a.txt", "alpha", ts);
        CreateFileWithTimestamp(_clientDir, Path.Combine("sub", "b.txt"), "bravo", ts);

        var first = await RunSyncAsync(bidirectional: true);
        Assert.Equal(0, first.client);

        // Capture what the server holds after the first sync.
        var afterFirst = Directory.GetFiles(_serverDir, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => File.GetLastWriteTimeUtc(p));
        Assert.Equal(2, afterFirst.Count);

        var second = await RunSyncAsync(bidirectional: true);
        Assert.Equal(0, second.client);

        // Nothing may be rewritten: identical file set, identical timestamps.
        // Before the timestamp fix every file re-transferred on every run forever.
        var afterSecond = Directory.GetFiles(_serverDir, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => File.GetLastWriteTimeUtc(p));

        Assert.Equal(afterFirst.Keys.OrderBy(k => k), afterSecond.Keys.OrderBy(k => k));
        foreach (var (path, stamp) in afterFirst)
            Assert.Equal(stamp, afterSecond[path]);
    }

    [Fact]
    public async Task ThreeBidirectionalSyncs_DoNotPingPong()
    {
        // See note in SecondSync_TransfersNothing_WhenNothingChanged: an old timestamp is
        // what gives this test teeth against the receive-time-stamping bug.
        CreateFileWithTimestamp(_clientDir, "shared.txt", "content",
            new DateTime(2026, 3, 20, 12, 0, 0, DateTimeKind.Utc));

        await RunSyncAsync(bidirectional: true);
        var clientStamp = File.GetLastWriteTimeUtc(Path.Combine(_clientDir, "shared.txt"));
        var serverStamp = File.GetLastWriteTimeUtc(Path.Combine(_serverDir, "shared.txt"));
        Assert.Equal(clientStamp, serverStamp);

        await RunSyncAsync(bidirectional: true);
        await RunSyncAsync(bidirectional: true);

        // Both sides must still agree, and neither may have been rewritten.
        Assert.Equal(clientStamp, File.GetLastWriteTimeUtc(Path.Combine(_clientDir, "shared.txt")));
        Assert.Equal(serverStamp, File.GetLastWriteTimeUtc(Path.Combine(_serverDir, "shared.txt")));
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
