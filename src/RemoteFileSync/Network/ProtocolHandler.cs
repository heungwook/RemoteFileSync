using System.Text;
using RemoteFileSync.Models;

namespace RemoteFileSync.Network;

public static class ProtocolHandler
{
    public static async Task WriteMessageAsync(Stream stream, MessageType type, byte[] payload, CancellationToken ct = default)
    {
        var header = new byte[5];
        header[0] = (byte)type;
        BitConverter.TryWriteBytes(header.AsSpan(1), payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<(MessageType type, byte[] payload)> ReadMessageAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[5];
        await ReadExactAsync(stream, header, ct);
        var type = (MessageType)header[0];
        var length = BitConverter.ToInt32(header, 1);
        var payload = new byte[length];
        if (length > 0) await ReadExactAsync(stream, payload, ct);
        return (type, payload);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0) throw new EndOfStreamException("Connection closed unexpectedly.");
            offset += read;
        }
    }

    public static byte[] SerializeHandshake(byte version, byte syncMode) =>
        new[] { version, syncMode };

    public static (byte version, byte syncMode) DeserializeHandshake(byte[] data) =>
        (data[0], data[1]);

    public static byte[] SerializeHandshakeAck(byte version, bool accepted) =>
        new[] { version, (byte)(accepted ? 0 : 1) };

    public static (byte version, bool accepted) DeserializeHandshakeAck(byte[] data) =>
        (data[0], data[1] == 0);

    public static byte[] SerializeManifest(FileManifest manifest)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(manifest.Count);
        foreach (var entry in manifest.Entries)
        {
            var pathBytes = Encoding.UTF8.GetBytes(entry.RelativePath);
            writer.Write((short)pathBytes.Length);
            writer.Write(pathBytes);
            writer.Write(entry.FileSize);
            writer.Write(entry.LastModifiedUtc.Ticks);
        }
        writer.Flush();
        return ms.ToArray();
    }

    public static FileManifest DeserializeManifest(byte[] data)
    {
        var manifest = new FileManifest();
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms, Encoding.UTF8);
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            short pathLen = reader.ReadInt16();
            var path = Encoding.UTF8.GetString(reader.ReadBytes(pathLen));
            long size = reader.ReadInt64();
            long ticks = reader.ReadInt64();
            manifest.Add(new FileEntry(path, size, new DateTime(ticks, DateTimeKind.Utc)));
        }
        return manifest;
    }

    public static byte[] SerializeSyncPlan(List<SyncPlanEntry> plan)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(plan.Count);
        foreach (var entry in plan)
        {
            writer.Write((byte)entry.Action);
            var pathBytes = Encoding.UTF8.GetBytes(entry.RelativePath);
            writer.Write((short)pathBytes.Length);
            writer.Write(pathBytes);
        }
        writer.Flush();
        return ms.ToArray();
    }

    public static List<SyncPlanEntry> DeserializeSyncPlan(byte[] data)
    {
        var plan = new List<SyncPlanEntry>();
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms, Encoding.UTF8);
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            var action = (SyncActionType)reader.ReadByte();
            short pathLen = reader.ReadInt16();
            var path = Encoding.UTF8.GetString(reader.ReadBytes(pathLen));
            plan.Add(new SyncPlanEntry(action, path));
        }
        return plan;
    }

    public static byte[] SerializeFileStart(short fileId, string relativePath, long originalSize, bool isCompressed, int blockSize)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(fileId);
        var pathBytes = Encoding.UTF8.GetBytes(relativePath);
        writer.Write((short)pathBytes.Length);
        writer.Write(pathBytes);
        writer.Write(originalSize);
        writer.Write((byte)(isCompressed ? 1 : 0));
        writer.Write(blockSize);
        writer.Flush();
        return ms.ToArray();
    }

    public static (short fileId, string relativePath, long originalSize, bool isCompressed, int blockSize) DeserializeFileStart(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms, Encoding.UTF8);
        short fileId = reader.ReadInt16();
        short pathLen = reader.ReadInt16();
        string path = Encoding.UTF8.GetString(reader.ReadBytes(pathLen));
        long originalSize = reader.ReadInt64();
        bool isCompressed = reader.ReadByte() == 1;
        int blockSize = reader.ReadInt32();
        return (fileId, path, originalSize, isCompressed, blockSize);
    }

    public static byte[] SerializeFileChunk(short fileId, int chunkIndex, byte[] chunkData)
    {
        var result = new byte[6 + chunkData.Length];
        BitConverter.TryWriteBytes(result.AsSpan(0), fileId);
        BitConverter.TryWriteBytes(result.AsSpan(2), chunkIndex);
        chunkData.CopyTo(result, 6);
        return result;
    }

    public static (short fileId, int chunkIndex, byte[] chunkData) DeserializeFileChunk(byte[] data)
    {
        short fileId = BitConverter.ToInt16(data, 0);
        int chunkIndex = BitConverter.ToInt32(data, 2);
        var chunkData = new byte[data.Length - 6];
        Array.Copy(data, 6, chunkData, 0, chunkData.Length);
        return (fileId, chunkIndex, chunkData);
    }

    public static byte[] SerializeFileEnd(short fileId, byte[] sha256Hash)
    {
        var result = new byte[2 + 32];
        BitConverter.TryWriteBytes(result.AsSpan(0), fileId);
        sha256Hash.CopyTo(result, 2);
        return result;
    }

    public static (short fileId, byte[] sha256Hash) DeserializeFileEnd(byte[] data)
    {
        short fileId = BitConverter.ToInt16(data, 0);
        var hash = new byte[32];
        Array.Copy(data, 2, hash, 0, 32);
        return (fileId, hash);
    }

    public static byte[] SerializeSyncComplete(int filesTransferred, long bytesTransferred, int filesDeleted, long elapsedMs)
    {
        var result = new byte[24];
        BitConverter.TryWriteBytes(result.AsSpan(0), filesTransferred);
        BitConverter.TryWriteBytes(result.AsSpan(4), bytesTransferred);
        BitConverter.TryWriteBytes(result.AsSpan(12), filesDeleted);
        BitConverter.TryWriteBytes(result.AsSpan(16), elapsedMs);
        return result;
    }

    public static (int filesTransferred, long bytesTransferred, int filesDeleted, long elapsedMs) DeserializeSyncComplete(byte[] data) =>
        (BitConverter.ToInt32(data, 0), BitConverter.ToInt64(data, 4), BitConverter.ToInt32(data, 12), BitConverter.ToInt64(data, 16));

    public static byte[] SerializeError(int errorCode, string message)
    {
        var msgBytes = Encoding.UTF8.GetBytes(message);
        var result = new byte[4 + msgBytes.Length];
        BitConverter.TryWriteBytes(result.AsSpan(0), errorCode);
        msgBytes.CopyTo(result, 4);
        return result;
    }

    public static (int errorCode, string message) DeserializeError(byte[] data) =>
        (BitConverter.ToInt32(data, 0), Encoding.UTF8.GetString(data, 4, data.Length - 4));

    public static byte[] SerializeDeleteFile(string relativePath, bool backupFirst)
    {
        var pathBytes = Encoding.UTF8.GetBytes(relativePath);
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write((short)pathBytes.Length);
        writer.Write(pathBytes);
        writer.Write((byte)(backupFirst ? 1 : 0));
        writer.Flush();
        return ms.ToArray();
    }

    public static (string relativePath, bool backupFirst) DeserializeDeleteFile(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms, Encoding.UTF8);
        short pathLen = reader.ReadInt16();
        var path = Encoding.UTF8.GetString(reader.ReadBytes(pathLen));
        bool backupFirst = reader.ReadByte() == 1;
        return (path, backupFirst);
    }

    public static byte[] SerializeDeleteConfirm(string relativePath, bool success)
    {
        var pathBytes = Encoding.UTF8.GetBytes(relativePath);
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write((short)pathBytes.Length);
        writer.Write(pathBytes);
        writer.Write((byte)(success ? 1 : 0));
        writer.Flush();
        return ms.ToArray();
    }

    public static (string relativePath, bool success) DeserializeDeleteConfirm(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms, Encoding.UTF8);
        short pathLen = reader.ReadInt16();
        var path = Encoding.UTF8.GetString(reader.ReadBytes(pathLen));
        bool success = reader.ReadByte() == 1;
        return (path, success);
    }
}
