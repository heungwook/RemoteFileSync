namespace RemoteFileSync.Sync;

/// <summary>
/// What the two sides looked like the last time they were known to agree. Storing BOTH sides
/// separately is the whole point: a single snapshot cannot tell an edited client copy from an
/// edited server copy, and that missing distinction is how a one-sided deletion used to be
/// mistaken for consensus and propagated over a live edit.
/// </summary>
/// <param name="Path">Relative path, forward-slash separated, matched case-insensitively.</param>
/// <param name="Status">"exists" while both sides hold the file; "deleted" once tombstoned.</param>
/// <param name="DeletedUtcTicks">
/// Null while the row is live. Set when the row is tombstoned, so the tombstone purge has a real
/// deletion instant to age against instead of reusing LastSyncedTicks as a sentinel.
/// </param>
public sealed record AncestorRow(
    string Path,
    long   ClientSize,
    long   ClientMtimeTicks,
    long   ServerSize,
    long   ServerMtimeTicks,
    string Status,
    long   LastSyncedTicks,
    long?  DeletedUtcTicks);
