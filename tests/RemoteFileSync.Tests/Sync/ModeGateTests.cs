using RemoteFileSync.Models;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

/// <summary>
/// Push and Pull used to be flattened into "not bidirectional", which made Pull permit uploads
/// and forbid downloads — the exact inversion of what the mode means.
/// </summary>
public class ModeGateTests
{
    [Theory]
    [InlineData(SyncMode.Push,   true,  false)]
    [InlineData(SyncMode.Pull,   false, true)]
    [InlineData(SyncMode.TwoWay, true,  true)]
    public void EachMode_PermitsExactlyTheDirectionsItsNameClaims(
        SyncMode mode, bool clientToServer, bool serverToClient)
    {
        Assert.Equal(clientToServer, ModeGate.ClientToServer(mode));
        Assert.Equal(serverToClient, ModeGate.ServerToClient(mode));
    }

    [Fact]
    public void Pull_PermitsTheDownwardDirection_WhichTheBidirectionalPredicateDenied()
    {
        // The old gate was `_options.Bidirectional`, false in Pull mode. A Pull run therefore
        // planned DeleteOnClient, the server sent DeleteFile for each, and the client never
        // entered the loop that reads them.
        Assert.True(ModeGate.ServerToClient(SyncMode.Pull));
        Assert.False(new SyncOptions { Mode = SyncMode.Pull }.Bidirectional);
    }

    [Fact]
    public void UndefinedMode_FailsClosedInBothDirections()
    {
        // `mode != SyncMode.Pull` / `mode != SyncMode.Push` both returned true for (SyncMode)0,
        // which is not a defined member and reaches this predicate straight from an
        // unauthenticated peer's handshake byte (the server clamps it before use today, but this
        // is a shared safety predicate and must not rely on every caller doing that). A whitelist
        // fails closed instead: an unrecognised mode admits neither direction.
        var undefined = (SyncMode)0;
        Assert.False(ModeGate.ClientToServer(undefined));
        Assert.False(ModeGate.ServerToClient(undefined));
    }
}
