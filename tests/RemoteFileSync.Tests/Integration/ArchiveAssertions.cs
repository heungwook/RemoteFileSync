using RemoteFileSync.Backup;

namespace RemoteFileSync.Tests.Integration;

/// <summary>
/// Assertions over the archive tree laid out as
/// &lt;archiveRoot&gt;/&lt;yyyyMMdd-HHmmss&gt;/&lt;reason&gt;/&lt;relative path&gt;.
/// The session folder is stamped at sync start, so a test cannot spell it out and must
/// search for the file instead.
/// </summary>
internal static class ArchiveAssertions
{
    /// <summary>
    /// Asserts exactly one archived copy of <paramref name="fileName"/> exists under the
    /// expected reason folder, and returns its full path. Insisting on exactly one is the
    /// point: two copies mean a single run scattered itself across two session folders.
    /// </summary>
    public static string AssertArchived(string archiveRoot, ArchiveReason reason, string fileName)
    {
        Assert.True(Directory.Exists(archiveRoot), $"no archive root at {archiveRoot}");
        var hits = Directory.GetFiles(archiveRoot, fileName, SearchOption.AllDirectories);
        Assert.Single(hits);
        var segment = $"{Path.DirectorySeparatorChar}{reason.ToString().ToLowerInvariant()}{Path.DirectorySeparatorChar}";
        Assert.Contains(segment, hits[0]);
        return hits[0];
    }

    /// <summary>
    /// An absent archive root and an empty one are equally acceptable: ArchiveManager may
    /// create its root eagerly, so only the file count is a real signal.
    /// </summary>
    public static void AssertNothingArchived(string archiveRoot)
    {
        if (!Directory.Exists(archiveRoot)) return;
        Assert.Empty(Directory.GetFiles(archiveRoot, "*", SearchOption.AllDirectories));
    }
}
