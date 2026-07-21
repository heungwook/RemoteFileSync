using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

/// <summary>
/// The single primitive that answers "has this side changed since the ancestor row was written?".
/// Every changed/unchanged decision in the planner must go through here so the two halves of the
/// test — size and mtime — can never drift apart between call sites.
/// </summary>
public static class ChangeDetector
{
    /// <summary>
    /// Filesystems round mtimes (FAT to 2s, some SMB shares to 1s), so a byte-identical file can
    /// come back with a slightly different stamp after a round trip. Matches the window
    /// <see cref="ConflictResolver"/> has always used for the same reason
    /// (src/RemoteFileSync/Sync/ConflictResolver.cs:7).
    /// </summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// True when <paramref name="current"/> still matches what the ancestor row recorded for that
    /// side. Both halves are required. Size is compared exactly and first: sizes never drift, and
    /// an in-place rewrite that changes length while landing inside the mtime tolerance window
    /// would otherwise read as untouched — which is exactly the state the decision tables treat
    /// as "safe to delete on this side".
    /// </summary>
    /// <param name="rowSize">The size column for the side being tested (client or server).</param>
    /// <param name="rowMtimeTicks">The mtime column for that same side.</param>
    public static bool Unchanged(FileEntry current, long rowSize, long rowMtimeTicks)
    {
        if (current.FileSize != rowSize) return false;
        return Math.Abs(current.LastModifiedUtc.Ticks - rowMtimeTicks) <= Tolerance.Ticks;
    }
}
