using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.Integration;

public class ConflictKeepBothSyncTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _serverDir;
    private readonly string _clientDir;
    private readonly string _dbDir;

    public ConflictKeepBothSyncTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"rfs_conflict_e2e_{Guid.NewGuid()}");
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

    private async Task<(int clientResult, int serverResult)> RunTwoWaySyncAsync(SyncDatabase db)
    {
        int port = GetFreePort();
        var serverOpts = new SyncOptions
        {
            IsServer = true, Once = true, Port = port, Folder = _serverDir,
            Mode = SyncMode.TwoWay, DeleteEnabled = true,
        };
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir,
            Mode = SyncMode.TwoWay, DeleteEnabled = true,
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

    /// <summary>
    /// Same wiring, but the server's outcome is observed and discarded. A client that aborts at
    /// the rename pass has already sent the plan and then goes away, so the server fails on a
    /// transfer that never arrives — its exit code is not what the abort tests pin, and an
    /// unobserved faulted task would surface later as an unrelated failure.
    /// </summary>
    private async Task<int> RunTwoWaySyncExpectingClientAbortAsync(SyncDatabase db)
    {
        int port = GetFreePort();
        var serverOpts = new SyncOptions
        {
            IsServer = true, Once = true, Port = port, Folder = _serverDir,
            Mode = SyncMode.TwoWay, DeleteEnabled = true,
        };
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir,
            Mode = SyncMode.TwoWay, DeleteEnabled = true,
        };

        using var serverLogger = new SyncLogger(false, null);
        using var clientLogger = new SyncLogger(false, null);

        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger, db: db);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(500);
        int clientResult = await client.RunAsync(cts.Token);
        try { await serverTask; } catch { /* expected: the peer went away mid-plan */ }
        return clientResult;
    }

    /// <summary>
    /// Mirror of <see cref="RunTwoWaySyncExpectingClientAbortAsync"/>: here the SERVER is the one
    /// whose conflict rename fails and returns 4, after it has already read the client's plan.
    /// Unlike the client-abort direction, the server's own return path does no further network
    /// I/O, so its task resolves normally — it is the CLIENT that is left mid-plan on a socket
    /// the server just closed, which is exactly the path SyncClient.cs must fail cleanly through.
    /// </summary>
    private async Task<(int clientResult, int serverResult)> RunTwoWaySyncExpectingServerAbortAsync(SyncDatabase db)
    {
        int port = GetFreePort();
        var serverOpts = new SyncOptions
        {
            IsServer = true, Once = true, Port = port, Folder = _serverDir,
            Mode = SyncMode.TwoWay, DeleteEnabled = true,
        };
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir,
            Mode = SyncMode.TwoWay, DeleteEnabled = true,
        };

        using var serverLogger = new SyncLogger(false, null);
        using var clientLogger = new SyncLogger(false, null);

        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger, db: db);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(500);
        int clientResult = await client.RunAsync(cts.Token);
        int serverResult = await serverTask;
        return (clientResult, serverResult);
    }

    [Fact]
    public async Task ServerConflictRenameFailure_ClientReportsCleanErrorInsteadOfCrashing()
    {
        // Mirror of ConflictRenameFailure_AbortsAboveTheAncestorWriteBlock, but the LOSING side
        // is the server: the server's own ApplyLocalRenames fails and SyncServer.cs returns 4
        // having already read the client's plan (SyncServer.cs:294). Nothing tells the client —
        // there is no frame for "I'm aborting" once the plan has been read — so its stream just
        // goes dead the next time it tries to use it. Before the client-side fix this either
        // spammed one logged error per remaining file and then threw WriteMessageAsync's
        // exception uncaught out of the delete phase, or hung; now it must stop the phase and
        // return a clean non-zero exit code through the normal path.
        var baseTs = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var dbPath = Path.Combine(_dbDir, "server-rename-abort.db");

        // Run 1 establishes an ancestor row for report.txt.
        CreateFileWithTimestamp(_clientDir, "report.txt", "original", baseTs);
        CreateFileWithTimestamp(_serverDir, "report.txt", "original", baseTs);
        using (var db = new SyncDatabase(dbPath))
            await RunTwoWaySyncAsync(db);

        // Client copy is newer this time, so the SERVER owns the rename and the SERVER loses.
        CreateFileWithTimestamp(_clientDir, "report.txt", "client edit", baseTs.AddHours(2));
        CreateFileWithTimestamp(_serverDir, "report.txt", "server edit", baseTs.AddHours(1));

        // Hold the server's losing copy open with FileShare.None so File.Move throws IOException
        // inside the server's ApplyLocalRenames — the same technique the client-side abort test
        // uses, aimed at the other peer's disk.
        using (var locked = new FileStream(Path.Combine(_serverDir, "report.txt"),
                   FileMode.Open, FileAccess.Read, FileShare.None))
        using (var db = new SyncDatabase(dbPath))
        {
            var (clientResult, serverResult) = await RunTwoWaySyncExpectingServerAbortAsync(db);
            Assert.Equal(4, serverResult);
            // 3 = the client's own "peer disconnected mid-plan" exit path. Anything else means
            // either the crash the review reported (an unhandled exception aborts the await
            // above before clientResult is even assigned) or a silent success that lost data.
            Assert.Equal(3, clientResult);
        }

        // Nothing was destroyed: the failed rename left the server's file exactly where it was.
        Assert.Equal("server edit", File.ReadAllText(Path.Combine(_serverDir, "report.txt")));
        Assert.Empty(Directory.GetFiles(_serverDir, "report.conflict-*"));
    }

    [Fact]
    public async Task TwoWayConflict_ClientCopyLosesWhenServerCopyIsNewer()
    {
        var baseTs = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var dbPath = Path.Combine(_dbDir, "conflict.db");

        // Run 1: establish an ancestor row so run 2 can see that BOTH sides changed.
        CreateFileWithTimestamp(_clientDir, "report.txt", "original", baseTs);
        CreateFileWithTimestamp(_serverDir, "report.txt", "original", baseTs);
        using (var db = new SyncDatabase(dbPath))
            await RunTwoWaySyncAsync(db);

        // Both sides edit the same path. The server copy is newer, so the client copy loses.
        CreateFileWithTimestamp(_clientDir, "report.txt", "client edit", baseTs.AddHours(1));
        CreateFileWithTimestamp(_serverDir, "report.txt", "server edit", baseTs.AddHours(2));

        using (var db = new SyncDatabase(dbPath))
        {
            var (clientResult, serverResult) = await RunTwoWaySyncAsync(db);
            Assert.Equal(0, clientResult);
            Assert.Equal(0, serverResult);
        }

        // The winner keeps the canonical name on both peers.
        Assert.Equal("server edit", File.ReadAllText(Path.Combine(_clientDir, "report.txt")));
        Assert.Equal("server edit", File.ReadAllText(Path.Combine(_serverDir, "report.txt")));

        // The loser survives under the conflict name on both peers, under the SAME name.
        var clientLosers = Directory.GetFiles(_clientDir, "report.conflict-*-client.txt");
        var serverLosers = Directory.GetFiles(_serverDir, "report.conflict-*-client.txt");
        Assert.Single(clientLosers);
        Assert.Single(serverLosers);
        Assert.Equal(Path.GetFileName(clientLosers[0]), Path.GetFileName(serverLosers[0]));
        Assert.Equal("client edit", File.ReadAllText(clientLosers[0]));
        Assert.Equal("client edit", File.ReadAllText(serverLosers[0]));
    }

    [Fact]
    public async Task TwoWayConflict_ServerCopyLosesWhenClientCopyIsNewer()
    {
        var baseTs = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var dbPath = Path.Combine(_dbDir, "conflict-serverloses.db");

        CreateFileWithTimestamp(_clientDir, "notes.md", "original", baseTs);
        CreateFileWithTimestamp(_serverDir, "notes.md", "original", baseTs);
        using (var db = new SyncDatabase(dbPath))
            await RunTwoWaySyncAsync(db);

        // Client copy is newer this time, so the SERVER copy is the loser — the case that pure
        // client-side expansion cannot express.
        CreateFileWithTimestamp(_clientDir, "notes.md", "client edit", baseTs.AddHours(2));
        CreateFileWithTimestamp(_serverDir, "notes.md", "server edit", baseTs.AddHours(1));

        using (var db = new SyncDatabase(dbPath))
        {
            var (clientResult, serverResult) = await RunTwoWaySyncAsync(db);
            Assert.Equal(0, clientResult);
            Assert.Equal(0, serverResult);
        }

        Assert.Equal("client edit", File.ReadAllText(Path.Combine(_clientDir, "notes.md")));
        Assert.Equal("client edit", File.ReadAllText(Path.Combine(_serverDir, "notes.md")));

        var clientLosers = Directory.GetFiles(_clientDir, "notes.conflict-*-server.md");
        var serverLosers = Directory.GetFiles(_serverDir, "notes.conflict-*-server.md");
        Assert.Single(clientLosers);
        Assert.Single(serverLosers);
        Assert.Equal(Path.GetFileName(clientLosers[0]), Path.GetFileName(serverLosers[0]));
        Assert.Equal("server edit", File.ReadAllText(clientLosers[0]));
        Assert.Equal("server edit", File.ReadAllText(serverLosers[0]));
    }

    [Fact]
    public async Task TwoWayConflict_TwoConflictsInOneSession_BothDirectionsSurviveTogether()
    {
        // The single-conflict tests above each exercise one loser direction in isolation. A
        // desync bug that only shows up once the plan carries MORE than one ConflictKeepBoth
        // entry — e.g. an off-by-one in which peer reads which frame — would pass both of those
        // and still corrupt a real multi-file sync. Mixing a client-loses and a server-loses
        // conflict in the SAME plan is the smallest session that can catch that: it is also the
        // only combination where the two peers execute their local rename passes over DIFFERENT
        // subsets of the same plan (see ConflictKeepBothExecutor.ApplyLocalRenames's `side`
        // filter) before either side's transfer phase begins.
        var baseTs = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var dbPath = Path.Combine(_dbDir, "conflict-multi.db");

        // Run 1 establishes ancestor rows for both files.
        CreateFileWithTimestamp(_clientDir, "report.txt", "original", baseTs);
        CreateFileWithTimestamp(_serverDir, "report.txt", "original", baseTs);
        CreateFileWithTimestamp(_clientDir, "notes.md", "original", baseTs);
        CreateFileWithTimestamp(_serverDir, "notes.md", "original", baseTs);
        using (var db = new SyncDatabase(dbPath))
            await RunTwoWaySyncAsync(db);

        // report.txt: server edit is newer, so the CLIENT copy loses.
        CreateFileWithTimestamp(_clientDir, "report.txt", "client edit", baseTs.AddHours(1));
        CreateFileWithTimestamp(_serverDir, "report.txt", "server edit", baseTs.AddHours(2));
        // notes.md: client edit is newer, so the SERVER copy loses.
        CreateFileWithTimestamp(_clientDir, "notes.md", "client edit", baseTs.AddHours(2));
        CreateFileWithTimestamp(_serverDir, "notes.md", "server edit", baseTs.AddHours(1));

        using (var db = new SyncDatabase(dbPath))
        {
            var (clientResult, serverResult) = await RunTwoWaySyncAsync(db);
            Assert.Equal(0, clientResult);
            Assert.Equal(0, serverResult);
        }

        // Both winners landed under their canonical name on both peers.
        Assert.Equal("server edit", File.ReadAllText(Path.Combine(_clientDir, "report.txt")));
        Assert.Equal("server edit", File.ReadAllText(Path.Combine(_serverDir, "report.txt")));
        Assert.Equal("client edit", File.ReadAllText(Path.Combine(_clientDir, "notes.md")));
        Assert.Equal("client edit", File.ReadAllText(Path.Combine(_serverDir, "notes.md")));

        // Both losers survived under the SAME renamed name on both peers.
        var reportClientLosers = Directory.GetFiles(_clientDir, "report.conflict-*-client.txt");
        var reportServerLosers = Directory.GetFiles(_serverDir, "report.conflict-*-client.txt");
        Assert.Single(reportClientLosers);
        Assert.Single(reportServerLosers);
        Assert.Equal(Path.GetFileName(reportClientLosers[0]), Path.GetFileName(reportServerLosers[0]));
        Assert.Equal("client edit", File.ReadAllText(reportClientLosers[0]));
        Assert.Equal("client edit", File.ReadAllText(reportServerLosers[0]));

        var notesClientLosers = Directory.GetFiles(_clientDir, "notes.conflict-*-server.md");
        var notesServerLosers = Directory.GetFiles(_serverDir, "notes.conflict-*-server.md");
        Assert.Single(notesClientLosers);
        Assert.Single(notesServerLosers);
        Assert.Equal(Path.GetFileName(notesClientLosers[0]), Path.GetFileName(notesServerLosers[0]));
        Assert.Equal("server edit", File.ReadAllText(notesClientLosers[0]));
        Assert.Equal("server edit", File.ReadAllText(notesServerLosers[0]));

        // All four resulting files, on both sides — nothing was skipped, duplicated, or blended.
        Assert.Equal(4, Directory.GetFiles(_clientDir).Length);
        Assert.Equal(4, Directory.GetFiles(_serverDir).Length);
    }

    [Fact]
    public async Task ConflictCopy_IsNotResyncedByTheNextScan()
    {
        var baseTs = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var dbPath = Path.Combine(_dbDir, "conflict-stable.db");

        CreateFileWithTimestamp(_clientDir, "report.txt", "original", baseTs);
        CreateFileWithTimestamp(_serverDir, "report.txt", "original", baseTs);
        using (var db = new SyncDatabase(dbPath))
            await RunTwoWaySyncAsync(db);

        CreateFileWithTimestamp(_clientDir, "report.txt", "client edit", baseTs.AddHours(1));
        CreateFileWithTimestamp(_serverDir, "report.txt", "server edit", baseTs.AddHours(2));
        using (var db = new SyncDatabase(dbPath))
            await RunTwoWaySyncAsync(db);

        var loser = Path.GetFileName(Directory.GetFiles(_clientDir, "report.conflict-*-client.txt").Single());

        // Run 3 must be a no-op: the conflict copy is byte- and mtime-identical on both peers,
        // so it converges instead of ping-ponging as a "new" file forever.
        using (var db = new SyncDatabase(dbPath))
        {
            var (clientResult, serverResult) = await RunTwoWaySyncAsync(db);
            Assert.Equal(0, clientResult);
            Assert.Equal(0, serverResult);
        }

        Assert.Single(Directory.GetFiles(_clientDir, "report.conflict-*"));
        Assert.Single(Directory.GetFiles(_serverDir, "report.conflict-*"));
        Assert.Equal("client edit", File.ReadAllText(Path.Combine(_clientDir, loser)));
        Assert.Equal("client edit", File.ReadAllText(Path.Combine(_serverDir, loser)));
    }

    [Fact]
    public async Task Conflict_IsLoggedAsAnEncodedConflictDetail()
    {
        // The review report decodes this column. A free-form English sentence parses to null
        // there and the report silently degrades to "no sizes, no mtimes" for every real
        // conflict — the exact defect this assertion exists to prevent.
        var baseTs = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var dbPath = Path.Combine(_dbDir, "conflict-detail.db");

        CreateFileWithTimestamp(_clientDir, "report.txt", "original", baseTs);
        CreateFileWithTimestamp(_serverDir, "report.txt", "original", baseTs);
        using (var db = new SyncDatabase(dbPath))
            await RunTwoWaySyncAsync(db);

        CreateFileWithTimestamp(_clientDir, "report.txt", "client edit!!", baseTs.AddHours(1));
        CreateFileWithTimestamp(_serverDir, "report.txt", "server edit", baseTs.AddHours(2));

        long sessionId;
        using (var db = new SyncDatabase(dbPath))
        {
            var (clientResult, _) = await RunTwoWaySyncAsync(db);
            Assert.Equal(0, clientResult);
            // GetRecentSessions orders by id DESC, so limit 1 is the run that just finished.
            sessionId = db.GetRecentSessions(1).First().Id;
        }

        using (var db = new SyncDatabase(dbPath))
        {
            var conflicts = db.GetSessionConflicts(sessionId);
            var row = Assert.Single(conflicts, c => c.Path == "report.txt");
            var decoded = ConflictDetail.Decode(row.Detail);
            Assert.NotNull(decoded);
            Assert.Equal("client edit!!".Length, decoded!.ClientSize);
            Assert.Equal("server edit".Length, decoded.ServerSize);
            Assert.Equal(baseTs.AddHours(1).Ticks, decoded.ClientMtimeTicks);
            Assert.NotNull(decoded.RenamedTo);
            Assert.EndsWith("-client.txt", decoded.RenamedTo!);
        }
    }

    [Fact]
    public async Task Resurrection_IsLoggedAsAnEncodedConflictDetail()
    {
        // The resurrection drain shares this phase's edit block with the conflict drain. Without
        // it GetSessionResurrections returns empty forever and the review report's resurrection
        // section is permanently dead, with nothing else in the suite noticing.
        var baseTs = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var dbPath = Path.Combine(_dbDir, "resurrection-detail.db");

        CreateFileWithTimestamp(_clientDir, "kept.txt", "original", baseTs);
        CreateFileWithTimestamp(_serverDir, "kept.txt", "original", baseTs);
        using (var db = new SyncDatabase(dbPath))
            await RunTwoWaySyncAsync(db);

        // The server deletes it; the client edits it. The edit wins and the file is resurrected.
        File.Delete(Path.Combine(_serverDir, "kept.txt"));
        CreateFileWithTimestamp(_clientDir, "kept.txt", "client edit", baseTs.AddHours(1));

        long sessionId;
        using (var db = new SyncDatabase(dbPath))
        {
            var (clientResult, _) = await RunTwoWaySyncAsync(db);
            Assert.Equal(0, clientResult);
            sessionId = db.GetRecentSessions(1).First().Id;
        }

        using (var db = new SyncDatabase(dbPath))
        {
            var row = Assert.Single(db.GetSessionResurrections(sessionId), r => r.Path == "kept.txt");
            var decoded = ConflictDetail.Decode(row.Detail);
            Assert.NotNull(decoded);
            // The client's copy survived, so its columns are measured...
            Assert.Equal("client edit".Length, decoded!.ClientSize);
            Assert.Equal(baseTs.AddHours(1).Ticks, decoded.ClientMtimeTicks);
            // ...and the deleted side has nothing to measure, so its columns are 0.
            Assert.Equal(0, decoded.ServerSize);
            Assert.Equal(0, decoded.ServerMtimeTicks);
            Assert.Null(decoded.RenamedTo);
        }
    }

    [Fact]
    public async Task ConflictRenameFailure_AbortsAboveTheAncestorWriteBlock()
    {
        // The ordering guarantee this phase exists to create: the rename pass can return 4, and
        // no ancestor row may survive a run that returned 4. If the ancestor-write block were
        // left above this pass, the assertion at the bottom would find a committed row for a
        // file no completed sync ever confirmed, and the next run would plan deletions against it.
        var baseTs = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var dbPath = Path.Combine(_dbDir, "conflict-rename-abort.db");

        // Run 1 establishes an ancestor row for report.txt only.
        CreateFileWithTimestamp(_clientDir, "report.txt", "original", baseTs);
        CreateFileWithTimestamp(_serverDir, "report.txt", "original", baseTs);
        using (var db = new SyncDatabase(dbPath))
            await RunTwoWaySyncAsync(db);

        // Two-sided edit with the server copy newer, so the CLIENT owns the rename.
        CreateFileWithTimestamp(_clientDir, "report.txt", "client edit", baseTs.AddHours(1));
        CreateFileWithTimestamp(_serverDir, "report.txt", "server edit", baseTs.AddHours(2));

        // A brand-new, byte- and mtime-identical pair. It plans as Skip and has no ancestor row
        // from run 1, so it is precisely the row the ancestor-write block would create -- if the
        // block ran. Nothing else in the run can write it.
        CreateFileWithTimestamp(_clientDir, "settled.txt", "same", baseTs);
        CreateFileWithTimestamp(_serverDir, "settled.txt", "same", baseTs);

        // Hold the losing copy open with FileShare.None. Scanning and planning read metadata
        // only, so the plan still says ConflictKeepBoth; File.Move then throws IOException inside
        // ApplyLocalRenames, which is the failure path the client must treat as fatal.
        using (var locked = new FileStream(Path.Combine(_clientDir, "report.txt"),
                   FileMode.Open, FileAccess.Read, FileShare.None))
        using (var db = new SyncDatabase(dbPath))
        {
            Assert.Equal(4, await RunTwoWaySyncExpectingClientAbortAsync(db));
        }

        using (var db = new SyncDatabase(dbPath))
        {
            // The abort happened above the ancestor-write block, so it committed nothing.
            Assert.Null(db.GetRow("settled.txt"));
        }

        // And the loser is still where it was: a failed rename destroys nothing.
        Assert.Equal("client edit", File.ReadAllText(Path.Combine(_clientDir, "report.txt")));
        Assert.Empty(Directory.GetFiles(_clientDir, "report.conflict-*"));
    }
}
