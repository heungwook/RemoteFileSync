using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.Integration;

public class DatabaseDeleteSyncTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _serverDir;
    private readonly string _clientDir;
    private readonly string _dbDir;

    public DatabaseDeleteSyncTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"rfs_dbdel_e2e_{Guid.NewGuid()}");
        _serverDir = Path.Combine(_testRoot, "server");
        _clientDir = Path.Combine(_testRoot, "client");
        _dbDir = Path.Combine(_testRoot, "db");
        Directory.CreateDirectory(_serverDir);
        Directory.CreateDirectory(_clientDir);
        Directory.CreateDirectory(_dbDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }

    private void CreateFileWithTimestamp(string baseDir, string relativePath, string content, DateTime utcTimestamp)
    {
        var fullPath = Path.Combine(baseDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        File.SetLastWriteTimeUtc(fullPath, utcTimestamp);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task<(int clientResult, int serverResult)> RunSyncAsync(
        int port, bool bidirectional, bool deleteEnabled, SyncDatabase? db = null)
    {
        var serverOpts = new SyncOptions { IsServer = true, Port = port, Folder = _serverDir, DeleteEnabled = deleteEnabled };
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Bidirectional = bidirectional, DeleteEnabled = deleteEnabled };

        using var serverLogger = new SyncLogger(false, null);
        using var clientLogger = new SyncLogger(false, null);

        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger, db: db);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(500);
        var clientResult = await client.RunAsync(cts.Token);
        var serverResult = await serverTask;
        return (clientResult, serverResult);
    }

    [Fact]
    public async Task PartialSync_NoResurrection()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);

        // Run 1: sync both files
        CreateFileWithTimestamp(_clientDir, "keep.txt", "keep", ts);
        CreateFileWithTimestamp(_serverDir, "keep.txt", "keep", ts);
        CreateFileWithTimestamp(_clientDir, "to-delete.txt", "delete me", ts);
        CreateFileWithTimestamp(_serverDir, "to-delete.txt", "delete me", ts);

        var dbPath = Path.Combine(_dbDir, "sync.db");
        int port = GetFreePort();

        using (var db = new SyncDatabase(dbPath))
        {
            await RunSyncAsync(port, bidirectional: true, deleteEnabled: true, db);
        }

        // Between syncs: client deletes a file
        File.Delete(Path.Combine(_clientDir, "to-delete.txt"));

        // Run 2: should detect deletion and propagate
        int port2 = GetFreePort();
        using (var db = new SyncDatabase(dbPath))
        {
            await RunSyncAsync(port2, bidirectional: true, deleteEnabled: true, db);
        }

        // to-delete.txt should be gone from server (backed up)
        Assert.False(File.Exists(Path.Combine(_serverDir, "to-delete.txt")));
        // keep.txt should still exist
        Assert.True(File.Exists(Path.Combine(_clientDir, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "keep.txt")));
    }

    [Fact]
    public async Task NewVsDeleted_DistinguishesCorrectly()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var dbPath = Path.Combine(_dbDir, "sync2.db");

        // Run 1: sync a file
        CreateFileWithTimestamp(_clientDir, "was-synced.txt", "synced once", ts);
        CreateFileWithTimestamp(_serverDir, "was-synced.txt", "synced once", ts);

        int port = GetFreePort();
        using (var db = new SyncDatabase(dbPath))
        {
            await RunSyncAsync(port, bidirectional: true, deleteEnabled: true, db);
        }

        // Remove from client (intentional delete)
        File.Delete(Path.Combine(_clientDir, "was-synced.txt"));

        // Add a brand-new file on server (never synced before)
        CreateFileWithTimestamp(_serverDir, "brand-new.txt", "new file", ts);

        // Run 2:
        int port2 = GetFreePort();
        using (var db = new SyncDatabase(dbPath))
        {
            await RunSyncAsync(port2, bidirectional: true, deleteEnabled: true, db);
        }

        // was-synced.txt: was in db as 'exists', now deleted on client → should be deleted on server
        Assert.False(File.Exists(Path.Combine(_serverDir, "was-synced.txt")));

        // brand-new.txt: NOT in db → treated as genuinely new → copied to client
        Assert.True(File.Exists(Path.Combine(_clientDir, "brand-new.txt")));
    }
}
