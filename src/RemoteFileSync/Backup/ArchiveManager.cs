using System.Globalization;
using RemoteFileSync.Security;

namespace RemoteFileSync.Backup;

public enum ArchiveReason { Deleted, Overwritten, Conflict }

/// <summary>
/// Why an archive attempt ended. `bool` conflates the last two: a caller about to destroy the
/// original must proceed on NothingToArchive (there was no previous version) and refuse on
/// Failed (there was one and we could not preserve it).
/// </summary>
public enum ArchiveOutcome { Archived, NothingToArchive, Failed }

public readonly record struct PruneResult(int SessionsRemoved, long BytesFreed);

/// <summary>
/// Archives files into one folder per sync session:
/// <c>&lt;archiveRoot&gt;/&lt;yyyyMMdd-HHmmss&gt;/&lt;reason&gt;/&lt;original relative path&gt;</c>.
/// The session stamp is supplied by the caller and captured ONCE per run; the superseded
/// BackupManager read DateTime.UtcNow per file, so a run crossing midnight UTC scattered one
/// logical session across two dated folders that could not be restored together.
/// </summary>
public sealed class ArchiveManager
{
    /// <summary>
    /// Session folder format. Public because Prune parses folder names back with this exact
    /// format, and a caller that fabricates or locates a session folder must use the same one.
    /// </summary>
    public const string SessionFolderFormat = "yyyyMMdd-HHmmss";

    private readonly string _syncFolder;
    private readonly object _lock = new();

    /// <param name="sessionStartUtc">
    /// The instant the sync session began, in UTC. Captured once by the caller; see the class
    /// remarks for why it is not read from the clock here.
    /// </param>
    public ArchiveManager(string syncFolder, string archiveRoot, DateTime sessionStartUtc)
    {
        _syncFolder = Path.GetFullPath(syncFolder);
        SessionFolderName = sessionStartUtc.ToString(SessionFolderFormat, CultureInfo.InvariantCulture);
        SessionRoot = Path.Combine(Path.GetFullPath(archiveRoot), SessionFolderName);
    }

    public string SessionFolderName { get; }

    public string SessionRoot { get; }

    /// <summary>
    /// Copies the file into this session's archive under <paramref name="reason"/>, optionally
    /// deleting the original afterwards. Returns false if the path is not contained by the sync
    /// root, the file does not exist, or the copy itself failed (locked file, full disk — see
    /// <see cref="TryArchive"/>). Callers MUST NOT run a destructive step on a discarded result:
    /// PathGuard fails closed on transient IO, so false does not imply "nothing to do".
    /// </summary>
    public bool Archive(string relativePath, ArchiveReason reason, bool removeOriginal)
        => TryArchive(relativePath, reason, removeOriginal) == ArchiveOutcome.Archived;

