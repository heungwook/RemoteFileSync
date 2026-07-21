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

    public DeletePathGuardTests()
    {
        _serverDir = Path.Combine(Path.GetTempPath(), $"rfs_delguard_{Guid.NewGuid()}");
        Directory.CreateDirectory(_serverDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_serverDir)) Directory.Delete(_serverDir, recursive: true);
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
}
