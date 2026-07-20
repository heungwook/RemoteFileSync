using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;

namespace RemoteFileSync.Tests.Network;

public class HandshakeCompatibilityTests : IDisposable
{
    private readonly string _folder;

    public HandshakeCompatibilityTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"rfs_hs_{Guid.NewGuid()}");
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
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
    public async Task Client_AgainstOlderServerAck_ReportsMismatchInsteadOfThrowing()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var fakeV2Server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync();
            using var stream = peer.GetStream();
            // Drain the client's handshake, then reply the way a v2 build does: two bytes,
            // version 2, byte[1] == 1 meaning "rejected".
            await ProtocolHandler.ReadMessageAsync(stream);
            await ProtocolHandler.WriteMessageAsync(
                stream, MessageType.HandshakeAck, new byte[] { 2, 1 });
        });

        var options = new SyncOptions
        {
            IsServer = false,
            Host = "127.0.0.1",
            Port = port,
            Folder = _folder,
            Mode = SyncMode.Push,
        };
        using var logger = new SyncLogger(verbose: false, logFile: null, suppressConsole: true);
        var client = new SyncClient(options, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int exit = await client.RunAsync(cts.Token);

        await fakeV2Server;
        listener.Stop();

        // Without the guard the 2-byte ack trips the v3 length check and InvalidDataException
        // escapes RunAsync, so this await throws and no exit code is ever produced.
        // 2 = connection failure, matching the existing protocol-mismatch branch.
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Server_AgainstOlderClientHandshake_StillSendsAWellFormedAck()
    {
        var options = new SyncOptions
        {
            IsServer = true,
            Once = true,
            BindAddress = "127.0.0.1",
            Port = GetFreePort(),
            Folder = _folder,
        };
        using var logger = new SyncLogger(verbose: false, logFile: null, suppressConsole: true);
        var server = new SyncServer(options, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);

        using var peer = new TcpClient();
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await peer.ConnectAsync(IPAddress.Loopback, options.Port, cts.Token);
                break;
            }
            catch (SocketException) when (attempt < 20)
            {
                // The listener may not have bound yet; RunAsync starts it asynchronously.
                await Task.Delay(100, cts.Token);
            }
        }

        using var stream = peer.GetStream();
        // A v2 client's handshake is exactly two bytes: version 2, syncMode 0.
        await ProtocolHandler.WriteMessageAsync(
            stream, MessageType.Handshake, new byte[] { 2, 0 }, cts.Token);

        // Without the truncation guard the server throws before writing anything, the accept
        // loop closes the socket, and this read fails with EndOfStreamException.
        var (type, payload) = await ProtocolHandler.ReadMessageAsync(stream, cts.Token);

        Assert.Equal(MessageType.HandshakeAck, type);
        var (version, accepted, _) = ProtocolHandler.DeserializeHandshakeAck(payload);
        Assert.Equal(ProtocolHandler.ProtocolVersion, version);
        Assert.False(accepted);

        Assert.Equal(3, await serverTask);
    }
}
