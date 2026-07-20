using RemoteFileSync.Network;
using RemoteFileSync.Security;

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

    /// <summary>
    /// <paramref name="onBytesSent"/> reports cumulative bytes as chunks go out, so callers
    /// can emit per-file progress.
    /// </summary>
    public async Task SendFileAsync(Stream networkStream, short fileId, string relativePath,
                                    CancellationToken ct, Action<long>? onBytesSent = null)
    {
        var sourcePath = PathGuard.ResolveWithinRoot(_rootFolder, relativePath);
        var sourceInfo = new FileInfo(sourcePath);
        var extension = Path.GetExtension(relativePath);
        bool alreadyCompressed = CompressionHelper.IsAlreadyCompressed(extension);

        string transferSource;
        string? tempCompressed = null;

        try
        {
            // Inside the try: CompressFile can throw (source locked, disk full), and the
            // finally below is what deletes the partial temp file.
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

            var sha256 = CompressionHelper.ComputeSha256(sourcePath);
            var startPayload = ProtocolHandler.SerializeFileStart(
                fileId, relativePath, sourceInfo.Length,
                isCompressed: !alreadyCompressed, _blockSize,
                lastModifiedUtcTicks: sourceInfo.LastWriteTimeUtc.Ticks);
            await ProtocolHandler.WriteMessageAsync(networkStream, MessageType.FileStart, startPayload, ct);

            using var fileStream = File.OpenRead(transferSource);
            var buffer = new byte[_blockSize];
            int chunkIndex = 0;
            int bytesRead;
            long totalSent = 0;
            while ((bytesRead = await fileStream.ReadAsync(buffer, ct)) > 0)
            {
                var chunkData = bytesRead == buffer.Length ? buffer : buffer[..bytesRead];
                var chunkPayload = ProtocolHandler.SerializeFileChunk(fileId, chunkIndex, chunkData);
                await ProtocolHandler.WriteMessageAsync(networkStream, MessageType.FileChunk, chunkPayload, ct);
                chunkIndex++;
                totalSent += bytesRead;
                onBytesSent?.Invoke(totalSent);
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
    /// <summary>
    /// Marker for in-progress receives. Staging files live beside their destination so the
    /// commit is a same-volume rename; FileScanner excludes them from every manifest and
    /// sweeps stale ones, so a crash cannot leak them into the synced set.
    /// </summary>
    public const string StagingSuffix = ".rfs-part-";

    private readonly string _rootFolder;

    public FileTransferReceiver(string rootFolder)
    {
        _rootFolder = Path.GetFullPath(rootFolder);
    }

    public Task<FileReceiveResult> ReceiveFileAsync(Stream networkStream, CancellationToken ct)
        => ReceiveFileAsync(networkStream, ct, onBeforeCommit: null);

    /// <summary>
    /// <paramref name="onBeforeCommit"/> receives the verified file's relative path immediately
    /// before the destination is replaced, so callers can snapshot the outgoing version. It is
    /// driven by the path actually received, not by plan order.
    /// <para>
    /// Its return value is a COMMIT GATE, not advisory: false means "the outgoing version is
    /// not protected" and the destination is left untouched. A hook that had nothing to
    /// snapshot (brand-new file) must return true. The result was previously discarded, so an
    /// archive that failed silently — PathGuard fails closed on transient IO — still let the
    /// overwrite destroy the only copy of the previous version.
    /// </para>
    /// </summary>
    public async Task<FileReceiveResult> ReceiveFileAsync(Stream networkStream, CancellationToken ct,
                                                          Func<string, bool>? onBeforeCommit)
    {
        var (startType, startData) = await ProtocolHandler.ReadMessageAsync(networkStream, ct);
        if (startType != MessageType.FileStart)
            return new FileReceiveResult(false, "", $"Expected FileStart, got {startType}");

        var (fileId, relativePath, originalSize, isCompressed, blockSize, lastModifiedUtcTicks) =
            ProtocolHandler.DeserializeFileStart(startData);

        if (!PathGuard.TryResolveWithinRoot(_rootFolder, relativePath, out var destPath))
            return new FileReceiveResult(false, relativePath, "Rejected path outside sync root");
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        // Staging file lives beside the destination so the commit is a same-volume rename.
        var stagingPath = destPath + $"{StagingSuffix}{Guid.NewGuid():N}";
        // Only needed for the compressed path: gzip must be fully received before it expands.
        string? gzPath = isCompressed
            ? Path.Combine(Path.GetTempPath(), $"rfs_recv_{Guid.NewGuid():N}.tmp")
            : null;

        try
        {
            // Uncompressed payloads are written straight into staging — no %TEMP% round trip.
            var sinkPath = gzPath ?? stagingPath;
            using (var sink = File.Create(sinkPath))
            {
                while (true)
                {
                    var (msgType, msgData) = await ProtocolHandler.ReadMessageAsync(networkStream, ct);
                    if (msgType == MessageType.FileChunk)
                    {
                        var (_, _, chunkData) = ProtocolHandler.DeserializeFileChunk(msgData);
                        await sink.WriteAsync(chunkData, ct);
                    }
                    else if (msgType == MessageType.FileEnd)
                    {
                        var (_, expectedHash) = ProtocolHandler.DeserializeFileEnd(msgData);
                        await sink.FlushAsync(ct);
                        sink.Flush(flushToDisk: true);   // FlushAsync only reaches the OS cache
                        sink.Close();

                        if (gzPath != null)
                            CompressionHelper.DecompressFile(gzPath, stagingPath);

                        var actualHash = CompressionHelper.ComputeSha256(stagingPath);
                        if (!actualHash.SequenceEqual(expectedHash))
                        {
                            // Destination is still the previous good file. Nothing is destroyed.
                            return new FileReceiveResult(false, relativePath, "Checksum mismatch");
                        }

                        // Preserve the source timestamp so the file compares equal on the next
                        // sync. A hostile peer can send arbitrary ticks, so clamp to valid range.
                        // File.Move preserves the stamp, so set it before committing.
                        var ticks = Math.Clamp(lastModifiedUtcTicks, 0, DateTime.MaxValue.Ticks);
                        File.SetLastWriteTimeUtc(stagingPath, new DateTime(ticks, DateTimeKind.Utc));

                        // The hook is a gate. It returns false only when it was asked to
                        // preserve an existing destination and could not; "nothing to preserve"
                        // returns true. Committing anyway would destroy the previous version
                        // with no restore point, which is precisely the failure this phase's
                        // archive exists to prevent — so the destination stays as it is and the
                        // finally block sweeps the staging file. The transfer is retried on the
                        // next sync, when the archive root may well be reachable again.
                        if (onBeforeCommit != null && !onBeforeCommit(relativePath))
                        {
                            return new FileReceiveResult(false, relativePath,
                                "Refusing to overwrite: pre-overwrite archive failed");
                        }

                        CommitWithRetry(stagingPath, destPath);
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
            // A cleanup failure must not replace a successful result with an exception.
            TryDelete(gzPath);
            TryDelete(stagingPath);
        }
    }

    private static void TryDelete(string? path)
    {
        if (path == null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>
    /// Same-volume rename. Retries briefly: replacing an existing file fails with a sharing
    /// violation when the destination is open without FILE_SHARE_DELETE, which is common on
    /// Windows (Office, editors, AV scanners).
    /// </summary>
    private static void CommitWithRetry(string stagingPath, string destPath)
    {
        const int attempts = 5;
        for (int i = 1; ; i++)
        {
            try { File.Move(stagingPath, destPath, overwrite: true); return; }
            catch (IOException) when (i < attempts) { Thread.Sleep(100 * i); }
        }
    }
}
