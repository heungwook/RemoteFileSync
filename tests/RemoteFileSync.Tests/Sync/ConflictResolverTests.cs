using RemoteFileSync.Models;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class ConflictResolverTests
{
    private static readonly DateTime BaseTime = new(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LastSync = new(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BeforeSync = new(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AfterSync = new(2026, 3, 27, 8, 0, 0, DateTimeKind.Utc);

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

    [Fact]
    public void DeletedOnClient_UntouchedOnServer_ReturnsDeleteOnServer()
    {
        var serverEntry = new FileEntry("file.txt", 100, BeforeSync);
        var result = ConflictResolver.ResolveDeleteConflict(
            deletedOnClient: true, survivingEntry: serverEntry, lastSyncUtc: LastSync);
        Assert.Equal(SyncActionType.DeleteOnServer, result);
    }

    [Fact]
    public void DeletedOnClient_ModifiedOnServer_ReturnsSendToClient()
    {
        var serverEntry = new FileEntry("file.txt", 200, AfterSync);
        var result = ConflictResolver.ResolveDeleteConflict(
            deletedOnClient: true, survivingEntry: serverEntry, lastSyncUtc: LastSync);
        Assert.Equal(SyncActionType.SendToClient, result);
    }

    [Fact]
    public void DeletedOnServer_UntouchedOnClient_ReturnsDeleteOnClient()
    {
        var clientEntry = new FileEntry("file.txt", 100, BeforeSync);
        var result = ConflictResolver.ResolveDeleteConflict(
            deletedOnClient: false, survivingEntry: clientEntry, lastSyncUtc: LastSync);
        Assert.Equal(SyncActionType.DeleteOnClient, result);
    }

    [Fact]
    public void DeletedOnServer_ModifiedOnClient_ReturnsSendToServer()
    {
        var clientEntry = new FileEntry("file.txt", 200, AfterSync);
        var result = ConflictResolver.ResolveDeleteConflict(
            deletedOnClient: false, survivingEntry: clientEntry, lastSyncUtc: LastSync);
        Assert.Equal(SyncActionType.SendToServer, result);
    }

    [Fact]
    public void DeleteConflict_TimestampWithinTolerance_TreatedAsUntouched()
    {
        var withinTolerance = LastSync.AddSeconds(1);
        var serverEntry = new FileEntry("file.txt", 100, withinTolerance);
        var result = ConflictResolver.ResolveDeleteConflict(
            deletedOnClient: true, survivingEntry: serverEntry, lastSyncUtc: LastSync);
        Assert.Equal(SyncActionType.DeleteOnServer, result);
    }

    [Fact]
    public void DeleteConflict_TimestampExactlyAtTolerance_TreatedAsUntouched()
    {
        var atTolerance = LastSync.AddSeconds(2);
        var serverEntry = new FileEntry("file.txt", 100, atTolerance);
        var result = ConflictResolver.ResolveDeleteConflict(
            deletedOnClient: true, survivingEntry: serverEntry, lastSyncUtc: LastSync);
        Assert.Equal(SyncActionType.DeleteOnServer, result);
    }

    [Fact]
    public void DeleteConflict_TimestampJustBeyondTolerance_TreatedAsModified()
    {
        var beyondTolerance = LastSync.AddSeconds(3);
        var serverEntry = new FileEntry("file.txt", 100, beyondTolerance);
        var result = ConflictResolver.ResolveDeleteConflict(
            deletedOnClient: true, survivingEntry: serverEntry, lastSyncUtc: LastSync);
        Assert.Equal(SyncActionType.SendToClient, result);
    }
}
