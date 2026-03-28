using System;
using System.IO;
using System.Linq;
using RemoteFileSync.State;

namespace RemoteFileSync.Tests.State;

public sealed class SyncDatabaseTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SyncDatabase _db;

    public SyncDatabaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _db = new SyncDatabase(Path.Combine(_tempDir, "sync.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // 1. Verify db file exists after construction
    [Fact]
    public void CreateDatabase_InitializesSchema()
    {
        Assert.True(File.Exists(Path.Combine(_tempDir, "sync.db")));
    }

    // 2. Start session, assert id > 0
    [Fact]
    public void StartSession_ReturnsPositiveId()
    {
        var id = _db.StartSession("push", "/local/folder", "remotehost", 8765);
        Assert.True(id > 0);
    }

    // 3. Complete session, verify via GetRecentSessions
    [Fact]
    public void CompleteSession_SetsCompletedUtcAndStats()
    {
        var id = _db.StartSession("push", "/local/folder", "remotehost", 8765);
        _db.CompleteSession(id, transferred: 5, deleted: 2, skipped: 1, exitCode: 0);

        var sessions = _db.GetRecentSessions(limit: 10).ToList();
        Assert.Single(sessions);
        var s = sessions[0];
        Assert.Equal(id, s.Id);
        Assert.Equal(5, s.FilesTransferred);
        Assert.Equal(2, s.FilesDeleted);
        Assert.Equal(1, s.FilesSkipped);
        Assert.Equal(0, s.ExitCode);
        Assert.NotNull(s.CompletedUtc);
    }

    // 4. Mark synced, verify GetFileState returns status='exists' + GetFileHistory has 1 entry
    [Fact]
    public void MarkSynced_CreatesFileAndVersion()
    {
        var sessionId = _db.StartSession("push", "/folder", "host", 8765);
        var now = DateTime.UtcNow;
        _db.MarkSynced("docs/file.txt", fileSize: 1024, lastModified: now, sessionId: sessionId, direction: "push");

        var state = _db.GetFileState("docs/file.txt");
        Assert.NotNull(state);
        Assert.Equal("exists", state!.Status);
        Assert.Equal(1024, state.FileSize);
        Assert.Equal("both", state.Side);

        var history = _db.GetFileHistory("docs/file.txt", limit: 10).ToList();
        Assert.Single(history);
        Assert.Equal("synced", history[0].Action);
    }

    // 5. Mark twice with different data, verify latest state + 2 history entries
    [Fact]
    public void MarkSynced_UpdatesExistingFile()
    {
        var sessionId = _db.StartSession("push", "/folder", "host", 8765);
        var t1 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        _db.MarkSynced("docs/report.txt", fileSize: 500, lastModified: t1, sessionId: sessionId, direction: "push");
        _db.MarkSynced("docs/report.txt", fileSize: 999, lastModified: t2, sessionId: sessionId, direction: "push");

        var state = _db.GetFileState("docs/report.txt");
        Assert.NotNull(state);
        Assert.Equal(999, state!.FileSize);
        Assert.Equal("exists", state.Status);

        var history = _db.GetFileHistory("docs/report.txt", limit: 10).ToList();
        Assert.Equal(2, history.Count);
    }

    // 6. Mark synced then deleted, verify status='deleted' + 2 history entries
    [Fact]
    public void MarkDeleted_SetsStatusDeleted()
    {
        var sessionId = _db.StartSession("push", "/folder", "host", 8765);
        _db.MarkSynced("docs/gone.txt", fileSize: 100, lastModified: DateTime.UtcNow, sessionId: sessionId, direction: "push");
        _db.MarkDeleted("docs/gone.txt", sessionId: sessionId, detail: "removed on server");

        var state = _db.GetFileState("docs/gone.txt");
        Assert.NotNull(state);
        Assert.Equal("deleted", state!.Status);

        var history = _db.GetFileHistory("docs/gone.txt", limit: 10).ToList();
        Assert.Equal(2, history.Count);
    }

    // 7. Mark "Docs/Report.DOCX", query "docs/report.docx", find it
    [Fact]
    public void GetFileState_CaseInsensitive()
    {
        var sessionId = _db.StartSession("push", "/folder", "host", 8765);
        _db.MarkSynced("Docs/Report.DOCX", fileSize: 2048, lastModified: DateTime.UtcNow, sessionId: sessionId, direction: "push");

        var state = _db.GetFileState("docs/report.docx");
        Assert.NotNull(state);
        Assert.Equal("exists", state!.Status);
    }

    // 8. Query nonexistent path, get null
    [Fact]
    public void GetFileState_NotFound_ReturnsNull()
    {
        var state = _db.GetFileState("nonexistent/file.txt");
        Assert.Null(state);
    }

    // 9. 2 files, delete 1, GetDeletedFiles returns only 1
    [Fact]
    public void GetDeletedFiles_ReturnsOnlyDeleted()
    {
        var sessionId = _db.StartSession("push", "/folder", "host", 8765);
        _db.MarkSynced("keep/file.txt", fileSize: 100, lastModified: DateTime.UtcNow, sessionId: sessionId, direction: "push");
        _db.MarkSynced("delete/file.txt", fileSize: 200, lastModified: DateTime.UtcNow, sessionId: sessionId, direction: "push");
        _db.MarkDeleted("delete/file.txt", sessionId: sessionId, detail: null);

        var deleted = _db.GetDeletedFiles().ToList();
        Assert.Single(deleted);
        Assert.Equal("delete/file.txt", deleted[0].Path, ignoreCase: true);
    }

    // 10. 2 files (1 exists, 1 deleted), GetAllTrackedFiles returns 2
    [Fact]
    public void GetAllTrackedFiles_ReturnsAll()
    {
        var sessionId = _db.StartSession("push", "/folder", "host", 8765);
        _db.MarkSynced("file1.txt", fileSize: 100, lastModified: DateTime.UtcNow, sessionId: sessionId, direction: "push");
        _db.MarkSynced("file2.txt", fileSize: 200, lastModified: DateTime.UtcNow, sessionId: sessionId, direction: "push");
        _db.MarkDeleted("file2.txt", sessionId: sessionId, detail: null);

        var all = _db.GetAllTrackedFiles().ToList();
        Assert.Equal(2, all.Count);
    }

    // 11. Mark synced then skipped, verify 2 history entries
    [Fact]
    public void MarkSkipped_CreatesVersionEntry()
    {
        var sessionId = _db.StartSession("push", "/folder", "host", 8765);
        _db.MarkSynced("file.txt", fileSize: 100, lastModified: DateTime.UtcNow, sessionId: sessionId, direction: "push");
        _db.MarkSkipped("file.txt", sessionId: sessionId);

        var history = _db.GetFileHistory("file.txt", limit: 10).ToList();
        Assert.Equal(2, history.Count);
        Assert.Equal("skipped", history[1].Action);

        // Files table should be unchanged
        var state = _db.GetFileState("file.txt");
        Assert.Equal("exists", state!.Status);
    }

    // 12. Mark new, verify status='new'
    [Fact]
    public void MarkNew_SetsStatusNew()
    {
        _db.MarkNew("incoming/newfile.txt", fileSize: 512, lastModified: DateTime.UtcNow, side: "remote");

        var state = _db.GetFileState("incoming/newfile.txt");
        Assert.NotNull(state);
        Assert.Equal("new", state!.Status);
        Assert.Equal("remote", state.Side);
    }

    // 13. Mark 2 files synced, leave 3rd untracked, complete session with exit 1, verify 2 have state and 3rd doesn't
    [Fact]
    public void PartialSync_PreservesPerFileState()
    {
        var sessionId = _db.StartSession("push", "/folder", "host", 8765);
        _db.MarkSynced("a.txt", fileSize: 1, lastModified: DateTime.UtcNow, sessionId: sessionId, direction: "push");
        _db.MarkSynced("b.txt", fileSize: 2, lastModified: DateTime.UtcNow, sessionId: sessionId, direction: "push");
        _db.CompleteSession(sessionId, transferred: 2, deleted: 0, skipped: 0, exitCode: 1);

        Assert.NotNull(_db.GetFileState("a.txt"));
        Assert.NotNull(_db.GetFileState("b.txt"));
        Assert.Null(_db.GetFileState("c.txt"));
    }

    // 14. Same folder with different case → same path
    [Fact]
    public void GetDbPath_DeterministicAndCaseInsensitive()
    {
        var path1 = SyncDatabase.GetDbPath("/base", "C:/MyFolder", "MyHost", 8765);
        var path2 = SyncDatabase.GetDbPath("/base", "c:/myfolder", "myhost", 8765);
        Assert.Equal(path1, path2);
    }

    // Issue 10: MarkDeleted on a path that was never tracked must not create phantom history
    [Fact]
    public void MarkDeleted_NonexistentPath_NoPhantomHistory()
    {
        var s = _db.StartSession("bidi", @"C:\Sync", "localhost", 15782);
        _db.MarkDeleted("nonexistent.txt", s, "should not create history");
        var history = _db.GetFileHistory("nonexistent.txt");
        Assert.Empty(history);
    }

    // Issue 11: MarkNew must write a 'created' version-history entry
    [Fact]
    public void MarkNew_CreatesVersionEntry()
    {
        _db.MarkNew("discovered.txt", 500, DateTime.UtcNow, "client");
        var history = _db.GetFileHistory("discovered.txt").ToList();
        Assert.Single(history);
        Assert.Equal("created", history[0].Action);
    }

    // 15. Mark synced → deleted → synced again, verify status='exists' + 3 history entries
    [Fact]
    public void PreviouslyDeleted_Reappeared_CanBeMarkedExists()
    {
        var sessionId = _db.StartSession("push", "/folder", "host", 8765);
        var now = DateTime.UtcNow;

        _db.MarkSynced("revived.txt", fileSize: 100, lastModified: now, sessionId: sessionId, direction: "push");
        _db.MarkDeleted("revived.txt", sessionId: sessionId, detail: "gone");
        _db.MarkSynced("revived.txt", fileSize: 200, lastModified: now.AddHours(1), sessionId: sessionId, direction: "pull");

        var state = _db.GetFileState("revived.txt");
        Assert.NotNull(state);
        Assert.Equal("exists", state!.Status);
        Assert.Equal(200, state.FileSize);

        var history = _db.GetFileHistory("revived.txt", limit: 10).ToList();
        Assert.Equal(3, history.Count);
    }
}
