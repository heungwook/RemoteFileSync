using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using RemoteFileSync.Backup;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.State;
using static RemoteFileSync.Tests.Integration.ArchiveAssertions;

namespace RemoteFileSync.Tests.Integration;

/// <summary>
/// Deletion propagation over a real socket, with the ancestor supplied by SyncDatabase.
/// These cases used to seed SyncStateManager; the binary-state ancestor was retired when
/// ComputePlan started taking an AncestorRow table, and a SyncStateManager seed now
/// influences nothing at all.
/// </summary>
public class DeleteSyncTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _serverDir;
    private readonly string _clientDir;
    private readonly string _stateDir;

    public DeleteSyncTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"rfs_del_e2e_{Guid.NewGuid()}");
        _serverDir = Path.Combine(_testRoot, "server");
        _clientDir = Path.Combine(_testRoot, "client");
        _stateDir = Path.Combine(_testRoot, "state");
        Directory.CreateDirectory(_serverDir);
        Directory.CreateDirectory(_clientDir);
        Directory.CreateDirectory(_stateDir);
    }

    public void Dispose()
    {
        // SQLite keeps the file handle in a connection pool; without this the temp tree
        // cannot be deleted and every run leaks a directory.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }

    private string DbPath => Path.Combine(_stateDir, "sync.db");

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

    /// <summary>
    /// Seeds an ancestor row asserting both sides held <paramref name="relativePath"/> at
    /// <paramref name="mtime"/> with <paramref name="size"/> bytes when they last agreed.
    /// </summary>
    private void SeedAncestor(SyncDatabase db, string relativePath, long size, DateTime mtime)
    {
        var session = db.StartSession("two-way+delete", _clientDir, "127.0.0.1", 1234);
        db.UpsertSynced(relativePath, size, mtime.Ticks, size, mtime.Ticks, session, "to_server");
        db.CompleteSession(session, 1, 0, 0, 0);
    }

    /// <summary>
    /// One full client/server sync. Once=true on the server, or the test hangs waiting for a
    /// second connection that never arrives.
    ///
    /// The client is handed a database *path*, never an open SyncDatabase: `new SyncDatabase(p)`
    /// creates the file, and the no-ancestor gate in SyncClient.RunAsync fires on "pair.marker
    /// present, database absent". A test that opened the database around the run would create the
    /// very file the gate looks for and silently disarm it. Post-run assertions therefore open
    /// their own short-lived instance after this method returns.
    /// </summary>
    private async Task<(int clientResult, int serverResult)> RunSyncAsync(
        SyncMode mode, bool deleteEnabled)
    {
        int port = GetFreePort();
        var serverOpts = new SyncOptions { IsServer = true, Once = true, Port = port, Folder = _serverDir, DeleteEnabled = deleteEnabled };
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Mode = mode, DeleteEnabled = deleteEnabled };

        using var serverLogger = new SyncLogger(false, null);
        using var clientLogger = new SyncLogger(false, null);

        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger, dbPath: DbPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(500);
        var clientResult = await client.RunAsync(cts.Token);
        var serverResult = await serverTask;
        return (clientResult, serverResult);
    }

    [Fact]
    public async Task DeleteSync_FirstRun_NoState_AdditiveOnly()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "client-file.txt", "from client", ts);
        CreateFileWithTimestamp(_serverDir, "server-file.txt", "from server", ts);

        var (clientResult, serverResult) = await RunSyncAsync(SyncMode.TwoWay, deleteEnabled: true);
        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);

        Assert.True(File.Exists(Path.Combine(_serverDir, "client-file.txt")));
        Assert.True(File.Exists(Path.Combine(_clientDir, "server-file.txt")));
        // A clean first run claims the pairing. From here on, a missing database is evidence of
        // lost state rather than of a tree that was never synced.
        Assert.True(PairMarker.Exists(DbPath));
        AssertNothingArchived(Path.Combine(_testRoot, ".rfs-archive-server"));
        AssertNothingArchived(Path.Combine(_testRoot, ".rfs-archive-client"));
    }

    [Fact]
    public async Task DeleteSync_Case1_PropagatesDeletion()
    {
        var beforeSync = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_serverDir, "to-delete.txt", "will be deleted", beforeSync);

        using (var seed = new SyncDatabase(DbPath))
            SeedAncestor(seed, "to-delete.txt", 15, beforeSync);
        SqliteConnection.ClearAllPools();

        var (clientResult, serverResult) = await RunSyncAsync(SyncMode.TwoWay, deleteEnabled: true);

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        Assert.False(File.Exists(Path.Combine(_serverDir, "to-delete.txt")));
        AssertArchived(Path.Combine(_testRoot, ".rfs-archive-server"), ArchiveReason.Deleted, "to-delete.txt");

        // Reopened only now, after the client has disposed the database it opened for itself.
        using var db = new SyncDatabase(DbPath);
        Assert.Equal("deleted", db.GetRow("to-delete.txt")!.Status);
    }

    [Fact]
    public async Task DeleteSync_Case2_RestoresModifiedFile()
    {
        var beforeSync = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var afterSync = new DateTime(2026, 3, 27, 8, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_serverDir, "modified.txt", "modified content", afterSync);

        using (var seed = new SyncDatabase(DbPath))
            SeedAncestor(seed, "modified.txt", 16, beforeSync);
        SqliteConnection.ClearAllPools();

        var (clientResult, serverResult) = await RunSyncAsync(SyncMode.TwoWay, deleteEnabled: true);

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        // Rule [2]: an edit outranks a deletion. Deleting the peer's newer work because the
        // local copy vanished is the single most destructive outcome this design prevents.
        Assert.True(File.Exists(Path.Combine(_serverDir, "modified.txt")));
        Assert.True(File.Exists(Path.Combine(_clientDir, "modified.txt")));
        Assert.Equal("modified content", File.ReadAllText(Path.Combine(_clientDir, "modified.txt")));

        // Restoring a file the user deleted is surprising, so it must be reported, not silent.
        // The writer is Phase 7's drain of PlanResult.Resurrections into LogResurrection, in the
        // same edit block as its conflict drain; without it this row is never written.
        using var db = new SyncDatabase(DbPath);
        var sessionId = db.GetRecentSessions(1).First().Id;
        Assert.Contains(db.GetSessionResurrections(sessionId),
            r => r.Path.Equals("modified.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeleteSync_BidiSymmetric()
    {
        var beforeSync = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_serverDir, "client-deleted.txt", "from server", beforeSync);
        CreateFileWithTimestamp(_clientDir, "server-deleted.txt", "from client", beforeSync);

        using (var seed = new SyncDatabase(DbPath))
        {
            SeedAncestor(seed, "client-deleted.txt", 11, beforeSync);
            SeedAncestor(seed, "server-deleted.txt", 11, beforeSync);
        }
        SqliteConnection.ClearAllPools();

        var (clientResult, serverResult) = await RunSyncAsync(SyncMode.TwoWay, deleteEnabled: true);

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        Assert.False(File.Exists(Path.Combine(_serverDir, "client-deleted.txt")));
        Assert.False(File.Exists(Path.Combine(_clientDir, "server-deleted.txt")));
        // Each side archives what it destroys, into its own archive root.
        AssertArchived(Path.Combine(_testRoot, ".rfs-archive-server"), ArchiveReason.Deleted, "client-deleted.txt");
        AssertArchived(Path.Combine(_testRoot, ".rfs-archive-client"), ArchiveReason.Deleted, "server-deleted.txt");
    }

    [Fact]
    public async Task Push_ServerSideDeletion_IsReSentBecauseTheClientIsAuthoritative()
    {
        // Renamed from DeleteSync_UniDirectional_ServerDeletionIgnored, and the expectation is
        // inverted on purpose: in Push mode the server is made to match the client, so
        // "client present, server absent" is SendToServer with no ancestor consulted. The old
        // behaviour — leave the gap alone — meant a file the user deleted on the server stayed
        // deleted while the client still held it, and the pair never converged.
        var beforeSync = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "file.txt", "still here", beforeSync);

        using (var seed = new SyncDatabase(DbPath))
            SeedAncestor(seed, "file.txt", 10, beforeSync);
        SqliteConnection.ClearAllPools();

        var (clientResult, serverResult) = await RunSyncAsync(SyncMode.Push, deleteEnabled: true);

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        Assert.True(File.Exists(Path.Combine(_clientDir, "file.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "file.txt")));
        Assert.Equal("still here", File.ReadAllText(Path.Combine(_serverDir, "file.txt")));
    }

    [Fact]
    public async Task DeleteSync_SecondRun_DetectsDeletions()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "keep.txt", "keep this", ts);
        CreateFileWithTimestamp(_serverDir, "keep.txt", "keep this", ts);
        CreateFileWithTimestamp(_clientDir, "will-delete.txt", "will be deleted", ts);
        CreateFileWithTimestamp(_serverDir, "will-delete.txt", "will be deleted", ts);

        var (r1c, r1s) = await RunSyncAsync(SyncMode.TwoWay, deleteEnabled: true);
        Assert.Equal(0, r1c);
        Assert.Equal(0, r1s);

        File.Delete(Path.Combine(_clientDir, "will-delete.txt"));

        var (r2c, r2s) = await RunSyncAsync(SyncMode.TwoWay, deleteEnabled: true);
        Assert.Equal(0, r2c);
        Assert.Equal(0, r2s);

        // Run 1 wrote pair.marker on its clean exit, and run 2 found the database still there,
        // so the no-ancestor gate stayed silent and run 2 is a genuine ancestor merge.
        using (var db = new SyncDatabase(DbPath))
        {
            Assert.Equal("deleted", db.GetRow("will-delete.txt")!.Status);
            Assert.Equal("exists", db.GetRow("keep.txt")!.Status);
        }

        Assert.False(File.Exists(Path.Combine(_serverDir, "will-delete.txt")));
        Assert.True(File.Exists(Path.Combine(_clientDir, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "keep.txt")));
    }
}
