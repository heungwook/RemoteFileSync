using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

/// <summary>
/// Everything the planner learned in one pass. It is not a bare list of entries because two of
/// the decision-table outcomes carry information the plan itself cannot express: a path kept
/// because this side edited it after the peer deleted it, and a path both sides changed.
/// Both must reach the end-of-sync review report, and the planner is pure — it has no sessionId
/// and must never write to the database — so it hands them back here and the caller persists
/// them AFTER the transfer phase succeeds. An aborted run therefore records nothing.
/// </summary>
public sealed class PlanResult
{
    /// <summary>The plan proper, in the order the executor should walk it.</summary>
    public List<SyncPlanEntry> Entries { get; init; } = new();

    /// <summary>
    /// Paths kept because this side modified them after the peer deleted them. Empty on the vast
    /// majority of runs; never null, because the caller drains it unconditionally.
    /// </summary>
    public List<ResurrectionInfo> Resurrections { get; init; } = new();

    /// <summary>
    /// Paths where both sides changed since the ancestor. Same non-null contract as
    /// <see cref="Resurrections"/>.
    /// </summary>
    public List<ConflictInfo> Conflicts { get; init; } = new();

    /// <summary>
    /// First-run overwrites: on the no-ancestor path a file both sides held with differing
    /// content resolves newest-wins and the loser is overwritten in place. The loser is archived
    /// so it is recoverable, but the run must report it — otherwise the review shows nothing for a
    /// sync that just replaced a user's edit. Same non-null contract as the other two lists.
    /// </summary>
    public List<OverwriteInfo> Overwrites { get; init; } = new();
}

/// <summary>
/// A deletion that lost to an edit. Losing the edit would be unrecoverable; an unwanted
/// resurrection costs the user one more delete, so the surviving copy wins and the fact is
/// reported rather than applied silently.
/// </summary>
/// <param name="KeptClientCopy">
/// True when the client's copy survived (the server had deleted it), false when the server's
/// copy survived. The report renders the side, so it cannot be inferred later.
/// </param>
public sealed record ResurrectionInfo(string Path, bool KeptClientCopy, long KeptSize, long KeptMtimeTicks);

/// <summary>
/// Both sides changed since the ancestor. Carries each side's own size and mtime so the report
/// can show the user what the two copies were without re-scanning either tree.
/// </summary>
public sealed record ConflictInfo(string Path, long ClientSize, long ClientMtimeTicks, long ServerSize, long ServerMtimeTicks);

/// <summary>
/// A first-run overwrite: both sides held the file with differing content, no ancestor said which
/// changed, so newest-wins kept one copy and overwrote the other. Carries both copies' size and
/// mtime plus which side survived, so the report can show what was replaced and what was kept
/// without re-scanning either tree.
/// </summary>
/// <param name="KeptClientCopy">
/// True when the client's copy won and the server's was overwritten; false for the mirror. The
/// report renders the surviving and replaced sides from this, so it cannot be inferred later.
/// </param>
public sealed record OverwriteInfo(
    string Path, bool KeptClientCopy,
    long KeptSize, long KeptMtimeTicks,
    long ReplacedSize, long ReplacedMtimeTicks);
