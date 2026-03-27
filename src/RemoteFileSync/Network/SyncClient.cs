using System.Diagnostics;
using System.Net.Sockets;
using RemoteFileSync.Backup;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Sync;
using RemoteFileSync.Transfer;

namespace RemoteFileSync.Network;

public sealed class SyncClient
{
    private readonly SyncOptions _options;
    private readonly SyncLogger _logger;

    public SyncClient(SyncOptions options, SyncLogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        using var tcp = new TcpClient();
        int retries = 3;

        for (int attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
                _logger.Summary($"Connecting to {_options.Host}:{_options.Port}...");
                await tcp.ConnectAsync(_options.Host!, _options.Port, ct);
                break;
            }
            catch (SocketException) when (attempt < retries)
            {
                _logger.Warning($"Connection attempt {attempt} failed. Retrying in 2s...");
                await Task.Delay(2000, ct);
            }
            catch (SocketException ex)
            {
                _logger.Error($"Connection failed after {retries} attempts: {ex.Message}");
                return 2;
            }
        }

        _logger.Summary($"Connected. {(_options.Bidirectional ? "Bi-directional" : "Uni-directional")} sync." +
            (_options.Verbose ? $" Block: {_options.BlockSize / 1024}KB, Threads: {_options.MaxThreads}" : ""));

        using var stream = tcp.GetStream();
        return await HandleConnectionAsync(stream, ct);
    }

    private async Task<int> HandleConnectionAsync(NetworkStream stream, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int skippedFiles = 0;

        // 1. Send handshake
        byte syncMode = (byte)((_options.Bidirectional ? 1 : 0) | (_options.DeleteEnabled ? 2 : 0));
        var hsPayload = ProtocolHandler.SerializeHandshake(1, syncMode);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Handshake, hsPayload, ct);

        // 2. Receive HandshakeAck
        var (ackType, ackData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        if (ackType != MessageType.HandshakeAck)
        {
            _logger.Error($"Expected HandshakeAck, got {ackType}");
            return 3;
        }
        var (_, accepted) = ProtocolHandler.DeserializeHandshakeAck(ackData);
        if (!accepted)
        {
            _logger.Error("Server rejected the connection.");
            return 2;
        }

        // 3. Scan local folder and send client manifest
        var scanner = new FileScanner(_options.Folder, _options.IncludePatterns, _options.ExcludePatterns);
        var clientManifest = scanner.Scan();
        _logger.Info($"Local manifest: {clientManifest.Count} files");
        var clientManifestBytes = ProtocolHandler.SerializeManifest(clientManifest);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Manifest, clientManifestBytes, ct);

        // 4. Receive server manifest
        var (mType, mData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var serverManifest = ProtocolHandler.DeserializeManifest(mData);
        _logger.Info($"Remote manifest: {serverManifest.Count} files");

        // 5. Compute sync plan and send
        var syncPlan = SyncEngine.ComputePlan(clientManifest, serverManifest, _options.Bidirectional);
        var actionable = syncPlan.Where(p => p.Action != SyncActionType.Skip).ToList();
        _logger.Info($"Sync plan: {actionable.Count} transfers, {syncPlan.Count(p => p.Action == SyncActionType.Skip)} skipped");
        var planBytes = ProtocolHandler.SerializeSyncPlan(syncPlan);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncPlan, planBytes, ct);

        var backup = new BackupManager(_options.Folder, _options.EffectiveBackupFolder);
        var sender = new FileTransferSender(_options.Folder, _options.BlockSize);
        var receiver = new FileTransferReceiver(_options.Folder);
        int filesTransferred = 0;
        long bytesTransferred = 0;

        // 6. Send files to server (SendToServer + ClientOnly)
        var toSend = syncPlan.Where(p =>
            p.Action == SyncActionType.SendToServer || p.Action == SyncActionType.ClientOnly).ToList();

        foreach (var action in toSend)
        {
            try
            {
                short fileId = (short)(filesTransferred % short.MaxValue);
                await sender.SendFileAsync(stream, fileId, action.RelativePath, ct);
                _logger.Info($"[→] {action.RelativePath}");
                filesTransferred++;
                var fi = new FileInfo(Path.Combine(_options.Folder, action.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                bytesTransferred += fi.Length;
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

        // 7. Receive files from server (SendToClient + ServerOnly) if bidirectional
        if (_options.Bidirectional)
        {
            var toReceive = syncPlan.Where(p =>
                p.Action == SyncActionType.SendToClient || p.Action == SyncActionType.ServerOnly).ToList();

            foreach (var action in toReceive)
            {
                if (action.Action == SyncActionType.SendToClient)
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

                var confirmPayload = System.Text.Encoding.UTF8.GetBytes(action.RelativePath);
                var confirm = new byte[confirmPayload.Length + 1];
                confirmPayload.CopyTo(confirm, 0);
                confirm[^1] = (byte)(result.Success ? 1 : 0);
                await ProtocolHandler.WriteMessageAsync(stream, MessageType.BackupConfirm, confirm, ct);
            }
        }

        // 8. Exchange SyncComplete
        sw.Stop();
        var completePayload = ProtocolHandler.SerializeSyncComplete(filesTransferred, bytesTransferred, 0, sw.ElapsedMilliseconds);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncComplete, completePayload, ct);
        var (scType, scData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        _logger.Summary($"Sync complete: {filesTransferred} files, {bytesTransferred / (1024.0 * 1024.0):F1} MB, {sw.ElapsedMilliseconds}ms");
        return skippedFiles > 0 ? 1 : 0;
    }
}
