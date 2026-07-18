using RemoteFileSync.Security;

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

    /// <summary>
    /// Copies the file into the dated backup tree, leaving the original in place.
    /// Use before overwriting a file with an incoming transfer.
    /// </summary>
    public bool BackupFile(string relativePath) => Snapshot(relativePath, removeOriginal: false);

    /// <summary>
    /// Copies the file into the dated backup tree, then deletes the original.
    /// Use when propagating a deletion.
    /// </summary>
    public bool BackupAndRemove(string relativePath) => Snapshot(relativePath, removeOriginal: true);

    private bool Snapshot(string relativePath, bool removeOriginal)
    {
        // relativePath can arrive from the network (deletion propagation), so it must be
        // contained before it reaches the filesystem.
        if (!PathGuard.TryResolveWithinRoot(_syncFolder, relativePath, out var sourcePath)) return false;
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

            // Copy first: if the copy fails we must not have destroyed the original.
            File.Copy(sourcePath, destPath, overwrite: false);
            if (removeOriginal) File.Delete(sourcePath);
            return true;
        }
    }
}
