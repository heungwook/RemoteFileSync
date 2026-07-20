using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Network;

/// <summary>
/// Unit-level coverage for SyncServer's own conflict-plan guards (SyncServer.cs, step 5a). These
/// drive the server directly over a hand-crafted protocol exchange rather than through a real
/// SyncClient — the guards exist specifically because the server does not authenticate the peer
/// or trust the plan it sends, so a hand-crafted plan is exactly what these tests need to
/// construct. The happy path is already covered by Integration/ConflictKeepBothSyncTests.cs;
/// this file covers the four `return 4` sites that reject it.
/// </summary>
public class ConflictGuardTests : IDisposable
{
    private readonly string _serverDir;

    public ConflictGuardTests()
    {
        _serverDir = Path.Combine(Path.GetTempPath(), $"rfs_guard_{Guid.NewGuid()}");
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

    /// <summary>
    /// Drives SyncServer through handshake, an empty client manifest, and a hand-crafted plan,
    /// then returns the session's exit code. <paramref name="syncMode"/> matches the wire format
    /// SyncClient sends: low 2 bits = SyncMode (1=Push, 2=Pull, 3=TwoWay), bit 2 (value 4) =
    /// deleteEnabled.
    /// </summary>
    private async Task<int> RunGuardScenarioAsync(byte syncMode, List<SyncPlanEntry> plan)
    {
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
        var hsPayload = ProtocolHandler.SerializeHandshake(
            ProtocolHandler.ProtocolVersion, syncMode, DateTime.UtcNow.Ticks);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Handshake, hsPayload, cts.Token);

        var (ackType, ackData) = await ProtocolHandler.ReadMessageAsync(stream, cts.Token);
        Assert.Equal(MessageType.HandshakeAck, ackType);
        var (_, accepted, _) = ProtocolHandler.DeserializeHandshakeAck(ackData);
        Assert.True(accepted);

        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Manifest,
            ProtocolHandler.SerializeManifest(new FileManifest()), cts.Token);

        // Server manifest — the content is not needed, but the frame must be drained or the
        // plan we send next lands on a stream that is one frame behind.
        await ProtocolHandler.ReadMessageAsync(stream, cts.Token);

        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncPlan,
            ProtocolHandler.SerializeSyncPlan(plan), cts.Token);

        return await serverTask;
    }

    [Fact]
    public async Task ConflictFromNonTwoWayPeer_Rejects()
    {
        // SyncServer.cs:244. A Push/Pull peer has no phase to receive a renamed loser back, so a
        // conflict entry from one is rejected outright regardless of what it names.
        var conflictName = ConflictNamer.Compose("report.txt", DateTime.UtcNow, ConflictNamer.ClientSide);
        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, conflictName) };

        int exit = await RunGuardScenarioAsync(syncMode: 1 /* Push, no delete */, plan);
        Assert.Equal(4, exit);
    }

    [Fact]
    public async Task ConflictTargetOccupied_DeleteNotNegotiated_Rejects()
    {
        // SyncServer.cs:262. The plan's conflict name lands on a real local file, and the peer
        // did not negotiate deletion — landing it anyway would delete that file without ever
        // sending a DeleteFile frame.
        var ts = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var conflictName = ConflictNamer.Compose("report.txt", ts, ConflictNamer.ServerSide);
        File.WriteAllText(Path.Combine(_serverDir, conflictName), "squatter");

        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, conflictName) };

        int exit = await RunGuardScenarioAsync(syncMode: 3 /* TwoWay, no delete */, plan);
        Assert.Equal(4, exit);

        // Rejected before ApplyLocalRenames runs, so the squatter file must survive untouched.
        Assert.Equal("squatter", File.ReadAllText(Path.Combine(_serverDir, conflictName)));
    }

    [Fact]
    public async Task ConflictTargetsExceedDeleteBudget_Rejects()
    {
        // SyncServer.cs:276. Delete IS negotiated this time, but occupying 3 of 10 tracked files
        // (30%) exceeds the default --max-delete-percent of 25.
        var ts = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var plan = new List<SyncPlanEntry>();
        for (int i = 0; i < 3; i++)
        {
            var conflictName = ConflictNamer.Compose($"occupied{i}.txt", ts, ConflictNamer.ServerSide);
            File.WriteAllText(Path.Combine(_serverDir, conflictName), "squatter");
            plan.Add(new SyncPlanEntry(SyncActionType.ConflictKeepBoth, conflictName));
        }
        // Pad the server's tracked population to 10 files (>= MinTrackedFilesForDeleteGuard), so
        // the percentage check is live rather than exempted by the small-population floor.
        for (int i = 0; i < 7; i++)
            File.WriteAllText(Path.Combine(_serverDir, $"plain{i}.txt"), "plain");

        int exit = await RunGuardScenarioAsync(syncMode: 7 /* TwoWay + delete */, plan);
        Assert.Equal(4, exit);
    }

    [Fact]
    public async Task ServerSideRenameFailure_Rejects()
    {
        // SyncServer.cs:294. The conflict name's original file does not exist on the server's
        // disk — ApplyLocalRenames records a Failure rather than renaming a file that is not
        // there, and a non-empty Failures list is fatal: the plan already promised the peer a
        // transfer under this name.
        var ts = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var conflictName = ConflictNamer.Compose("missing.txt", ts, ConflictNamer.ServerSide);
        // Deliberately NOT creating server-side "missing.txt".

        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, conflictName) };

        // No delete bit: occupied is 0 either way (the conflict name itself is free too), so
        // this exercises the rename failure specifically rather than the occupancy guards above.
        int exit = await RunGuardScenarioAsync(syncMode: 3 /* TwoWay, no delete */, plan);
        Assert.Equal(4, exit);
    }
}
