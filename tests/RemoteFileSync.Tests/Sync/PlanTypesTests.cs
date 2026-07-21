using RemoteFileSync.Models;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class PlanTypesTests
{
    [Fact]
    public void AncestorRow_PositionalOrderIsClientThenServer()
    {
        // Every value is distinct so a transposed parameter in the record declaration is caught.
        // The order matters beyond style: the Push table deletes on the server when the CLIENT
        // columns say the client had the file unchanged, and the Pull table is the mirror. Swap
        // the two pairs and every one-sided deletion resolves against the wrong side.
        var row = new AncestorRow(
            Path: "docs/report.docx",
            ClientSize: 11,
            ClientMtimeTicks: 22,
            ServerSize: 33,
            ServerMtimeTicks: 44,
            Status: "exists",
            LastSyncedTicks: 55,
            DeletedUtcTicks: 66);

        Assert.Equal("docs/report.docx", row.Path);
        Assert.Equal(11, row.ClientSize);
        Assert.Equal(22, row.ClientMtimeTicks);
        Assert.Equal(33, row.ServerSize);
        Assert.Equal(44, row.ServerMtimeTicks);
        Assert.Equal("exists", row.Status);
        Assert.Equal(55, row.LastSyncedTicks);
        Assert.Equal(66, row.DeletedUtcTicks);
    }

    [Fact]
    public void AncestorRow_LiveRowHasNoDeletionTimestamp()
    {
        // DeletedUtcTicks is nullable precisely so "exists" rows carry no deletion instant.
        // Making it non-nullable would force a sentinel that the tombstone purge would then
        // read as a real deletion date.
        var row = new AncestorRow("a.txt", 1, 2, 1, 2, "exists", 3, null);
        Assert.Null(row.DeletedUtcTicks);
    }

    [Fact]
    public void PlanResult_DefaultsToEmptyNonNullLists()
    {
        // The caller drains all three lists unconditionally after the transfer phase. If any of
        // them defaulted to null, a plan that produced no conflicts — the overwhelmingly common
        // case — would NullReferenceException on the drain instead of doing nothing.
        var result = new PlanResult();

        Assert.NotNull(result.Entries);
        Assert.NotNull(result.Resurrections);
        Assert.NotNull(result.Conflicts);
        Assert.Empty(result.Entries);
        Assert.Empty(result.Resurrections);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void PlanResult_ObjectInitialiserPopulatesAllThreeLists()
    {
        var result = new PlanResult
        {
            Entries = new List<SyncPlanEntry> { new(SyncActionType.SendToServer, "a.txt") },
            Resurrections = new List<ResurrectionInfo>
            {
                new("b.txt", KeptClientCopy: true, KeptSize: 10, KeptMtimeTicks: 20),
            },
            Conflicts = new List<ConflictInfo>
            {
                new("c.txt", ClientSize: 1, ClientMtimeTicks: 2, ServerSize: 3, ServerMtimeTicks: 4),
            },
        };

        Assert.Equal("a.txt", Assert.Single(result.Entries).RelativePath);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[0].Action);

        var resurrection = Assert.Single(result.Resurrections);
        Assert.Equal("b.txt", resurrection.Path);
        Assert.True(resurrection.KeptClientCopy);
        Assert.Equal(10, resurrection.KeptSize);
        Assert.Equal(20, resurrection.KeptMtimeTicks);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("c.txt", conflict.Path);
        Assert.Equal(1, conflict.ClientSize);
        Assert.Equal(2, conflict.ClientMtimeTicks);
        Assert.Equal(3, conflict.ServerSize);
        Assert.Equal(4, conflict.ServerMtimeTicks);
    }

    [Fact]
    public void PlanResult_OverwritesDefaultsToEmptyNonNullList()
    {
        // Overwrites is the third side channel the caller drains after the transfer phase. Like
        // Conflicts and Resurrections it must never be null, or a first run that overwrote a
        // loser — the case this list exists to report — would NullReference on the drain.
        var result = new PlanResult();
        Assert.NotNull(result.Overwrites);
        Assert.Empty(result.Overwrites);
    }

    [Fact]
    public void OverwriteInfo_UsesValueEquality()
    {
        // Same reason as ConflictInfo: the report and E2E suites match on constructed values, so
        // reference equality would fail every assertion for the wrong reason. KeptClientCopy is
        // the side discriminator the report renders, so it must participate in equality.
        var a = new OverwriteInfo("f.txt", KeptClientCopy: true,
            KeptSize: 150, KeptMtimeTicks: 22, ReplacedSize: 100, ReplacedMtimeTicks: 11);
        var b = new OverwriteInfo("f.txt", KeptClientCopy: true,
            KeptSize: 150, KeptMtimeTicks: 22, ReplacedSize: 100, ReplacedMtimeTicks: 11);
        Assert.Equal(a, b);
        Assert.NotEqual(a, a with { KeptClientCopy = false });
        Assert.NotEqual(a, a with { ReplacedSize = 99 });
    }

    [Fact]
    public void ConflictInfo_UsesValueEquality()
    {
        // The end-of-sync report and the E2E suites locate entries with Assert.Contains against a
        // constructed expected value. Declaring these as classes rather than records would make
        // every such assertion a reference comparison and fail for the wrong reason.
        var a = new ConflictInfo("c.txt", 1, 2, 3, 4);
        var b = new ConflictInfo("c.txt", 1, 2, 3, 4);
        Assert.Equal(a, b);
        Assert.NotEqual(a, new ConflictInfo("c.txt", 1, 2, 3, 5));
    }

    [Fact]
    public void ResurrectionInfo_UsesValueEquality()
    {
        var a = new ResurrectionInfo("b.txt", true, 10, 20);
        var b = new ResurrectionInfo("b.txt", true, 10, 20);
        Assert.Equal(a, b);
        // KeptClientCopy is the side discriminator the report renders; it must participate.
        Assert.NotEqual(a, new ResurrectionInfo("b.txt", false, 10, 20));
    }
}
