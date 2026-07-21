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
    private readonly JsonProgressWriter _progress;
    private readonly StdinCommandReader _stdinReader;
    // Not readonly: when the caller supplies a path instead of an instance, RunAsync opens the
    // database itself — and it must not do so until the no-ancestor gate has run, because
    // `new SyncDatabase(path)` creates the file whose absence the gate is looking for.
    // RunAsync clears this again in a finally when it was the opener, so the field never
    // outlives the instance it points at; see the at-most-once note on RunAsync.
    private SyncDatabase? _db;
    private readonly string? _dbPath;

    /// <param name="db">
    /// An already-open database owned by the caller. Never disposed here.
    /// </param>
    /// <param name="dbPath">
    /// Where the ancestor database lives. Supplying this instead of <paramref name="db"/> lets
    /// RunAsync evaluate the no-ancestor gate against the on-disk state before anything opens
    /// (and thereby creates) the file, and lets it write pair.marker on a clean exit.
    /// </param>
    public SyncClient(SyncOptions options, SyncLogger logger,
                      JsonProgressWriter? progressWriter = null,
                      StdinCommandReader? stdinReader = null,
                      SyncDatabase? db = null,
                      string? dbPath = null)
    {
        _options = options;
        _logger = logger;
        _progress = progressWriter ?? JsonProgressWriter.Null;
        _stdinReader = stdinReader ?? StdinCommandReader.Null;
        _db = db;
        _dbPath = dbPath;
    }

    /// <summary>
    /// True when this pair has synced before but its ancestor database is gone or unusable.
    /// An absent database on its own is indistinguishable from one deleted after a hundred
    /// successful syncs, so pair.marker is the only thing separating a safe additive first run
    /// from a destructive one.
    /// </summary>
    public static bool PairStateLost(string dbPath)
    {
        if (!PairMarker.Exists(dbPath)) return false;
        if (!File.Exists(dbPath)) return true;

        // A header probe, not an open. `new SyncDatabase(path)` would create the file when it is
        // missing and run migrations when it is not, and Microsoft.Data.Sqlite pools the
        // connection so the handle outlives the `using` — a probe that mutates, locks or removes
        // its subject is not a probe. Sixteen bytes is enough: a truncated, empty or foreign file
        // fails the magic and carries no ancestor rows either way.
        try
        {
            using var fs = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[SqliteFileMagic.Length];
            int read = fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            return read < header.Length || !header.SequenceEqual(SqliteFileMagic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable for any reason means we cannot prove the ancestor table is there. Fail
            // closed: refusing a run is recoverable, propagating deletions computed without an
            // ancestor table is not.
            return true;
        }
    }

    /// <summary>The SQLite file header, "SQLite format 3\0".</summary>
    private static readonly byte[] SqliteFileMagic =
        System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");

    /// <summary>
    /// Runs one sync session. When the caller supplied <c>dbPath</c> rather than an open
    /// <c>db</c>, this method opens the ancestor database, publishes it to <c>_db</c> for the
    /// duration of the session, and clears the field again in <c>finally</c> before disposing —
    /// so a second call on the same instance re-opens its own database and works, it does not
    /// find a disposed one. Prefer a new <see cref="SyncClient"/> per session anyway; nothing
    /// about reuse is exercised or supported beyond "does not crash". (An instance handed in by
    /// the caller is never disposed here and belongs to the caller.)
    /// </summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        // No-ancestor safety gate. Before the socket, before the database is opened, before any
        // state is written — nothing at all must happen on a refusal. --mirror is the documented
        // escape: it means "make the destination match the source", which needs no ancestor.
        if (_dbPath != null && _options.DeleteEnabled && !_options.MirrorDeletes
            && PairStateLost(_dbPath))
        {
            var msg = "Sync state lost: this pair has synced before (pair.marker is present) but " +
                      $"its database at '{_dbPath}' is missing or unreadable. Without it, every " +
                      "file present on only one side is indistinguishable from one the peer " +
                      "deleted. Restore the database from backup, or re-run with --mirror to " +
                      "accept the destination being overwritten to match the source.";
            _logger.Error(msg);
            _progress.WriteError(msg, fatal: true);
            return 4;
        }

        // Only now open the ancestor database.
        SyncDatabase? opened = null;
        if (_db == null && _dbPath != null && _options.DeleteEnabled)
        {
            var binPath = Path.Combine(Path.GetDirectoryName(_dbPath)!, "sync-state.bin");
            SyncDatabase.MigrateFromBinary(binPath, _dbPath);
            opened = new SyncDatabase(_dbPath);
            _db = opened;
        }

        try
        {
            return await RunSessionAsync(ct);
        }
        finally
        {
            // Only what this method opened is disposed — a caller-supplied instance is not ours.
            // The field is cleared *before* the dispose so `_db` can never name a disposed
            // object: a `using` declaration would dispose it and leave the field pointing at the
            // corpse, and the next RunAsync call would hand HandleConnectionAsync that.
            if (opened != null)
            {
                _db = null;
                opened.Dispose();
            }
        }
    }

    /// <summary>
    /// The connect-retry loop and the session itself. Split out of <see cref="RunAsync"/> only so
    /// the database-ownership try/finally above can wrap every exit path, including the early
    /// returns from failed connects.
    /// </summary>
    private async Task<int> RunSessionAsync(CancellationToken ct)
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
        var modeLabel = _options.Mode switch
        {
            SyncMode.Push => "Push",
            SyncMode.Pull => "Pull",
            _ => "Two-way",
        };
        var deleteLabel = _options.DeleteEnabled ? " + delete" : "";
        _logger.Summary($"Connected. {modeLabel} sync{deleteLabel}." +
            (_options.Verbose ? $" Block: {_options.BlockSize / 1024}KB, Threads: {_options.MaxThreads}" : ""));
        _progress.WriteStatus("connected", mode: $"{modeLabel}{deleteLabel}");

        using var stream = owned.GetStream();
        var exit = await HandleConnectionAsync(stream, ct);

        // Arm the gate once an ancestor table has actually been built, which happens on exit 0
        // AND exit 1: the ancestor rows are written before the transfer loops and regardless of
        // skippedFiles, so exit 1 ("completed with skipped files") leaves the same usable table
        // behind that exit 0 does. Gating on exit == 0 alone left a pair with one
        // permanently-locked file — which exits 1 on every run — never arming the marker, so a
        // later-wiped database for that pair would never trip the no-ancestor gate at all: the
        // exact loss the gate exists to catch. 2 (connection failure), 3 (protocol/fatal) and 4
        // (safety abort) genuinely leave nothing behind and stay excluded.
        if ((exit == 0 || exit == 1) && _dbPath != null && _options.DeleteEnabled)
            PairMarker.Write(_dbPath);

        return exit;
    }

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

        // The sync_sessions row is opened inside the try below, NOT here: every abort above the
        // try — the corrupt-ancestor LoadAll gate especially, but also a failed manifest exchange
        // — must return before any row exists, or it leaks a row whose completed_utc stays NULL
        // forever. sessionId is declared here so the finally can still complete it.
        long sessionId = 0;

        // 3. Scan local folder and send client manifest
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
        // A null ancestor table is the honest signal for "we do not know what changed", and the
        // engine refuses to emit any deletion on that path.
        // `skew` is the measured client-vs-server clock offset from the v3 handshake above.
        // Passing ClockSkew.None here would leave a peer with a fast clock winning every
        // newest-wins comparison forever, re-transferring the same bytes on every run.
        IReadOnlyDictionary<string, AncestorRow>? ancestor;
        try
        {
            ancestor = _db?.LoadAll();
        }
        catch (Exception ex) when (_dbPath != null && PairMarker.Exists(_dbPath))
        {
            // PairStateLost's header probe only proves the file opens as SQLite; it does not
            // prove the `files` table inside it survived intact. A database whose header is
            // fine but whose ancestor table was dropped or corrupted passes that probe and
            // throws here instead — the same "prior state now unreadable" case the gate above
            // exists to catch, just discovered one step later. The marker's presence is what
            // proves a real prior sync happened, so this earns the identical exit 4 + restore
            // guidance rather than surfacing as a generic crash. Without the marker check this
            // filter would also swallow a genuine first run's exceptions and fabricate a "state
            // lost" report for state that never existed — so an absent marker falls through
            // uncaught, exactly like today, to the generic fatal-error path.
            var msg = "Sync state lost: this pair has synced before (pair.marker is present) but " +
                      $"its database at '{_dbPath}' could not be read ({ex.GetType().Name}: " +
                      $"{ex.Message}). Without it, every file present on only one side is " +
                      "indistinguishable from one the peer deleted. Restore the database from " +
                      "backup, or re-run with --mirror to accept the destination being " +
                      "overwritten to match the source.";
            _logger.Error(msg);
            _progress.WriteError(msg, fatal: true);
            return 4;
        }
        var planResult = SyncEngine.ComputePlan(
            clientManifest, serverManifest, _options.Mode, ancestor,
            _options.DeleteEnabled, _options.MirrorDeletes, skew);
        var syncPlan = planResult.Entries;

        // A path excluded by local filters must be invisible: never transferred, and above all
        // never deleted. Its absence from the manifest is otherwise indistinguishable from a
        // deletion, so tightening a filter would wipe those files on the peer.
        var filteredOut = syncPlan.Where(p => !scanner.IsIncluded(p.RelativePath)).ToList();
        if (filteredOut.Count > 0)
        {
            _logger.Info($"Ignoring {filteredOut.Count} path(s) excluded by local filters.");
            syncPlan = syncPlan.Where(p => scanner.IsIncluded(p.RelativePath)).ToList();
            // The row retirement that used to happen here moved down beside the ancestor writes,
            // so that every `return 4` in this method precedes all database mutation.
        }

        // Every ConflictKeepBoth becomes a frame-free local rename plus one transfer in each
        // direction, and this MUST happen before the plan is serialised: both peers execute the
        // list they are handed, so a conflict the server has to interpret for itself is a desync
        // waiting to happen. sessionStartUtc is the session's single clock read, so the conflict
        // name and the archive session folder carry the same timestamp.
        var conflictExpansion = ConflictKeepBothExecutor.Expand(
            syncPlan, clientManifest, serverManifest, skew, sessionStartUtc, _options.Folder);
        syncPlan = conflictExpansion.Entries;

        // ConflictKeepBoth entries move no bytes, so they are not transfers: counting them would
        // make the GUI's progress bar overshoot and never reach 100%.
        var transferCount = syncPlan.Count(p => p.Action != SyncActionType.Skip
            && p.Action != SyncActionType.DeleteOnServer && p.Action != SyncActionType.DeleteOnClient
            && p.Action != SyncActionType.ConflictKeepBoth);
        var deleteCount = syncPlan.Count(p => p.Action == SyncActionType.DeleteOnServer || p.Action == SyncActionType.DeleteOnClient);
        var skipCount = syncPlan.Count(p => p.Action == SyncActionType.Skip);
        var deleteSummary = deleteCount > 0 ? $", {deleteCount} delete" : "";
        var conflictSummary = conflictExpansion.RenamedTo.Count > 0
            ? $", {conflictExpansion.RenamedTo.Count} conflict" : "";
        _logger.Info($"Sync plan: {transferCount} transfers{deleteSummary}{conflictSummary}, {skipCount} skipped");

        // Total bytes the client will push, so the GUI can show real progress rather than
        // guessing from file counts. A conflict copy is not in the manifest yet — it is named
        // after a file that is — so fall back to the original's size rather than counting zero.
        long plannedBytes = syncPlan
            .Where(p => p.Action is SyncActionType.SendToServer or SyncActionType.ClientOnly)
            .Sum(p => clientManifest.Get(p.RelativePath)?.FileSize
                   ?? (ConflictNamer.TryParse(p.RelativePath, out var origin, out _)
                        ? clientManifest.Get(origin)?.FileSize ?? 0
                        : 0));
        _progress.WritePlan(transferCount, deleteCount, skipCount, plannedBytes);
        var planBytes = ProtocolHandler.SerializeSyncPlan(syncPlan);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncPlan, planBytes, ct);

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
        var sender = new FileTransferSender(_options.Folder, _options.BlockSize);
        var receiver = new FileTransferReceiver(_options.Folder);
        int filesTransferred = 0;
        int filesDeleted = 0;
        long bytesTransferred = 0;

        try  // Guarantee CompleteSession in finally block
        {
        // Open the session row as the first thing inside the try, so from here on every abort
        // returns through the finally that completes it. The corrupt-ancestor gate and the
        // manifest exchange already ran above and returned without opening it.
        if (_options.DeleteEnabled && _db != null)
        {
            // The session label is what the review report and the session history render, so it
            // must name the real mode: "uni" covered Push and Pull alike, making a run that
            // deleted local files indistinguishable from one that only uploaded.
            var sessionMode = $"{_options.Mode.ToString().ToLowerInvariant()}+delete"
                            + (_options.MirrorDeletes ? "+mirror" : "");
            sessionId = _db.StartSession(sessionMode, _options.Folder, _options.Host!, _options.Port);
            _logger.Info($"Sync session started (id={sessionId})");
        }

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
                // Bound each direction separately against the manifest of the side being deleted
                // FROM. The old denominator was the tracked-row count, which is 0 on a wiped or
                // never-built database — the guard then divided into nothing and skipped itself
                // in exactly the situation state loss creates, where every peer-only file is
                // indistinguishable from one the peer deleted.
                //
                // Each peer is authoritative for the deletions applied to itself: clientManifest
                // is this client's own scan, so an inflated peer manifest cannot relax the
                // DeleteOnClient bound. serverManifest arrived over the wire and is advisory —
                // the server re-checks DeleteOnServer against its own scan before applying it.
                int serverDeletes = syncPlan.Count(p => p.Action == SyncActionType.DeleteOnServer);
                int clientDeletes = syncPlan.Count(p => p.Action == SyncActionType.DeleteOnClient);

                if (!WithinDeleteBudget(serverDeletes, serverManifest.Count, "server")) return 4;
                if (!WithinDeleteBudget(clientDeletes, clientManifest.Count, "client")) return 4;
            }
        }

        // 6b. Conflict renames. Frame-free, and BEFORE any transfer: this is the only step where
        // the two peers do different work, so it must finish on both sides before a single file
        // frame moves or their transfer sets stop lining up. `archive` is the session's one
        // ArchiveManager — a second instance here would fork the restore point across two session
        // folders and shadow the outer local.
        //
        // This block sits ABOVE every database write that follows, deliberately: it can return 4,
        // and an aborted run must not leave committed rows behind.
        var conflictEntries = syncPlan.Where(p => p.Action == SyncActionType.ConflictKeepBoth).ToList();
        if (conflictEntries.Count > 0)
        {
            var conflictOutcome = ConflictKeepBothExecutor.ApplyLocalRenames(
                syncPlan, ConflictNamer.ClientSide, _options.Folder, archive);

            // Fatal, not skippable: the plan already promised the peer a transfer under the
            // conflict name, and a sender that cannot open its source throws while compressing or
            // hashing it — both before the first frame is written — leaving the peer blocked on a
            // FileStart that never arrives.
            if (conflictOutcome.Failures.Count > 0)
            {
                var msg = $"Refusing to sync: conflict rename failed for {conflictOutcome.Failures.Count} " +
                          $"path(s): {string.Join("; ", conflictOutcome.Failures)}";
                _logger.Error(msg);
                _progress.WriteError(msg, fatal: true);
                return 4;
            }

            // The rename itself succeeded; only the belt-and-braces pre-rename snapshot did not.
            // Warn rather than abort — the bytes are intact under the new name.
            foreach (var path in conflictOutcome.NotArchived)
                _logger.Warning($"Conflict copy of {path} was renamed but could not be archived first.");

            foreach (var entry in conflictEntries)
            {
                if (!ConflictNamer.TryParse(entry.RelativePath, out var original, out var losingSide)) continue;
                _logger.Info($"[!] Conflict on {original}: {losingSide} copy kept as {entry.RelativePath}");
            }
        }

        // Retire rows for paths the local filters excluded. This ran up beside the filtering
        // itself, above both delete guards and the rename pass, so an exit-4 abort still
        // committed it. Every `return 4` above now precedes it.
        if (_db != null && filteredOut.Count > 0)
        {
            foreach (var entry in filteredOut)
                _db.MarkDeleted(entry.RelativePath, sessionId, "excluded by filters; retiring tracked row");
        }

        // Moved below both delete guards and the conflict rename pass. This block used to run
        // above them, so an exit-4 abort still committed its rows; the next run then planned
        // fewer deletions, slipped under the same threshold, and executed them against state that
        // was never confirmed by a completed sync.
        if (_db != null && ancestor != null)
        {
            // Paths the local filters excluded were already dropped from syncPlan above. Drop
            // them from the side channels too: an excluded path must stay invisible, and
            // reporting a resurrection for one names a file the user took out of scope.
            planResult.Resurrections.RemoveAll(r => !scanner.IsIncluded(r.Path));
            planResult.Conflicts.RemoveAll(c => !scanner.IsIncluded(c.Path));

            foreach (var skip in syncPlan.Where(p => p.Action == SyncActionType.Skip))
            {
                var skippedOnClient = clientManifest.Get(skip.RelativePath);
                var skippedOnServer = serverManifest.Get(skip.RelativePath);

                if (skippedOnClient != null && skippedOnServer != null)
                {
                    // An ancestor row asserts "both sides held this file and agreed", so it may
                    // only be written when both sides actually have it, each column carrying its
                    // own side's size and mtime. The old code fell back to
                    // `client ?? server` and stamped one side's values into both columns, which
                    // manufactured a peer state that never existed: in Push a server-only file
                    // was recorded as "the client had it too" and run 2 deleted it, and in Pull
                    // the mirror deleted the user's own local-only files.
                    _db.UpsertSynced(skip.RelativePath,
                        skippedOnClient.FileSize, skippedOnClient.LastModifiedUtc.Ticks,
                        skippedOnServer.FileSize, skippedOnServer.LastModifiedUtc.Ticks,
                        sessionId, "skipped");
                }
                else
                {
                    // One-sided skip: Push leaving a server-only file alone, or Pull leaving a
                    // client-only file alone. Record that we saw and skipped it, without
                    // claiming the peer ever had it.
                    _db.MarkSkipped(skip.RelativePath, sessionId);
                }
            }

            // Retire rows for files now absent on both sides. Left as 'exists', a later restore
            // on one side is resolved as a deletion on the other. The snapshot loaded before
            // planning is the right input here: it is precisely the last state both sides agreed
            // on, and re-reading the table would also pick up the rows just written above.
            foreach (var row in ancestor.Values)
            {
                if (row.Status != "exists") continue;
                if (clientManifest.Contains(row.Path) || serverManifest.Contains(row.Path)) continue;
                _db.Tombstone(row.Path, sessionId, "absent on both sides; retiring tracked row");
            }
        }

        // 7. Send files to server (SendToServer + ClientOnly). Pull never uploads: the server is
        // authoritative, so a stale client copy must not be pushed back over it. Gated here as
        // well as in the planner because the plan travels to the peer, which sizes its receive
        // loop from it — both halves must be gated on the same predicate.
        var toSend = ModeGate.ClientToServer(_options.Mode)
            ? syncPlan.Where(p =>
                p.Action == SyncActionType.SendToServer || p.Action == SyncActionType.ClientOnly).ToList()
            : new List<SyncPlanEntry>();

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
            catch (Exception ex) when (IsPeerDisconnect(ex))
            {
                // The server closed the socket out from under us — most likely one of its own
                // conflict-plan guards rejected the plan and returned without telling us (it has
                // no frame for "I'm aborting" once it has already read the plan). Every remaining
                // WriteMessageAsync/ReadMessageAsync on this stream would fail the same way, so
                // this is terminal exactly like the desynced flag above: stop the phase here
                // instead of grinding through every remaining file with one logged error each and
                // then throwing uncaught out of the delete phase below.
                _logger.Error($"Peer closed the connection while sending {action.RelativePath} " +
                              $"({ex.GetType().Name}: {ex.Message}). The server likely rejected " +
                              "the sync plan; aborting.");
                desynced = true;
                break;
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
            _progress.WriteError("Protocol desync or peer disconnect during transfer phase; aborting sync.", fatal: true);
            return 3;   // inside the try — the finally still calls CompleteSession
        }

        // 8. Deletion Phase (Server): Send DeleteFile for DeleteOnServer actions. Pull never
        // deletes on the server; the server's matching receive loop is gated identically, so a
        // plan that somehow carried DeleteOnServer in Pull mode is dropped by both peers rather
        // than by one of them.
        if (_options.DeleteEnabled && ModeGate.ClientToServer(_options.Mode))
        {
            var serverDeletes = syncPlan.Where(p => p.Action == SyncActionType.DeleteOnServer).ToList();
            foreach (var del in serverDeletes)
            {
                if (!_stdinReader.WaitWhilePaused(ct)) { _logger.Warning("Stop requested."); stopped = true; break; }
                try
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
                catch (Exception ex) when (IsPeerDisconnect(ex))
                {
                    // Mirror of the Phase A catch above: an asymmetric --force-delete run (client
                    // forced, server not) can reach this loop with the socket already closed —
                    // the server hit its own delete budget, return-4'd and disposed its stream
                    // before this write ever lands. Every later frame on this stream fails the
                    // same way, so stop the phase here instead of throwing out of the delete
                    // loop uncaught.
                    _logger.Error($"Peer closed the connection while deleting {del.RelativePath} " +
                                  $"on the server ({ex.GetType().Name}: {ex.Message}). The peer " +
                                  "likely aborted after its own delete-budget guard rejected the " +
                                  "plan; aborting.");
                    desynced = true;
                    break;
                }
            }
        }

        if (desynced)
        {
            // Same terminal handling as the Phase A check above: a peer disconnect discovered
            // during the delete phase leaves every later frame on this stream unusable too.
            skippedFiles++;
            _progress.WriteError("Protocol desync or peer disconnect during delete phase; aborting sync.", fatal: true);
            return 3;   // inside the try — the finally still calls CompleteSession
        }

        // 9. Receive files from server (SendToClient + ServerOnly). Push never writes to the
        // client; the peer sends nothing in that mode, so entering this loop would block on a
        // frame that never arrives until the session times out.
        if (ModeGate.ServerToClient(_options.Mode))
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
                        onBeforeCommit: p =>
                        {
                            // `action.Action` is the peer's label for this path, deserialized
                            // from syncPlan — not a fact about our filesystem, and it is also
                            // decided from a manifest scanned earlier in the run, so a file the
                            // user creates locally mid-sync would look ServerOnly to the plan
                            // even though it now exists here. A hostile or buggy peer, or that
                            // TOCTOU window, must not skip the archive: TryArchive itself checks
                            // the LOCAL file and returns NothingToArchive when there truly is
                            // nothing to protect, so gating on the peer's label instead would let
                            // either case get a local file overwritten with no archived copy.
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

        // 10. Deletion Phase (Client): Receive DeleteFile for DeleteOnClient actions.
        // Gated on mode rather than Bidirectional: Pull plans DeleteOnClient too, and the old
        // gate dropped every one of them while the server sat in its send loop waiting for a
        // DeleteConfirm that never came.
        if (_options.DeleteEnabled && ModeGate.ServerToClient(_options.Mode))
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

                // The budget above bounds the COUNT of deletions the plan approved; it says
                // nothing about which PATHS. `del` is the entry that count was computed from, and
                // DeleteFile frames arrive in the same order this loop iterates `clientDeletes`,
                // so a wire path that does not match `del.RelativePath` names a file the approved
                // plan never listed — a hostile or buggy server could otherwise delete any N
                // in-folder files it likes, where N is merely the approved count. PathGuard (via
                // Archive below) only keeps the result inside the sync root; it does not check
                // that the path was ever planned, which is the check this is.
                if (!string.Equals(path, del.RelativePath, StringComparison.Ordinal))
                {
                    _logger.Error($"Protocol mismatch: expected DeleteFile for {del.RelativePath} " +
                                  $"(next in plan order), got {path}. Refusing to delete a path " +
                                  "the approved plan did not list.");
                    skippedFiles++;
                    _progress.WriteDelete(path, backed_up: backupFirst, success: false);
                    var mismatchConfirm = ProtocolHandler.SerializeDeleteConfirm(path, success: false);
                    await ProtocolHandler.WriteMessageAsync(stream, MessageType.DeleteConfirm, mismatchConfirm, ct);
                    continue;
                }

                try
                {
                    // `backupFirst` is decoded from the wire and DELIBERATELY IGNORED. It stays
                    // in the protocol so old peers keep parsing DeleteFile, and it is still
                    // echoed to the progress stream below as "what the peer asked for", but it
                    // no longer selects a delete-without-archive path: our own client always
                    // sends true, so that path was reachable only from a hostile or buggy peer,
                    // and all it could ever do is destroy a file with no restore point.
                    // Archive() already performs the same PathGuard containment check against
                    // _options.Folder and returns false when it fails, so the separate guard
                    // branch that used to sit here is redundant, not lost.
                    if (archive.Archive(path, ArchiveReason.Deleted, removeOriginal: true))
                    {
                        success = true;
                        filesDeleted++;
                        _logger.Info($"[DEL] {path} (deleted locally)");
                        _db?.MarkDeleted(path, sessionId, "deleted on server, propagated to client");
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

                _progress.WriteDelete(path, backed_up: backupFirst, success: success);
                var confirmPayload = ProtocolHandler.SerializeDeleteConfirm(path, success);
                await ProtocolHandler.WriteMessageAsync(stream, MessageType.DeleteConfirm, confirmPayload, ct);
            }
        }

        // 10b. Record the conflicts and resurrections, now that both transfer phases have
        // completed. Draining here rather than at plan time means a run that aborts mid-transfer
        // records nothing, so the review report can never claim an outcome that was not actually
        // executed. Both drains live here, together: file_versions has exactly one writer.
        //
        // The detail column is an ENCODED ConflictDetail, never English: the review report decodes
        // it to print both sides' size and mtime, and Decode returns null on anything else.
        if (_db != null)
        {
            foreach (var conflict in planResult.Conflicts)
            {
                conflictExpansion.RenamedTo.TryGetValue(conflict.Path, out var renamedTo);
                _db.LogConflict(conflict.Path, sessionId, new ConflictDetail(
                    conflict.ClientSize, conflict.ClientMtimeTicks,
                    conflict.ServerSize, conflict.ServerMtimeTicks,
                    renamedTo).Encode());
            }

            // ResurrectionInfo carries only the KEPT side. The losing side was deleted, so it has
            // no size and no mtime to record and its two columns are written as 0 — which is
            // unambiguous, because a surviving file always has a non-zero mtime tick count. A
            // zero mtime column therefore reads as "this side is the one that had been deleted".
            // RenamedTo is null: a resurrection renames nothing.
            //
            // The review report tells these rows apart from conflict rows by the file_versions
            // action column, not by the detail, so a zeroed column is never read as a measured one.
            foreach (var resurrection in planResult.Resurrections)
            {
                var detail = resurrection.KeptClientCopy
                    ? new ConflictDetail(resurrection.KeptSize, resurrection.KeptMtimeTicks, 0, 0, null)
                    : new ConflictDetail(0, 0, resurrection.KeptSize, resurrection.KeptMtimeTicks, null);
                _db.LogResurrection(resurrection.Path, sessionId, detail.Encode());
            }
        }

        // 11. Exchange SyncComplete
        sw.Stop();
        int exitCode = (skippedFiles > 0 || stopped) ? 1 : 0;
        _progress.WriteComplete(filesTransferred, filesDeleted, bytesTransferred, sw.ElapsedMilliseconds, exitCode);
        try
        {
            var completePayload = ProtocolHandler.SerializeSyncComplete(filesTransferred, bytesTransferred, filesDeleted, sw.ElapsedMilliseconds);
            await ProtocolHandler.WriteMessageAsync(stream, MessageType.SyncComplete, completePayload, ct);
            await ProtocolHandler.ReadMessageAsync(stream, ct);
        }
        catch (Exception ex) when (IsPeerDisconnect(ex))
        {
            // Same peer-disconnect handling as the transfer and delete phases: a socket the peer
            // already closed (its own guard rejected the plan, or it aborted for any other
            // reason) fails this closing exchange exactly like it would fail an earlier one, and
            // deserves the same clean exit rather than an unhandled throw this late in the run.
            _logger.Error($"Peer closed the connection during the closing SyncComplete exchange " +
                          $"({ex.GetType().Name}: {ex.Message}). Aborting.");
            _progress.WriteError("Protocol desync or peer disconnect during sync completion; aborting sync.", fatal: true);
            return 3;   // inside the try — the finally still calls CompleteSession
        }
        var deletedLabel = filesDeleted > 0 ? $", {filesDeleted} deleted" : "";
        _logger.Summary($"Sync complete: {filesTransferred} files transferred{deletedLabel}, {bytesTransferred / (1024.0 * 1024.0):F1} MB, {sw.ElapsedMilliseconds}ms");

        // Placed after the summary so it is the last thing on screen. A conflict, a resurrection
        // or a first-run overwrite is a decision the sync made on the operator's behalf, and a
        // single INF line among a thousand transfer lines is not a report — they will not see it.
        // Conflicts and resurrections are read back from rows Phase 7's drain committed; overwrites
        // are handed straight from planResult because the no-ancestor path that produces them can
        // run with _db == null (a TwoWay run without --delete), so no DB row ever exists for them.
        // archive.SessionRoot lets the report name where an overwritten local copy was archived.
        ReviewReport.Emit(_db, sessionId, _logger, _progress,
            planResult.Overwrites, archive.SessionRoot);

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

    /// <summary>
    /// True when <paramref name="ex"/> means the socket itself is gone, as opposed to a
    /// per-file failure (missing local file, checksum mismatch, sharing violation) that leaves
    /// the connection usable for the next file in the loop. EndOfStreamException is the one
    /// ProtocolHandler.ReadExactAsync raises itself, always for "the peer closed its end"; a
    /// SocketException carried as IOException.InnerException is NetworkStream's wrapping of the
    /// same condition on a write. A bare IOException is deliberately excluded — that is also
    /// what a locked or unreadable source file throws, and misclassifying it here would abort
    /// the whole transfer phase over one file instead of skipping just that file.
    /// </summary>
    private static bool IsPeerDisconnect(Exception ex) =>
        ex is EndOfStreamException
        || ex is SocketException
        || (ex is IOException { InnerException: SocketException });

    /// <summary>
    /// Percentage bound for one direction, plus the operator-facing message. The arithmetic
    /// lives in <see cref="DeleteBudget"/> so this peer and its peer apply the same rule.
    /// </summary>
    private bool WithinDeleteBudget(int deletes, int destinationCount, string destinationLabel)
    {
        if (DeleteBudget.Within(deletes, destinationCount, _options.MaxDeletePercent)) return true;

        var msg = $"Refusing to sync: {deletes} of {destinationCount} file(s) on the " +
                  $"{destinationLabel} would be deleted, exceeding --max-delete-percent " +
                  $"{_options.MaxDeletePercent}. Check that --folder on both sides points where " +
                  "you expect, and that the sync database was not moved or deleted. If this is " +
                  "intentional, re-run with --force-delete on BOTH sides — the peer enforces its " +
                  "own copy of this bound.";
        _logger.Error(msg);
        _progress.WriteError(msg, fatal: true);
        return false;
    }
}
