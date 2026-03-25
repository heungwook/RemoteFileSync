using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

public sealed class FileScanner
{
    private readonly string _rootPath;
    private readonly List<string> _includePatterns;
    private readonly List<string> _excludePatterns;

    public FileScanner(string rootPath, List<string> include, List<string> exclude)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _includePatterns = include;
        _excludePatterns = exclude;
    }

    public FileManifest Scan()
    {
        var manifest = new FileManifest();
        if (!Directory.Exists(_rootPath)) return manifest;

        foreach (var fullPath in Directory.EnumerateFiles(_rootPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(_rootPath, fullPath).Replace('\\', '/');
            if (!MatchesFilters(relativePath)) continue;

            var info = new FileInfo(fullPath);
            manifest.Add(new FileEntry(relativePath, info.Length, info.LastWriteTimeUtc));
        }
        return manifest;
    }

    private bool MatchesFilters(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);

        if (_includePatterns.Count > 0)
        {
            bool included = false;
            foreach (var pattern in _includePatterns)
            {
                if (GlobMatch(fileName, pattern)) { included = true; break; }
            }
            if (!included) return false;
        }

        foreach (var pattern in _excludePatterns)
        {
            if (GlobMatch(fileName, pattern)) return false;
        }
        return true;
    }

    public static bool GlobMatch(string input, string pattern)
    {
        int i = 0, p = 0;
        int starI = -1, starP = -1;

        while (i < input.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(input[i])))
            { i++; p++; }
            else if (p < pattern.Length && pattern[p] == '*')
            { starI = i; starP = p; p++; }
            else if (starP >= 0)
            { p = starP + 1; starI++; i = starI; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
