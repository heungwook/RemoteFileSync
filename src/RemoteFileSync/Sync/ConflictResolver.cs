using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

public static class ConflictResolver
{
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

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

    /// <summary>
    /// Resolves the action when a file was deleted on one side and still exists on the other.
    /// Case 1: Surviving file untouched (modTime ≤ lastSyncUtc + tolerance) → propagate deletion.
    /// Case 2: Surviving file modified (modTime > lastSyncUtc + tolerance) → restore (copy to deleting side).
    /// </summary>
    public static SyncActionType ResolveDeleteConflict(bool deletedOnClient, FileEntry survivingEntry, DateTime lastSyncUtc)
    {
        bool untouched = survivingEntry.LastModifiedUtc <= lastSyncUtc + TimestampTolerance;

        if (deletedOnClient)
            return untouched ? SyncActionType.DeleteOnServer : SyncActionType.SendToClient;
        else
            return untouched ? SyncActionType.DeleteOnClient : SyncActionType.SendToServer;
    }
}
