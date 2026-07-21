using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Network;

/// <summary>
/// The delete-frame path guard. The budget in SyncServer.cs (and its mirror in SyncClient.cs)
/// bounds the COUNT of deletions a plan approved, but the peer names the PATH in each DeleteFile
/// frame — so a hostile or buggy peer that sends the wrong path for an approved entry must not
/// have that path deleted. This drives SyncServer directly over a hand-crafted protocol exchange,
/// the same style as ConflictGuardTests: the "client" here is a raw TcpClient we script by hand,
/// which is exactly what's needed to send a DeleteFile frame the real SyncClient would never send.
/// </summary>
public class DeletePathGuardTests : IDisposable
{
    private readonly string _serverDir;
    private readonly string _clientDir;

    public DeletePathGuardTests()
    {
        _serverDir = Path.Combine(Path.GetTempPath(), $"rfs_delguard_{Guid.NewGuid()}");
        Directory.CreateDirectory(_serverDir);
        _clientDir = Path.Combine(Path.GetTempPath(), $"rfs_delguard_client_{Guid.NewGuid()}");
        Directory.CreateDirectory(_clientDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_serverDir)) Directory.Delete(_serverDir, recursive: true);
        if (Directory.Exists(_clientDir)) Directory.Delete(_clientDir, recursive: true);
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
    public async Task DeleteFrame_NamesAPathThePlanDidNotApprove_UnlistedFileSurvives()
    {
        // "planned.txt" is the one entry the delete budget approved; "untouched.txt" stands in
        // for an arbitrary in-folder file a hostile peer could name instead. Both stay well under
        // MinTrackedFilesForDeleteGuard(10), so the percentage bound is exempt and cannot be what
        // saves either file — only the path check can.
        File.WriteAllText(Path.Combine(_serverDir, "planned.txt"), "keep me honest");
        File.WriteAllText(Path.Combine(_serverDir, "untouched.txt"), "not in the plan");

        int port = GetFreePort();
        var serverOpts = new SyncOptions
        {
            IsServer = true, Once = true, BindAddress = "127.0.0.1", Port = port, Folder = _serverDir,
        };
        using var serverLogger = new SyncLogger(verbose: false, logFile: null, suppressConsole: true);
        var server = new SyncServer(serverOpts, serverLogger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);

        using var peer = new TcpClient();
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await peer.ConnectAsync(IPAddress.Loopback, port, cts.Token);
                break;
            }
            catch (SocketException) when (attempt < 20)
            {
                // The listener may not have bound yet; RunAsync starts it asynchronously.
                await Task.Delay(100, cts.Token);
            }
        }

        using var stream = peer.GetStream();

        // Push + delete: syncMode low 2 bits = 1 (Push), bit 2 (value 4) = deleteEnabled. Push
        // keeps ModeGate.ServerToClient false, so no download or client-delete phase runs and the
        // scripted exchange stays to exactly the frames this test needs.
        var hsPayload = ProtocolHandler.SerializeHandshake(ProtocolHandler.ProtocolVersion, syncMode: 5, DateTime.UtcNow.Ticks);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Handshake, hsPayload, cts.Token);

        var (ackType, ackData) = await ProtocolHandler.ReadMessageAsync(stream, cts.Token);
        Assert.Equal(MessageType.HandshakeAck, ackType);
        var (_, accepted, _) = ProtocolHandler.DeserializeHandshakeAck(ackData);
        Assert.True(accepted);

        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Manifest,
            ProtocolHandler.SerializeManifest(new FileManifest()), cts.Token);

        // Server manifest — content unneeded, but the frame must be drained or the plan we send
        // next lands one frame behind.
        await ProtocolHandler.ReadMessageAsync(stream, cts.Token);

        var plan = new List<SyncPlanEntry> { new(SyncActionType.DeleteOnServer, "planned.txt") };
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncPlan,
            ProtocolHandler.SerializeSyncPlan(plan), cts.Token);

        // The mismatch: the plan approved exactly one deletion, "planned.txt", but this frame
        // names a different file the plan never listed. Pre-fix, the server trusted the wire path
        // and archived+removed whatever it named; post-fix it must refuse.
        var deletePayload = ProtocolHandler.SerializeDeleteFile("untouched.txt", backupFirst: true);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.DeleteFile, deletePayload, cts.Token);

        var (confirmType, confirmData) = await ProtocolHandler.ReadMessageAsync(stream, cts.Token);
        Assert.Equal(MessageType.DeleteConfirm, confirmType);
        var (_, success) = ProtocolHandler.DeserializeDeleteConfirm(confirmData);
        Assert.False(success, "the server confirmed a delete for a path the approved plan did not list.");

        // SyncComplete exchange: the server writes, then reads one frame back before returning.
        await ProtocolHandler.ReadMessageAsync(stream, cts.Token);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncComplete,
            ProtocolHandler.SerializeSyncComplete(0, 0, 0, 0), cts.Token);

        int exit = await serverTask;

        // The mismatch counts as a skipped file, not a fatal abort: exit 1, not 4 or a crash.
        Assert.Equal(1, exit);

        // The point of the guard: the path the wire actually named must survive...
        Assert.True(File.Exists(Path.Combine(_serverDir, "untouched.txt")),
            "the unlisted path was deleted even though the plan never approved it.");
        // ...and so must the path the plan DID approve, since the frame naming it never arrived.
        Assert.True(File.Exists(Path.Combine(_serverDir, "planned.txt")));
    }

    /// <summary>
    /// The client-side analogue: SyncClient.cs's own delete-path guard (~:761) got the
    /// byte-identical fix as the server-side one above, but had no dedicated test — it was
    /// proven only by full-suite non-regression. Same script, reversed roles: a real SyncClient
    /// drives a raw TcpClient we play as the SERVER, which approves nothing and simply sends a
    /// DeleteFile frame for a path the client's own plan never listed.
    ///
    /// Pull + --mirror needs no ancestor database to reach a DeleteOnClient plan: "planned.txt"
    /// is absent from the scripted server's manifest, so the client's no-ancestor mirror logic
    /// (PlanPull) plans exactly one DeleteOnClient for it. "untouched.txt" is advertised with
    /// the same size/mtime it actually has on disk, so it plans Skip — approved for nothing.
    /// </summary>
    [Fact]
    public async Task ClientDeleteFrame_NamesAPathThePlanDidNotApprove_UnlistedFileSurvives()
    {
        File.WriteAllText(Path.Combine(_clientDir, "planned.txt"), "keep me honest");
        File.WriteAllText(Path.Combine(_clientDir, "untouched.txt"), "not in the plan");
        var untouchedOnDisk = new FileInfo(Path.Combine(_clientDir, "untouched.txt"));

        int port = GetFreePort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        var clientOpts = new SyncOptions
        {
            IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir,
            Mode = SyncMode.Pull, DeleteEnabled = true, MirrorDeletes = true,
        };
        using var clientLogger = new SyncLogger(verbose: false, logFile: null, suppressConsole: true);
        var client = new SyncClient(clientOpts, clientLogger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var clientTask = client.RunAsync(cts.Token);

        using var peer = await listener.AcceptTcpClientAsync(cts.Token);
        listener.Stop();
        using var stream = peer.GetStream();

        var (hsType, _) = await ProtocolHandler.ReadMessageAsync(stream, cts.Token);
        Assert.Equal(MessageType.Handshake, hsType);
        var ackPayload = ProtocolHandler.SerializeHandshakeAck(
            ProtocolHandler.ProtocolVersion, accepted: true, DateTime.UtcNow.Ticks);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.HandshakeAck, ackPayload, cts.Token);

        // Client manifest — content unneeded, but must be drained or the server manifest sent
        // next lands one frame behind.
        await ProtocolHandler.ReadMessageAsync(stream, cts.Token);

        // "untouched.txt" mirrors what the client already has, so PlanPull's SameContent check
        // plans Skip for it; "planned.txt" is simply absent, so --mirror plans DeleteOnClient.
        var serverManifest = new FileManifest();
        serverManifest.Add(new FileEntry("untouched.txt", untouchedOnDisk.Length, untouchedOnDisk.LastWriteTimeUtc));
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Manifest,
            ProtocolHandler.SerializeManifest(serverManifest), cts.Token);

        // The plan the client computed and sent — drained, not used: the manifests above already
        // determine it deterministically (exactly one DeleteOnClient, "planned.txt").
        await ProtocolHandler.ReadMessageAsync(stream, cts.Token);

        // The mismatch: the client's own plan approved deleting exactly "planned.txt", but this
        // frame names a different file the plan never listed. Pre-fix, the client trusted the
        // wire path and archived+removed whatever it named; post-fix it must refuse.
        var deletePayload = ProtocolHandler.SerializeDeleteFile("untouched.txt", backupFirst: true);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.DeleteFile, deletePayload, cts.Token);

        var (confirmType, confirmData) = await ProtocolHandler.ReadMessageAsync(stream, cts.Token);
        Assert.Equal(MessageType.DeleteConfirm, confirmType);
        var (_, success) = ProtocolHandler.DeserializeDeleteConfirm(confirmData);
        Assert.False(success, "the client confirmed a delete for a path the approved plan did not list.");

        // SyncComplete exchange: the client writes, then reads one frame back before returning.
        await ProtocolHandler.ReadMessageAsync(stream, cts.Token);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncComplete,
            ProtocolHandler.SerializeSyncComplete(0, 0, 0, 0), cts.Token);

        int exit = await clientTask;

        // The mismatch counts as a skipped file, not a fatal abort or a crash: exit 1.
        Assert.Equal(1, exit);

        // The point of the guard: the path the wire actually named must survive...
        Assert.True(File.Exists(Path.Combine(_clientDir, "untouched.txt")),
            "the unlisted path was deleted even though the plan never approved it.");
        // ...and so must the path the plan DID approve, since the frame naming it never arrived.
        Assert.True(File.Exists(Path.Combine(_clientDir, "planned.txt")));
    }
}
