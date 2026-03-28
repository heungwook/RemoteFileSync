using System.Diagnostics;
using System.Net.Sockets;
using RemoteFileSync.Backup;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Progress;
using RemoteFileSync.State;
using RemoteFileSync.Sync;
using RemoteFileSync.Transfer;

namespace RemoteFileSync.Network;

public sealed class SyncClient
{
    private readonly SyncOptions _options;
    private readonly SyncLogger _logger;
    private readonly SyncStateManager? _stateManager;
    private readonly JsonProgressWriter _progress;
    private readonly StdinCommandReader _stdinReader;
    private readonly SyncDatabase? _db;

    public SyncClient(SyncOptions options, SyncLogger logger,
                      SyncStateManager? stateManager = null,
                      JsonProgressWriter? progressWriter = null,
                      StdinCommandReader? stdinReader = null,
                      SyncDatabase? db = null)
    {
        _options = options;
        _logger = logger;
        _stateManager = stateManager;
        _progress = progressWriter ?? JsonProgressWriter.Null;
        _stdinReader = stdinReader ?? StdinCommandReader.Null;
        _db = db;
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

        _progress.WriteStatus("connecting", host: _options.Host, port: _options.Port);
        var modeLabel = _options.Bidirectional ? "Bi-directional" : "Uni-directional";
        var deleteLabel = _options.DeleteEnabled ? " + delete" : "";
        _logger.Summary($"Connected. {modeLabel} sync{deleteLabel}." +
            (_options.Verbose ? $" Block: {_options.BlockSize / 1024}KB, Threads: {_options.MaxThreads}" : ""));
        _progress.WriteStatus("connected", mode: $"{modeLabel}{deleteLabel}");

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

        // Start database session
        long sessionId = 0;
        if (_options.DeleteEnabled && _db != null)
        {
            var mode = $"{(_options.Bidirectional ? "bidi" : "uni")}+delete";
            sessionId = _db.StartSession(mode, _options.Folder, _options.Host!, _options.Port);
            _logger.Info($"Sync session started (id={sessionId})");
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
        _progress.WriteManifest("local", clientManifest.Count, clientManifest.Entries.Sum(e => e.FileSize));
        var clientManifestBytes = ProtocolHandler.SerializeManifest(clientManifest);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Manifest, clientManifestBytes, ct);

        // 5. Receive server manifest
        var (mType, mData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var serverManifest = ProtocolHandler.DeserializeManifest(mData);
        _logger.Info($"Remote manifest: {serverManifest.Count} files");
        _progress.WriteManifest("remote", serverManifest.Count, serverManifest.Entries.Sum(e => e.FileSize));

        // 6. Compute sync plan and send
        var syncPlan = (_db != null)
            ? SyncEngine.ComputePlan(clientManifest, serverManifest, _options.Bidirectional, _db, _options.DeleteEnabled)
            : SyncEngine.ComputePlan(clientManifest, serverManifest, _options.Bidirectional, previousState, _options.DeleteEnabled);
        var transferCount = syncPlan.Count(p => p.Action != SyncActionType.Skip
            && p.Action != SyncActionType.DeleteOnServer && p.Action != SyncActionType.DeleteOnClient);
        var deleteCount = syncPlan.Count(p => p.Action == SyncActionType.DeleteOnServer || p.Action == SyncActionType.DeleteOnClient);
        var skipCount = syncPlan.Count(p => p.Action == SyncActionType.Skip);
        var deleteSummary = deleteCount > 0 ? $", {deleteCount} delete" : "";
        _logger.Info($"Sync plan: {transferCount} transfers{deleteSummary}, {skipCount} skipped");
        _progress.WritePlan(transferCount, deleteCount, skipCount);
        var planBytes = ProtocolHandler.SerializeSyncPlan(syncPlan);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncPlan, planBytes, ct);

        if (_db != null)
        {
            foreach (var skip in syncPlan.Where(p => p.Action == SyncActionType.Skip))
                _db.MarkSkipped(skip.RelativePath, sessionId);
        }

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
            _stdinReader.PauseGate.Wait();
            if (_stdinReader.StopToken.IsCancellationRequested) { _logger.Warning("Stop requested."); break; }
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
                _progress.WriteFileEnd(action.RelativePath, success: true, thread: 0);
                if (_db != null)
                {
                    var sfi = new FileInfo(Path.Combine(_options.Folder, action.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                    _db.MarkSynced(action.RelativePath, sfi.Length, sfi.LastWriteTimeUtc, sessionId, "to_server");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to send {action.RelativePath}: {ex.Message}");
                skippedFiles++;
                _progress.WriteFileEnd(action.RelativePath, success: false, error: ex.Message);
            }
        }

        // 8. Deletion Phase (Server): Send DeleteFile for DeleteOnServer actions
        if (_options.DeleteEnabled)
        {
            var serverDeletes = syncPlan.Where(p => p.Action == SyncActionType.DeleteOnServer).ToList();
            foreach (var del in serverDeletes)
            {
                _stdinReader.PauseGate.Wait();
                if (_stdinReader.StopToken.IsCancellationRequested) { _logger.Warning("Stop requested."); break; }
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
                        _db?.MarkDeleted(del.RelativePath, sessionId, "deleted on client, propagated to server");
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
                _stdinReader.PauseGate.Wait();
                if (_stdinReader.StopToken.IsCancellationRequested) { _logger.Warning("Stop requested."); break; }
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
                    if (_db != null)
                    {
                        var rfi = new FileInfo(Path.Combine(_options.Folder, result.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                        _db.MarkSynced(result.RelativePath, rfi.Length, rfi.LastWriteTimeUtc, sessionId, "to_client");
                    }
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
                _stdinReader.PauseGate.Wait();
                if (_stdinReader.StopToken.IsCancellationRequested) { _logger.Warning("Stop requested."); break; }
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
                        if (backup.BackupFile(path))
                        {
                            success = true;
                            filesDeleted++;
                            _logger.Info($"[DEL] {path} (deleted locally)");
                            _db?.MarkDeleted(path, sessionId, "deleted on server, propagated to client");
                        }
                        else
                        {
                            _logger.Warning($"File not found for backup/delete: {path}. Skipping.");
                            skippedFiles++;
                        }
                    }
                    else
                    {
                        var fullPath = Path.Combine(_options.Folder, path.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                            success = true;
                            filesDeleted++;
                            _logger.Info($"[DEL] {path} (deleted locally)");
                            _db?.MarkDeleted(path, sessionId, "deleted on server, propagated to client");
                        }
                        else
                        {
                            _logger.Warning($"File not found for delete: {path}. Skipping.");
                            skippedFiles++;
                        }
                    }
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

        // 11. Exchange SyncComplete
        sw.Stop();
        int exitCode = skippedFiles > 0 ? 1 : 0;
        _progress.WriteComplete(filesTransferred, filesDeleted, bytesTransferred, sw.ElapsedMilliseconds, exitCode);
        var completePayload = ProtocolHandler.SerializeSyncComplete(filesTransferred, bytesTransferred, filesDeleted, sw.ElapsedMilliseconds);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncComplete, completePayload, ct);
        var (scType, scData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var deletedLabel = filesDeleted > 0 ? $", {filesDeleted} deleted" : "";
        _logger.Summary($"Sync complete: {filesTransferred} files transferred{deletedLabel}, {bytesTransferred / (1024.0 * 1024.0):F1} MB, {sw.ElapsedMilliseconds}ms");

        // 12. Complete database session (always, regardless of exit code)
        if (_db != null && sessionId > 0)
        {
            _db.CompleteSession(sessionId, filesTransferred, filesDeleted,
                syncPlan.Count(p => p.Action == SyncActionType.Skip), exitCode);
            _logger.Debug($"Sync session {sessionId} completed (exit code {exitCode})");
        }

        // Fallback: save binary state when db is null (backward compat)
        if (_db == null && exitCode == 0 && _options.DeleteEnabled && _stateManager != null)
        {
            var mergedManifest = SyncEngine.BuildMergedManifest(clientManifest, serverManifest, syncPlan);
            _stateManager.SaveState(_options.Folder, _options.Host!, _options.Port, mergedManifest, DateTime.UtcNow);
            _logger.Debug($"Sync state saved: {mergedManifest.Count} files");
        }

        return exitCode;
    }
}
