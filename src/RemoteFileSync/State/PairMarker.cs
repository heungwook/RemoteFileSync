namespace RemoteFileSync.State;

/// <summary>
/// A sentinel file written beside sync.db after the first clean sync of a pair.
/// Its presence next to a MISSING or unreadable database is what distinguishes lost sync
/// state from a genuine first run. Without it the two are indistinguishable, and a wiped
/// database makes every peer file look brand new — which under --mirror mirrors a
/// full-tree delete back onto the peer.
/// </summary>
public static class PairMarker
{
    private const string MarkerFileName = "pair.marker";

    public static string PathFor(string dbPath)
    {
        var dir = System.IO.Path.GetDirectoryName(dbPath);
        return string.IsNullOrEmpty(dir)
            ? MarkerFileName
            : System.IO.Path.Combine(dir, MarkerFileName);
    }

    public static bool Exists(string dbPath) => File.Exists(PathFor(dbPath));

    public static void Write(string dbPath)
    {
        var markerPath = PathFor(dbPath);
        var dir = System.IO.Path.GetDirectoryName(markerPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Content is diagnostic only; the gate reads existence, never the bytes. Rewriting
        // on every clean exit keeps Write idempotent without a pre-existence check.
        File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O"));
    }
}
