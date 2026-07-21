using RemoteFileSync.Backup;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class ConflictNamerTests : IDisposable
{
    private static readonly DateTime Stamp = new(2026, 7, 20, 14, 30, 52, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"rfs_cname_{Guid.NewGuid()}");

    public ConflictNamerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    // Plain name with an extension: the contract's worked example.
    [InlineData("report.docx", "server", "report.conflict-20260720-143052-server.docx")]
    [InlineData("report.docx", "client", "report.conflict-20260720-143052-client.docx")]
    // No extension at all.
    [InlineData("README", "server", "README.conflict-20260720-143052-server")]
    // Multiple dots: Path.GetExtension takes only the last, so ".tar" stays in the stem.
    [InlineData("archive.tar.gz", "client", "archive.tar.conflict-20260720-143052-client.gz")]
    // Subdirectory: the loser must land beside the winner, not at the root.
    [InlineData("docs/q3/report.docx", "server", "docs/q3/report.conflict-20260720-143052-server.docx")]
    // Dotfile: GetExtension returns the whole name, leaving an empty stem. Still round-trips.
    [InlineData(".gitignore", "client", ".conflict-20260720-143052-client.gitignore")]
    public void Compose_MatchesContractFormat(string relativePath, string losingSide, string expected)
    {
        Assert.Equal(expected, ConflictNamer.Compose(relativePath, Stamp, losingSide));
    }

    [Fact]
    public void Compose_OrdinalTwoAppendsSuffixBeforeExtension()
    {
        Assert.Equal("report.conflict-20260720-143052-server-2.docx",
            ConflictNamer.Compose("report.docx", Stamp, ConflictNamer.ServerSide, ordinal: 2));
    }

    [Fact]
    public void Compose_RejectsUnknownLosingSide()
    {
        Assert.Throws<ArgumentException>(() => ConflictNamer.Compose("a.txt", Stamp, "peer"));
    }

    [Fact]
    public void Compose_StampMatchesTheArchiveSessionFolderName()
    {
        // The conflict copy and the archived snapshot of the same file must be findable by the
        // same timestamp string. Two independently-written format strings would drift apart the
        // first time either is edited, leaving the user unable to correlate them.
        var name = ConflictNamer.Compose("report.docx", Stamp, ConflictNamer.ServerSide);
        Assert.Contains(Stamp.ToString(ArchiveManager.SessionFolderFormat), name);
    }

    // ── MakeUnique: the collision walk ────────────────────────────────────────

    [Fact]
    public void MakeUnique_ReturnsBareNameWhenNothingOccupiesIt()
    {
        Assert.Equal("report.conflict-20260720-143052-client.txt",
            ConflictNamer.MakeUnique(_dir, "report.txt", Stamp, ConflictNamer.ClientSide));
    }

    [Fact]
    public void MakeUnique_WalksOrdinalPastExistingFiles()
    {
        File.WriteAllText(Path.Combine(_dir, "report.conflict-20260720-143052-client.txt"), "first");
        Assert.Equal("report.conflict-20260720-143052-client-2.txt",
            ConflictNamer.MakeUnique(_dir, "report.txt", Stamp, ConflictNamer.ClientSide));

        File.WriteAllText(Path.Combine(_dir, "report.conflict-20260720-143052-client-2.txt"), "second");
        Assert.Equal("report.conflict-20260720-143052-client-3.txt",
            ConflictNamer.MakeUnique(_dir, "report.txt", Stamp, ConflictNamer.ClientSide));
    }

    [Fact]
    public void MakeUnique_PreservesSubdirectoryAndCreatesNoFile()
    {
        var name = ConflictNamer.MakeUnique(_dir, "docs/report.txt", Stamp, ConflictNamer.ServerSide);
        Assert.Equal("docs/report.conflict-20260720-143052-server.txt", name);
        Assert.False(File.Exists(Path.Combine(_dir, "docs", "report.conflict-20260720-143052-server.txt")));
    }

    // ── TryParse: round-trip and rejection ────────────────────────────────────

    [Theory]
    [InlineData("report.docx", "server")]
    [InlineData("README", "client")]
    [InlineData("archive.tar.gz", "server")]
    [InlineData("docs/q3/report.docx", "client")]
    [InlineData(".gitignore", "server")]
    public void TryParse_RoundTripsCompose(string relativePath, string losingSide)
    {
        var name = ConflictNamer.Compose(relativePath, Stamp, losingSide);
        Assert.True(ConflictNamer.TryParse(name, out var original, out var side));
        Assert.Equal(relativePath, original);
        Assert.Equal(losingSide, side);
    }

    [Fact]
    public void TryParse_RoundTripsOrdinalNames()
    {
        var name = ConflictNamer.Compose("report.docx", Stamp, ConflictNamer.ServerSide, ordinal: 7);
        Assert.True(ConflictNamer.TryParse(name, out var original, out var side));
        Assert.Equal("report.docx", original);
        Assert.Equal(ConflictNamer.ServerSide, side);
    }

    [Fact]
    public void TryParse_NestedConflictUnwrapsOnlyTheOuterLayer()
    {
        // A conflict copy that conflicts again must resolve to the conflict copy, not to the
        // original — unwrapping both layers would rename over the first conflict copy.
        var inner = ConflictNamer.Compose("report.docx", Stamp, ConflictNamer.ClientSide);
        var outer = ConflictNamer.Compose(inner, Stamp, ConflictNamer.ServerSide);
        Assert.True(ConflictNamer.TryParse(outer, out var original, out var side));
        Assert.Equal(inner, original);
        Assert.Equal(ConflictNamer.ServerSide, side);
    }

    [Theory]
    [InlineData("report.docx")]                                   // no infix at all
    [InlineData("my.conflict-notes.txt")]                         // infix present, no stamp
    [InlineData("report.conflict-20260720-143052-peer.docx")]     // unknown side
    [InlineData("report.conflict-2026072-143052-server.docx")]    // 7-digit date
    [InlineData("report.conflict-20260720-14305-server.docx")]    // 5-digit time
    [InlineData("report.conflict-20260720-143052-server-x.docx")] // non-numeric ordinal
    [InlineData("")]
    public void TryParse_RejectsNamesItDidNotProduce(string candidate)
    {
        Assert.False(ConflictNamer.TryParse(candidate, out _, out _));
    }
}
