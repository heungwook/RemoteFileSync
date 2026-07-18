using RemoteFileSync.Models;
using RemoteFileSync.Transfer;

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

    /// <summary>Age after which an abandoned staging file is swept during a scan.</summary>
    private static readonly TimeSpan StagingFileMaxAge = TimeSpan.FromHours(24);

    public FileManifest Scan()
    {
        var manifest = new FileManifest();
        if (!Directory.Exists(_rootPath)) return manifest;

        SweepAbandonedStagingFiles();

        foreach (var fullPath in Directory.EnumerateFiles(_rootPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(_rootPath, fullPath).Replace('\\', '/');
            if (!MatchesFilters(relativePath)) continue;

            var info = new FileInfo(fullPath);
            manifest.Add(new FileEntry(relativePath, info.Length, info.LastWriteTimeUtc));
        }
        return manifest;
    }

    /// <summary>
    /// Deletes staging files left behind by a crashed or cancelled receive. They are excluded
    /// from the manifest, so without this they would accumulate inside the sync tree forever.
    /// </summary>
    private void SweepAbandonedStagingFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow - StagingFileMaxAge;
            foreach (var stale in Directory.EnumerateFiles(
                         _rootPath, $"*{FileTransferReceiver.StagingSuffix}*", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(stale) < cutoff) File.Delete(stale);
                }
                catch { /* best effort: another process may hold or have removed it */ }
            }
        }
        catch { /* sweeping is opportunistic and must never fail a sync */ }
    }

    private bool MatchesFilters(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);

        // Never surface in-progress receives: they are not real content, and including them
        // would propagate partial files to the peer.
        if (fileName.Contains(FileTransferReceiver.StagingSuffix, StringComparison.Ordinal)) return false;

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
