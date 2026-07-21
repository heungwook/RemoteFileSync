namespace RemoteFileSync.Models;

public sealed class SyncOptions
{
    public bool IsServer { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 15782;
    public string Folder { get; set; } = string.Empty;
    public SyncMode Mode { get; set; } = SyncMode.Push;

    /// <summary>
    /// Compatibility shim for the pre-<see cref="SyncMode"/> callers. Read-only on purpose: a
    /// settable copy would let it disagree with <see cref="Mode"/>, so a Pull sync could still
    /// take the bidirectional write-to-server branches.
    /// </summary>
    public bool Bidirectional => Mode == SyncMode.TwoWay;
    public bool DeleteEnabled { get; set; }
    public bool JsonProgress { get; set; }
    public string? BackupFolder { get; set; }
    public List<string> IncludePatterns { get; set; } = new();
    public List<string> ExcludePatterns { get; set; } = new();
    public int BlockSize { get; set; } = 65536;
    public int MaxThreads { get; set; } = 1;
    public bool Verbose { get; set; }
    public string? LogFile { get; set; }

    /// <summary>
    /// Interface the server binds to. Defaults to loopback: this protocol has NO
    /// authentication, so exposing it on all interfaces grants anyone who can reach the port
    /// arbitrary read/write/delete within the sync folder.
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// Server: handle one connection and exit. Off by default — a persistent listener means a
    /// stray connection (port scan, failed attempt) cannot kill the server before the real
    /// client arrives. Use for scripted one-shot syncs.
    /// </summary>
    public bool Once { get; set; }

    /// <summary>
    /// Abort the sync if deletions would exceed this percentage of tracked files. Guards
    /// against an empty or repointed peer folder wiping the other side. 100 disables it.
    /// Only applied once the tracked population reaches
    /// <see cref="MinTrackedFilesForDeleteGuard"/> — see that field for why.
    /// </summary>
    public int MaxDeletePercent { get; set; } = 25;

    /// <summary>
    /// The percentage guard is only meaningful at scale. Deleting 1 of 2 tracked files is
    /// 50% but entirely ordinary; deleting 2500 of 10000 is a catastrophe. Applying the
    /// percentage to tiny populations would fire constantly on normal edits and train users
    /// to pass --force-delete by reflex, disabling the guard exactly when it matters.
    /// </summary>
    public const int MinTrackedFilesForDeleteGuard = 10;

    /// <summary>Bypass <see cref="MaxDeletePercent"/> for an intentional bulk deletion.</summary>
    public bool ForceDelete { get; set; }

    /// <summary>
    /// Backup destination. Defaults to a sibling ".rfs-backups-NAME" directory OUTSIDE the
    /// sync folder — placing backups inside the synced tree makes them re-scan as new files
    /// and propagate to the peer, growing without bound.
    /// Throws when the sync folder has no parent (a drive root or UNC share root); there is
    /// no safe default in that case and the user must pass --backup-folder explicitly.
    /// </summary>
    public string EffectiveBackupFolder
    {
        get
        {
            if (BackupFolder != null) return BackupFolder;

            var full = Path.GetFullPath(Folder).TrimEnd(Path.DirectorySeparatorChar);
            var parent = Path.GetDirectoryName(full);
            var name = Path.GetFileName(full);

            // A drive root ("E:\") or UNC share root ("\\server\share") has no parent.
            // Falling back to the sync folder here would silently reintroduce the bug
            // where backups land inside the synced tree.
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
                throw new ArgumentException(
                    $"--folder '{Folder}' is a drive or share root and has no parent directory, " +
                    "so there is no safe default backup location. Pass --backup-folder explicitly " +
                    "(it must be outside the sync folder).");

            return Path.Combine(parent, $".rfs-backups-{name}");
        }
    }

    /// <summary>
    /// Propagate deletions from the authoritative side even when the ancestor table has no
    /// evidence the file was ever synced. Off by default: without an ancestor row a missing
    /// file is indistinguishable from a file that was simply never sent, so mirroring would
    /// delete work the peer created independently.
    /// </summary>
    public bool MirrorDeletes { get; set; }

    /// <summary>Archive destination override. See <see cref="EffectiveArchiveFolder"/>.</summary>
    public string? ArchiveFolder { get; set; }

    /// <summary>
    /// Where deleted/overwritten/conflicting files are parked before removal. Defaults to a
    /// sibling ".rfs-archive-NAME" directory OUTSIDE the sync folder — archiving inside the
    /// synced tree makes the archived copy re-scan as a new file and propagate back to the
    /// peer, resurrecting exactly the file that was just deleted.
    /// Throws when the sync folder has no parent (a drive root or UNC share root); there is
    /// no safe default in that case and the user must pass --archive-folder explicitly.
    /// </summary>
    public string EffectiveArchiveFolder
    {
        get
        {
            if (ArchiveFolder != null) return ArchiveFolder;

            var full = Path.GetFullPath(Folder).TrimEnd(Path.DirectorySeparatorChar);
            var parent = Path.GetDirectoryName(full);
            var name = Path.GetFileName(full);

            // A drive root ("E:\") or UNC share root ("\\server\share") has no parent.
            // Falling back to the sync folder here would put the archive inside the synced
            // tree, which resurrects deletions on the next run.
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
                throw new ArgumentException(
                    $"--folder '{Folder}' is a drive or share root and has no parent directory, " +
                    "so there is no safe default archive location. Pass --archive-folder explicitly " +
                    "(it must be outside the sync folder).");

            return Path.Combine(parent, $".rfs-archive-{name}");
        }
    }

    /// <summary>Prune archived sessions older than this many days. 0 = keep forever.</summary>
    public int ArchiveKeepDays { get; set; } = 30;

    /// <summary>Prune oldest archived sessions once the archive exceeds this size. 0 = no cap.</summary>
    public long ArchiveMaxBytes { get; set; }

    /// <summary>
    /// Clock offsets above this are reported rather than silently trusted. Newest-wins
    /// comparisons are only meaningful within a small skew; a peer an hour ahead would make
    /// every one of its files look newer and overwrite the whole other side.
    /// </summary>
    public const int SuspiciousSkewSeconds = 60;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Folder))
            throw new ArgumentException("--folder is required.");
        if (!Directory.Exists(Folder))
            throw new ArgumentException($"Folder does not exist: {Folder}");
        if (!IsServer && string.IsNullOrWhiteSpace(Host))
            throw new ArgumentException("--host is required in client mode.");
        if (Port < 1 || Port > 65535)
            throw new ArgumentException($"--port must be 1-65535, got {Port}.");
        // Validate here rather than at bind time so a bad value yields a usage message
        // instead of an opaque fatal error. Hostnames are intentionally rejected.
        if (IsServer && !System.Net.IPAddress.TryParse(BindAddress, out _))
            throw new ArgumentException(
                $"--bind must be an IP address (got '{BindAddress}'). Use 0.0.0.0 to listen on all interfaces.");

        const int minBlock = 4096;
        const int maxBlock = 4 * 1024 * 1024;
        if (BlockSize < minBlock)
        {
            Console.Error.WriteLine($"Warning: --block-size {BlockSize} clamped to minimum {minBlock}.");
            BlockSize = minBlock;
        }
        if (BlockSize > maxBlock)
        {
            Console.Error.WriteLine($"Warning: --block-size {BlockSize} clamped to maximum {maxBlock}.");
            BlockSize = maxBlock;
        }
        if (MaxThreads < 1) MaxThreads = 1;

        if (ArchiveKeepDays < 0)
            throw new ArgumentException(
                $"--archive-keep-days must be >= 0 (0 = keep forever), got {ArchiveKeepDays}. " +
                "A negative age makes every archived session look expired and empties the archive " +
                "on its first prune.");
        if (ArchiveMaxBytes < 0)
            throw new ArgumentException(
                $"--archive-max-size must be >= 0 (0 = no cap), got {ArchiveMaxBytes}. " +
                "A negative cap is below any real archive size, so every session would be pruned.");

        // Backups inside the sync folder are re-scanned as new files and propagated to the
        // peer, growing without bound. Reject that outright rather than discovering it later.
        var syncFull = Path.GetFullPath(Folder);
        if (!syncFull.EndsWith(Path.DirectorySeparatorChar)) syncFull += Path.DirectorySeparatorChar;
        var backupFull = Path.GetFullPath(EffectiveBackupFolder);
        if (backupFull.StartsWith(syncFull, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"--backup-folder must be outside the sync folder (got '{backupFull}' inside '{syncFull}'). " +
                "Backups inside the sync folder are re-synced to the peer and grow without bound.");

        // Same containment rule for the archive, but a worse failure than the backup case: an
        // archived deletion sitting inside the synced tree propagates back to the peer and
        // recreates the file that was just deleted, so the deletion silently undoes itself on
        // the next run.
        var archiveFull = Path.GetFullPath(EffectiveArchiveFolder);
        // Compare with a trailing separator on BOTH sides. Without it the sync folder itself —
        // the most destructive value --archive-folder can take — is not a prefix match of
        // syncFull and slips through.
        var archiveProbe = archiveFull.EndsWith(Path.DirectorySeparatorChar)
            ? archiveFull
            : archiveFull + Path.DirectorySeparatorChar;
        if (archiveProbe.StartsWith(syncFull, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"--archive-folder must be outside the sync folder (got '{archiveFull}' inside '{syncFull}'). " +
                "Archived deletions inside the sync folder are re-synced to the peer and resurrect " +
                "the files that were just deleted.");
    }
}
