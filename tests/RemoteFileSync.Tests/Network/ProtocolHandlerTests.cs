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

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task ReadMessage_RejectsOutOfRangeLength_WithoutAllocating(int declaredLength)
    {
        // A 5-byte frame from an unauthenticated peer must not drive a huge allocation
        // (or throw OverflowException on a negative length).
        var header = new byte[5];
        header[0] = (byte)MessageType.Handshake;
        BitConverter.TryWriteBytes(header.AsSpan(1), declaredLength);

        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await ProtocolHandler.ReadMessageAsync(stream));
    }

    [Fact]
    public void SerializePath_ThrowsOnOversizedPath()
    {
        var huge = new string('a', short.MaxValue + 1);
        var manifest = new FileManifest();
        manifest.Add(new FileEntry(huge, 1, DateTime.UtcNow));
        // Previously an unchecked (short) cast wrapped negative and corrupted the frame.
        Assert.Throws<InvalidDataException>(() => ProtocolHandler.SerializeManifest(manifest));
    }

    [Fact]
    public void FileStart_RoundTripsIncludingTimestamp()
    {
        var ticks = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;
        var payload = ProtocolHandler.SerializeFileStart(1, "a/b.txt", 1234, true, 65536, ticks);
        var (id, path, size, compressed, block, mtime) = ProtocolHandler.DeserializeFileStart(payload);

        Assert.Equal((short)1, id);
        Assert.Equal("a/b.txt", path);
        Assert.Equal(1234, size);
        Assert.True(compressed);
        Assert.Equal(65536, block);
        Assert.Equal(ticks, mtime);   // the assertion that would have caught the convergence bug
    }

    [Fact]
    public void DeserializeHandshake_RejectsTruncatedPayload()
    {
        // Reading past the end of a short frame would fabricate a clock reading out of
        // whatever bytes followed and hand ClockSkew garbage, so the length guard must fire
        // before any indexing. A v2 peer's 2-byte handshake and 2-byte ack land here too.
        Assert.Throws<InvalidDataException>(() => ProtocolHandler.DeserializeHandshake(new byte[] { 2 }));
        Assert.Throws<InvalidDataException>(() => ProtocolHandler.DeserializeHandshake(new byte[] { 2, 1 }));
        Assert.Throws<InvalidDataException>(() => ProtocolHandler.DeserializeHandshake(new byte[10]));
        Assert.Throws<InvalidDataException>(() => ProtocolHandler.DeserializeHandshakeAck(Array.Empty<byte>()));
        Assert.Throws<InvalidDataException>(() => ProtocolHandler.DeserializeHandshakeAck(new byte[] { 2, 1 }));
        Assert.Throws<InvalidDataException>(() => ProtocolHandler.DeserializeHandshakeAck(new byte[9]));
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
        long sent = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc).Ticks;
        var bytes = ProtocolHandler.SerializeHandshake(version: 3, syncMode: 1, clientSentTicks: sent);
        Assert.Equal(11, bytes.Length);
        Assert.Equal(3, bytes[0]);
        Assert.Equal(1, bytes[1]);
        Assert.Equal(sent, BitConverter.ToInt64(bytes, 2));
        // The reserved byte is sent, and sent as zero, so both v3 peers agree on the frame
        // length; a future flag can occupy it without another version bump.
        Assert.Equal(0, bytes[10]);
    }

    [Fact]
    public void DeserializeHandshake_ParsesCorrectly()
    {
        long sent = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc).Ticks;
        var bytes = new byte[11];
        bytes[0] = 3;
        bytes[1] = 0;
        BitConverter.TryWriteBytes(bytes.AsSpan(2), sent);

        var (version, syncMode, clientSentTicks) = ProtocolHandler.DeserializeHandshake(bytes);

        Assert.Equal(3, version);
        Assert.Equal(0, syncMode);
        Assert.Equal(sent, clientSentTicks);
    }

    [Theory]
    [InlineData((byte)SyncMode.Push, false, false)]
    [InlineData((byte)SyncMode.Pull, true, false)]
    [InlineData((byte)SyncMode.TwoWay, true, true)]
    [InlineData((byte)SyncMode.TwoWay, false, true)]
    public void Handshake_SyncMode_RoundTrips(byte mode, bool deleteEnabled, bool mirrorDeletes)
    {
        byte packed = (byte)(mode | (deleteEnabled ? 4 : 0) | (mirrorDeletes ? 8 : 0));
        long sent = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc).Ticks;

        var data = ProtocolHandler.SerializeHandshake(3, packed, sent);
        var (version, syncMode, clientSentTicks) = ProtocolHandler.DeserializeHandshake(data);

        Assert.Equal(3, version);
        Assert.Equal(sent, clientSentTicks);
        Assert.Equal((SyncMode)mode, (SyncMode)(syncMode & 0b11));
        Assert.Equal(deleteEnabled, (syncMode & 4) != 0);
        Assert.Equal(mirrorDeletes, (syncMode & 8) != 0);
    }

    [Fact]
    public void HandshakeAck_RoundTripsServerTicks()
    {
        long serverTicks = new DateTime(2026, 7, 20, 10, 0, 5, DateTimeKind.Utc).Ticks;
        var data = ProtocolHandler.SerializeHandshakeAck(3, accepted: true, serverTicks);
        Assert.Equal(10, data.Length);

        var (version, accepted, ticks) = ProtocolHandler.DeserializeHandshakeAck(data);
        Assert.Equal(3, version);
        Assert.True(accepted);
        Assert.Equal(serverTicks, ticks);

        // Byte 1 keeps v2's polarity (0 == accepted) so a rejection is still legible to a
        // peer that only reads the first two bytes.
        var rejected = ProtocolHandler.SerializeHandshakeAck(3, accepted: false, serverTicks);
        Assert.Equal(1, rejected[1]);
        Assert.False(ProtocolHandler.DeserializeHandshakeAck(rejected).accepted);
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
