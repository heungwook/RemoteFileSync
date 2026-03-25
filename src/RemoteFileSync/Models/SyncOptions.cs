namespace RemoteFileSync.Models;

public sealed class SyncOptions
{
    public bool IsServer { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 15782;
    public string Folder { get; set; } = string.Empty;
    public bool Bidirectional { get; set; }
    public string? BackupFolder { get; set; }
    public List<string> IncludePatterns { get; set; } = new();
    public List<string> ExcludePatterns { get; set; } = new();
    public int BlockSize { get; set; } = 65536;
    public int MaxThreads { get; set; } = 1;
    public bool Verbose { get; set; }
    public string? LogFile { get; set; }

    public string EffectiveBackupFolder => BackupFolder ?? Folder;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Folder))
            throw new ArgumentException("--folder is required.");
        if (!Directory.Exists(Folder))
            throw new ArgumentException($"Folder does not exist: {Folder}");
        if (!IsServer && string.IsNullOrWhiteSpace(Host))
            throw new ArgumentException("--host is required in client mode.");
        if (Port < 1 || Port > 65535)
            throw new ArgumentException($"--port must be 1-65535, got {Port}.");

        const int minBlock = 4096;
        const int maxBlock = 4 * 1024 * 1024;
        if (BlockSize < minBlock)
        {
            Console.Error.WriteLine($"Warning: --block-size {BlockSize} clamped to minimum {minBlock}.");
            BlockSize = minBlock;
        }
        if (BlockSize > maxBlock)
        {
            Console.Error.WriteLine($"Warning: --block-size {BlockSize} clamped to maximum {maxBlock}.");
            BlockSize = maxBlock;
        }
        if (MaxThreads < 1) MaxThreads = 1;
    }
}
