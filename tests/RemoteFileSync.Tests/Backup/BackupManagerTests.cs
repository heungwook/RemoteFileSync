using RemoteFileSync.Backup;

namespace RemoteFileSync.Tests.Backup;

public class BackupManagerTests : IDisposable
{
    private readonly string _syncDir;
    private readonly string _backupDir;

    public BackupManagerTests()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rfs_bak_{Guid.NewGuid()}");
        _syncDir = Path.Combine(root, "sync");
        _backupDir = Path.Combine(root, "backup");
        Directory.CreateDirectory(_syncDir);
        Directory.CreateDirectory(_backupDir);
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_syncDir)!;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private void CreateSyncFile(string relativePath, string content = "original")
    {
        var full = Path.Combine(_syncDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void BackupFile_MovesToDatedFolder()
    {
        CreateSyncFile("report.docx");
        var mgr = new BackupManager(_syncDir, _backupDir);
        var result = mgr.BackupFile("report.docx");
        Assert.True(result);
        Assert.False(File.Exists(Path.Combine(_syncDir, "report.docx")));
        var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
        Assert.True(File.Exists(Path.Combine(_backupDir, dateStr, "report.docx")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(_backupDir, dateStr, "report.docx")));
    }

    [Fact]
    public void BackupFile_PreservesSubdirectoryStructure()
    {
        CreateSyncFile("docs/sub/file.txt");
        var mgr = new BackupManager(_syncDir, _backupDir);
        mgr.BackupFile("docs/sub/file.txt");
        var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
        Assert.True(File.Exists(Path.Combine(_backupDir, dateStr, "docs", "sub", "file.txt")));
    }

    [Fact]
    public void BackupFile_DuplicateSameDay_AppendsNumericSuffix()
    {
        CreateSyncFile("report.docx", "version1");
        var mgr = new BackupManager(_syncDir, _backupDir);
        mgr.BackupFile("report.docx");
        CreateSyncFile("report.docx", "version2");
        mgr.BackupFile("report.docx");
        var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
        Assert.True(File.Exists(Path.Combine(_backupDir, dateStr, "report.docx")));
        Assert.True(File.Exists(Path.Combine(_backupDir, dateStr, "report_1.docx")));
        Assert.Equal("version1", File.ReadAllText(Path.Combine(_backupDir, dateStr, "report.docx")));
        Assert.Equal("version2", File.ReadAllText(Path.Combine(_backupDir, dateStr, "report_1.docx")));
    }

    [Fact]
    public void BackupFile_FileDoesNotExist_ReturnsFalse()
    {
        var mgr = new BackupManager(_syncDir, _backupDir);
        Assert.False(mgr.BackupFile("nonexistent.txt"));
    }

    [Fact]
    public void BackupFile_ThreadSafe_NoCrash()
    {
        for (int i = 0; i < 10; i++) CreateSyncFile($"file{i}.txt", $"content{i}");
        var mgr = new BackupManager(_syncDir, _backupDir);
        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(() => mgr.BackupFile($"file{i}.txt"))).ToArray();
        Task.WaitAll(tasks);
        Assert.All(tasks, t => Assert.True(t.Result));
    }
}
