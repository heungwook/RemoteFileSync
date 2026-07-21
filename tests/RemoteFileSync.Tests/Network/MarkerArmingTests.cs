using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.State;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Network;

/// <summary>
/// pair.marker arms whenever RunAsync leaves behind a usable ancestor table, which happens on
/// exit 0 AND exit 1 (skipped files) alike — the ancestor rows are written before the transfer
/// loops and regardless of skippedFiles. Gating the write on exit == 0 alone left a pair with one
/// permanently-locked file, which exits 1 on every run, never arming the marker: exactly the
/// state-loss scenario the no-ancestor gate (SyncClientGateTests) exists to catch.
///
/// This drives a real SyncClient against a hand-scripted "server" (raw TcpListener, no real
/// SyncServer) so the single skipped file is deterministic rather than racing a real archive: the
/// script deletes the client's own file out from under the delete phase, which makes
/// ArchiveManager.Archive report NothingToArchive without needing a file lock or a timing window.
/// </summary>
public class MarkerArmingTests : IDisposable
{
    private readonly string _root;
    private readonly string _clientDir;
    private readonly string _dbPath;

    public MarkerArmingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rfs_arm_{Guid.NewGuid()}");
        _clientDir = Path.Combine(_root, "client");
        Directory.CreateDirectory(_clientDir);
        _dbPath = Path.Combine(_root, "state", "sync.db");
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

    [Fact]
    public async Task ExitOne_OneSkippedFile_StillArmsTheMarker()
    {
        var ghostPath = Path.Combine(_clientDir, "ghost.txt");
        File.WriteAllText(ghostPath, "will be deleted out from under the delete phase");
        var ghostInfo = new FileInfo(ghostPath);

        // Seed an ancestor row asserting both sides held "ghost.txt", unchanged on the client
        // since that agreement. Real client mtime/size, so ChangeDetector reads the client copy
        // as untouched and PlanTwoWayWithAncestor emits DeleteOnClient rather than a conflict.
        using (var seedDb = new SyncDatabase(_dbPath))
        {
            seedDb.UpsertSynced("ghost.txt",
                clientSize: ghostInfo.Length, clientMtimeTicks: ghostInfo.LastWriteTimeUtc.Ticks,
                serverSize: 999, serverMtimeTicks: DateTime.UtcNow.AddDays(-30).Ticks,
                sessionId: 0, direction: "seed");
        }
        Assert.False(PairMarker.Exists(_dbPath), "the marker must not be armed before this run.");

        int port = GetFreePort();
        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port,
            Folder = _clientDir, Mode = SyncMode.TwoWay, DeleteEnabled = true,
            ArchiveFolder = Path.Combine(_root, "client-archive"),
            BackupFolder = Path.Combine(_root, "client-backup"),
        };
        using var clientLogger = new SyncLogger(verbose: false, logFile: null, suppressConsole: true);
        var client = new SyncClient(clientOpts, clientLogger, dbPath: _dbPath);

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var clientTask = client.RunAsync(cts.Token);

        using var peer = await listener.AcceptTcpClientAsync(cts.Token);
        using var stream = peer.GetStream();
        listener.Stop();

        // 1. Handshake
        await ProtocolHandler.ReadMessageAsync(stream, cts.Token);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.HandshakeAck,
            ProtocolHandler.SerializeHandshakeAck(ProtocolHandler.ProtocolVersion, accepted: true, DateTime.UtcNow.Ticks),
            cts.Token);

        // 2. Client manifest (contains "ghost.txt") — drain it, content not needed.
        await ProtocolHandler.ReadMessageAsync(stream, cts.Token);

        // 3. Server manifest: empty. The server no longer has "ghost.txt" — that absence is what
        // makes the plan a DeleteOnClient rather than a Skip.
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Manifest,
            ProtocolHandler.SerializeManifest(new FileManifest()), cts.Token);

        // 4. Sync plan: exactly one DeleteOnClient for "ghost.txt".
        var (planType, planData) = await ProtocolHandler.ReadMessageAsync(stream, cts.Token);
        Assert.Equal(MessageType.SyncPlan, planType);
        var plan = ProtocolHandler.DeserializeSyncPlan(planData);
        var entry = Assert.Single(plan);
        Assert.Equal(SyncActionType.DeleteOnClient, entry.Action);
        Assert.Equal("ghost.txt", entry.RelativePath);

        // Remove the file out from under the delete phase that is about to run. The frame below
        // names the correct, planned path — this is not the I1 path-mismatch guard firing, it is
        // Archive() reporting NothingToArchive for a file that is legitimately gone by the time
        // the delete phase reaches it, which is exactly the "one skipped file" exit-1 scenario.
        File.Delete(ghostPath);

        var deletePayload = ProtocolHandler.SerializeDeleteFile("ghost.txt", backupFirst: true);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.DeleteFile, deletePayload, cts.Token);

        var (confirmType, confirmData) = await ProtocolHandler.ReadMessageAsync(stream, cts.Token);
        Assert.Equal(MessageType.DeleteConfirm, confirmType);
        var (_, success) = ProtocolHandler.DeserializeDeleteConfirm(confirmData);
        Assert.False(success, "there is nothing left on disk to archive; the delete must be reported as skipped.");

        // 5. SyncComplete exchange: client writes, then reads one frame back.
        await ProtocolHandler.ReadMessageAsync(stream, cts.Token);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncComplete,
            ProtocolHandler.SerializeSyncComplete(0, 0, 0, 0), cts.Token);

        int exit = await clientTask;

        Assert.Equal(1, exit);
        Assert.True(PairMarker.Exists(_dbPath),
            "exit 1 left a fully-built ancestor table behind but did not arm the marker.");
    }
}
