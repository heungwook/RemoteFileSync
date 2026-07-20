using System.Diagnostics;
using System.Net;
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

public sealed class SyncServer
{
    private readonly SyncOptions _options;
    private readonly SyncLogger _logger;
    private readonly JsonProgressWriter _progress;
    private readonly StdinCommandReader _stdinReader;
    private readonly SyncDatabase? _db;

    public SyncServer(SyncOptions options, SyncLogger logger,
                      JsonProgressWriter? progressWriter = null,
                      StdinCommandReader? stdinReader = null,
                      SyncDatabase? db = null)
    {
        _options = options;
        _logger = logger;
        _progress = progressWriter ?? JsonProgressWriter.Null;
        _stdinReader = stdinReader ?? StdinCommandReader.Null;
        _db = db;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (!IPAddress.TryParse(_options.BindAddress, out var bindIp))
            throw new ArgumentException($"Invalid --bind address: {_options.BindAddress}");

        var listener = new TcpListener(bindIp, _options.Port);
        listener.Start();
        _logger.Summary($"Listening on {bindIp}:{_options.Port}...");
        if (!IPAddress.IsLoopback(bindIp))
            _logger.Warning(
                "This server is reachable from the network and has NO AUTHENTICATION. " +
                "Any peer can read, write, and delete within the sync folder. " +
                "Use only on a trusted network or over a VPN/SSH tunnel.");
        _progress.WriteStatus("listening", port: _options.Port);

        // Dispose order matters: `linked` must be torn down before StdinCommandReader.Dispose
        // cancels and disposes StopToken.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stdinReader.StopToken.Token);
        bool anySessionFailed = false;

        try
        {
            do
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(linked.Token);
                }
                catch (SocketException ex)
                {
                    // A failed accept must not kill the listener.
                    _logger.Warning($"Accept failed: {ex.Message}");
                    continue;
                }

                using (client)
                {
                    _logger.Summary("Client connected.");
                    _progress.WriteStatus("connected");

                    // A peer that connects and never sends must not hang the accept loop:
                    // one idle socket would otherwise block every other client indefinitely.
                    // client.ReceiveTimeout does NOT apply to async NetworkStream reads.
                    using var session = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    session.CancelAfter(SessionTimeout);

                    try
                    {
                        using var stream = client.GetStream();
                        int exit = await HandleConnectionAsync(stream, session.Token);
                        if (exit != 0) anySessionFailed = true;
                        if (_options.Once) return exit;
                    }
                    catch (OperationCanceledException) when (!linked.IsCancellationRequested)
                    {
                        _logger.Error("Session timed out.");
                        _progress.WriteError("Session timed out.", fatal: false);
                        anySessionFailed = true;
                        if (_options.Once) return 3;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One bad session must not kill the listener.
                        _logger.Error($"Session failed: {ex.Message}");
                        _progress.WriteError($"Session failed: {ex.Message}", fatal: false);
                        anySessionFailed = true;
                        if (_options.Once) return 3;
                    }
                }
                _progress.WriteStatus("listening", port: _options.Port);
            }
            while (!linked.IsCancellationRequested);
        }
        catch (OperationCanceledException) { /* graceful stop */ }
        finally
        {
            listener.Stop();
        }

        // Aggregate rather than "whatever the last session returned": a clean shutdown after
        // many good syncs and one bad one must not report a nondeterministic code.
        return anySessionFailed ? 1 : 0;
    }

    /// <summary>
    /// Upper bound on a single session, so an idle peer cannot stall the accept loop. Must
    /// exceed the longest legitimate sync.
    /// </summary>
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromHours(6);

    private async Task<int> HandleConnectionAsync(NetworkStream stream, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int skippedFiles = 0;
        bool stopped = false;

        // ONE clock read for the whole run. Everything stamped with this instant — the archive
        // session folder below and the conflict-rename filenames a later phase adds — must
        // agree, or a run longer than a second scatters its own output across two session
        // names and neither is a complete restore point.
        var sessionStartUtc = DateTime.UtcNow;

        // 1. Receive handshake
        var (hsType, hsData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        if (hsType != MessageType.Handshake)
        {
            _logger.Error($"Expected Handshake, got {hsType}");
            return 3;
        }
        byte version;
        byte syncMode;
        try
        {
            (version, syncMode, _) = ProtocolHandler.DeserializeHandshake(hsData);
        }
        catch (InvalidDataException)
        {
            // A v2 client's handshake is 2 bytes, which the v3 length guard rejects before its
            // version byte can be read, so the versionOk path below cannot report the mismatch.
            // Answer with a well-formed rejecting ack anyway: otherwise the accept loop's
            // catch-all closes the socket and the peer reports an unexplained disconnect
            // instead of "upgrade both sides".
            await ProtocolHandler.WriteMessageAsync(stream, MessageType.HandshakeAck,
                ProtocolHandler.SerializeHandshakeAck(
                    ProtocolHandler.ProtocolVersion, accepted: false, DateTime.UtcNow.Ticks), ct);
            _logger.Error("Rejected client: its handshake is shorter than protocol " +
                          $"v{ProtocolHandler.ProtocolVersion} requires — the peer is an older build.");
            return 3;
        }

        // Clamped through a switch rather than cast: syncMode arrives from an unauthenticated
        // peer and 0 is not a defined SyncMode member, so a raw cast would yield an enum value
        // that every later "== SyncMode.Push" comparison reads as "not Push" and admits writes
        // to the server's tree on.
        var mode = (syncMode & 0b11) switch
        {
            2 => SyncMode.Pull,
            3 => SyncMode.TwoWay,
            _ => SyncMode.Push,
        };
        bool deleteEnabled = (syncMode & 4) != 0;
        // Decoded for reporting only. The server executes the plan the client computed, and the
        // mirror decision is already baked into that plan, so acting on this bit here would
        // apply the rule twice. Kept as a named local so the log line and Phase 8's server-side
        // delete accounting read the same value.
        bool mirrorDeletes = (syncMode & 8) != 0;
        _logger.Info($"Handshake: v{version}, mode={mode}" +
                     (deleteEnabled ? " +delete" : "") + (mirrorDeletes ? " +mirror" : ""));

        // 2. Send HandshakeAck — reject version mismatches rather than misparse frames.
        // serverTicks is stamped at send time so the client's round-trip halving is honest.
        bool versionOk = version == ProtocolHandler.ProtocolVersion;
        var ackPayload = ProtocolHandler.SerializeHandshakeAck(
            ProtocolHandler.ProtocolVersion, accepted: versionOk, DateTime.UtcNow.Ticks);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.HandshakeAck, ackPayload, ct);
        if (!versionOk)
        {
            _logger.Error($"Rejected client: protocol v{version}, this build speaks v{ProtocolHandler.ProtocolVersion}.");
            return 3;
        }

        // 3. Receive client manifest
        var (mType, mData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var clientManifest = ProtocolHandler.DeserializeManifest(mData);
        _logger.Info($"Client manifest: {clientManifest.Count} files");
        _progress.WriteManifest("remote", clientManifest.Count, clientManifest.Entries.Sum(e => e.FileSize));

        // 4. Scan local folder and send server manifest
        var scanner = new FileScanner(_options.Folder, _options.IncludePatterns, _options.ExcludePatterns);
        var serverManifest = scanner.Scan();
        _logger.Info($"Local manifest: {serverManifest.Count} files");
        _progress.WriteManifest("local", serverManifest.Count, serverManifest.Entries.Sum(e => e.FileSize));
        var serverManifestBytes = ProtocolHandler.SerializeManifest(serverManifest);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Manifest, serverManifestBytes, ct);

        // 5. Receive sync plan
        var (pType, pData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var syncPlan = ProtocolHandler.DeserializeSyncPlan(pData);
        _logger.Info($"Sync plan: {syncPlan.Count} actions");

        // Retention runs here, before the first archive write and before the first transfer,
        // so the session folder this run is about to create can never be a prune candidate.
        // TimeSpan.Zero — never TimeSpan.MaxValue — is the "keep forever" sentinel:
        // DateTime.UtcNow - TimeSpan.MaxValue throws and would abort the sync at session start.
        var keepAge = _options.ArchiveKeepDays > 0
            ? TimeSpan.FromDays(_options.ArchiveKeepDays)
            : TimeSpan.Zero;
        var pruned = ArchiveManager.Prune(_options.EffectiveArchiveFolder, keepAge, _options.ArchiveMaxBytes);
        if (pruned.SessionsRemoved > 0)
            _logger.Info($"Archive retention: removed {pruned.SessionsRemoved} session(s), " +
                         $"freed {pruned.BytesFreed / 1024} KB.");

        // The single ArchiveManager for this session. Later phases REUSE this local; a second
        // instance means a second session folder for the same run.
        var archive = new ArchiveManager(_options.Folder, _options.EffectiveArchiveFolder, sessionStartUtc);
        var receiver = new FileTransferReceiver(_options.Folder);
        var sender = new FileTransferSender(_options.Folder, _options.BlockSize);
        int filesTransferred = 0;
        long bytesTransferred = 0;
        int filesDeleted = 0;

        // 6. Receive files from client (SendToServer + ClientOnly)
        var toReceive = syncPlan.Where(p =>
            p.Action == SyncActionType.SendToServer || p.Action == SyncActionType.ClientOnly).ToList();

        foreach (var action in toReceive)
        {
            if (!_stdinReader.WaitWhilePaused(ct)) { _logger.Warning("Stop requested."); stopped = true; break; }
            // Snapshot via the pre-commit hook, keyed on the path actually received:
            // backing up by plan index moved the wrong file whenever the peer skipped one.
            FileReceiveResult result;
            try
            {
                result = await receiver.ReceiveFileAsync(stream, ct,
                    onBeforeCommit: p =>
                    {
                        // `action.Action` is the peer's label for this path, deserialized from
                        // syncPlan — not a fact about our filesystem. A hostile or buggy peer
                        // that mislabels an overwrite as ClientOnly must not skip the archive:
                        // TryArchive itself checks the LOCAL file and returns NothingToArchive
                        // when there truly is nothing to protect, so gating on the peer's label
                        // instead would let that peer get a local file overwritten with no
                        // archived copy.
                        var outcome = archive.TryArchive(p, ArchiveReason.Overwritten, removeOriginal: false);
                        if (outcome == ArchiveOutcome.Failed)
                            _logger.Error($"Pre-overwrite archive failed for {p}; " +
                                          "refusing to overwrite the local copy.");
                        return outcome != ArchiveOutcome.Failed;
                    });
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

        // 7. Deletion Phase (Server): Receive DeleteFile from client for DeleteOnServer actions
        if (deleteEnabled)
        {
            // The plan arrives over the wire from a peer we do not authenticate, so the server
            // enforces its own bound rather than trusting the client's guard.
            int requested = syncPlan.Count(p => p.Action == SyncActionType.DeleteOnServer);
            if (requested > 0 && serverManifest.Count >= SyncOptions.MinTrackedFilesForDeleteGuard
                && !_options.ForceDelete)
            {
                double pct = requested * 100.0 / serverManifest.Count;
                if (pct > _options.MaxDeletePercent)
                {
                    var msg = $"Rejecting sync plan: peer requested deletion of {requested} of " +
                              $"{serverManifest.Count} local files ({pct:F0}%), exceeding " +
                              $"--max-delete-percent {_options.MaxDeletePercent}.";
                    _logger.Error(msg);
                    _progress.WriteError(msg, fatal: true);
                    return 4;
                }
            }

            var serverDeletes = syncPlan.Where(p => p.Action == SyncActionType.DeleteOnServer).ToList();
            foreach (var del in serverDeletes)
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
                    // `backupFirst` is decoded from the wire and DELIBERATELY IGNORED. It stays
                    // in the protocol so old peers keep parsing DeleteFile, but it no longer
                    // selects a delete-without-archive path: our own client always sends true,
                    // so that path was reachable only from a hostile or buggy peer, and all it
                    // could ever do is destroy a file with no restore point. Archive() already
                    // performs the same PathGuard containment check against _options.Folder and
                    // returns false when it fails, so the separate guard branch that used to sit
                    // here is redundant, not lost.
                    if (archive.Archive(path, ArchiveReason.Deleted, removeOriginal: true))
                    {
                        success = true;
                        filesDeleted++;
                        _logger.Info($"[DEL] {path}");
                    }
                    else
                    {
                        // Not found, outside the sync root, or unarchivable — in every case we
                        // decline to delete rather than delete unprotected.
                        _logger.Warning($"Could not archive {path} for deletion. Skipping.");
                        skippedFiles++;
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

        // 8. Send files to client (SendToClient + ServerOnly). Two-way only at this stage; the
        // mode-dispatch phase widens the condition to admit Pull, which also writes to the
        // client. Behaviour is unchanged here — this is the rename forced by dropping the
        // `bidirectional` local in favour of `mode`.
        if (mode == SyncMode.TwoWay)
        {
            var toSend = syncPlan.Where(p =>
                p.Action == SyncActionType.SendToClient || p.Action == SyncActionType.ServerOnly).ToList();

            bool desynced = false;
            foreach (var action in toSend)
            {
                if (!_stdinReader.WaitWhilePaused(ct)) { _logger.Warning("Stop requested."); stopped = true; break; }
                try
                {
                    short fileId = (short)(filesTransferred % short.MaxValue);
                    await sender.SendFileAsync(stream, fileId, action.RelativePath, ct);

                    var (cType, cData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
                    if (cType != MessageType.BackupConfirm)
                    {
                        // See SyncClient: must not throw into the swallow-all catch below.
                        _logger.Error($"Protocol desync: expected BackupConfirm for {action.RelativePath}, " +
                                      $"got {cType}. Aborting transfer phase.");
                        desynced = true;
                        break;
                    }

                    if (cData.Length > 0 && cData[^1] != 1)
                    {
                        _logger.Error($"Peer failed to commit {action.RelativePath}.");
                        skippedFiles++;
                        continue;
                    }

                    _logger.Info($"[→] {action.RelativePath}");
                    filesTransferred++;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to send {action.RelativePath}: {ex.Message}");
                    skippedFiles++;
                }
            }

            if (desynced)
            {
                // Every later phase reads from the same misaligned stream, so this is terminal.
                skippedFiles++;
                _progress.WriteError("Protocol desync during transfer phase; aborting sync.", fatal: true);
                return 3;
            }
        }

        // 9. Deletion Phase (Client): Send DeleteFile for DeleteOnClient actions
        if (deleteEnabled && mode == SyncMode.TwoWay)
        {
            var clientDeletes = syncPlan.Where(p => p.Action == SyncActionType.DeleteOnClient).ToList();
            foreach (var del in clientDeletes)
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
                        _logger.Info($"[DEL→] Client deleted {del.RelativePath}");
                    }
                    else
                    {
                        _logger.Warning($"Client failed to delete {del.RelativePath}");
                        skippedFiles++;
                    }
                }
            }
        }

        // 10. Exchange SyncComplete
        sw.Stop();
        int exitCode = (skippedFiles > 0 || stopped) ? 1 : 0;
        _progress.WriteComplete(filesTransferred, filesDeleted, bytesTransferred, sw.ElapsedMilliseconds, exitCode);
        var completePayload = ProtocolHandler.SerializeSyncComplete(filesTransferred, bytesTransferred, filesDeleted, sw.ElapsedMilliseconds);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncComplete, completePayload, ct);
        var (scType, scData) = await ProtocolHandler.ReadMessageAsync(stream, ct);
        var deletedSummary = filesDeleted > 0 ? $", {filesDeleted} deleted" : "";
        _logger.Summary($"Sync complete: {filesTransferred} files transferred{deletedSummary}, {bytesTransferred / (1024.0 * 1024.0):F1} MB, {sw.ElapsedMilliseconds}ms");
        return exitCode;
    }
}
