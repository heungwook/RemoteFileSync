using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

/// <summary>
/// Difference between the peer's wall clock and ours, measured over the handshake round-trip.
/// Newest-wins resolution compares an mtime stamped by the server against one stamped by the
/// client; on machines whose clocks disagree that comparison picks the wrong winner and the
/// loser's edit is silently overwritten — and because the offset is constant, it picks wrong
/// the same way on every subsequent run, so the same bytes are re-copied forever. Every
/// cross-side timestamp comparison must go through <see cref="NormaliseServerTime"/> first.
/// </summary>
public readonly record struct ClockSkew(TimeSpan Offset)
{
    /// <summary>No correction. Use only where the peer's clock reading is genuinely unavailable.</summary>
    public static ClockSkew None { get; } = new(TimeSpan.Zero);

    /// <summary>
    /// NTP-style single-sample estimate: assume the server stamped its reply at the midpoint of
    /// the round-trip, so offset = serverTicks - (clientSentTicks + rtt/2). Halving the
    /// round-trip is what keeps ordinary network latency out of the offset — without it a slow
    /// link reads as a fast clock. Positive means the server clock is ahead of ours.
    /// </summary>
    public static ClockSkew Measure(long clientSentTicks, long serverTicks, long clientRecvTicks)
    {
        long rtt = clientRecvTicks - clientSentTicks;
        return new ClockSkew(TimeSpan.FromTicks(serverTicks - (clientSentTicks + rtt / 2)));
    }

    /// <summary>Converts a server-stamped UTC time into this machine's frame of reference.</summary>
    public DateTime NormaliseServerTime(DateTime serverUtc) => serverUtc - Offset;

    /// <summary>
    /// Beyond this the measurement is no longer plausible transit noise and mtime ordering
    /// between the two sides cannot be trusted, so the user must be told. Compared on the
    /// absolute value: a server an hour behind mis-orders timestamps exactly as badly as one an
    /// hour ahead.
    /// </summary>
    public bool IsSuspicious =>
        Math.Abs(Offset.TotalSeconds) > SyncOptions.SuspiciousSkewSeconds;
}
