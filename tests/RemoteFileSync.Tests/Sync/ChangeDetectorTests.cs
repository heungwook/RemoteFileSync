using RemoteFileSync.Models;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class ChangeDetectorTests
{
    private static readonly DateTime RowTime = new(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SameSizeSameMtime_Unchanged()
    {
        var current = new FileEntry("f.txt", 100, RowTime);
        Assert.True(ChangeDetector.Unchanged(current, 100, RowTime.Ticks));
    }

    [Fact]
    public void SizeChanged_MtimeIdentical_ReportsChanged()
    {
        // The size half of the check, isolated. An in-place rewrite that lands in the same mtime
        // slot is invisible to a timestamp-only comparison; the engine would then read the file
        // as untouched and let the peer's deletion propagate over a live edit.
        var current = new FileEntry("f.txt", 250, RowTime);
        Assert.False(ChangeDetector.Unchanged(current, 100, RowTime.Ticks));
    }

    [Fact]
    public void SizeChanged_MtimeInsideTolerance_ReportsChanged()
    {
        // Same failure one step subtler: the mtime moved, but by less than the tolerance, so the
        // timestamp half votes "unchanged". Size must still veto.
        var current = new FileEntry("f.txt", 250, RowTime.AddSeconds(1));
        Assert.False(ChangeDetector.Unchanged(current, 100, RowTime.Ticks));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.5)]
    [InlineData(-1.5)]
    [InlineData(2.0)]
    [InlineData(-2.0)]
    public void MtimeDriftWithinTolerance_Unchanged(double seconds)
    {
        var current = new FileEntry("f.txt", 100, RowTime.AddSeconds(seconds));
        Assert.True(ChangeDetector.Unchanged(current, 100, RowTime.Ticks));
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(-3.0)]
    public void MtimeDriftBeyondTolerance_ReportsChanged(double seconds)
    {
        // The mtime half of the check, isolated: size is identical in both rows, so only the
        // timestamp comparison can produce False here.
        var current = new FileEntry("f.txt", 100, RowTime.AddSeconds(seconds));
        Assert.False(ChangeDetector.Unchanged(current, 100, RowTime.Ticks));
    }

    [Fact]
    public void ToleranceIsTwoSeconds()
    {
        // Pinned because the decision tables are specified against this exact window and the
        // integration fixtures stamp files relative to it.
        Assert.Equal(TimeSpan.FromSeconds(2), ChangeDetector.Tolerance);
    }
}
