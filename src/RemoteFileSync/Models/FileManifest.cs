namespace RemoteFileSync.Models;

public sealed class FileManifest
{
    private readonly Dictionary<string, FileEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<FileEntry> Entries => _entries.Values;
    public int Count => _entries.Count;

    public void Add(FileEntry entry) { _entries[entry.RelativePath] = entry; }

    public FileEntry? Get(string relativePath)
    {
        _entries.TryGetValue(relativePath.Replace('\\', '/'), out var entry);
        return entry;
    }

    public bool Contains(string relativePath) => _entries.ContainsKey(relativePath.Replace('\\', '/'));
    public IEnumerable<string> AllPaths => _entries.Keys;
}
