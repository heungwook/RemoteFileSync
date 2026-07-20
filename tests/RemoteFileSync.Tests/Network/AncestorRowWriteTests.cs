using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.Network;

public class AncestorRowWriteTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _serverDir;
    private readonly string _clientDir;
    private readonly string _dbDir;

    public AncestorRowWriteTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"rfs_ancestorwrite_{Guid.NewGuid()}");
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

    private static void CreateFileWithTimestamp(string baseDir, string relativePath,
                                                string content, DateTime utcTimestamp)
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

    private async Task<(int clientResult, int serverResult)> RunPushAsync(int port, SyncDatabase db)
    {
        var serverOpts = new SyncOptions
        {
            IsServer = true, Once = true, Port = port, Folder = _serverDir, DeleteEnabled = true,
        };
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir,
            Mode = SyncMode.Push, DeleteEnabled = true,
        };

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
    public async Task Push_SkippedServerOnlyFile_WritesNoAncestorRow()
    {
        // A server-only file is planned Skip in Push (no row proves the client ever had it).
        // Recording an ancestor row for it asserts "both sides agreed on these bytes", which is
        // a state that never existed — and the Push table reads that row on the next run as
        // "the client had it", which is licence to delete.
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_serverDir, "server-only.txt", "only on the server", ts);

        var dbPath = Path.Combine(_dbDir, "sync.db");
        int port = GetFreePort();

        using (var db = new SyncDatabase(dbPath))
        {
            var (clientResult, serverResult) = await RunPushAsync(port, db);
            Assert.Equal(0, clientResult);
            Assert.Equal(0, serverResult);
        }

        using (var db = new SyncDatabase(dbPath))
        {
            Assert.Null(db.GetRow("server-only.txt"));
        }
    }

    [Fact]
    public async Task Push_SecondRun_DoesNotDeleteFileTheClientNeverHad()
    {
        // The end-to-end consequence of the fabricated row: run 1 invents the ancestor, run 2
        // reads it back as consensus and deletes the user's server-only file. Only one deletion
        // would be planned, which is below MinTrackedFilesForDeleteGuard, so neither the client
        // nor the server blast-radius guard intervenes — nothing stops it but this fix.
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_serverDir, "server-only.txt", "only on the server", ts);

        var dbPath = Path.Combine(_dbDir, "sync2.db");

        using (var db = new SyncDatabase(dbPath))
        {
            await RunPushAsync(GetFreePort(), db);
        }

        using (var db = new SyncDatabase(dbPath))
        {
            await RunPushAsync(GetFreePort(), db);
        }

        Assert.True(File.Exists(Path.Combine(_serverDir, "server-only.txt")));
    }
}
