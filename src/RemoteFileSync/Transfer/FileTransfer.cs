using RemoteFileSync.Network;

namespace RemoteFileSync.Transfer;

public sealed class FileTransferSender
{
    private readonly string _rootFolder;
    private readonly int _blockSize;

    public FileTransferSender(string rootFolder, int blockSize)
    {
        _rootFolder = Path.GetFullPath(rootFolder);
        _blockSize = blockSize;
    }

    public async Task SendFileAsync(Stream networkStream, short fileId, string relativePath, CancellationToken ct)
    {
        var sourcePath = Path.Combine(_rootFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var sourceInfo = new FileInfo(sourcePath);
        var extension = Path.GetExtension(relativePath);
        bool alreadyCompressed = CompressionHelper.IsAlreadyCompressed(extension);

        string transferSource;
        string? tempCompressed = null;

        if (!alreadyCompressed)
        {
            tempCompressed = Path.Combine(Path.GetTempPath(), $"rfs_gz_{Guid.NewGuid()}.tmp");
            CompressionHelper.CompressFile(sourcePath, tempCompressed);
            transferSource = tempCompressed;
        }
        else
        {
            transferSource = sourcePath;
        }

        try
        {
            var sha256 = CompressionHelper.ComputeSha256(sourcePath);
            var startPayload = ProtocolHandler.SerializeFileStart(fileId, relativePath, sourceInfo.Length, isCompressed: !alreadyCompressed, _blockSize);
            await ProtocolHandler.WriteMessageAsync(networkStream, MessageType.FileStart, startPayload, ct);

            using var fileStream = File.OpenRead(transferSource);
            var buffer = new byte[_blockSize];
            int chunkIndex = 0;
            int bytesRead;
            while ((bytesRead = await fileStream.ReadAsync(buffer, ct)) > 0)
            {
                var chunkData = bytesRead == buffer.Length ? buffer : buffer[..bytesRead];
                var chunkPayload = ProtocolHandler.SerializeFileChunk(fileId, chunkIndex, chunkData);
                await ProtocolHandler.WriteMessageAsync(networkStream, MessageType.FileChunk, chunkPayload, ct);
                chunkIndex++;
            }

            var endPayload = ProtocolHandler.SerializeFileEnd(fileId, sha256);
            await ProtocolHandler.WriteMessageAsync(networkStream, MessageType.FileEnd, endPayload, ct);
        }
        finally
        {
            if (tempCompressed != null && File.Exists(tempCompressed)) File.Delete(tempCompressed);
        }
    }
}

public record FileReceiveResult(bool Success, string RelativePath, string? ErrorMessage = null);

public sealed class FileTransferReceiver
{
    private readonly string _rootFolder;

    public FileTransferReceiver(string rootFolder)
    {
        _rootFolder = Path.GetFullPath(rootFolder);
    }

    public async Task<FileReceiveResult> ReceiveFileAsync(Stream networkStream, CancellationToken ct)
    {
        var (startType, startData) = await ProtocolHandler.ReadMessageAsync(networkStream, ct);
        if (startType != MessageType.FileStart)
            return new FileReceiveResult(false, "", $"Expected FileStart, got {startType}");

        var (fileId, relativePath, originalSize, isCompressed, blockSize) = ProtocolHandler.DeserializeFileStart(startData);
        var tempPath = Path.Combine(Path.GetTempPath(), $"rfs_recv_{Guid.NewGuid()}.tmp");

        try
        {
            using (var tempStream = File.Create(tempPath))
            {
                while (true)
                {
                    var (msgType, msgData) = await ProtocolHandler.ReadMessageAsync(networkStream, ct);
                    if (msgType == MessageType.FileChunk)
                    {
                        var (_, _, chunkData) = ProtocolHandler.DeserializeFileChunk(msgData);
                        await tempStream.WriteAsync(chunkData, ct);
                    }
                    else if (msgType == MessageType.FileEnd)
                    {
                        var (_, expectedHash) = ProtocolHandler.DeserializeFileEnd(msgData);
                        await tempStream.FlushAsync(ct);
                        tempStream.Close();

                        var destPath = Path.Combine(_rootFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

                        if (isCompressed)
                            CompressionHelper.DecompressFile(tempPath, destPath);
                        else
                            File.Copy(tempPath, destPath, overwrite: true);

                        var actualHash = CompressionHelper.ComputeSha256(destPath);
                        if (!actualHash.SequenceEqual(expectedHash))
                        {
                            File.Delete(destPath);
                            return new FileReceiveResult(false, relativePath, "Checksum mismatch");
                        }
                        return new FileReceiveResult(true, relativePath);
                    }
                    else
                    {
                        return new FileReceiveResult(false, relativePath, $"Unexpected message type: {msgType}");
                    }
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
