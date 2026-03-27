using RemoteFileSync.Transfer;

namespace RemoteFileSync.Tests.Transfer;

public class CompressionHelperTests : IDisposable
{
    private readonly string _tempDir;

    public CompressionHelperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rfs_comp_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Theory]
    [InlineData(".zip", true)]
    [InlineData(".gz", true)]
    [InlineData(".7z", true)]
    [InlineData(".jpg", true)]
    [InlineData(".png", true)]
    [InlineData(".mp4", true)]
    [InlineData(".mp3", true)]
    [InlineData(".docx", true)]
    [InlineData(".pdf", true)]
    [InlineData(".txt", false)]
    [InlineData(".csv", false)]
    [InlineData(".xml", false)]
    [InlineData(".cs", false)]
    [InlineData(".html", false)]
    [InlineData("", false)]
    public void IsAlreadyCompressed_DetectsCorrectly(string extension, bool expected)
    {
        Assert.Equal(expected, CompressionHelper.IsAlreadyCompressed(extension));
    }

    [Fact]
    public void CompressFile_ProducesSmallerOutput_ForTextFile()
    {
        var source = Path.Combine(_tempDir, "source.txt");
        var compressed = Path.Combine(_tempDir, "source.txt.gz");
        File.WriteAllText(source, new string('A', 10000));
        CompressionHelper.CompressFile(source, compressed);
        Assert.True(File.Exists(compressed));
        Assert.True(new FileInfo(compressed).Length < new FileInfo(source).Length);
    }

    [Fact]
    public void DecompressFile_RestoresOriginalContent()
    {
        var source = Path.Combine(_tempDir, "original.txt");
        var compressed = Path.Combine(_tempDir, "compressed.gz");
        var decompressed = Path.Combine(_tempDir, "restored.txt");
        var content = "The quick brown fox jumps over the lazy dog. " +
                      string.Join("", Enumerable.Range(0, 100).Select(i => $"Line {i}\n"));
        File.WriteAllText(source, content);
        CompressionHelper.CompressFile(source, compressed);
        CompressionHelper.DecompressFile(compressed, decompressed);
        Assert.Equal(content, File.ReadAllText(decompressed));
    }

    [Fact]
    public void ComputeSha256_ReturnsConsistentHash()
    {
        var file = Path.Combine(_tempDir, "hashme.txt");
        File.WriteAllText(file, "deterministic content");
        var hash1 = CompressionHelper.ComputeSha256(file);
        var hash2 = CompressionHelper.ComputeSha256(file);
        Assert.Equal(32, hash1.Length);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeSha256_DifferentContent_DifferentHash()
    {
        var file1 = Path.Combine(_tempDir, "a.txt");
        var file2 = Path.Combine(_tempDir, "b.txt");
        File.WriteAllText(file1, "content A");
        File.WriteAllText(file2, "content B");
        Assert.NotEqual(CompressionHelper.ComputeSha256(file1), CompressionHelper.ComputeSha256(file2));
    }
}
