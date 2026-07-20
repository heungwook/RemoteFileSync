using RemoteFileSync.Models;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

/// <summary>
/// The blast-radius bound for propagated deletions. Shared by both peers so they cannot
/// disagree about what is acceptable.
/// </summary>
public class DeleteBudgetTests
{
    [Fact]
    public void ZeroDestinationCount_RefusesRatherThanDisarming()
    {
        // The old client guard divided by the tracked-row count and skipped itself when that was
        // below the floor. A wiped database has zero rows, so the guard went inert precisely
        // when state loss had made every peer-only file look like a deletion.
        Assert.False(DeleteBudget.Within(deletes: 20, destinationCount: 0, maxDeletePercent: 25));
    }

    [Fact]
    public void NoDeletes_IsAlwaysWithinBudget()
    {
        Assert.True(DeleteBudget.Within(deletes: 0, destinationCount: 0, maxDeletePercent: 0));
        Assert.True(DeleteBudget.Within(deletes: 0, destinationCount: 5000, maxDeletePercent: 0));
    }

    [Fact]
    public void BelowTheFloor_ThePercentageIsNoiseAndTheGuardIsExempt()
    {
        int belowFloor = SyncOptions.MinTrackedFilesForDeleteGuard - 1;
        Assert.True(DeleteBudget.Within(belowFloor, belowFloor, maxDeletePercent: 25));
    }

    [Fact]
    public void AtTheFloor_AWholesaleDeletionIsRefused()
    {
        int atFloor = SyncOptions.MinTrackedFilesForDeleteGuard;
        Assert.False(DeleteBudget.Within(atFloor, atFloor, maxDeletePercent: 25));
    }

    [Theory]
    [InlineData(2, 20, 25, true)]    // 10% — ordinary
    [InlineData(5, 20, 25, true)]    // exactly at the limit — allowed
    [InlineData(6, 20, 25, false)]   // 30% — over
    [InlineData(20, 20, 100, true)]  // 100 disables the guard
    public void PercentageIsBoundedByTheDestinationPopulation(
        int deletes, int destinationCount, int maxDeletePercent, bool expected)
    {
        Assert.Equal(expected, DeleteBudget.Within(deletes, destinationCount, maxDeletePercent));
    }

    [Fact]
    public void ANegativeDestinationCount_IsTreatedAsUncountable_NotAsRoomToDelete()
    {
        // Not reachable from a manifest today, but the zero-denominator rule exists because a
        // count that cannot be trusted must never read as "the deletion is small". A negative
        // count is the same class of nonsense and must fail the same way.
        Assert.False(DeleteBudget.Within(deletes: 1, destinationCount: -5, maxDeletePercent: 25));
    }
}
