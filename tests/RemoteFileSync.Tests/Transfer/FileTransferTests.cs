using RemoteFileSync.Network;
using RemoteFileSync.Transfer;

namespace RemoteFileSync.Tests.Transfer;

public class FileTransferTests : IDisposable
{
    private readonly string _tempDir;

    public FileTransferTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rfs_xfer_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task SendAndReceive_TextFile_RoundTrips()
    {
        var sourceDir = Path.Combine(_tempDir, "source");
        var destDir = Path.Combine(_tempDir, "dest");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);
        var content = "Hello, world! " + new string('X', 5000);
        File.WriteAllText(Path.Combine(sourceDir, "test.txt"), content);

        using var pipeStream = new MemoryStream();
        var sender = new FileTransferSender(sourceDir, blockSize: 1024);
        var receiver = new FileTransferReceiver(destDir);

        await sender.SendFileAsync(pipeStream, fileId: 1, relativePath: "test.txt", CancellationToken.None);
        pipeStream.Position = 0;
        var result = await receiver.ReceiveFileAsync(pipeStream, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("test.txt", result.RelativePath);
        Assert.Equal(content, File.ReadAllText(Path.Combine(destDir, "test.txt")));
    }

    [Fact]
    public async Task SendAndReceive_AlreadyCompressedFile_NoGzip()
    {
        var sourceDir = Path.Combine(_tempDir, "source2");
        var destDir = Path.Combine(_tempDir, "dest2");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);
        var data = new byte[2048];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(Path.Combine(sourceDir, "photo.jpg"), data);

        using var pipeStream = new MemoryStream();
        var sender = new FileTransferSender(sourceDir, blockSize: 512);
        var receiver = new FileTransferReceiver(destDir);

        await sender.SendFileAsync(pipeStream, fileId: 2, relativePath: "photo.jpg", CancellationToken.None);
        pipeStream.Position = 0;
        var result = await receiver.ReceiveFileAsync(pipeStream, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(destDir, "photo.jpg")));
    }

    [Fact]
    public async Task SendAndReceive_SubdirectoryFile_CreatesPath()
    {
        var sourceDir = Path.Combine(_tempDir, "source3");
        var destDir = Path.Combine(_tempDir, "dest3");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);
        Directory.CreateDirectory(Path.Combine(sourceDir, "sub", "deep"));
        File.WriteAllText(Path.Combine(sourceDir, "sub", "deep", "nested.txt"), "deep content");

        using var pipeStream = new MemoryStream();
        var sender = new FileTransferSender(sourceDir, blockSize: 4096);
        var receiver = new FileTransferReceiver(destDir);

        await sender.SendFileAsync(pipeStream, fileId: 3, relativePath: "sub/deep/nested.txt", CancellationToken.None);
        pipeStream.Position = 0;
        var result = await receiver.ReceiveFileAsync(pipeStream, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(destDir, "sub", "deep", "nested.txt")));
        Assert.Equal("deep content", File.ReadAllText(Path.Combine(destDir, "sub", "deep", "nested.txt")));
    }
}
