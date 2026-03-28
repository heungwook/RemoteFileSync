using System.Security.Cryptography;
using System.Text;
using RemoteFileSync.Models;

namespace RemoteFileSync.State;

public sealed record SyncState(FileManifest Manifest, DateTime LastSyncUtc);

[Obsolete("Use SyncDatabase instead. Kept for migration from sync-state.bin.")]
public sealed class SyncStateManager
{
    private static readonly byte[] Magic = "RFS1"u8.ToArray();
    private readonly string _baseDir;

    public SyncStateManager(string baseDir)
    {
        _baseDir = baseDir;
    }

    public static string DefaultBaseDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteFileSync");

    public static string ComputePairId(string localFolder, string remoteHost, int port)
    {
        var input = $"{localFolder.TrimEnd('\\', '/')}|{remoteHost}:{port}".ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public string GetStatePath(string localFolder, string remoteHost, int port)
    {
        var pairId = ComputePairId(localFolder, remoteHost, port);
        return Path.Combine(_baseDir, pairId, "sync-state.bin");
    }

    public SyncState? LoadState(string localFolder, string remoteHost, int port)
    {
        var path = GetStatePath(localFolder, remoteHost, port);
        if (!File.Exists(path)) return null;

        try
        {
            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs, Encoding.UTF8);

            var magic = reader.ReadBytes(4);
            if (!magic.AsSpan().SequenceEqual(Magic)) return null;

            var ticks = reader.ReadInt64();
            var lastSyncUtc = new DateTime(ticks, DateTimeKind.Utc);

            var manifest = new FileManifest();
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                short pathLen = reader.ReadInt16();
                var relPath = Encoding.UTF8.GetString(reader.ReadBytes(pathLen));
                long fileSize = reader.ReadInt64();
                long modTicks = reader.ReadInt64();
                manifest.Add(new FileEntry(relPath, fileSize, new DateTime(modTicks, DateTimeKind.Utc)));
            }

            return new SyncState(manifest, lastSyncUtc);
        }
        catch
        {
            return null;
        }
    }

    public void SaveState(string localFolder, string remoteHost, int port,
                          FileManifest mergedManifest, DateTime syncUtc)
    {
        var path = GetStatePath(localFolder, remoteHost, port);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var tmpPath = path + ".tmp";
        using (var fs = File.Create(tmpPath))
        using (var writer = new BinaryWriter(fs, Encoding.UTF8))
        {
            writer.Write(Magic);
            writer.Write(syncUtc.Ticks);
            writer.Write(mergedManifest.Count);
            foreach (var entry in mergedManifest.Entries)
            {
                var pathBytes = Encoding.UTF8.GetBytes(entry.RelativePath);
                writer.Write((short)pathBytes.Length);
                writer.Write(pathBytes);
                writer.Write(entry.FileSize);
                writer.Write(entry.LastModifiedUtc.Ticks);
            }
        }

        File.Move(tmpPath, path, overwrite: true);
    }
}
