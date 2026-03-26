using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Backup;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Sync;
using RemoteFileSync.Transfer;

namespace RemoteFileSync.Network;

public sealed class SyncServer
{
    private readonly SyncOptions _options;
    private readonly SyncLogger _logger;

    public SyncServer(SyncOptions options, SyncLogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, _options.Port);
        listener.Start();
        _logger.Summary($"Listening on port {_options.Port}...");

        try
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            _logger.Summary("Client connected.");
            using var stream = client.GetStream();
            return await HandleConnectionAsync(stream, ct);
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<int> HandleConnectionAsync(NetworkStream stream, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int skippedFiles = 0;

        // 1. Receive handshake
        var (hsType, hsData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        if (hsType != MessageType.Handshake)
        {
            _logger.Error($"Expected Handshake, got {hsType}");
            return 3;
        }
        var (version, bidirectional) = ProtocolHandler.DeserializeHandshake(hsData);
        _logger.Info($"Handshake: v{version}, {(bidirectional ? "bidirectional" : "unidirectional")}");

        // 2. Send HandshakeAck
        var ackPayload = ProtocolHandler.SerializeHandshakeAck(1, accepted: true);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.HandshakeAck, ackPayload, ct);

        // 3. Receive client manifest
        var (mType, mData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var clientManifest = ProtocolHandler.DeserializeManifest(mData);
        _logger.Info($"Client manifest: {clientManifest.Count} files");

        // 4. Scan local folder and send server manifest
        var scanner = new FileScanner(_options.Folder, _options.IncludePatterns, _options.ExcludePatterns);
        var serverManifest = scanner.Scan();
        _logger.Info($"Local manifest: {serverManifest.Count} files");
        var serverManifestBytes = ProtocolHandler.SerializeManifest(serverManifest);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Manifest, serverManifestBytes, ct);

        // 5. Receive sync plan
        var (pType, pData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var syncPlan = ProtocolHandler.DeserializeSyncPlan(pData);
        _logger.Info($"Sync plan: {syncPlan.Count} actions");

        var backup = new BackupManager(_options.Folder, _options.EffectiveBackupFolder);
        var receiver = new FileTransferReceiver(_options.Folder);
        var sender = new FileTransferSender(_options.Folder, _options.BlockSize);
        int filesTransferred = 0;
        long bytesTransferred = 0;

        // 6. Receive files from client (SendToServer + ClientOnly)
        var toReceive = syncPlan.Where(p =>
            p.Action == SyncActionType.SendToServer || p.Action == SyncActionType.ClientOnly).ToList();

        foreach (var action in toReceive)
        {
            if (action.Action == SyncActionType.SendToServer)
            {
                if (!backup.BackupFile(action.RelativePath))
                    _logger.Debug($"No existing file to backup: {action.RelativePath}");
            }

            var result = await receiver.ReceiveFileAsync(stream, ct);
            if (result.Success)
            {
                _logger.Info($"[←] {result.RelativePath}");
                filesTransferred++;
                var fi = new FileInfo(Path.Combine(_options.Folder, result.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                bytesTransferred += fi.Length;
            }
            else
            {
                _logger.Error($"Failed to receive {action.RelativePath}: {result.ErrorMessage}");
                skippedFiles++;
            }

            // Send BackupConfirm
            var confirmPayload = System.Text.Encoding.UTF8.GetBytes(action.RelativePath);
            var confirm = new byte[confirmPayload.Length + 1];
            confirmPayload.CopyTo(confirm, 0);
            confirm[^1] = (byte)(result.Success ? 1 : 0);
            await ProtocolHandler.WriteMessageAsync(stream, MessageType.BackupConfirm, confirm, ct);
        }

        // 7. Send files to client (SendToClient + ServerOnly) if bidirectional
        if (bidirectional)
        {
            var toSend = syncPlan.Where(p =>
                p.Action == SyncActionType.SendToClient || p.Action == SyncActionType.ServerOnly).ToList();

            foreach (var action in toSend)
            {
                try
                {
                    short fileId = (short)(filesTransferred % short.MaxValue);
                    await sender.SendFileAsync(stream, fileId, action.RelativePath, ct);
                    _logger.Info($"[→] {action.RelativePath}");
                    filesTransferred++;
                    var (cType, _) = await ProtocolHandler.ReadMessageAsync(stream, ct);
                    if (cType != MessageType.BackupConfirm)
                        _logger.Warning($"Expected BackupConfirm, got {cType}");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to send {action.RelativePath}: {ex.Message}");
                    skippedFiles++;
                }
            }
        }

        // 8. Exchange SyncComplete
        sw.Stop();
        var completePayload = ProtocolHandler.SerializeSyncComplete(filesTransferred, bytesTransferred, sw.ElapsedMilliseconds);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncComplete, completePayload, ct);
        var (scType, scData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        _logger.Summary($"Sync complete: {filesTransferred} files, {bytesTransferred / (1024.0 * 1024.0):F1} MB, {sw.ElapsedMilliseconds}ms");
        return skippedFiles > 0 ? 1 : 0;
    }
}
