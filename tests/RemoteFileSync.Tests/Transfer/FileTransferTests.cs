using RemoteFileSync.Backup;
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

    [Fact]
    public async Task Receive_PreCommitHookReturnsFalse_RefusesToOverwriteAndKeepsOldBytes()
    {
        var sourceDir = Path.Combine(_tempDir, "gate_source");
        var destDir = Path.Combine(_tempDir, "gate_dest");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);

        var destFile = Path.Combine(destDir, "important.txt");
        File.WriteAllText(destFile, "PRECIOUS ORIGINAL");
        File.WriteAllText(Path.Combine(sourceDir, "important.txt"), "replacement payload");

        // A REAL ArchiveManager, made to fail the way production fails: its archive root is
        // unreachable because a plain FILE sits where the session folder must be created, so
        // Directory.CreateDirectory throws IOException. This is the same observable outcome as
        // PathGuard failing closed on transient IO (PathGuard.cs:85-86) — Archive returns false
        // and does NOT throw — but it is deterministic, whereas a reparse-point/locking race
        // is not reproducible in CI.
        var blocker = Path.Combine(_tempDir, "not-a-directory");
        File.WriteAllText(blocker, "x");
        var archive = new ArchiveManager(destDir, Path.Combine(blocker, "archive"),
                                         new DateTime(2026, 7, 19, 14, 30, 52, DateTimeKind.Utc));

        using var pipeStream = new MemoryStream();
        var sender = new FileTransferSender(sourceDir, blockSize: 1024);
        var receiver = new FileTransferReceiver(destDir);
        await sender.SendFileAsync(pipeStream, fileId: 1, relativePath: "important.txt", CancellationToken.None);
        pipeStream.Position = 0;

        var result = await receiver.ReceiveFileAsync(pipeStream, CancellationToken.None,
            onBeforeCommit: p =>
                archive.TryArchive(p, ArchiveReason.Overwritten, removeOriginal: false)
                    != ArchiveOutcome.Failed);

        // The commit is refused, loudly. Before this task the transfer reported success and the
        // only copy of "PRECIOUS ORIGINAL" ceased to exist.
        Assert.False(result.Success);
        Assert.Equal("Refusing to overwrite: pre-overwrite archive failed", result.ErrorMessage);
        Assert.Equal("PRECIOUS ORIGINAL", File.ReadAllText(destFile));
        Assert.Empty(Directory.GetFiles(destDir, $"*{FileTransferReceiver.StagingSuffix}*"));
    }

    [Fact]
    public async Task Receive_ArchiveManagerRootedOutsideTheSyncFolder_HasNothingToArchiveAndStillCommits()
    {
        // The companion case, and the reason the gate is NOT a bare `&& archive.Archive(...)`.
        // Rooting the manager elsewhere means the source path it guards simply does not exist,
        // which is indistinguishable — through `bool` — from the failure above. It is the
        // BRAND-NEW-FILE shape: there is no outgoing version to preserve, so the commit MUST
        // proceed. Gating on `Archive(...)` alone would break every first-ever file transfer.
        var sourceDir = Path.Combine(_tempDir, "new_source");
        var destDir = Path.Combine(_tempDir, "new_dest");
        var elsewhere = Path.Combine(_tempDir, "elsewhere");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(sourceDir, "brand-new.txt"), "first version");

        var archive = new ArchiveManager(elsewhere, Path.Combine(_tempDir, "arc"),
                                         new DateTime(2026, 7, 19, 14, 30, 52, DateTimeKind.Utc));

        using var pipeStream = new MemoryStream();
        var sender = new FileTransferSender(sourceDir, blockSize: 1024);
        var receiver = new FileTransferReceiver(destDir);
        await sender.SendFileAsync(pipeStream, fileId: 1, relativePath: "brand-new.txt", CancellationToken.None);
        pipeStream.Position = 0;

        Assert.Equal(ArchiveOutcome.NothingToArchive,
            archive.TryArchive("brand-new.txt", ArchiveReason.Overwritten, removeOriginal: false));

        var result = await receiver.ReceiveFileAsync(pipeStream, CancellationToken.None,
            onBeforeCommit: p =>
                archive.TryArchive(p, ArchiveReason.Overwritten, removeOriginal: false)
                    != ArchiveOutcome.Failed);

        Assert.True(result.Success);
        Assert.Equal("first version", File.ReadAllText(Path.Combine(destDir, "brand-new.txt")));
    }
}
