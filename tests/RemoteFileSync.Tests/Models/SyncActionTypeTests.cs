using RemoteFileSync.Models;

namespace RemoteFileSync.Tests.Models;

public class SyncActionTypeTests
{
    [Fact]
    public void ConflictKeepBoth_IsSeven()
    {
        Assert.Equal(7, (byte)SyncActionType.ConflictKeepBoth);
    }

    [Fact]
    public void ExistingActionTypes_KeepTheirWireNumbers()
    {
        // These bytes are written by SerializeSyncPlan and read by the peer's deserializer,
        // so they are wire format. Renumbering any of them silently repoints a peer's action:
        // a plan that said "SendToServer" would arrive as some other action entirely.
        Assert.Equal(0, (byte)SyncActionType.SendToServer);
        Assert.Equal(1, (byte)SyncActionType.SendToClient);
        Assert.Equal(2, (byte)SyncActionType.ClientOnly);
        Assert.Equal(3, (byte)SyncActionType.ServerOnly);
        Assert.Equal(4, (byte)SyncActionType.Skip);
        Assert.Equal(5, (byte)SyncActionType.DeleteOnServer);
        Assert.Equal(6, (byte)SyncActionType.DeleteOnClient);
    }
}
