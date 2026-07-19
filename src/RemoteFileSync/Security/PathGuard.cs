using RemoteFileSync.Transfer;

namespace RemoteFileSync.Security;

/// <summary>
/// Validates that a peer-supplied relative path resolves inside the sync root.
/// Every path that arrives over the network must pass through here before it reaches the
/// filesystem — Path.Combine does not neutralise "..", and a rooted second argument silently
/// discards the root entirely.
/// </summary>
public static class PathGuard
{
    public static bool TryResolveWithinRoot(string root, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        // Reject anything drive-qualified, UNC, or otherwise rooted, plus NTFS
        // alternate-data-stream syntax ("file.txt:hidden").
        // NOTE: Path.GetInvalidPathChars() is only 36 chars (", <, >, | and C0 controls) —
        // it does NOT include ':', '*' or '?', so it cannot carry this check alone.
        if (Path.IsPathRooted(relativePath)) return false;
        if (relativePath.Contains(':')) return false;

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);

        // Per-segment validation: invalid filename chars, and trailing dots/spaces which
        // Windows silently strips ("a..." and "a. ." both resolve to "a"). Aliasing is not an
        // escape, but it makes several manifest paths collide on one destination whose on-disk
        // name never matches the manifest — so those files would re-transfer forever.
        foreach (var segment in normalized.Split(Path.DirectorySeparatorChar))
        {
            if (segment.Length == 0) continue;               // collapse doubled separators
            if (segment == "." || segment == "..") continue; // resolved below, then range-checked
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            if (segment != segment.TrimEnd('.', ' ')) return false;
        }

        // Never accept our own staging files as a wire path: the scanner excludes them from
        // the manifest, so the sender would re-transfer such a file on every run, forever.
        if (Path.GetFileName(normalized).Contains(FileTransferReceiver.StagingSuffix, StringComparison.Ordinal))
            return false;

        var rootFull = Path.GetFullPath(root);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
            rootFull += Path.DirectorySeparatorChar;

        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(rootFull, normalized));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // Trailing separator on rootFull means the root itself is also rejected.
        // Ordinal (not OrdinalIgnoreCase): `combined` is derived from `rootFull` via
        // Path.Combine and GetFullPath preserves that literal prefix, so the comparison is
        // against an identical string and the tighter form is correct.
        if (!combined.StartsWith(rootFull, StringComparison.Ordinal)) return false;

        // Path.GetFullPath is PURELY LEXICAL — it does not resolve junctions, directory
        // symlinks, or mount points. Without this walk, a reparse point inside the root
        // (`mklink /J C:\sync\link C:\Windows\System32`, creatable by any user) makes
        // "link/evil.dll" pass every check above and land outside the root.
        if (HasReparsePointAncestor(rootFull, combined)) return false;

        fullPath = combined;
        return true;
    }

    private static bool HasReparsePointAncestor(string rootFull, string target)
    {
        var dir = Path.GetDirectoryName(target);
        while (dir != null && dir.Length >= rootFull.Length - 1)
        {
            try
            {
                var info = new DirectoryInfo(dir);
                if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            }
            catch (IOException) { return true; }               // fail closed
            catch (UnauthorizedAccessException) { return true; }

            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return false;
    }

    public static string ResolveWithinRoot(string root, string relativePath) =>
        TryResolveWithinRoot(root, relativePath, out var full)
            ? full
            : throw new UnauthorizedAccessException($"Rejected path outside sync root: '{relativePath}'");
}
