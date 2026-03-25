namespace RemoteFileSync.Models;

public enum SyncActionType : byte
{
    SendToServer = 0,
    SendToClient = 1,
    ClientOnly = 2,
    ServerOnly = 3,
    Skip = 4
}

public sealed class SyncPlanEntry
{
    public SyncActionType Action { get; }
    public string RelativePath { get; }

    public SyncPlanEntry(SyncActionType action, string relativePath)
    {
        Action = action;
        RelativePath = relativePath;
    }

    public override string ToString() => $"{Action}: {RelativePath}";
}
