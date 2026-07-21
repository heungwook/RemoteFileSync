using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.Integration;

/// <summary>
/// The deletion blast-radius guard: an empty or repointed peer folder must not be able to
/// wipe the other side.
/// </summary>
public class DeleteThresholdTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _serverDir;
    private readonly string _clientDir;
    private readonly string _stateDir;

    public DeleteThresholdTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"rfs_thresh_{Guid.NewGuid()}");
        _serverDir = Path.Combine(_testRoot, "server");
        _clientDir = Path.Combine(_testRoot, "client");
        _stateDir = Path.Combine(_testRoot, "state");
        Directory.CreateDirectory(_serverDir);
        Directory.CreateDirectory(_clientDir);
        Directory.CreateDirectory(_stateDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task<int> RunClientAsync(SyncDatabase db, bool forceDelete)
    {
        int port = GetFreePort();
        // The server enforces its blast-radius bound independently of the client — it does not
        // trust a plan from an unauthenticated peer — so an intentional bulk deletion needs
        // --force-delete on BOTH sides, which is what both guard messages now tell the operator.
        // Without this the server returns 4 immediately after reading the plan, the client (which
        // skipped its own guard) blocks on a read outside its try block, and the test errors on a
        // torn socket instead of asserting anything.
        var serverOpts = new SyncOptions { IsServer = true, Once = true, Port = port, Folder = _serverDir, ForceDelete = forceDelete };
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir,
            Mode = SyncMode.TwoWay, DeleteEnabled = true, ForceDelete = forceDelete,
        };

        using var serverLogger = new SyncLogger(false, null);
        using var clientLogger = new SyncLogger(false, null);
        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger, db: db);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(300);
        var result = await client.RunAsync(cts.Token);
        try { await serverTask; } catch { /* server may abort too */ }
        return result;
    }

    /// <summary>
    /// 20 files tracked as synced, then the server folder is emptied. Without the guard every
    /// one of them is deleted on the client.
    /// </summary>
    private SyncDatabase SeedTrackedFiles(int count)
    {
        var db = new SyncDatabase(Path.Combine(_stateDir, "state.db"));
        var session = db.StartSession("two-way+delete", _clientDir, "127.0.0.1", 1234);
        for (int i = 0; i < count; i++)
        {
            var name = $"file{i:D3}.txt";
            var full = Path.Combine(_clientDir, name);
            File.WriteAllText(full, $"content {i}");
            // The row must be read back from the file just written, never guessed. The old seed
            // hardcoded size 9 (wrong from file010.txt onward) and an mtime of UtcNow-1day, so
            // ChangeDetector.Unchanged reported "the client changed since the last sync" and the
            // plan came back with zero deletions — the threshold guard could not fire and this
            // suite passed 4-vs-0 for a reason unrelated to the guard it names.
            var fi = new FileInfo(full);
            db.UpsertSynced(name, fi.Length, fi.LastWriteTimeUtc.Ticks,
                                  fi.Length, fi.LastWriteTimeUtc.Ticks, session, "to_server");
        }
        db.CompleteSession(session, count, 0, 0, 0);
        return db;
    }

    [Fact]
    public async Task EmptyPeerFolder_AbortsInsteadOfMassDeleting()
    {
        using var db = SeedTrackedFiles(20);   // server folder is left empty

        var exit = await RunClientAsync(db, forceDelete: false);

        Assert.Equal(4, exit);   // aborted by a safety guard
        // Every client file must survive.
        Assert.Equal(20, Directory.GetFiles(_clientDir).Length);
    }

    [Fact]
    public async Task ForceDelete_OverridesTheThreshold()
    {
        using var db = SeedTrackedFiles(20);

        var exit = await RunClientAsync(db, forceDelete: true);

        Assert.NotEqual(4, exit);   // the guard did not block it
    }

    [Fact]
    public void SmallPopulations_AreExemptFromThePercentageGuard()
    {
        // Deleting 1 of 2 files is 50% but entirely ordinary. The guard must not fire below
        // the floor, or users learn to pass --force-delete by reflex.
        Assert.True(SyncOptions.MinTrackedFilesForDeleteGuard > 2);
    }
}
