using System.Net;
using System.Net.Sockets;
using RemoteFileSync.Logging;
using RemoteFileSync.Models;
using RemoteFileSync.Network;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.Integration;

public class DeleteSyncTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _serverDir;
    private readonly string _clientDir;
    private readonly string _stateDir;

    public DeleteSyncTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"rfs_del_e2e_{Guid.NewGuid()}");
        _serverDir = Path.Combine(_testRoot, "server");
        _clientDir = Path.Combine(_testRoot, "client");
        _stateDir = Path.Combine(_testRoot, "state");
        Directory.CreateDirectory(_serverDir);
        Directory.CreateDirectory(_clientDir);
        Directory.CreateDirectory(_stateDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }

    private void CreateFileWithTimestamp(string baseDir, string relativePath, string content, DateTime utcTimestamp)
    {
        var fullPath = Path.Combine(baseDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        File.SetLastWriteTimeUtc(fullPath, utcTimestamp);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task<(int clientResult, int serverResult)> RunSyncAsync(int port, bool bidirectional, bool deleteEnabled, SyncStateManager? stateManager = null)
    {
        var serverOpts = new SyncOptions { IsServer = true, Port = port, Folder = _serverDir, DeleteEnabled = deleteEnabled };
        var clientOpts = new SyncOptions { IsServer = false, Host = "127.0.0.1", Port = port, Folder = _clientDir, Bidirectional = bidirectional, DeleteEnabled = deleteEnabled };

        using var serverLogger = new SyncLogger(false, null);
        using var clientLogger = new SyncLogger(false, null);

        var server = new SyncServer(serverOpts, serverLogger);
        var client = new SyncClient(clientOpts, clientLogger, stateManager);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(500);
        var clientResult = await client.RunAsync(cts.Token);
        var serverResult = await serverTask;
        return (clientResult, serverResult);
    }

    [Fact]
    public async Task DeleteSync_FirstRun_NoState_AdditiveOnly()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        CreateFileWithTimestamp(_clientDir, "client-file.txt", "from client", ts);
        CreateFileWithTimestamp(_serverDir, "server-file.txt", "from server", ts);

        int port = GetFreePort();
        var stateManager = new SyncStateManager(_stateDir);

        var (clientResult, serverResult) = await RunSyncAsync(port, bidirectional: true, deleteEnabled: true, stateManager);

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        Assert.True(File.Exists(Path.Combine(_serverDir, "client-file.txt")));
        Assert.True(File.Exists(Path.Combine(_clientDir, "server-file.txt")));
        var statePath = stateManager.GetStatePath(_clientDir, "127.0.0.1", port);
        Assert.True(File.Exists(statePath));
    }

    [Fact]
    public async Task DeleteSync_Case1_PropagatesDeletion()
    {
        var beforeSync = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var lastSync = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);

        CreateFileWithTimestamp(_serverDir, "to-delete.txt", "will be deleted", beforeSync);

        int port = GetFreePort();
        var stateManager = new SyncStateManager(_stateDir);
        var stateManifest = new FileManifest();
        stateManifest.Add(new FileEntry("to-delete.txt", 15, beforeSync));
        stateManager.SaveState(_clientDir, "127.0.0.1", port, stateManifest, lastSync);

        var (clientResult, serverResult) = await RunSyncAsync(port, bidirectional: true, deleteEnabled: true, stateManager);

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        Assert.False(File.Exists(Path.Combine(_serverDir, "to-delete.txt")));
        var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
        Assert.True(File.Exists(Path.Combine(_serverDir, dateStr, "to-delete.txt")));
    }

    [Fact]
    public async Task DeleteSync_Case2_RestoresModifiedFile()
    {
        var beforeSync = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var lastSync = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
        var afterSync = new DateTime(2026, 3, 27, 8, 0, 0, DateTimeKind.Utc);

        CreateFileWithTimestamp(_serverDir, "modified.txt", "modified content", afterSync);

        int port = GetFreePort();
        var stateManager = new SyncStateManager(_stateDir);
        var stateManifest = new FileManifest();
        stateManifest.Add(new FileEntry("modified.txt", 16, beforeSync));
        stateManager.SaveState(_clientDir, "127.0.0.1", port, stateManifest, lastSync);

        var (clientResult, serverResult) = await RunSyncAsync(port, bidirectional: true, deleteEnabled: true, stateManager);

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        Assert.True(File.Exists(Path.Combine(_serverDir, "modified.txt")));
        Assert.True(File.Exists(Path.Combine(_clientDir, "modified.txt")));
        Assert.Equal("modified content", File.ReadAllText(Path.Combine(_clientDir, "modified.txt")));
    }

    [Fact]
    public async Task DeleteSync_BidiSymmetric()
    {
        var beforeSync = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var lastSync = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);

        CreateFileWithTimestamp(_serverDir, "client-deleted.txt", "from server", beforeSync);
        CreateFileWithTimestamp(_clientDir, "server-deleted.txt", "from client", beforeSync);

        int port = GetFreePort();
        var stateManager = new SyncStateManager(_stateDir);
        var stateManifest = new FileManifest();
        stateManifest.Add(new FileEntry("client-deleted.txt", 11, beforeSync));
        stateManifest.Add(new FileEntry("server-deleted.txt", 11, beforeSync));
        stateManager.SaveState(_clientDir, "127.0.0.1", port, stateManifest, lastSync);

        var (clientResult, serverResult) = await RunSyncAsync(port, bidirectional: true, deleteEnabled: true, stateManager);

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        // Both files should be deleted from their respective sides
        Assert.False(File.Exists(Path.Combine(_serverDir, "client-deleted.txt")));
        Assert.False(File.Exists(Path.Combine(_clientDir, "server-deleted.txt")));
        // Both files should be backed up before deletion
        var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
        Assert.True(File.Exists(Path.Combine(_serverDir, dateStr, "client-deleted.txt")));
        Assert.True(File.Exists(Path.Combine(_clientDir, dateStr, "server-deleted.txt")));
    }

    [Fact]
    public async Task DeleteSync_UniDirectional_ServerDeletionIgnored()
    {
        // In unidirectional mode, a server-side deletion is silently ignored:
        // the file is neither re-pushed nor deleted on the client.
        var beforeSync = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
        var lastSync = new DateTime(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);

        // file.txt exists only on client (server deleted it since last sync)
        CreateFileWithTimestamp(_clientDir, "file.txt", "still here", beforeSync);

        int port = GetFreePort();
        var stateManager = new SyncStateManager(_stateDir);
        var stateManifest = new FileManifest();
        stateManifest.Add(new FileEntry("file.txt", 10, beforeSync));
        stateManager.SaveState(_clientDir, "127.0.0.1", port, stateManifest, lastSync);

        var (clientResult, serverResult) = await RunSyncAsync(port, bidirectional: false, deleteEnabled: true, stateManager);

        Assert.Equal(0, clientResult);
        Assert.Equal(0, serverResult);
        // Client retains its file (not deleted locally)
        Assert.True(File.Exists(Path.Combine(_clientDir, "file.txt")));
        // Server deletion is silently ignored in unidirectional mode — file is NOT re-pushed
        Assert.False(File.Exists(Path.Combine(_serverDir, "file.txt")));
    }

    [Fact]
    public async Task DeleteSync_SecondRun_DetectsDeletions()
    {
        var ts = new DateTime(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);

        CreateFileWithTimestamp(_clientDir, "keep.txt", "keep this", ts);
        CreateFileWithTimestamp(_serverDir, "keep.txt", "keep this", ts);
        CreateFileWithTimestamp(_clientDir, "will-delete.txt", "will be deleted", ts);
        CreateFileWithTimestamp(_serverDir, "will-delete.txt", "will be deleted", ts);

        int port = GetFreePort();
        var stateManager = new SyncStateManager(_stateDir);

        var (r1c, r1s) = await RunSyncAsync(port, bidirectional: true, deleteEnabled: true, stateManager);
        Assert.Equal(0, r1c);
        Assert.Equal(0, r1s);

        File.Delete(Path.Combine(_clientDir, "will-delete.txt"));

        // Reuse the same port so the state key (localFolder + host + port) matches the first run.
        var (r2c, r2s) = await RunSyncAsync(port, bidirectional: true, deleteEnabled: true, stateManager);
        Assert.Equal(0, r2c);
        Assert.Equal(0, r2s);

        Assert.False(File.Exists(Path.Combine(_serverDir, "will-delete.txt")));
        Assert.True(File.Exists(Path.Combine(_clientDir, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(_serverDir, "keep.txt")));
    }
}
