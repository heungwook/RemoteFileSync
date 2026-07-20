using System.Text;
using RemoteFileSync.Models;

namespace RemoteFileSync.Network;

public static class ProtocolHandler
{
    /// <summary>
    /// Wire protocol version. v2 added lastModifiedUtcTicks to the FileStart frame. v3 widened
    /// the handshake to carry the full SyncMode plus the delete and mirror flags — v2 had a
    /// single "bidirectional" bit, which cannot express push/pull/two-way — and made both sides
    /// stamp DateTime.UtcNow.Ticks so the client can measure the peer's clock offset; see
    /// <see cref="Sync.ClockSkew"/>.
    /// Peers running different versions are rejected during handshake: a v1 peer silently
    /// ignores the trailing timestamp bytes, which makes sync never converge.
    /// </summary>
    public const byte ProtocolVersion = 3;

    /// <summary>
    /// Upper bound on a single frame, guarding against a hostile length prefix. Note this also
    /// bounds the manifest frame, capping a synced tree at roughly 1.3M files; if that ever
    /// binds, chunk the manifest across frames rather than raising this.
    /// </summary>
    public const int MaxMessageBytes = 64 * 1024 * 1024;

    private static void WritePath(BinaryWriter writer, string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        if (bytes.Length > short.MaxValue)
            throw new InvalidDataException(
                $"Path exceeds {short.MaxValue} UTF-8 bytes and cannot be framed: {path}");
        writer.Write((short)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadPath(BinaryReader reader)
    {
        short len = reader.ReadInt16();
        if (len < 0) throw new InvalidDataException($"Negative path length: {len}");
        var bytes = reader.ReadBytes(len);
        if (bytes.Length != len) throw new InvalidDataException("Truncated path in frame.");
        return Encoding.UTF8.GetString(bytes);
    }

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
        if (length < 0 || length > MaxMessageBytes)
            throw new InvalidDataException($"Invalid message length {length} (allowed 0..{MaxMessageBytes}).");
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

    /// <summary>
    /// v3 handshake, 11 bytes: [0] version, [1] syncMode bits (low 2 = SyncMode, bit 2 =
    /// deleteEnabled, bit 3 = mirrorDeletes), [2..9] clientSentTicks, [10] reserved (0).
    /// </summary>
    public static byte[] SerializeHandshake(byte version, byte syncMode, long clientSentTicks)
    {
        var result = new byte[11];
        result[0] = version;
        result[1] = syncMode;
        BitConverter.TryWriteBytes(result.AsSpan(2), clientSentTicks);
        // result[10] stays 0. It is reserved but still transmitted, so both v3 peers agree on
        // the frame length and a later flag can occupy it without another version bump.
        return result;
    }

    public static (byte version, byte syncMode, long clientSentTicks) DeserializeHandshake(byte[] data)
    {
        // Length is checked before any indexing: a short frame from an older or hostile peer
        // would otherwise read whatever followed the buffer as a clock reading, and ClockSkew
        // would silently normalise every server mtime by a garbage offset.
        if (data.Length < 11) throw new InvalidDataException("Handshake payload truncated.");
        return (data[0], data[1], BitConverter.ToInt64(data, 2));
    }

    /// <summary>
    /// v3 ack, 10 bytes: [0] version, [1] accepted (0 = accepted — v2's polarity, kept so an
    /// older peer reading only the first two bytes still sees the verdict), [2..9] serverTicks.
    /// </summary>
    public static byte[] SerializeHandshakeAck(byte version, bool accepted, long serverTicks)
    {
        var result = new byte[10];
        result[0] = version;
        result[1] = (byte)(accepted ? 0 : 1);
        BitConverter.TryWriteBytes(result.AsSpan(2), serverTicks);
        return result;
    }

    public static (byte version, bool accepted, long serverTicks) DeserializeHandshakeAck(byte[] data)
    {
        if (data.Length < 10) throw new InvalidDataException("HandshakeAck payload truncated.");
        return (data[0], data[1] == 0, BitConverter.ToInt64(data, 2));
    }

    public static byte[] SerializeManifest(FileManifest manifest)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(manifest.Count);
        foreach (var entry in manifest.Entries)
        {
            WritePath(writer, entry.RelativePath);
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
            var path = ReadPath(reader);
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
            WritePath(writer, entry.RelativePath);
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
            var path = ReadPath(reader);
            plan.Add(new SyncPlanEntry(action, path));
        }
        return plan;
    }

    public static byte[] SerializeFileStart(short fileId, string relativePath, long originalSize,
                                            bool isCompressed, int blockSize, long lastModifiedUtcTicks)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        writer.Write(fileId);
        WritePath(writer, relativePath);
        writer.Write(originalSize);
        writer.Write((byte)(isCompressed ? 1 : 0));
        writer.Write(blockSize);
        writer.Write(lastModifiedUtcTicks);
        writer.Flush();
        return ms.ToArray();
    }

    public static (short fileId, string relativePath, long originalSize, bool isCompressed,
                   int blockSize, long lastModifiedUtcTicks) DeserializeFileStart(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms, Encoding.UTF8);
        short fileId = reader.ReadInt16();
        string path = ReadPath(reader);
        long originalSize = reader.ReadInt64();
        bool isCompressed = reader.ReadByte() == 1;
        int blockSize = reader.ReadInt32();
        long lastModifiedUtcTicks = reader.ReadInt64();
        return (fileId, path, originalSize, isCompressed, blockSize, lastModifiedUtcTicks);
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
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        WritePath(writer, relativePath);
        writer.Write((byte)(backupFirst ? 1 : 0));
        writer.Flush();
        return ms.ToArray();
    }

    public static (string relativePath, bool backupFirst) DeserializeDeleteFile(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms, Encoding.UTF8);
        var path = ReadPath(reader);
        bool backupFirst = reader.ReadByte() == 1;
        return (path, backupFirst);
    }

    public static byte[] SerializeDeleteConfirm(string relativePath, bool success)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        WritePath(writer, relativePath);
        writer.Write((byte)(success ? 1 : 0));
        writer.Flush();
        return ms.ToArray();
    }

    public static (string relativePath, bool success) DeserializeDeleteConfirm(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms, Encoding.UTF8);
        var path = ReadPath(reader);
        bool success = reader.ReadByte() == 1;
        return (path, success);
    }
}
