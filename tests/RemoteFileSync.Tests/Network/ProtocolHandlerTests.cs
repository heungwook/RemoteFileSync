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
        var bytes = ProtocolHandler.SerializeHandshake(version: 1, bidirectional: true);
        Assert.Equal(2, bytes.Length);
        Assert.Equal(1, bytes[0]);
        Assert.Equal(1, bytes[1]);
    }

    [Fact]
    public void DeserializeHandshake_ParsesCorrectly()
    {
        var bytes = new byte[] { 1, 0 };
        var (version, bidi) = ProtocolHandler.DeserializeHandshake(bytes);
        Assert.Equal(1, version);
        Assert.False(bidi);
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
