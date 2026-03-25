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
}
