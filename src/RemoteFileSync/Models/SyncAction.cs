namespace RemoteFileSync.Models;

/// <summary>
/// What to do with one path. The numeric values are WIRE FORMAT: SerializeSyncPlan writes each
/// one as a single byte and the peer's deserializer casts that byte straight back to this enum.
/// New members are therefore APPENDED with the next free number — never renumbered, never
/// reordered, and no member is ever removed. Renumbering would not break the build, which is
/// exactly why it is dangerous: an old peer would keep sending 5 for DeleteOnServer while a
/// renumbered new peer read 5 as something else, and the mismatch only surfaces as files being
/// deleted or overwritten on the wrong side.
/// </summary>
public enum SyncActionType : byte
{
    SendToServer = 0,
    SendToClient = 1,
    ClientOnly = 2,
    ServerOnly = 3,
    Skip = 4,
    DeleteOnServer = 5,
    DeleteOnClient = 6,

    /// <summary>
    /// Both sides changed the file since the common ancestor and neither edit can be discarded,
    /// so the loser is kept under a renamed sibling instead of being overwritten. Emitted by the
    /// plan builder (Phase 6) and materialised as the rename (Phase 7).
    /// </summary>
    ConflictKeepBoth = 7,
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
