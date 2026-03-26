using System.IO.Compression;
using System.Security.Cryptography;

namespace RemoteFileSync.Transfer;

public static class CompressionHelper
{
    private static readonly HashSet<string> CompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".gz", ".7z", ".rar", ".tgz", ".bz2", ".xz", ".zst",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".avif",
        ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".webm",
        ".mp3", ".aac", ".flac", ".ogg", ".wma", ".m4a",
        ".pdf", ".docx", ".xlsx", ".pptx"
    };

    public static bool IsAlreadyCompressed(string extensionOrPath)
    {
        var ext = extensionOrPath.StartsWith('.') ? extensionOrPath : Path.GetExtension(extensionOrPath);
        return CompressedExtensions.Contains(ext);
    }

    public static void CompressFile(string sourcePath, string compressedPath)
    {
        using var sourceStream = File.OpenRead(sourcePath);
        using var destStream = File.Create(compressedPath);
        using var gzipStream = new GZipStream(destStream, CompressionLevel.Optimal);
        sourceStream.CopyTo(gzipStream);
    }

    public static void DecompressFile(string compressedPath, string destPath)
    {
        using var sourceStream = File.OpenRead(compressedPath);
        using var gzipStream = new GZipStream(sourceStream, CompressionMode.Decompress);
        using var destStream = File.Create(destPath);
        gzipStream.CopyTo(destStream);
    }

    public static byte[] ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return SHA256.HashData(stream);
    }
}