    /// <summary>
    /// As <see cref="Archive"/>, but distinguishes "there was no file to preserve" from "we
    /// could not preserve it". PathGuard fails CLOSED on transient IO (PathGuard.cs:85-86), so
    /// containment failure is reported as Failed, never as NothingToArchive.
    /// </summary>
    public ArchiveOutcome TryArchive(string relativePath, ArchiveReason reason, bool removeOriginal)
    {
        // relativePath can arrive from the network (deletion propagation), so it must be
        // contained before it reaches the filesystem.
        if (!PathGuard.TryResolveWithinRoot(_syncFolder, relativePath, out var sourcePath))
            return ArchiveOutcome.Failed;
        if (!File.Exists(sourcePath)) return ArchiveOutcome.NothingToArchive;

        lock (_lock)
        {
            // Derive the archive-relative path from the GUARDED source, never from the wire
            // string. PathGuard accepts dot segments as long as the final resolved path lands
            // inside the root, so "../../../<tail of the sync root>/f.txt" is a legal alias for
            // a file we own. Replaying that alias from SessionRoot pushes the destination back
            // OUT of the archive (GetFullPath clamps ".." at the drive root) and into the live
            // sync tree, where the next scan re-syncs the "archive" copy to the peer and Prune
            // can never reclaim it.
            var rel = Path.GetRelativePath(_syncFolder, sourcePath);
            var reasonRoot = Path.Combine(SessionRoot, ReasonFolder(reason));
            var destDir = Path.Combine(reasonRoot, Path.GetDirectoryName(rel) ?? "");
            var destPath = Path.Combine(destDir, Path.GetFileName(rel));

            // Invariant assertion, not a second guard: `rel` cannot escape today. It exists so
            // that a future edit reintroducing an unguarded string here fails by returning
            // false instead of silently writing outside the session folder.
            var reasonRootFull = Path.GetFullPath(reasonRoot);
            if (!reasonRootFull.EndsWith(Path.DirectorySeparatorChar))
                reasonRootFull += Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(destPath).StartsWith(reasonRootFull, StringComparison.Ordinal))
                return ArchiveOutcome.Failed;

            // Everything from here down touches the filesystem. An exception escaping this
            // method would unwind ReceiveFileAsync's onBeforeCommit callback, which is invoked
            // inside the try whose finally sweeps the staging file (FileTransfer.cs:177 vs
            // :193-198) — so staging is not at risk — but the exception itself would still
            // propagate out of ReceiveFileAsync, and the caller would get no FileReceiveResult
            // at all. Report the failure instead of throwing it.
            try
            {
                Directory.CreateDirectory(destDir);

                var fileName = Path.GetFileNameWithoutExtension(rel);
                var ext = Path.GetExtension(rel);

                // One path can be archived twice in a session (overwritten, then deleted), and a
                // clobbering copy would destroy the earlier version.
                int suffix = 1;
                while (File.Exists(destPath))
                {
                    destPath = Path.Combine(destDir, $"{fileName}_{suffix}{ext}");
                    suffix++;
                }

                // Copy first: if the copy fails we must not have destroyed the original.
                File.Copy(sourcePath, destPath, overwrite: false);
                if (removeOriginal) File.Delete(sourcePath);
                return ArchiveOutcome.Archived;
            }
            catch (IOException) { return ArchiveOutcome.Failed; }
            catch (UnauthorizedAccessException) { return ArchiveOutcome.Failed; }
        }
    }

    private static string ReasonFolder(ArchiveReason reason) => reason switch
    {
        ArchiveReason.Deleted => "deleted",
        ArchiveReason.Overwritten => "overwritten",
        ArchiveReason.Conflict => "conflict",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unmapped ArchiveReason."),
    };

    /// <summary>
    /// Applies retention to <paramref name="archiveRoot"/>: first drops sessions older than
    /// <paramref name="keepAge"/>, then drops the oldest survivors until the total falls to
    /// <paramref name="maxBytes"/>. <c>keepAge == TimeSpan.Zero</c> disables the age rule
    /// (--archive-keep-days 0 = keep forever); <c>maxBytes &lt;= 0</c> disables the size cap.
    /// Whole session folders only — a half-emptied session is not a restore point. This is a
    /// best-effort guarantee, not an atomic one: TryDeleteSession's underlying
    /// Directory.Delete(recursive: true) can remove several files and then hit one that is
    /// locked, in which case it reports the session as a survivor rather than removed, but the
    /// folder on disk is already missing whatever it deleted before the failure.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="keepAge"/> is negative. Zero means "keep forever"; a negative age is a
    /// caller bug (an --archive-keep-days that was never validated) and must not be silently
    /// read as "keep forever", which is the one reading that hides the bug. This matches
    /// SyncDatabase.PurgeTombstonesOlderThan.
    /// </exception>
    public static PruneResult Prune(string archiveRoot, TimeSpan keepAge, long maxBytes)
    {
        if (keepAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(keepAge), keepAge,
                "keepAge must be >= TimeSpan.Zero (Zero = keep forever).");

        // Both rules off: skip the directory walk entirely rather than sizing the whole archive
        // on every sync just to decide that nothing is eligible.
        if (keepAge == TimeSpan.Zero && maxBytes <= 0) return new PruneResult(0, 0);

        var rootFull = Path.GetFullPath(archiveRoot);
        if (!Directory.Exists(rootFull)) return new PruneResult(0, 0);

        // Prune runs between StartSession and the try/finally that guarantees CompleteSession
        // (SyncClient.cs), so an exception here would leak an open session row. Retention is
        // best-effort by design (see TryDeleteSession below), and the TOCTOU against the
        // Directory.Exists check above — the folder can vanish or lose permissions between the
        // two calls — must degrade the same way a locked session folder does, not abort the sync.
        string[] dirs;
        try { dirs = Directory.GetDirectories(rootFull); }
        catch (IOException) { return new PruneResult(0, 0); }
        catch (UnauthorizedAccessException) { return new PruneResult(0, 0); }

        var sessions = new List<(DateTime Start, string Path, long Bytes)>();
        foreach (var dir in dirs)
        {
            // Only folders we created are eligible. A legacy yyyyMMdd backup tree, or anything a
            // user dropped into the archive root, fails the parse and is left strictly alone —
            // retention must never delete something this code did not write.
            if (!DateTime.TryParseExact(Path.GetFileName(dir), SessionFolderFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
                continue;

            sessions.Add((start, dir, DirectorySize(dir)));
        }

        // Oldest first: retention consumes history from the far end, never the recent end.
        sessions.Sort((a, b) => a.Start.CompareTo(b.Start));

        int removed = 0;
        long freed = 0;
        var survivors = new List<(DateTime Start, string Path, long Bytes)>();

        if (keepAge > TimeSpan.Zero)
        {
            // The cutoff is computed INSIDE this guard, and clamped. Prune runs at session
            // start, before any transfer: `DateTime.UtcNow - keepAge` throws
            // ArgumentOutOfRangeException for a keepAge older than the calendar itself, which
            // would abort the entire sync rather than merely skipping retention.
            var now = DateTime.UtcNow;
            var cutoff = keepAge < now - DateTime.MinValue ? now - keepAge : DateTime.MinValue;

            foreach (var s in sessions)
            {
                if (s.Start < cutoff && TryDeleteSession(s.Path))
                {
                    removed++;
                    freed += s.Bytes;
                    continue;
                }
                survivors.Add(s);
            }
        }
        else
        {
            survivors.AddRange(sessions);
        }

        if (maxBytes > 0)
        {
            long total = 0;
            foreach (var s in survivors) total += s.Bytes;

            foreach (var s in survivors)
            {
                if (total <= maxBytes) break;
                if (!TryDeleteSession(s.Path)) continue;
                removed++;
                freed += s.Bytes;
                total -= s.Bytes;
            }
        }

        return new PruneResult(removed, freed);
    }

    /// <summary>
    /// Retention is best-effort: a locked or unreadable session folder must not fail the sync
    /// that is about to run, since Prune executes before any transfer. That best-effort promise
    /// is not just about the try/catch here — Directory.Delete(recursive: true) is not atomic,
    /// so a failure partway through leaves the session folder missing whatever it removed before
    /// hitting the locked entry. Prune's "whole session folders only" guarantee is therefore
    /// best-effort against this kind of OS-level partial failure, not a hard invariant.
    /// </summary>
    private static bool TryDeleteSession(string sessionPath)
    {
        try
        {
            Directory.Delete(sessionPath, recursive: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static long DirectorySize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                // A file vanishing mid-walk must not abort retention for the whole archive.
                try { total += new FileInfo(file).Length; }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return total;
    }
}
