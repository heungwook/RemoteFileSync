using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

/// <summary>
/// Blast-radius bound for propagated deletions, expressed once so the two peers cannot apply
/// different rules to the same plan.
/// </summary>
public static class DeleteBudget
{
    /// <summary>
    /// True when <paramref name="deletes"/> is an acceptable share of
    /// <paramref name="destinationCount"/> — the live file count on the side being deleted FROM,
    /// which is the population actually at risk.
    /// </summary>
    public static bool Within(int deletes, int destinationCount, int maxDeletePercent)
    {
        if (deletes <= 0) return true;

        // A destination we cannot count is not a destination we may empty. Deleting N files from
        // a side that reports zero files is arithmetically impossible, so a zero here means the
        // count is missing or the peer is lying — never that the deletion is small.
        if (destinationCount <= 0) return false;

        // Below the floor the percentage is noise: 1 of 2 files is 50% but entirely ordinary,
        // and a guard that fires on ordinary edits trains users into --force-delete by reflex,
        // disabling it for the run that actually needed it.
        if (destinationCount < SyncOptions.MinTrackedFilesForDeleteGuard) return true;

        return deletes * 100.0 / destinationCount <= maxDeletePercent;
    }
}
