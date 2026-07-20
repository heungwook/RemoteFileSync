using System.Diagnostics;
using System.Net.Sockets;
using RemoteFileSync.Backup;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Progress;
using RemoteFileSync.Security;
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
        int retries = 3;
        TcpClient? tcp = null;

        for (int attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
                // A socket whose connect failed cannot be reconnected, so each attempt needs
                // a fresh client; reusing one made retries 2 and 3 fail instantly.
                tcp?.Dispose();
                tcp = new TcpClient();
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
                var msg = $"Connection failed after {retries} attempts: {ex.Message}";
                _logger.Error(msg);
                _progress.WriteError(msg, fatal: true);
                tcp?.Dispose();
                return 2;
            }
        }

        if (tcp is null) return 2;
        using var owned = tcp;

        _progress.WriteStatus("connecting", host: _options.Host, port: _options.Port);
        var modeLabel = _options.Bidirectional ? "Bi-directional" : "Uni-directional";
        var deleteLabel = _options.DeleteEnabled ? " + delete" : "";
        _logger.Summary($"Connected. {modeLabel} sync{deleteLabel}." +
            (_options.Verbose ? $" Block: {_options.BlockSize / 1024}KB, Threads: {_options.MaxThreads}" : ""));
        _progress.WriteStatus("connected", mode: $"{modeLabel}{deleteLabel}");

        using var stream = owned.GetStream();
        return await HandleConnectionAsync(stream, ct);
    }

    private async Task<int> HandleConnectionAsync(NetworkStream stream, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int skippedFiles = 0;
        bool stopped = false;

        // 1. Send handshake
        byte syncMode = (byte)((byte)_options.Mode
                               | (_options.DeleteEnabled ? 4 : 0)
                               | (_options.MirrorDeletes ? 8 : 0));
        // Stamped immediately before the write so the round-trip ClockSkew halves is the
        // network's latency and not our own frame-building time.
        long clientSentTicks = DateTime.UtcNow.Ticks;
        var hsPayload = ProtocolHandler.SerializeHandshake(
            ProtocolHandler.ProtocolVersion, syncMode, clientSentTicks);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Handshake, hsPayload, ct);

        // 2. Receive HandshakeAck
        var (ackType, ackData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        long clientRecvTicks = DateTime.UtcNow.Ticks;
        if (ackType != MessageType.HandshakeAck)
        {
            _logger.Error($"Expected HandshakeAck, got {ackType}");
            return 3;
        }
        byte serverVersion;
        bool accepted;
        long serverTicks;
        try
        {
            (serverVersion, accepted, serverTicks) = ProtocolHandler.DeserializeHandshakeAck(ackData);
        }
        catch (InvalidDataException)
        {
            // A v2 server answers with a 2-byte ack, which the v3 length guard rejects before
            // the version byte can be read — so the "server speaks v{n}" branch below would
            // never run for the one case it was written for. Without this catch the exception
            // escapes RunAsync entirely and the user sees Program.cs's generic
            // "Fatal error: HandshakeAck payload truncated." with no idea what to do about it.
            _logger.Error(
                "Protocol mismatch: the server's HandshakeAck is shorter than protocol " +
                $"v{ProtocolHandler.ProtocolVersion} requires, so the server is an older build. " +
                "Upgrade both sides to the same build.");
            return 2;
        }
        if (serverVersion != ProtocolHandler.ProtocolVersion)
        {
            _logger.Error($"Protocol mismatch: server speaks v{serverVersion}, this build speaks " +
                          $"v{ProtocolHandler.ProtocolVersion}. Upgrade both sides to the same build. " +
                          "(A v1 server silently discards the timestamp field and sync will never converge.)");
            return 2;
        }
        if (!accepted)
        {
            _logger.Error("Server rejected the connection.");
            return 2;
        }

        // Measured once per session and reused by every cross-side timestamp comparison.
        // Newest-wins resolution pits a client-stamped mtime against a server-stamped one; on
        // machines whose clocks disagree that comparison picks the wrong winner and the loser's
        // edit is overwritten with no conflict recorded.
        var skew = ClockSkew.Measure(clientSentTicks, serverTicks, clientRecvTicks);
        if (skew.IsSuspicious)
        {
            _logger.Warning(
                $"Server clock differs from this machine by {skew.Offset.TotalSeconds:+0.0;-0.0} seconds " +
                $"(threshold {SyncOptions.SuspiciousSkewSeconds}s; positive means the server is ahead). " +
                "Two-way sync breaks ties by comparing the two sides' modification times, so a skew " +
                "this large can select the older edit as the winner and overwrite the newer one. " +
                "Fix NTP on both machines before relying on two-way sync.");
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

        // A path excluded by local filters must be invisible: never transferred, and above all
        // never deleted. Its absence from the manifest is otherwise indistinguishable from a
        // deletion, so tightening a filter would wipe those files on the peer.
        var filteredOut = syncPlan.Where(p => !scanner.IsIncluded(p.RelativePath)).ToList();
        if (filteredOut.Count > 0)
        {
            _logger.Info($"Ignoring {filteredOut.Count} path(s) excluded by local filters.");
            syncPlan = syncPlan.Where(p => scanner.IsIncluded(p.RelativePath)).ToList();
            if (_db != null)
            {
                foreach (var entry in filteredOut)
                    _db.MarkDeleted(entry.RelativePath, sessionId, "excluded by filters; retiring tracked row");
            }
        }

        var transferCount = syncPlan.Count(p => p.Action != SyncActionType.Skip
            && p.Action != SyncActionType.DeleteOnServer && p.Action != SyncActionType.DeleteOnClient);
        var deleteCount = syncPlan.Count(p => p.Action == SyncActionType.DeleteOnServer || p.Action == SyncActionType.DeleteOnClient);
        var skipCount = syncPlan.Count(p => p.Action == SyncActionType.Skip);
        var deleteSummary = deleteCount > 0 ? $", {deleteCount} delete" : "";
        _logger.Info($"Sync plan: {transferCount} transfers{deleteSummary}, {skipCount} skipped");

        // Total bytes the client will push, so the GUI can show real progress rather than
        // guessing from file counts.
        long plannedBytes = syncPlan
            .Where(p => p.Action is SyncActionType.SendToServer or SyncActionType.ClientOnly)
            .Sum(p => clientManifest.Get(p.RelativePath)?.FileSize ?? 0);
        _progress.WritePlan(transferCount, deleteCount, skipCount, plannedBytes);
        var planBytes = ProtocolHandler.SerializeSyncPlan(syncPlan);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncPlan, planBytes, ct);

        if (_db != null)
        {
            foreach (var skip in syncPlan.Where(p => p.Action == SyncActionType.Skip))
            {
                // Use client manifest entry (or server as fallback) to record in files table so
                // deletion can be detected on the next run.
                var entry = clientManifest.Get(skip.RelativePath)
                         ?? serverManifest.Get(skip.RelativePath);
                if (entry != null)
                    _db.MarkSynced(skip.RelativePath, entry.FileSize, entry.LastModifiedUtc, sessionId, "skipped");
                else
                    _db.MarkSkipped(skip.RelativePath, sessionId);
            }

            // Retire tracked rows for files absent on both sides. Left as 'exists', a later
            // restore on one side is resolved as a deletion on the other.
            foreach (var fs in _db.GetAllTrackedFiles())
            {
                if (fs.Status != "exists") continue;
                if (clientManifest.Contains(fs.Path) || serverManifest.Contains(fs.Path)) continue;
                _db.MarkDeleted(fs.Path, sessionId, "absent on both sides; retiring tracked row");
            }
        }

        var backup = new BackupManager(_options.Folder, _options.EffectiveBackupFolder);
        var sender = new FileTransferSender(_options.Folder, _options.BlockSize);
        var receiver = new FileTransferReceiver(_options.Folder);
        int filesTransferred = 0;
        int filesDeleted = 0;
        long bytesTransferred = 0;

        try  // Guarantee CompleteSession in finally block
        {
        // Deletion safety gates. Both live inside the try so an abort still completes the
        // DB session; returning from outside it would leak an open session row.
        if (_options.DeleteEnabled && deleteCount > 0)
        {
            // An incomplete scan cannot justify a deletion: the peer cannot tell a file that
            // was unreadable from one that was removed.
            if (scanner.InaccessibleDirectories > 0)
            {
                var msg = $"Refusing to propagate deletions: {scanner.InaccessibleDirectories} " +
                          "directory(ies) could not be read, so the local manifest is incomplete.";
                _logger.Error(msg);
                _progress.WriteError(msg, fatal: true);
                return 4;
            }

            if (!_options.ForceDelete)
            {
                // Denominator is the tracked-file population, NOT a manifest count: with
                // max(client, server) a peer repointed at a larger unrelated folder yields a
                // small percentage and every tracked file gets deleted anyway.
                int tracked = _db != null
                    ? _db.GetAllTrackedFiles().Count(f => f.Status == "exists")
                    : previousState?.Manifest.Count ?? 0;

                if (tracked >= SyncOptions.MinTrackedFilesForDeleteGuard)
                {
                    double pct = deleteCount * 100.0 / tracked;
                    if (pct > _options.MaxDeletePercent)
                    {
                        var msg = $"Refusing to sync: {deleteCount} of {tracked} tracked files " +
                                  $"({pct:F0}%) would be deleted, exceeding --max-delete-percent " +
                                  $"{_options.MaxDeletePercent}. Check that --folder on both sides " +
                                  "points where you expect. If this is intentional, re-run with --force-delete.";
                        _logger.Error(msg);
                        _progress.WriteError(msg, fatal: true);
                        return 4;
                    }
                }
            }
        }

        // 7. Send files to server (SendToServer + ClientOnly)
        var toSend = syncPlan.Where(p =>
            p.Action == SyncActionType.SendToServer || p.Action == SyncActionType.ClientOnly).ToList();

        bool desynced = false;
        foreach (var action in toSend)
        {
            if (!_stdinReader.WaitWhilePaused(ct)) { _logger.Warning("Stop requested."); stopped = true; break; }
            try
            {
                short fileId = (short)(filesTransferred % short.MaxValue);
                var planned = clientManifest.Get(action.RelativePath);
                _progress.WriteFileStart("to_server", action.RelativePath, planned?.FileSize ?? 0,
                    compressed: !CompressionHelper.IsAlreadyCompressed(Path.GetExtension(action.RelativePath)),
                    thread: 0);
                await sender.SendFileAsync(stream, fileId, action.RelativePath, ct,
                    onBytesSent: sent => _progress.WriteFileProgress(
                        action.RelativePath, sent, planned?.FileSize ?? 0, thread: 0));

                var (cType, cData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
                if (cType != MessageType.BackupConfirm)
                {
                    // MUST NOT throw: the catch below swallows everything, so the loop would
                    // continue on a stream that is now off by one frame and attribute the next
                    // file's confirm to the wrong file — and that flag drives MarkSynced.
                    _logger.Error($"Protocol desync: expected BackupConfirm for {action.RelativePath}, " +
                                  $"got {cType}. Aborting transfer phase.");
                    desynced = true;
                    break;
                }

                // The peer reports whether it actually committed the file. Trusting the send
                // alone caused failed writes to be recorded as synced, which the next run then
                // resolved to a deletion of the surviving local copy.
                bool peerCommitted = cData.Length > 0 && cData[^1] == 1;
                if (!peerCommitted)
                {
                    _logger.Error($"Peer failed to commit {action.RelativePath}; not recording as synced.");
                    skippedFiles++;
                    _progress.WriteFileEnd(action.RelativePath, success: false, error: "peer rejected file");
                    continue;
                }

                _logger.Info($"[→] {action.RelativePath}");
                filesTransferred++;
                var sfi = new FileInfo(Path.Combine(_options.Folder, action.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                bytesTransferred += sfi.Length;
                _progress.WriteFileEnd(action.RelativePath, success: true, thread: 0);
                _db?.MarkSynced(action.RelativePath, sfi.Length, sfi.LastWriteTimeUtc, sessionId, "to_server");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to send {action.RelativePath}: {ex.Message}");
                skippedFiles++;
                _progress.WriteFileEnd(action.RelativePath, success: false, error: ex.Message);
            }
        }

        if (desynced)
        {
            // Every later phase reads from the same misaligned stream, so this is terminal.
            skippedFiles++;
            _progress.WriteError("Protocol desync during transfer phase; aborting sync.", fatal: true);
            return 3;   // inside the try — the finally still calls CompleteSession
        }

        // 8. Deletion Phase (Server): Send DeleteFile for DeleteOnServer actions
        if (_options.DeleteEnabled)
        {
            var serverDeletes = syncPlan.Where(p => p.Action == SyncActionType.DeleteOnServer).ToList();
            foreach (var del in serverDeletes)
            {
                if (!_stdinReader.WaitWhilePaused(ct)) { _logger.Warning("Stop requested."); stopped = true; break; }
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
                        _progress.WriteDelete(del.RelativePath, backed_up: true, success: true);
                        _db?.MarkDeleted(del.RelativePath, sessionId, "deleted on client, propagated to server");
                    }
                    else
                    {
                        _logger.Warning($"Server failed to delete {del.RelativePath}");
                        _progress.WriteDelete(del.RelativePath, backed_up: false, success: false);
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
                if (!_stdinReader.WaitWhilePaused(ct)) { _logger.Warning("Stop requested."); stopped = true; break; }
                // Snapshot via the pre-commit hook, keyed on the path actually received:
                // backing up by plan index moved the wrong file whenever the peer skipped one.
                FileReceiveResult result;
                try
                {
                    result = await receiver.ReceiveFileAsync(stream, ct,
                        onBeforeCommit: p => action.Action == SyncActionType.SendToClient && backup.BackupFile(p));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Error($"Error receiving {action.RelativePath}: {ex.Message}");
                    result = new FileReceiveResult(false, action.RelativePath, ex.Message);
                }
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
                if (!_stdinReader.WaitWhilePaused(ct)) { _logger.Warning("Stop requested."); stopped = true; break; }
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
                        if (backup.BackupAndRemove(path))
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
                    else if (!PathGuard.TryResolveWithinRoot(_options.Folder, path, out var fullPath))
                    {
                        _logger.Error($"Rejected delete for path outside sync root: {path}");
                        skippedFiles++;
                    }
                    else
                    {
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

                _progress.WriteDelete(path, backed_up: backupFirst, success: success);
                var confirmPayload = ProtocolHandler.SerializeDeleteConfirm(path, success);
                await ProtocolHandler.WriteMessageAsync(stream, MessageType.DeleteConfirm, confirmPayload, ct);
            }
        }

        // 11. Exchange SyncComplete
        sw.Stop();
        int exitCode = (skippedFiles > 0 || stopped) ? 1 : 0;
        _progress.WriteComplete(filesTransferred, filesDeleted, bytesTransferred, sw.ElapsedMilliseconds, exitCode);
        var completePayload = ProtocolHandler.SerializeSyncComplete(filesTransferred, bytesTransferred, filesDeleted, sw.ElapsedMilliseconds);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncComplete, completePayload, ct);
        var (scType, scData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var deletedLabel = filesDeleted > 0 ? $", {filesDeleted} deleted" : "";
        _logger.Summary($"Sync complete: {filesTransferred} files transferred{deletedLabel}, {bytesTransferred / (1024.0 * 1024.0):F1} MB, {sw.ElapsedMilliseconds}ms");

        // Fallback: save binary state when db is null (backward compat)
        if (_db == null && exitCode == 0 && _options.DeleteEnabled && _stateManager != null)
        {
            var mergedManifest = SyncEngine.BuildMergedManifest(clientManifest, serverManifest, syncPlan);
            _stateManager.SaveState(_options.Folder, _options.Host!, _options.Port, mergedManifest, DateTime.UtcNow);
            _logger.Debug($"Sync state saved: {mergedManifest.Count} files");
        }

        return exitCode;
        }
        finally
        {
            // 12. Guarantee session completion even on exception/cancellation
            if (_db != null && sessionId > 0)
            {
                var finalExitCode = (skippedFiles > 0 || stopped) ? 1 : 0;
                _db.CompleteSession(sessionId, filesTransferred, filesDeleted,
                    syncPlan.Count(p => p.Action == SyncActionType.Skip), finalExitCode);
                _logger.Debug($"Sync session {sessionId} completed (exit code {finalExitCode})");
            }
        }
    }
}
