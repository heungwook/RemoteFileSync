using RemoteFileSync.Models;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class ConflictResolverTests
{
    private static readonly DateTime BaseTime = new(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SameTimestampAndSize_ReturnsSkip()
    {
        var client = new FileEntry("f.txt", 100, BaseTime);
        var server = new FileEntry("f.txt", 100, BaseTime);
        Assert.Equal(SyncActionType.Skip, ConflictResolver.Resolve(client, server));
    }

    [Fact]
    public void TimestampWithin2Seconds_SameSize_ReturnsSkip()
    {
        var client = new FileEntry("f.txt", 100, BaseTime);
        var server = new FileEntry("f.txt", 100, BaseTime.AddSeconds(1.5));
        Assert.Equal(SyncActionType.Skip, ConflictResolver.Resolve(client, server));
    }

    [Fact]
    public void ClientNewer_ReturnsSendToServer()
    {
        var client = new FileEntry("f.txt", 100, BaseTime.AddMinutes(5));
        var server = new FileEntry("f.txt", 100, BaseTime);
        Assert.Equal(SyncActionType.SendToServer, ConflictResolver.Resolve(client, server));
    }

    [Fact]
    public void ServerNewer_ReturnsSendToClient()
    {
        var client = new FileEntry("f.txt", 100, BaseTime);
        var server = new FileEntry("f.txt", 100, BaseTime.AddMinutes(5));
        Assert.Equal(SyncActionType.SendToClient, ConflictResolver.Resolve(client, server));
    }

    [Fact]
    public void SameTimestamp_LargerClient_ReturnsSendToServer()
    {
        var client = new FileEntry("f.txt", 200, BaseTime);
        var server = new FileEntry("f.txt", 100, BaseTime);
        Assert.Equal(SyncActionType.SendToServer, ConflictResolver.Resolve(client, server));
    }

    [Fact]
    public void SameTimestamp_LargerServer_ReturnsSendToClient()
    {
        var client = new FileEntry("f.txt", 100, BaseTime);
        var server = new FileEntry("f.txt", 200, BaseTime);
        Assert.Equal(SyncActionType.SendToClient, ConflictResolver.Resolve(client, server));
    }

    [Fact]
    public void Tolerance_JustOver2Seconds_NotSkipped()
    {
        var client = new FileEntry("f.txt", 100, BaseTime);
        var server = new FileEntry("f.txt", 100, BaseTime.AddSeconds(2.5));
        Assert.Equal(SyncActionType.SendToClient, ConflictResolver.Resolve(client, server));
    }
}
