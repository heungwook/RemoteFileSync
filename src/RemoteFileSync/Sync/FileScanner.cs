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

    /// <summary>
    /// Number of directories skipped during the last scan because they could not be read.
    /// Non-zero means the manifest is incomplete: the peer cannot distinguish a file that is
    /// missing from one that was deleted, so callers MUST NOT propagate deletions after this.
    /// </summary>
    public int InaccessibleDirectories { get; private set; }

    public FileManifest Scan()
    {
        var manifest = new FileManifest();
        InaccessibleDirectories = 0;
        if (!Directory.Exists(_rootPath)) return manifest;

        SweepAbandonedStagingFiles();
        ScanDirectory(_rootPath, manifest);
        return manifest;
    }

    private void ScanDirectory(string directory, FileManifest manifest)
    {
        string[] files;
        string[] subdirectories;
        try
        {
            files = Directory.GetFiles(directory);
            subdirectories = Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // Count rather than swallow: one locked subdirectory must not abort the whole
            // sync, but it also must not silently look like a mass deletion.
            InaccessibleDirectories++;
            return;
        }

        foreach (var fullPath in files)
        {
            var relativePath = Path.GetRelativePath(_rootPath, fullPath).Replace('\\', '/');
            if (!MatchesFilters(relativePath)) continue;

            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists) continue;   // vanished between enumeration and stat
                manifest.Add(new FileEntry(relativePath, info.Length, info.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
                                          or UnauthorizedAccessException or IOException)
            {
                // Transient or permission-denied on a single file: skip it, keep scanning.
            }
        }

        foreach (var subdirectory in subdirectories)
        {
            // Do not follow junctions or symlinks: they can point outside the sync root.
            try
            {
                if (new DirectoryInfo(subdirectory).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                InaccessibleDirectories++;
                continue;
            }
            ScanDirectory(subdirectory, manifest);
        }
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

    /// <summary>
    /// Whether a relative path survives this scanner's include/exclude filters. Exposed so
    /// the sync plan can ignore filtered paths entirely rather than treating their absence
    /// from the manifest as a deletion.
    /// </summary>
    public bool IsIncluded(string relativePath) => MatchesFilters(relativePath);

    private bool MatchesFilters(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);

        // Never surface in-progress receives: they are not real content, and including them
        // would propagate partial files to the peer.
        if (fileName.Contains(FileTransferReceiver.StagingSuffix, StringComparison.Ordinal)) return false;

        // A pattern containing a separator is a path pattern, matched against the full relative
        // path; otherwise it is a name pattern. Previously every pattern matched the filename
        // only, so path patterns like "node_modules/*" could never match and silently did
        // nothing. Patterns are normalised to '/' first: relativePath uses '/', but a Windows
        // user naturally types "node_modules\*", which would otherwise be misclassified.
        bool Matches(string pattern)
        {
            var pat = pattern.Replace('\\', '/');
            return pat.Contains('/')
                ? GlobMatch(relativePath, pat)
                : GlobMatch(fileName, pat);
        }

        if (_includePatterns.Count > 0)
        {
            bool included = false;
            foreach (var pattern in _includePatterns)
            {
                if (Matches(pattern)) { included = true; break; }
            }
            if (!included) return false;
        }

        foreach (var pattern in _excludePatterns)
        {
            if (Matches(pattern)) return false;
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
