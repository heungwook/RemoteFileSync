using RemoteFileSync.Models;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class ClockSkewTests
{
    private static readonly DateTime Base = new(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Measure_ServerAhead_RecoversOffsetWithoutTransitTime()
    {
        // Server clock runs 5 minutes fast; the handshake round-trip takes 200ms and the server
        // stamps its reply at the midpoint. The estimate must recover exactly the 5 minutes and
        // none of the transit time — folding transit into the offset would make every sync over
        // a slow link look like a clock problem.
        var expected = TimeSpan.FromMinutes(5);
        long clientSent = Base.Ticks;
        long clientRecv = clientSent + TimeSpan.FromMilliseconds(200).Ticks;
        long serverTicks = clientSent + TimeSpan.FromMilliseconds(100).Ticks + expected.Ticks;

        var skew = ClockSkew.Measure(clientSent, serverTicks, clientRecv);

        Assert.Equal(expected, skew.Offset);
    }

    [Fact]
    public void Measure_ServerBehind_ProducesNegativeOffset()
    {
        var behind = TimeSpan.FromSeconds(90);
        long clientSent = Base.Ticks;
        long clientRecv = clientSent + TimeSpan.FromMilliseconds(40).Ticks;
        long serverTicks = clientSent + TimeSpan.FromMilliseconds(20).Ticks - behind.Ticks;

        var skew = ClockSkew.Measure(clientSent, serverTicks, clientRecv);

        Assert.Equal(-behind, skew.Offset);
    }

    [Fact]
    public void Measure_ClocksAgree_ProducesZeroOffset()
    {
        long clientSent = Base.Ticks;
        long clientRecv = clientSent + TimeSpan.FromMilliseconds(80).Ticks;
        long serverTicks = clientSent + TimeSpan.FromMilliseconds(40).Ticks;

        Assert.Equal(TimeSpan.Zero, ClockSkew.Measure(clientSent, serverTicks, clientRecv).Offset);
    }

    [Fact]
    public void NormaliseServerTime_SubtractsOffsetAndKeepsUtcKind()
    {
        var skew = new ClockSkew(TimeSpan.FromMinutes(5));
        var serverUtc = new DateTime(2026, 7, 20, 10, 5, 0, DateTimeKind.Utc);

        var normalised = skew.NormaliseServerTime(serverUtc);

        Assert.Equal(new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc), normalised);
        // Kind must survive: a normalised time that silently became Unspecified compares wrong
        // against the client's Utc mtimes everywhere downstream.
        Assert.Equal(DateTimeKind.Utc, normalised.Kind);
    }

    [Fact]
    public void NormaliseServerTime_NegativeOffsetMovesForward()
    {
        var skew = new ClockSkew(TimeSpan.FromMinutes(-5));
        var serverUtc = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 7, 20, 10, 5, 0, DateTimeKind.Utc),
                     skew.NormaliseServerTime(serverUtc));
    }

    [Fact]
    public void None_IsZeroNotSuspiciousAndIdentity()
    {
        Assert.Equal(TimeSpan.Zero, ClockSkew.None.Offset);
        Assert.False(ClockSkew.None.IsSuspicious);
        Assert.Equal(Base, ClockSkew.None.NormaliseServerTime(Base));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(SyncOptions.SuspiciousSkewSeconds - 1, false)]
    [InlineData(SyncOptions.SuspiciousSkewSeconds, false)]
    [InlineData(SyncOptions.SuspiciousSkewSeconds + 1, true)]
    [InlineData(-(SyncOptions.SuspiciousSkewSeconds + 1), true)]
    public void IsSuspicious_TripsBothDirectionsStrictlyAboveThreshold(int offsetSeconds, bool expected)
    {
        // Strictly above, and symmetric: a server an hour behind mis-orders timestamps exactly
        // as badly as one an hour ahead, so an unsigned or one-sided check would miss half the
        // real cases.
        var skew = new ClockSkew(TimeSpan.FromSeconds(offsetSeconds));
        Assert.Equal(expected, skew.IsSuspicious);
    }
}
