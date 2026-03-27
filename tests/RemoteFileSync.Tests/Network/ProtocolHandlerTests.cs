using RemoteFileSync.Models;
using RemoteFileSync.Network;

namespace RemoteFileSync.Tests.Network;

public class ProtocolHandlerTests
{
    [Fact]
    public async Task WriteAndReadMessage_RoundTrips()
    {
        using var stream = new MemoryStream();
        var payload = new byte[] { 0x01, 0x02, 0x03 };
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.Handshake, payload);
        stream.Position = 0;
        var (type, data) = await ProtocolHandler.ReadMessageAsync(stream);
        Assert.Equal(MessageType.Handshake, type);
        Assert.Equal(payload, data);
    }

    [Fact]
    public void SerializeManifest_RoundTrips()
    {
        var manifest = new FileManifest();
        manifest.Add(new FileEntry("docs/a.txt", 1024, new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc)));
        manifest.Add(new FileEntry("b.csv", 2048, new DateTime(2026, 3, 25, 8, 30, 0, DateTimeKind.Utc)));
        var bytes = ProtocolHandler.SerializeManifest(manifest);
        var restored = ProtocolHandler.DeserializeManifest(bytes);
        Assert.Equal(2, restored.Count);
        var a = restored.Get("docs/a.txt");
        Assert.NotNull(a);
        Assert.Equal(1024, a.FileSize);
        var b = restored.Get("b.csv");
        Assert.NotNull(b);
        Assert.Equal(2048, b.FileSize);
    }

    [Fact]
    public void SerializeSyncPlan_RoundTrips()
    {
        var plan = new List<SyncPlanEntry>
        {
            new(SyncActionType.SendToServer, "a.txt"),
            new(SyncActionType.SendToClient, "b.txt"),
            new(SyncActionType.ClientOnly, "c.txt"),
            new(SyncActionType.Skip, "d.txt"),
        };
        var bytes = ProtocolHandler.SerializeSyncPlan(plan);
        var restored = ProtocolHandler.DeserializeSyncPlan(bytes);
        Assert.Equal(4, restored.Count);
        Assert.Equal(SyncActionType.SendToServer, restored[0].Action);
        Assert.Equal("a.txt", restored[0].RelativePath);
    }

    [Fact]
    public void SerializeHandshake_CorrectBytes()
    {
        var bytes = ProtocolHandler.SerializeHandshake(version: 1, syncMode: 1);
        Assert.Equal(2, bytes.Length);
        Assert.Equal(1, bytes[0]);
        Assert.Equal(1, bytes[1]);
    }

    [Fact]
    public void DeserializeHandshake_ParsesCorrectly()
    {
        var bytes = new byte[] { 1, 0 };
        var (version, syncMode) = ProtocolHandler.DeserializeHandshake(bytes);
        Assert.Equal(1, version);
        Assert.Equal(0, syncMode);
    }

    [Fact]
    public void Handshake_SyncMode_RoundTrips()
    {
        var data = ProtocolHandler.SerializeHandshake(1, 3);
        var (version, syncMode) = ProtocolHandler.DeserializeHandshake(data);
        Assert.Equal(1, version);
        Assert.Equal(3, syncMode);
    }

    [Fact]
    public void DeleteFile_SerializeDeserialize_RoundTrips()
    {
        var data = ProtocolHandler.SerializeDeleteFile("docs/old-report.docx", backupFirst: true);
        var (path, backupFirst) = ProtocolHandler.DeserializeDeleteFile(data);
        Assert.Equal("docs/old-report.docx", path);
        Assert.True(backupFirst);
    }

    [Fact]
    public void DeleteFile_NoBackup_RoundTrips()
    {
        var data = ProtocolHandler.SerializeDeleteFile("temp/cache.bin", backupFirst: false);
        var (path, backupFirst) = ProtocolHandler.DeserializeDeleteFile(data);
        Assert.Equal("temp/cache.bin", path);
        Assert.False(backupFirst);
    }

    [Fact]
    public void DeleteConfirm_Success_RoundTrips()
    {
        var data = ProtocolHandler.SerializeDeleteConfirm("docs/old-report.docx", success: true);
        var (path, success) = ProtocolHandler.DeserializeDeleteConfirm(data);
        Assert.Equal("docs/old-report.docx", path);
        Assert.True(success);
    }

    [Fact]
    public void DeleteConfirm_Failure_RoundTrips()
    {
        var data = ProtocolHandler.SerializeDeleteConfirm("locked/file.txt", success: false);
        var (path, success) = ProtocolHandler.DeserializeDeleteConfirm(data);
        Assert.Equal("locked/file.txt", path);
        Assert.False(success);
    }

    [Fact]
    public void SyncPlan_WithDeleteActions_RoundTrips()
    {
        var plan = new List<SyncPlanEntry>
        {
            new(SyncActionType.SendToServer, "update.txt"),
            new(SyncActionType.DeleteOnServer, "old.txt"),
            new(SyncActionType.DeleteOnClient, "removed.txt"),
            new(SyncActionType.Skip, "same.txt")
        };
        var data = ProtocolHandler.SerializeSyncPlan(plan);
        var result = ProtocolHandler.DeserializeSyncPlan(data);
        Assert.Equal(4, result.Count);
        Assert.Equal(SyncActionType.DeleteOnServer, result[1].Action);
        Assert.Equal("old.txt", result[1].RelativePath);
        Assert.Equal(SyncActionType.DeleteOnClient, result[2].Action);
    }

    [Fact]
    public void SyncComplete_WithFilesDeleted_RoundTrips()
    {
        var data = ProtocolHandler.SerializeSyncComplete(10, 1024000, 3, 5000);
        var (transferred, bytes, deleted, elapsed) = ProtocolHandler.DeserializeSyncComplete(data);
        Assert.Equal(10, transferred);
        Assert.Equal(1024000, bytes);
        Assert.Equal(3, deleted);
        Assert.Equal(5000, elapsed);
    }

    [Fact]
    public void EmptyManifest_RoundTrips()
    {
        var manifest = new FileManifest();
        var bytes = ProtocolHandler.SerializeManifest(manifest);
        var restored = ProtocolHandler.DeserializeManifest(bytes);
        Assert.Equal(0, restored.Count);
    }

    [Fact]
    public async Task WriteMessage_LargePayload_Works()
    {
        using var stream = new MemoryStream();
        var payload = new byte[100_000];
        Random.Shared.NextBytes(payload);
        await ProtocolHandler.WriteMessageAsync(stream, MessageType.FileChunk, payload);
        stream.Position = 0;
        var (type, data) = await ProtocolHandler.ReadMessageAsync(stream);
        Assert.Equal(MessageType.FileChunk, type);
        Assert.Equal(payload, data);
    }
}
