using System.Diagnostics;
using System.Net.Sockets;
using RemoteFileSync.Backup;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.State;
using RemoteFileSync.Sync;
using RemoteFileSync.Transfer;

namespace RemoteFileSync.Network;

public sealed class SyncClient
{
    private readonly SyncOptions _options;
    private readonly SyncLogger _logger;
    private readonly SyncStateManager? _stateManager;

    public SyncClient(SyncOptions options, SyncLogger logger, SyncStateManager? stateManager = null)
    {
        _options = options;
        _logger = logger;
        _stateManager = stateManager;
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

        var modeLabel = _options.Bidirectional ? "Bi-directional" : "Uni-directional";
        var deleteLabel = _options.DeleteEnabled ? " + delete" : "";
        _logger.Summary($"Connected. {modeLabel} sync{deleteLabel}." +
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

        // 3. Load previous state (if delete enabled)
        SyncState? previousState = null;
        if (_options.DeleteEnabled && _stateManager != null)
        {
            previousState = _stateManager.LoadState(_options.Folder, _options.Host!, _options.Port);
            if (previousState == null)
                _logger.Info("No previous sync state found. First run with --delete: fully additive.");
            else
                _logger.Info($"Loaded sync state: {previousState.Manifest.Count} files from {previousState.LastSyncUtc:u}");
        }

        // 4. Scan local folder and send client manifest
        var scanner = new FileScanner(_options.Folder, _options.IncludePatterns, _options.ExcludePatterns);
        var clientManifest = scanner.Scan();
        _logger.Info($"Local manifest: {clientManifest.Count} files");
        var clientManifestBytes = ProtocolHandler.SerializeManifest(clientManifest);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Manifest, clientManifestBytes, ct);

        // 5. Receive server manifest
        var (mType, mData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var serverManifest = ProtocolHandler.DeserializeManifest(mData);
        _logger.Info($"Remote manifest: {serverManifest.Count} files");

        // 6. Compute sync plan and send
        var syncPlan = SyncEngine.ComputePlan(
            clientManifest, serverManifest, _options.Bidirectional,
            previousState, _options.DeleteEnabled);
        var transferCount = syncPlan.Count(p => p.Action != SyncActionType.Skip
            && p.Action != SyncActionType.DeleteOnServer && p.Action != SyncActionType.DeleteOnClient);
        var deleteCount = syncPlan.Count(p => p.Action == SyncActionType.DeleteOnServer || p.Action == SyncActionType.DeleteOnClient);
        var skipCount = syncPlan.Count(p => p.Action == SyncActionType.Skip);
        var deleteSummary = deleteCount > 0 ? $", {deleteCount} delete" : "";
        _logger.Info($"Sync plan: {transferCount} transfers{deleteSummary}, {skipCount} skipped");
        var planBytes = ProtocolHandler.SerializeSyncPlan(syncPlan);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncPlan, planBytes, ct);

        var backup = new BackupManager(_options.Folder, _options.EffectiveBackupFolder);
        var sender = new FileTransferSender(_options.Folder, _options.BlockSize);
        var receiver = new FileTransferReceiver(_options.Folder);
        int filesTransferred = 0;
        int filesDeleted = 0;
        long bytesTransferred = 0;

        // 7. Send files to server (SendToServer + ClientOnly)
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

        // 8. Deletion Phase (Server): Send DeleteFile for DeleteOnServer actions
        if (_options.DeleteEnabled)
        {
            var serverDeletes = syncPlan.Where(p => p.Action == SyncActionType.DeleteOnServer).ToList();
            foreach (var del in serverDeletes)
            {
                var payload = ProtocolHandler.SerializeDeleteFile(del.RelativePath, backupFirst: true);
                await ProtocolHandler.WriteMessageAsync(stream, MessageType.DeleteFile, payload, ct);

                var (confType, confData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
                if (confType == MessageType.DeleteConfirm)
                {
                    var (_, success) = ProtocolHandler.DeserializeDeleteConfirm(confData);
                    if (success)
                    {
                        filesDeleted++;
                        _logger.Info($"[DEL→] {del.RelativePath} (deleted on server)");
                    }
                    else
                    {
                        _logger.Warning($"Server failed to delete {del.RelativePath}");
                        skippedFiles++;
                    }
                }
            }
        }

        // 9. Receive files from server (SendToClient + ServerOnly) if bidirectional
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

        // 10. Deletion Phase (Client): Receive DeleteFile for DeleteOnClient actions
        if (_options.DeleteEnabled && _options.Bidirectional)
        {
            var clientDeletes = syncPlan.Where(p => p.Action == SyncActionType.DeleteOnClient).ToList();
            foreach (var del in clientDeletes)
            {
                var (delType, delData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
                if (delType != MessageType.DeleteFile)
                {
                    _logger.Warning($"Expected DeleteFile, got {delType}");
                    skippedFiles++;
                    continue;
                }

                var (path, backupFirst) = ProtocolHandler.DeserializeDeleteFile(delData);
                bool success = false;
                try
                {
                    if (backupFirst)
                    {
                        backup.BackupFile(path);
                    }
                    else
                    {
                        var fullPath = Path.Combine(_options.Folder, path.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(fullPath)) File.Delete(fullPath);
                    }
                    success = true;
                    filesDeleted++;
                    _logger.Info($"[DEL] {path} (deleted locally)");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to delete {path}: {ex.Message}");
                    skippedFiles++;
                }

                var confirmPayload = ProtocolHandler.SerializeDeleteConfirm(path, success);
                await ProtocolHandler.WriteMessageAsync(stream, MessageType.DeleteConfirm, confirmPayload, ct);
            }
        }

        // 11. Save state (only on full success)
        int exitCode = skippedFiles > 0 ? 1 : 0;
        if (exitCode == 0 && _options.DeleteEnabled && _stateManager != null)
        {
            var mergedManifest = SyncEngine.BuildMergedManifest(clientManifest, serverManifest, syncPlan);
            _stateManager.SaveState(_options.Folder, _options.Host!, _options.Port, mergedManifest, DateTime.UtcNow);
            _logger.Debug($"Sync state saved: {mergedManifest.Count} files");
        }

        // 12. Exchange SyncComplete
        sw.Stop();
        var completePayload = ProtocolHandler.SerializeSyncComplete(filesTransferred, bytesTransferred, filesDeleted, sw.ElapsedMilliseconds);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncComplete, completePayload, ct);
        var (scType, scData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var deletedLabel = filesDeleted > 0 ? $", {filesDeleted} deleted" : "";
        _logger.Summary($"Sync complete: {filesTransferred} files transferred{deletedLabel}, {bytesTransferred / (1024.0 * 1024.0):F1} MB, {sw.ElapsedMilliseconds}ms");
        return exitCode;
    }
}
