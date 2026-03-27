namespace RemoteFileSync.Models;

public sealed class FileEntry : IEquatable<FileEntry>
{
    public string RelativePath { get; }
    public long FileSize { get; }
    public DateTime LastModifiedUtc { get; }

    public FileEntry(string relativePath, long fileSize, DateTime lastModifiedUtc)
    {
        RelativePath = relativePath.Replace('\\', '/');
        FileSize = fileSize;
        LastModifiedUtc = lastModifiedUtc;
    }

    public bool Equals(FileEntry? other)
    {
        if (other is null) return false;
        return RelativePath == other.RelativePath
            && FileSize == other.FileSize
            && LastModifiedUtc == other.LastModifiedUtc;
    }

    public override bool Equals(object? obj) => Equals(obj as FileEntry);
    public override int GetHashCode() => HashCode.Combine(RelativePath, FileSize, LastModifiedUtc);
    public override string ToString() => $"{RelativePath} ({FileSize} bytes, {LastModifiedUtc:u})";
}
