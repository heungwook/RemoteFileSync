using RemoteFileSync.Security;

namespace RemoteFileSync.Tests.Security;

public class PathGuardTests : IDisposable
{
    private readonly string _root;

    public PathGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rfs_guard_{Guid.NewGuid()}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    // Traversal
    [InlineData("../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("sub/../../escape.txt")]
    [InlineData("a/b/../../../escape.txt")]
    // Absolute / drive-qualified / UNC
    [InlineData(@"C:\Windows\System32\evil.dll")]
    [InlineData(@"\\server\share\evil.dll")]
    [InlineData("/etc/passwd")]
    // NTFS alternate data stream
    [InlineData("file.txt:stream")]
    // Empty / whitespace
    [InlineData("")]
    [InlineData("   ")]
    // Trailing dots and spaces alias to a different on-disk name
    [InlineData("report...")]
    [InlineData("report. .")]
    [InlineData("sub/report ")]
    // Our own staging marker must never be accepted from the wire
    [InlineData("doc.txt.rfs-part-abc123")]
    public void RejectsPathsOutsideRootOrOtherwiseUnsafe(string candidate)
    {
        Assert.False(PathGuard.TryResolveWithinRoot(_root, candidate, out var resolved));
        Assert.Equal(string.Empty, resolved);
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("sub/dir/a.txt")]
    [InlineData("sub\\dir\\a.txt")]
    [InlineData("sub/../a.txt")]     // normalises back inside the root
    [InlineData("Ünïcödé.txt")]
    public void AcceptsPathsInsideRoot(string candidate)
    {
        Assert.True(PathGuard.TryResolveWithinRoot(_root, candidate, out var resolved));
        Assert.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsSiblingDirectoryWithSharedPrefix()
    {
        // "C:\sync" must not accept a path resolving into "C:\syncbackup".
        var sibling = _root + "backup";
        Directory.CreateDirectory(sibling);
        try
        {
            var escape = Path.Combine("..", Path.GetFileName(sibling), "loot.txt");
            Assert.False(PathGuard.TryResolveWithinRoot(_root, escape, out _));
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public void ResolveWithinRoot_ThrowsOnEscape()
    {
        var ex = Assert.Throws<UnauthorizedAccessException>(
            () => PathGuard.ResolveWithinRoot(_root, "../escape.txt"));
        Assert.Contains("outside sync root", ex.Message);
    }

    [Fact]
    public void RejectsTheRootItself()
    {
        Assert.False(PathGuard.TryResolveWithinRoot(_root, ".", out _));
    }
}
