using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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
