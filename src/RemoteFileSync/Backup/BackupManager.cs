namespace RemoteFileSync.Backup;

public sealed class BackupManager
{
    private readonly string _syncFolder;
    private readonly string _backupFolder;
    private readonly object _lock = new();

    public BackupManager(string syncFolder, string backupFolder)
    {
        _syncFolder = Path.GetFullPath(syncFolder);
        _backupFolder = Path.GetFullPath(backupFolder);
    }

    public bool BackupFile(string relativePath)
    {
        var sourcePath = Path.Combine(_syncFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(sourcePath)) return false;

        lock (_lock)
        {
            var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
            var backupDir = Path.Combine(_backupFolder, dateStr,
                Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? "");
            Directory.CreateDirectory(backupDir);

            var fileName = Path.GetFileNameWithoutExtension(relativePath);
            var ext = Path.GetExtension(relativePath);
            var destPath = Path.Combine(backupDir, Path.GetFileName(relativePath));

            int suffix = 1;
            while (File.Exists(destPath))
            {
                destPath = Path.Combine(backupDir, $"{fileName}_{suffix}{ext}");
                suffix++;
            }

            File.Move(sourcePath, destPath);
            return true;
        }
    }
}
