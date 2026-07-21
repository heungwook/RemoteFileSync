using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.Network;

/// <summary>
/// The no-ancestor gate. It lives in SyncClient.RunAsync rather than Program.Main so it is
/// reachable without a live socket, and so it runs before anything opens (and therefore
/// creates) the database whose absence it is testing for.
/// </summary>
public class SyncClientGateTests : IDisposable
{
    private readonly string _root;
    private readonly string _folder;
    private readonly string _dbPath;

    public SyncClientGateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rfs_gate_{Guid.NewGuid()}");
        _folder = Path.Combine(_root, "sync");
        Directory.CreateDirectory(_folder);
        _dbPath = Path.Combine(_root, "state", "sync.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A port bound just long enough to learn it is free, then released.</summary>
    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private SyncOptions ClientOptions(bool mirrorDeletes = false) => new()
    {
        IsServer = false,
        Host = "127.0.0.1",
        Port = ClosedPort(),
        Folder = _folder,
        Mode = SyncMode.Pull,
        DeleteEnabled = true,
        MirrorDeletes = mirrorDeletes,
        BackupFolder = Path.Combine(_root, "backup"),
        ArchiveFolder = Path.Combine(_root, "archive"),
    };

    [Fact]
    public async Task MarkerWithoutDatabase_AbortsWithExitFourBeforeConnecting()
    {
        PairMarker.Write(_dbPath);              // this pair has synced before
        Assert.False(File.Exists(_dbPath));     // ...and its ancestor table is gone

        using var logger = new SyncLogger(false, null, suppressConsole: true);
        var client = new SyncClient(ClientOptions(), logger, dbPath: _dbPath);

        var sw = Stopwatch.StartNew();
        var exit = await client.RunAsync(CancellationToken.None);
        sw.Stop();

        Assert.Equal(4, exit);
        // Before the socket: three refused connects cost ~4s of retry backoff and return 2.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"the gate ran after the connection attempt ({sw.Elapsed})");
        // ...and before anything created the database it just refused to run without.
        Assert.False(File.Exists(_dbPath));
    }

    [Fact]
    public async Task UnreadableDatabase_WithMarker_AbortsWithoutConsumingTheEvidence()
    {
        const string junk = "not a sqlite database";
        File.WriteAllText(_dbPath, junk);
        PairMarker.Write(_dbPath);

        using var logger = new SyncLogger(false, null, suppressConsole: true);
        var client = new SyncClient(ClientOptions(), logger, dbPath: _dbPath);

        Assert.Equal(4, await client.RunAsync(CancellationToken.None));

        // The probe must not rewrite, truncate or delete the file it inspected: a user restoring
        // from backup needs whatever is there, and a probe that mutates its subject is not one.
        Assert.Equal(junk, File.ReadAllText(_dbPath));
        // ...and must not leave a handle open. This throws IOException if it did.
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task MarkerWithoutDatabase_WithMirror_IsNotRefused()
    {
        PairMarker.Write(_dbPath);

        using var logger = new SyncLogger(false, null, suppressConsole: true);
        var client = new SyncClient(ClientOptions(mirrorDeletes: true), logger, dbPath: _dbPath);

        // --mirror is the documented escape: the operator has accepted that the destination is
        // overwritten to match the source, so a missing ancestor table is not fatal. Reaching
        // the connect retries and failing with 2 is the proof that the gate did not fire.
        Assert.Equal(2, await client.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NoMarker_IsAGenuineFirstRunAndTheClientOpensItsOwnDatabase()
    {
        Assert.False(PairMarker.Exists(_dbPath));

        using var logger = new SyncLogger(false, null, suppressConsole: true);
        var client = new SyncClient(ClientOptions(), logger, dbPath: _dbPath);

        Assert.Equal(2, await client.RunAsync(CancellationToken.None));
        // Program no longer opens the database; the client does, after the gate has passed.
        Assert.True(File.Exists(_dbPath));
    }

    /// <summary>
    /// The header-only probe in <see cref="SyncClient.PairStateLost"/> proves the file opens as
    /// SQLite; it does not prove the `files` (ancestor) table inside it survived intact. A DB
    /// whose header and user_version are fine but whose `files` table was dropped or corrupted
    /// passes that probe, so the gate does not fire, and execution reaches `_db.LoadAll()` —
    /// which throws. Pre-fix that exception escaped to Program.cs as a generic "Fatal error"
    /// (exit 3); this pins the fix: the same marker-present "state lost" contract as the header
    /// gate, exit 4, before anything is deleted.
    /// </summary>
    [Fact]
    public async Task CorruptAncestorTable_WithMarker_AbortsWithExitFourNotThree()
    {
        // A real database, so the header and user_version are genuinely valid — then the one
        // table LoadAll reads from is dropped out from underneath it, leaving everything the
        // magic-header probe checks intact.
        using (var db = new SyncDatabase(_dbPath)) { }
        using (var raw = new SqliteConnection($"Data Source={_dbPath}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "DROP TABLE files;";
            cmd.ExecuteNonQuery();
        }
        PairMarker.Write(_dbPath);

        // Proves the premise: the probe alone does not catch this case, so the fix has to live
        // past it, at the LoadAll call.
        Assert.False(SyncClient.PairStateLost(_dbPath));

        // A false-negative in the fix (no abort, plan computed and executed) would delete this.
        File.WriteAllText(Path.Combine(_folder, "local.txt"), "must survive");

        // A real peer: this gate fires only after the handshake and manifest exchange, so a
        // closed port (as the other gate tests use, to prove they run BEFORE connecting) would
        // never reach it.
        int port = ClosedPort();
        var peerFolder = Path.Combine(_root, "peer");
        Directory.CreateDirectory(peerFolder);
        var serverOpts = new SyncOptions
        {
            IsServer = true, Once = true, BindAddress = "127.0.0.1", Port = port, Folder = peerFolder,
        };
        using var serverLogger = new SyncLogger(false, null, suppressConsole: true);
        var server = new SyncServer(serverOpts, serverLogger);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);

        using var logger = new SyncLogger(false, null, suppressConsole: true);
        var clientOpts = ClientOptions();
        clientOpts.Port = port;
        var client = new SyncClient(clientOpts, logger, dbPath: _dbPath);

        var exit = await client.RunAsync(cts.Token);

        Assert.Equal(4, exit);
        Assert.True(File.Exists(Path.Combine(_folder, "local.txt")),
            "a local file was touched even though the run should have aborted before planning.");

        // The client aborts before ever sending a plan, so the server's read of it fails too;
        // best-effort drain so the task does not dangle past the test.
        try { await serverTask; } catch { /* the server may fault on the torn connection too */ }
    }

    [Fact]
    public async Task CorruptAncestorTable_WithMarker_DoesNotLeakAnOpenSessionRow()
    {
        // Same F1 case as above: a valid database whose `files` table is dropped, so LoadAll
        // throws and the run aborts with exit 4 — but sync_sessions survives and is readable.
        // The session row is opened by StartSession, which must run only AFTER the corrupt-
        // ancestor gate has passed; otherwise the abort returns past the try/finally that owns
        // CompleteSession and leaves a row with completed_utc = NULL forever.
        using (var db = new SyncDatabase(_dbPath)) { }
        using (var raw = new SqliteConnection($"Data Source={_dbPath}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "DROP TABLE files;";
            cmd.ExecuteNonQuery();
        }
        PairMarker.Write(_dbPath);

        int port = ClosedPort();
        var peerFolder = Path.Combine(_root, "peer");
        Directory.CreateDirectory(peerFolder);
        var serverOpts = new SyncOptions
        {
            IsServer = true, Once = true, BindAddress = "127.0.0.1", Port = port, Folder = peerFolder,
        };
        using var serverLogger = new SyncLogger(false, null, suppressConsole: true);
        var server = new SyncServer(serverOpts, serverLogger);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);

        using var logger = new SyncLogger(false, null, suppressConsole: true);
        var clientOpts = ClientOptions();
        clientOpts.Port = port;
        var client = new SyncClient(clientOpts, logger, dbPath: _dbPath);

        Assert.Equal(4, await client.RunAsync(cts.Token));
        try { await serverTask; } catch { /* the server may fault on the torn connection too */ }

        // No StartSession row may have been left open by the aborted run.
        using var check = new SqliteConnection($"Data Source={_dbPath}");
        check.Open();
        using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM sync_sessions WHERE completed_utc IS NULL;";
        Assert.Equal(0L, (long)count.ExecuteScalar()!);
    }

    [Fact]
    public void PairStateLost_FollowsTheStateTableExactly()
    {
        // A live database is kept in its own directory: PairMarker.PathFor is per-directory, so
        // two databases under one directory would share a marker.
        var livePath = Path.Combine(_root, "live", "sync.db");

        // neither: a genuine first run, additive and safe.
        Assert.False(SyncClient.PairStateLost(_dbPath));

        // database, no marker: still a first run — the marker is only written on a clean exit.
        using (var db = new SyncDatabase(livePath)) { }
        Assert.False(SyncClient.PairStateLost(livePath));

        // database + marker: the normal steady state.
        PairMarker.Write(livePath);
        Assert.False(SyncClient.PairStateLost(livePath));

        // marker without database: state loss, not a first run. Every one-sided file would
        // otherwise resolve to a deletion.
        PairMarker.Write(_dbPath);
        Assert.True(SyncClient.PairStateLost(_dbPath));

        // unreadable counts the same as absent: a foreign file yields no ancestor rows.
        File.WriteAllText(_dbPath, "not a sqlite database");
        Assert.True(SyncClient.PairStateLost(_dbPath));

        // a zero-length file is the same case, and is what a half-finished restore leaves.
        File.WriteAllText(_dbPath, "");
        Assert.True(SyncClient.PairStateLost(_dbPath));
    }
}
