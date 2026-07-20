using RemoteFileSync.Backup;

namespace RemoteFileSync.Sync;

/// <summary>
/// Builds the name a losing copy is renamed to when a ConflictKeepBoth entry is executed:
/// {nameWithoutExtension}.conflict-{yyyyMMdd-HHmmss}-{losingSide}{extension}
///
/// The name is chosen once, by the client, and travels inside the sync plan. Both peers must
/// land the loser on the byte-identical path: if they disagree, the next scan sees two unrelated
/// files and copies each one to the other side, forever.
/// </summary>
public static class ConflictNamer
{
    public const string Infix = ".conflict-";
    public const string ClientSide = "client";
    public const string ServerSide = "server";

    /// <summary>Upper bound on collision retries, so a directory the process cannot write to
    /// fails loudly instead of spinning.</summary>
    public const int MaxOrdinal = 1000;

    /// <summary>Deliberately the SAME constant the archive session folder uses. The conflict copy
    /// and its archived snapshot are correlated by this string; two independent format literals
    /// would drift the first time either was edited.</summary>
    private const string StampFormat = ArchiveManager.SessionFolderFormat;

    /// <summary>
    /// <paramref name="ordinal"/> 1 produces the bare name; 2 and above append "-{ordinal}" so a
    /// second conflict on the same path within the same second does not overwrite the first.
    /// </summary>
    public static string Compose(string relativePath, DateTime sessionStartUtc, string losingSide, int ordinal = 1)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("relativePath must not be empty", nameof(relativePath));
        if (losingSide != ClientSide && losingSide != ServerSide)
            throw new ArgumentException($"losingSide must be '{ClientSide}' or '{ServerSide}'", nameof(losingSide));
        if (ordinal < 1)
            throw new ArgumentOutOfRangeException(nameof(ordinal), "ordinal starts at 1");

        // Wire paths are always '/'-separated. Split the directory off by hand rather than via
        // Path.GetDirectoryName, which would rewrite the separator on Windows and produce a name
        // the peer cannot match against its own manifest.
        var normalized = relativePath.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        var dir = slash >= 0 ? normalized[..(slash + 1)] : string.Empty;
        var fileName = slash >= 0 ? normalized[(slash + 1)..] : normalized;

        // GetExtension takes the LAST dot, so "archive.tar.gz" keeps ".tar" in the stem and only
        // ".gz" is re-appended — which is what the user sees in Explorer and what double-click
        // still opens.
        var extension = Path.GetExtension(fileName);
        var stem = extension.Length > 0 ? fileName[..^extension.Length] : fileName;

        var suffix = ordinal > 1 ? $"-{ordinal}" : string.Empty;
        return $"{dir}{stem}{Infix}{sessionStartUtc.ToString(StampFormat)}-{losingSide}{suffix}{extension}";
    }

    /// <summary>
    /// Compose, walked forward until nothing occupies the name inside <paramref name="syncFolder"/>.
    /// Only the client calls this: the chosen name goes into the plan and the server renames to
    /// exactly that name, so the two folders cannot diverge on a collision.
    /// </summary>
    public static string MakeUnique(string syncFolder, string relativePath, DateTime sessionStartUtc, string losingSide)
    {
        for (int ordinal = 1; ordinal <= MaxOrdinal; ordinal++)
        {
            var candidate = Compose(relativePath, sessionStartUtc, losingSide, ordinal);
            var full = Path.Combine(syncFolder, candidate.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full) && !Directory.Exists(full)) return candidate;
        }
        throw new IOException(
            $"Could not find a free conflict name for '{relativePath}' after {MaxOrdinal} attempts.");
    }

    /// <summary>
    /// Recovers the path a conflict copy was renamed from, and which side lost. Returns false for
    /// any name this class did not produce, so an ordinary file that happens to contain
    /// ".conflict-" is never mistaken for a rename instruction from the peer.
    /// </summary>
    public static bool TryParse(string conflictRelativePath, out string originalPath, out string losingSide)
    {
        originalPath = string.Empty;
        losingSide = string.Empty;
        if (string.IsNullOrWhiteSpace(conflictRelativePath)) return false;

        var normalized = conflictRelativePath.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        var dir = slash >= 0 ? normalized[..(slash + 1)] : string.Empty;
        var fileName = slash >= 0 ? normalized[(slash + 1)..] : normalized;

        // LastIndexOf, not IndexOf: a file already named "a.conflict-...-client.txt" that
        // conflicts a second time must parse back to that name, not to "a.txt" — otherwise the
        // rename would clobber the first conflict copy.
        int idx = fileName.LastIndexOf(Infix, StringComparison.Ordinal);
        if (idx < 0) return false;

        var stem = fileName[..idx];
        var rest = fileName[(idx + Infix.Length)..];
        var extension = Path.GetExtension(rest);
        var token = extension.Length > 0 ? rest[..^extension.Length] : rest;

        var parts = token.Split('-');
        if (parts.Length is not (3 or 4)) return false;
        if (parts[0].Length != 8 || !parts[0].All(char.IsAsciiDigit)) return false;
        if (parts[1].Length != 6 || !parts[1].All(char.IsAsciiDigit)) return false;
        if (parts[2] != ClientSide && parts[2] != ServerSide) return false;
        if (parts.Length == 4 && (parts[3].Length == 0 || !parts[3].All(char.IsAsciiDigit))) return false;

        losingSide = parts[2];
        originalPath = dir + stem + extension;
        return originalPath.Length > 0;
    }
}
