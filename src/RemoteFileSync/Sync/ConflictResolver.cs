using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

public static class ConflictResolver
{
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Newest wins, ties broken by size. Only valid on the no-ancestor path: with no record of
    /// what the two sides last agreed on, the timestamp is the only signal available. Callers
    /// must normalise the server entry for clock skew before calling.
    /// </summary>
    public static SyncActionType Resolve(FileEntry clientEntry, FileEntry serverEntry)
    {
        var timeDiff = clientEntry.LastModifiedUtc - serverEntry.LastModifiedUtc;

        if (Math.Abs(timeDiff.TotalSeconds) <= TimestampTolerance.TotalSeconds
            && clientEntry.FileSize == serverEntry.FileSize)
            return SyncActionType.Skip;

        if (Math.Abs(timeDiff.TotalSeconds) > TimestampTolerance.TotalSeconds)
            return timeDiff.TotalSeconds > 0 ? SyncActionType.SendToServer : SyncActionType.SendToClient;

        if (clientEntry.FileSize > serverEntry.FileSize) return SyncActionType.SendToServer;
        if (serverEntry.FileSize > clientEntry.FileSize) return SyncActionType.SendToClient;

        return SyncActionType.Skip;
    }

    // ResolveDeleteConflict was removed: it decided whether a surviving file had been edited by
    // comparing its mtime against the session-wide LastSynced, which deleted any file whose stamp
    // merely looked older. SyncEngine now answers that from the per-side AncestorRow.
}
