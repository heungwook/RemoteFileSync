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
    public async Task ChecksumMismatch_LeavesExistingDestinationUntouched()
    {
        var sourceDir = Path.Combine(_tempDir, "cm_source");
        var destDir = Path.Combine(_tempDir, "cm_dest");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);

        // A valuable pre-existing file at the destination.
        var destFile = Path.Combine(destDir, "important.txt");
        File.WriteAllText(destFile, "PRECIOUS ORIGINAL");

        File.WriteAllText(Path.Combine(sourceDir, "important.txt"), "replacement payload");

        using var pipeStream = new MemoryStream();
        var sender = new FileTransferSender(sourceDir, blockSize: 1024);
        await sender.SendFileAsync(pipeStream, fileId: 1, relativePath: "important.txt", CancellationToken.None);

        // Corrupt the trailing FileEnd hash so verification must fail.
        var bytes = pipeStream.ToArray();
        bytes[^1] ^= 0xFF;

        using var corrupted = new MemoryStream(bytes);
        var receiver = new FileTransferReceiver(destDir);
        var result = await receiver.ReceiveFileAsync(corrupted, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Checksum mismatch", result.ErrorMessage);
        // The old behaviour overwrote the destination and then deleted it, destroying data.
        Assert.True(File.Exists(destFile));
        Assert.Equal("PRECIOUS ORIGINAL", File.ReadAllText(destFile));
        // And no staging debris is left behind.
        Assert.Empty(Directory.GetFiles(destDir, $"*{FileTransferReceiver.StagingSuffix}*"));
    }

    [Fact]
    public async Task Receive_InvokesPreCommitHookWithTheReceivedPath()
    {
        var sourceDir = Path.Combine(_tempDir, "hook_source");
        var destDir = Path.Combine(_tempDir, "hook_dest");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);
        File.WriteAllText(Path.Combine(sourceDir, "doc.txt"), "payload");

        using var pipeStream = new MemoryStream();
        var sender = new FileTransferSender(sourceDir, blockSize: 1024);
        var receiver = new FileTransferReceiver(destDir);

        await sender.SendFileAsync(pipeStream, fileId: 1, relativePath: "doc.txt", CancellationToken.None);
        pipeStream.Position = 0;

        string? hookedPath = null;
        var result = await receiver.ReceiveFileAsync(pipeStream, CancellationToken.None,
            onBeforeCommit: p => { hookedPath = p; return true; });

        Assert.True(result.Success);
        // Driven by the path actually received, not by plan order.
        Assert.Equal("doc.txt", hookedPath);
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
