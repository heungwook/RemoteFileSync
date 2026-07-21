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
/// Acceptance tests for the ancestor-based merge, over a real loopback socket. These exist
/// because every unit-level merge bug this redesign fixed presented in the field as data loss
/// across a full client/server round trip, not as a wrong return value.
/// </summary>
public class TwoWayMergeE2ETests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _serverDir;
    private readonly string _clientDir;
    private readonly string _dbDir;

    public TwoWayMergeE2ETests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"rfs_merge_e2e_{Guid.NewGuid()}");
        _serverDir = Path.Combine(_testRoot, "server");
        _clientDir = Path.Combine(_testRoot, "client");
        _dbDir = Path.Combine(_testRoot, "db");
        Directory.CreateDirectory(_serverDir);
        Directory.CreateDirectory(_clientDir);
        Directory.CreateDirectory(_dbDir);
    }

    public void Dispose()
    {
        // SQLite keeps the file handle in a connection pool; without this the temp tree
        // cannot be deleted and every run leaks a directory.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }

    private string DbPath => Path.Combine(_dbDir, "sync.db");

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
    /// One full client/server sync. Once=true on the server, or the test hangs waiting for a
    /// second connection that never arrives.
    ///
    /// The client is given a database *path* and opens the database itself, after its
    /// no-ancestor gate has run. Handing it an already-open SyncDatabase would mean the test
    /// created the file whose absence the gate keys on, disarming it for every case here —
    /// Task 10.9 most of all. Post-run assertions open their own instance once this returns.
    /// </summary>
    private async Task<(int clientResult, int serverResult)> RunSyncAsync(
        SyncMode mode, bool deleteEnabled = true, bool mirror = false)
    {
        int port = GetFreePort();
        var serverOpts = new SyncOptions
        {
            IsServer = true, Once = true, Port = port, Folder = _serverDir,
            DeleteEnabled = deleteEnabled,
        };
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir,
            Mode = mode, DeleteEnabled = deleteEnabled, MirrorDeletes = mirror,
        };

        using var serverLogger = new SyncLogger(false, null);
        using var clientLogger = new SyncLogger(false, null);
        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger, dbPath: DbPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(500);
        var clientResult = await client.RunAsync(cts.Token);

        // A safety guard aborts the client before or during the session, which tears the socket
        // down under the server too. The server's exit code is not the subject of those tests,
        // and letting the fault escape here would mask the client code the test is asserting on.
        int serverResult;
        try { serverResult = await serverTask; } catch { serverResult = -1; }
        return (clientResult, serverResult);
    }

    /// <summary>
    /// Runs one clean sync so the ancestor table and pair.marker exist. Every "peer-only file
    /// survives" test needs this: on a first run the additive-only rule suppresses all
    /// deletions before the Push/Pull table is ever consulted, so the assertion would pass
    /// even against a table that deletes unconditionally.
    /// </summary>
    private async Task PrimeAsync(SyncMode mode)
    {
        var (clientResult, _) = await RunSyncAsync(mode);
        Assert.Equal(0, clientResult);
        // Written by SyncClient itself on a clean exit — nothing in this file writes it, so this
        // assertion is also the check that the client arms the gate for the runs that follow.
        Assert.True(PairMarker.Exists(DbPath));
        // The client's own SyncDatabase is disposed by now, but Microsoft.Data.Sqlite pools the
        // handle. Release it so Task 10.9 can delete the file the way the field loses it.
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task TwoWay_ClientDelete_RemovesServerCopyAndTombstonesRow()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "gone.txt", "bye", ts);
        CreateFileWithTimestamp(_clientDir, "stay.txt", "keep", ts);

        await PrimeAsync(SyncMode.TwoWay);
        Assert.True(File.Exists(Path.Combine(_serverDir, "gone.txt")));

        File.Delete(Path.Combine(_clientDir, "gone.txt"));

        var (clientResult, _) = await RunSyncAsync(SyncMode.TwoWay);
        Assert.Equal(0, clientResult);

        using (var db = new SyncDatabase(DbPath))
        {
            var row = db.GetRow("gone.txt");
            Assert.NotNull(row);
            Assert.Equal("deleted", row!.Status);
            // A tombstone with no timestamp can never be purged, so the table grows forever.
            Assert.NotNull(row.DeletedUtcTicks);
            Assert.Equal("exists", db.GetRow("stay.txt")!.Status);
        }

        Assert.False(File.Exists(Path.Combine(_serverDir, "gone.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "stay.txt")));
        AssertArchived(Path.Combine(_testRoot, ".rfs-archive-server"), ArchiveReason.Deleted, "gone.txt");
    }

    [Fact]
    public async Task TwoWay_ClientDeleteVsServerEdit_RestoresFileAndLogsResurrection()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var later = new DateTime(2026, 3, 27, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "contested.txt", "original", ts);

        await PrimeAsync(SyncMode.TwoWay);

        // Rule [2]: an edit outranks a deletion. Deleting the peer's newer work because the
        // local copy vanished is the single most destructive outcome this design prevents.
        File.Delete(Path.Combine(_clientDir, "contested.txt"));
        CreateFileWithTimestamp(_serverDir, "contested.txt", "server edited it", later);

        var (clientResult, _) = await RunSyncAsync(SyncMode.TwoWay);
        Assert.Equal(0, clientResult);

        using (var db = new SyncDatabase(DbPath))
        {
            // The restore is surprising to the user, so it must surface in the review report
            // rather than happening silently. This is the only end-to-end check that the client
            // actually drains PlanResult.Resurrections into the database — a drain Phase 7 owns,
            // in the same edit block and at the same anchor as its conflict drain.
            var sessionId = db.GetRecentSessions(1).First().Id;
            Assert.Contains(db.GetSessionResurrections(sessionId),
                r => r.Path.Equals("contested.txt", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("exists", db.GetRow("contested.txt")!.Status);
        }

        Assert.True(File.Exists(Path.Combine(_clientDir, "contested.txt")));
        Assert.Equal("server edited it", File.ReadAllText(Path.Combine(_clientDir, "contested.txt")));
        Assert.Equal("server edited it", File.ReadAllText(Path.Combine(_serverDir, "contested.txt")));
    }

    [Fact]
    public async Task TwoWay_EditBothSides_KeepsBothCopiesWithRenamedLoser()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "shared.txt", "original", ts);

        await PrimeAsync(SyncMode.TwoWay);

        CreateFileWithTimestamp(_clientDir, "shared.txt", "client edit",
            new DateTime(2026, 3, 27, 9, 0, 0, DateTimeKind.Utc));
        CreateFileWithTimestamp(_serverDir, "shared.txt", "server edit",
            new DateTime(2026, 3, 27, 11, 0, 0, DateTimeKind.Utc));

        var (clientResult, _) = await RunSyncAsync(SyncMode.TwoWay);
        Assert.Equal(0, clientResult);

        using (var db = new SyncDatabase(DbPath))
        {
            var sessionId = db.GetRecentSessions(1).First().Id;
            Assert.Contains(db.GetSessionConflicts(sessionId),
                c => c.Path.Equals("shared.txt", StringComparison.OrdinalIgnoreCase));
        }

        // Neither edit may be lost. Each side ends with the winner under the original name and
        // the loser beside it — picking a winner and discarding the other is silent data loss.
        foreach (var dir in new[] { _clientDir, _serverDir })
        {
            var conflicts = Directory.GetFiles(dir, "shared.conflict-*.txt");
            Assert.Single(conflicts);
            Assert.Matches(@"shared\.conflict-\d{8}-\d{6}-(client|server)\.txt$", conflicts[0]);

            var contents = new[]
            {
                File.ReadAllText(Path.Combine(dir, "shared.txt")),
                File.ReadAllText(conflicts[0]),
            };
            Assert.Contains("client edit", contents);
            Assert.Contains("server edit", contents);
        }
    }

    [Fact]
    public async Task Push_ServerOnlyFile_SurvivesWithoutMirror()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "pushed.txt", "from client", ts);

        // The priming run is what gives this test teeth: without it the run is an
        // additive-only first run, deletions are suppressed before the Push table is
        // consulted, and the assertion would hold against a table that deletes blindly.
        await PrimeAsync(SyncMode.Push);
        CreateFileWithTimestamp(_serverDir, "server-only.txt", "server keeps this", ts);

        var (clientResult, _) = await RunSyncAsync(SyncMode.Push, deleteEnabled: true, mirror: false);

        Assert.Equal(0, clientResult);
        Assert.True(File.Exists(Path.Combine(_serverDir, "pushed.txt")));
        // No ancestor row ever said the client had this file, so its absence on the client is
        // not evidence of a deletion. Deleting it destroys files the client never knew about.
        Assert.True(File.Exists(Path.Combine(_serverDir, "server-only.txt")));
        AssertNothingArchived(Path.Combine(_testRoot, ".rfs-archive-server"));
    }

    [Fact]
    public async Task Push_Mirror_DeletesServerOnlyFile()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "pushed.txt", "from client", ts);

        await PrimeAsync(SyncMode.Push);
        CreateFileWithTimestamp(_serverDir, "server-only.txt", "server loses this", ts);

        var (clientResult, _) = await RunSyncAsync(SyncMode.Push, deleteEnabled: true, mirror: true);

        Assert.Equal(0, clientResult);
        Assert.True(File.Exists(Path.Combine(_serverDir, "pushed.txt")));
        // --mirror is the explicit "make the peer identical" opt-in: history stops mattering.
        Assert.False(File.Exists(Path.Combine(_serverDir, "server-only.txt")));
        AssertArchived(Path.Combine(_testRoot, ".rfs-archive-server"), ArchiveReason.Deleted, "server-only.txt");
    }

    [Fact]
    public async Task Pull_ClientOnlyFile_SurvivesWithoutMirror()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_serverDir, "pulled.txt", "from server", ts);

        await PrimeAsync(SyncMode.Pull);
        CreateFileWithTimestamp(_clientDir, "client-only.txt", "client keeps this", ts);

        var (clientResult, _) = await RunSyncAsync(SyncMode.Pull, deleteEnabled: true, mirror: false);

        Assert.Equal(0, clientResult);
        Assert.True(File.Exists(Path.Combine(_clientDir, "pulled.txt")));
        // Exact mirror of the Push case: no ancestor row, so no evidence of a deletion.
        Assert.True(File.Exists(Path.Combine(_clientDir, "client-only.txt")));
        AssertNothingArchived(Path.Combine(_testRoot, ".rfs-archive-client"));
    }

    [Fact]
    public async Task Pull_Mirror_DeletesClientOnlyFile()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_serverDir, "pulled.txt", "from server", ts);

        await PrimeAsync(SyncMode.Pull);
        CreateFileWithTimestamp(_clientDir, "client-only.txt", "client loses this", ts);

        var (clientResult, _) = await RunSyncAsync(SyncMode.Pull, deleteEnabled: true, mirror: true);

        Assert.Equal(0, clientResult);
        Assert.True(File.Exists(Path.Combine(_clientDir, "pulled.txt")));
        Assert.False(File.Exists(Path.Combine(_clientDir, "client-only.txt")));
        // The deleting side archives into its own root. Archiving under .rfs-archive-server
        // would mean the server is destroying files it does not own.
        AssertArchived(Path.Combine(_testRoot, ".rfs-archive-client"), ArchiveReason.Deleted, "client-only.txt");
        AssertNothingArchived(Path.Combine(_testRoot, ".rfs-archive-server"));
    }

    [Fact]
    public async Task ThreeIdenticalRuns_Converge_NoTransfersOrDeletesAfterTheFirst()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "a.txt", "alpha", ts);
        CreateFileWithTimestamp(_clientDir, Path.Combine("sub", "b.txt"), "bravo", ts);
        CreateFileWithTimestamp(_serverDir, "c.txt", "charlie", ts);

        for (int i = 0; i < 3; i++)
        {
            var (clientResult, _) = await RunSyncAsync(SyncMode.TwoWay);
            Assert.Equal(0, clientResult);
        }

        // Opened after all three runs, so nothing here created the database ahead of the gate.
        using var db = new SyncDatabase(DbPath);
        // GetRecentSessions is newest-first (ORDER BY id DESC): [0] = run 3, [1] = run 2. A
        // merge that keeps re-sending or re-deleting the same files never settles, and that
        // ping-pong is invisible to a per-file assertion but obvious in the session counters.
        var sessions = db.GetRecentSessions(3).ToList();
        Assert.Equal(3, sessions.Count);
        Assert.Equal(0, sessions[0].FilesTransferred);
        Assert.Equal(0, sessions[0].FilesDeleted);
        Assert.Equal(0, sessions[1].FilesTransferred);
        Assert.Equal(0, sessions[1].FilesDeleted);

        // And the tree really did converge.
        Assert.True(File.Exists(Path.Combine(_serverDir, "a.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "sub", "b.txt")));
        Assert.True(File.Exists(Path.Combine(_clientDir, "c.txt")));
    }

    /// <summary>
    /// Deletes the database file and its WAL sidecars, leaving pair.marker in place. This is
    /// how the loss presents in the field: a restored profile, or a cleaned %LOCALAPPDATA%,
    /// takes sync.db but leaves the marker behind.
    ///
    /// Nothing may reopen the database between here and the run under test. `new SyncDatabase(p)`
    /// re-creates the file, so a test that wrapped its run in `using var db = new
    /// SyncDatabase(DbPath)` would put back the very file SyncClient.PairStateLost looks for and
    /// the gate could never fire — the test would pass 0 and prove nothing.
    /// </summary>
    private void LoseDatabaseKeepMarker()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(DbPath);
        foreach (var sidecar in Directory.GetFiles(_dbDir, "sync.db-*"))
            File.Delete(sidecar);
        Assert.False(File.Exists(DbPath));
        Assert.True(PairMarker.Exists(DbPath));
    }

    [Fact]
    public async Task LostDatabase_WithSurvivingPairMarker_AbortsWithoutDeleting()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "one.txt", "1", ts);
        CreateFileWithTimestamp(_clientDir, "two.txt", "2", ts);

        await PrimeAsync(SyncMode.TwoWay);
        LoseDatabaseKeepMarker();

        // No SyncDatabase is opened here, deliberately: the gate keys on the file being absent.
        var (clientResult, _) = await RunSyncAsync(SyncMode.TwoWay);
        // An absent database beside a marker a previous run wrote means "state lost", not
        // "nothing was ever synced". Treating it as a first run and rebuilding the ancestor
        // from the two live trees would resurrect everything either side deleted while the
        // database was gone.
        Assert.Equal(4, clientResult);
        // The refusal happens before anything opens the database, so it is still absent.
        Assert.False(File.Exists(DbPath));

        Assert.True(File.Exists(Path.Combine(_clientDir, "one.txt")));
        Assert.True(File.Exists(Path.Combine(_clientDir, "two.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "one.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "two.txt")));
    }

    [Fact]
    public async Task LostDatabase_WithMirror_ProceedsInsteadOfAborting()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "one.txt", "1", ts);

        await PrimeAsync(SyncMode.TwoWay);
        LoseDatabaseKeepMarker();

        // --mirror is the documented escape hatch: the user has declared which side is
        // authoritative, so missing history is no longer a reason to refuse. Asserting the
        // success code rather than merely "not 4" — NotEqual(4) would also pass on a connection
        // failure (2) or a protocol abort (3), which is no proof at all. Again no SyncDatabase
        // is opened here: the client rebuilds it itself once the gate has waved the run through.
        var (clientResult, _) = await RunSyncAsync(SyncMode.TwoWay, mirror: true);
        Assert.Equal(0, clientResult);

        Assert.True(File.Exists(Path.Combine(_clientDir, "one.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "one.txt")));
    }
}
