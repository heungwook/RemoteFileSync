using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RemoteFileSync.Logging;
using RemoteFileSync.Progress;
using RemoteFileSync.State;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public sealed class ReviewReportTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly string _logPath;

    public ReviewReportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rfs_review_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "sync.db");
        _logPath = Path.Combine(_tempDir, "sync.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly DateTime ClientMtime = new(2026, 7, 20, 14, 30, 52, DateTimeKind.Utc);
    private static readonly DateTime ServerMtime = new(2026, 7, 20, 14, 31, 10, DateTimeKind.Utc);
    private static readonly DateTime ResClientMtime = new(2026, 7, 20, 9, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime ResServerMtime = new(2026, 7, 19, 17, 0, 0, DateTimeKind.Utc);

    private static string ConflictDetailText() => new ConflictDetail(
        ClientSize: 2100000, ClientMtimeTicks: ClientMtime.Ticks,
        ServerSize: 2050112, ServerMtimeTicks: ServerMtime.Ticks,
        RenamedTo: "docs/report.conflict-20260720-143052-server.docx").Encode();

    private static string ResurrectionDetailText() => new ConflictDetail(
        ClientSize: 1024, ClientMtimeTicks: ResClientMtime.Ticks,
        ServerSize: 900, ServerMtimeTicks: ResServerMtime.Ticks,
        RenamedTo: null).Encode();

    private static ConflictEntry Conflict(string path) =>
        new(path, ConflictDetailText(), new DateTime(2026, 7, 20, 14, 31, 11, DateTimeKind.Utc));

    private static ConflictEntry Resurrection(string path) =>
        new(path, ResurrectionDetailText(), new DateTime(2026, 7, 20, 9, 16, 0, DateTimeKind.Utc));

    // Client copy newer -> client kept, the SERVER copy is the one overwritten (on the server).
    private static readonly DateTime OwClientKeptMtime = new(2026, 7, 20, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OwServerReplacedMtime = new(2026, 7, 20, 7, 30, 0, DateTimeKind.Utc);
    private static OverwriteInfo OverwriteClientKept(string path) =>
        new(path, KeptClientCopy: true,
            KeptSize: 5000, KeptMtimeTicks: OwClientKeptMtime.Ticks,
            ReplacedSize: 4000, ReplacedMtimeTicks: OwServerReplacedMtime.Ticks);

    // Server copy newer -> server kept, the CLIENT copy is overwritten and archived LOCALLY.
    private static readonly DateTime OwServerKeptMtime = new(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OwClientReplacedMtime = new(2026, 7, 20, 9, 0, 0, DateTimeKind.Utc);
    private static OverwriteInfo OverwriteServerKept(string path) =>
        new(path, KeptClientCopy: false,
            KeptSize: 7000, KeptMtimeTicks: OwServerKeptMtime.Ticks,
            ReplacedSize: 6000, ReplacedMtimeTicks: OwClientReplacedMtime.Ticks);

    // ---- BuildLines ----

    [Fact]
    public void BuildLines_NothingToReview_ReturnsEmpty()
    {
        Assert.Empty(ReviewReport.BuildLines(Array.Empty<ConflictEntry>(), Array.Empty<ConflictEntry>()));
    }

    [Fact]
    public void BuildLines_Conflict_ShowsBothSidesAndTheRenamedCopy()
    {
        var text = string.Join("\n", ReviewReport.BuildLines(
            new[] { Conflict("docs/report.docx") }, Array.Empty<ConflictEntry>()));

        Assert.Contains("[CONFLICT] docs/report.docx", text);
        Assert.Contains("client: 2100000 bytes  2026-07-20 14:30:52Z", text);
        Assert.Contains("server: 2050112 bytes  2026-07-20 14:31:10Z", text);
        Assert.Contains("kept as: docs/report.conflict-20260720-143052-server.docx", text);
        Assert.Contains("both copies kept", text);
    }

    [Fact]
    public void BuildLines_Resurrection_ShowsBothSidesAndNoRenameLine()
    {
        var text = string.Join("\n", ReviewReport.BuildLines(
            Array.Empty<ConflictEntry>(), new[] { Resurrection("notes/todo.txt") }));

        Assert.Contains("[RESURRECTED] notes/todo.txt", text);
        Assert.Contains("client: 1024 bytes  2026-07-20 09:15:00Z", text);
        Assert.Contains("server: 900 bytes  2026-07-19 17:00:00Z", text);
        Assert.Contains("kept: modified after the peer deleted it", text);
        Assert.DoesNotContain("kept as:", text);
    }

    [Fact]
    public void BuildLines_HeaderCountsBothKinds()
    {
        var lines = ReviewReport.BuildLines(
            new[] { Conflict("a.docx"), Conflict("b.docx") },
            new[] { Resurrection("c.txt") });

        Assert.Equal("Review: 3 item(s) need attention", lines[0]);
    }

    [Fact]
    public void BuildLines_HeaderCountsAllThreeKinds()
    {
        var lines = ReviewReport.BuildLines(
            new[] { Conflict("a.docx") },
            new[] { Resurrection("b.txt") },
            new[] { OverwriteClientKept("c.bin"), OverwriteServerKept("d.md") });

        Assert.Equal("Review: 4 item(s) need attention", lines[0]);
    }

    [Fact]
    public void BuildLines_Overwrite_ClientKept_ShowsServerReplacedOnTheServer()
    {
        // The client copy won newest-wins, so the SERVER copy is the loser and was archived on
        // the server — the client cannot name that path, so the convention is printed, never a
        // wrong local path (team-lead's instruction: a wrong path is worse than a folder).
        var text = string.Join("\n", ReviewReport.BuildLines(
            Array.Empty<ConflictEntry>(), Array.Empty<ConflictEntry>(),
            new[] { OverwriteClientKept("data/big.bin") }));

        Assert.Contains("[FIRST-RUN OVERWRITE] data/big.bin", text);
        Assert.Contains("client: 5000 bytes  2026-07-20 08:00:00Z", text);
        Assert.Contains("server: 4000 bytes  2026-07-20 07:30:00Z", text);
        Assert.Contains("kept: client copy", text);
        Assert.Contains("replaced: server copy", text);
        Assert.Contains("overwritten/data/big.bin", text);
    }

    [Fact]
    public void BuildLines_Overwrite_ServerKept_ShowsClientReplacedAtDerivedLocalArchivePath()
    {
        // The server copy won, so the CLIENT copy was overwritten and archived locally by the
        // receive loop's pre-overwrite hook. That path is reliably derivable from the session
        // archive root, so the report shows it exactly.
        var archiveRoot = Path.Combine(_tempDir, "arch", "20260720-120000");
        var expected = Path.Combine(archiveRoot, "overwritten",
            "docs/spec.md".Replace('/', Path.DirectorySeparatorChar));

        var text = string.Join("\n", ReviewReport.BuildLines(
            Array.Empty<ConflictEntry>(), Array.Empty<ConflictEntry>(),
            new[] { OverwriteServerKept("docs/spec.md") },
            archiveSessionRoot: archiveRoot));

        Assert.Contains("[FIRST-RUN OVERWRITE] docs/spec.md", text);
        Assert.Contains("client: 6000 bytes  2026-07-20 09:00:00Z", text);
        Assert.Contains("server: 7000 bytes  2026-07-20 10:00:00Z", text);
        Assert.Contains("kept: server copy", text);
        Assert.Contains($"replaced: client copy archived at {expected}", text);
    }

    [Fact]
    public void BuildLines_Overwrite_ServerKept_NoArchiveRoot_FallsBackToConvention()
    {
        // Without a session archive root the exact path cannot be built, so the convention is
        // printed rather than a half-formed path.
        var text = string.Join("\n", ReviewReport.BuildLines(
            Array.Empty<ConflictEntry>(), Array.Empty<ConflictEntry>(),
            new[] { OverwriteServerKept("docs/spec.md") }));

        Assert.Contains("[FIRST-RUN OVERWRITE] docs/spec.md", text);
        Assert.Contains("overwritten/docs/spec.md", text);
    }

    [Fact]
    public void BuildLines_UndecodableDetail_StillListsTheFileAndPrintsTheRawText()
    {
        // A row written by a build that predates ConflictDetail decodes to null. Dropping it
        // would hide the exact case the review exists to surface, so it is listed verbatim.
        var entry = new ConflictEntry("legacy.docx", "both sides changed; kept both copies",
            new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc));

        var text = string.Join("\n", ReviewReport.BuildLines(new[] { entry }, Array.Empty<ConflictEntry>()));

        Assert.Contains("[CONFLICT] legacy.docx", text);
        Assert.Contains("detail: both sides changed; kept both copies", text);
        Assert.Contains("both copies kept", text);
    }

    [Fact]
    public void BuildLines_OutOfRangeTicks_PrintUnknownInsteadOfThrowing()
    {
        // long.MaxValue is not a valid DateTime tick count. new DateTime(ticks) would throw
        // ArgumentOutOfRangeException and take down the whole review over one corrupt row.
        var detail = new ConflictDetail(
            ClientSize: 5, ClientMtimeTicks: long.MaxValue,
            ServerSize: 6, ServerMtimeTicks: -1,
            RenamedTo: null).Encode();
        var entry = new ConflictEntry("corrupt.bin", detail, new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc));

        var text = string.Join("\n", ReviewReport.BuildLines(new[] { entry }, Array.Empty<ConflictEntry>()));

        Assert.Contains("[CONFLICT] corrupt.bin", text);
        Assert.Contains("client: 5 bytes  unknown", text);
        Assert.Contains("server: 6 bytes  unknown", text);
    }

    // ---- Emit ----

    [Fact]
    public void Emit_ReadsTheTwoActionsThroughTheirOwnReaders()
    {
        // LogConflict and LogResurrection are separate writers over the action column; nothing
        // inspects the detail string. This pins that the report never conflates the two.
        using var db = new SyncDatabase(_dbPath);
        var sessionId = db.StartSession("two-way+delete", _tempDir, "localhost", 15782);
        db.LogConflict("docs/report.docx", sessionId, ConflictDetailText());
        db.LogResurrection("notes/todo.txt", sessionId, ResurrectionDetailText());

        Assert.Equal("docs/report.docx", Assert.Single(db.GetSessionConflicts(sessionId)).Path);
        Assert.Equal("notes/todo.txt", Assert.Single(db.GetSessionResurrections(sessionId)).Path);
    }

    [Fact]
    public void Emit_LogsBothSectionsAndEmitsOneJsonEventPerItem()
    {
        using var sw = new StringWriter();
        var progress = new JsonProgressWriter(sw);

        using (var db = new SyncDatabase(_dbPath))
        using (var logger = new SyncLogger(verbose: false, logFile: _logPath, suppressConsole: true))
        {
            var sessionId = db.StartSession("two-way+delete", _tempDir, "localhost", 15782);
            db.LogConflict("docs/report.docx", sessionId, ConflictDetailText());
            db.LogResurrection("notes/todo.txt", sessionId, ResurrectionDetailText());

            ReviewReport.Emit(db, sessionId, logger, progress);
        }

        var log = File.ReadAllText(_logPath);
        Assert.Contains("Review: 2 item(s) need attention", log);
        Assert.Contains("[CONFLICT] docs/report.docx", log);
        Assert.Contains("client: 2100000 bytes  2026-07-20 14:30:52Z", log);
        Assert.Contains("server: 2050112 bytes  2026-07-20 14:31:10Z", log);
        Assert.Contains("[RESURRECTED] notes/todo.txt", log);
        Assert.Contains("client: 1024 bytes  2026-07-20 09:15:00Z", log);
        Assert.Contains("server: 900 bytes  2026-07-19 17:00:00Z", log);

        var events = sw.ToString().Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, events.Length);
        var first = JsonDocument.Parse(events[0]).RootElement;
        Assert.Equal("review", first.GetProperty("event").GetString());
        Assert.Equal("conflict", first.GetProperty("kind").GetString());
        Assert.Equal("docs/report.docx", first.GetProperty("path").GetString());
        Assert.Equal(2100000, first.GetProperty("client_size").GetInt64());
        Assert.Equal("docs/report.conflict-20260720-143052-server.docx",
            first.GetProperty("renamed_to").GetString());
        var second = JsonDocument.Parse(events[1]).RootElement;
        Assert.Equal("resurrection", second.GetProperty("kind").GetString());
        Assert.Equal("notes/todo.txt", second.GetProperty("path").GetString());
        Assert.Equal(1024, second.GetProperty("client_size").GetInt64());
        Assert.False(second.TryGetProperty("renamed_to", out _));
    }

    [Fact]
    public void Emit_UndecodableDetail_SendsTheSentinelInsteadOfAFabricatedSize()
    {
        using var sw = new StringWriter();
        var progress = new JsonProgressWriter(sw);

        using (var db = new SyncDatabase(_dbPath))
        using (var logger = new SyncLogger(verbose: false, logFile: _logPath, suppressConsole: true))
        {
            var sessionId = db.StartSession("two-way+delete", _tempDir, "localhost", 15782);
            db.LogConflict("legacy.docx", sessionId, "both sides changed; kept both copies");
            ReviewReport.Emit(db, sessionId, logger, progress);
        }

        var evt = JsonDocument.Parse(sw.ToString().Trim()).RootElement;
        Assert.Equal("legacy.docx", evt.GetProperty("path").GetString());
        Assert.Equal(-1, evt.GetProperty("client_size").GetInt64());
        Assert.Equal("", evt.GetProperty("client_mtime").GetString());
        Assert.Equal(-1, evt.GetProperty("server_size").GetInt64());
        Assert.False(evt.TryGetProperty("renamed_to", out _));
    }

    [Fact]
    public void Emit_OverwritesInMemory_LoggedAlongsideDbRowsAndEmittedAsOverwriteEvents()
    {
        // Two sources for one report: conflicts/resurrections read from the DB, overwrites passed
        // in from planResult. Both reflect the same session. The overwrite event carries both
        // sides' real sizes and omits renamed_to (an overwrite renames nothing).
        using var sw = new StringWriter();
        var progress = new JsonProgressWriter(sw);

        using (var db = new SyncDatabase(_dbPath))
        using (var logger = new SyncLogger(verbose: false, logFile: _logPath, suppressConsole: true))
        {
            var sessionId = db.StartSession("two-way+delete", _tempDir, "localhost", 15782);
            db.LogConflict("docs/report.docx", sessionId, ConflictDetailText());
            ReviewReport.Emit(db, sessionId, logger, progress,
                new[] { OverwriteServerKept("docs/spec.md") },
                archiveSessionRoot: Path.Combine(_tempDir, "arch", "20260720-120000"));
        }

        var log = File.ReadAllText(_logPath);
        Assert.Contains("Review: 2 item(s) need attention", log);
        Assert.Contains("[CONFLICT] docs/report.docx", log);
        Assert.Contains("[FIRST-RUN OVERWRITE] docs/spec.md", log);

        var events = sw.ToString().Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, events.Length);
        Assert.Equal("conflict", JsonDocument.Parse(events[0]).RootElement.GetProperty("kind").GetString());
        var ow = JsonDocument.Parse(events[1]).RootElement;
        Assert.Equal("review", ow.GetProperty("event").GetString());
        Assert.Equal("overwrite", ow.GetProperty("kind").GetString());
        Assert.Equal("docs/spec.md", ow.GetProperty("path").GetString());
        Assert.Equal(6000, ow.GetProperty("client_size").GetInt64());
        Assert.Equal(7000, ow.GetProperty("server_size").GetInt64());
        Assert.False(ow.TryGetProperty("renamed_to", out _));
    }

    [Fact]
    public void Emit_OverwritesWithNullDb_StillReported()
    {
        // A TwoWay run without --delete has _db == null, yet the no-ancestor path can still
        // overwrite a loser. The old guard returned before writing a byte on a null db; the
        // report must instead still surface the overwrite that actually happened.
        using var sw = new StringWriter();
        var progress = new JsonProgressWriter(sw);
        using (var logger = new SyncLogger(verbose: false, logFile: _logPath, suppressConsole: true))
        {
            ReviewReport.Emit(null, 0, logger, progress,
                new[] { OverwriteClientKept("data/big.bin") });
        }

        Assert.Contains("[FIRST-RUN OVERWRITE] data/big.bin", File.ReadAllText(_logPath));
        var ow = JsonDocument.Parse(sw.ToString().Trim()).RootElement;
        Assert.Equal("overwrite", ow.GetProperty("kind").GetString());
        Assert.Equal("data/big.bin", ow.GetProperty("path").GetString());
    }

    [Fact]
    public void Emit_CleanSession_PrintsAndEmitsNothing()
    {
        // A quiet sync must stay quiet. An empty "Review" header on every run trains the
        // operator to skip the section on the run that matters.
        using var sw = new StringWriter();
        var progress = new JsonProgressWriter(sw);

        using (var db = new SyncDatabase(_dbPath))
        using (var logger = new SyncLogger(verbose: false, logFile: _logPath, suppressConsole: true))
        {
            var sessionId = db.StartSession("two-way+delete", _tempDir, "localhost", 15782);
            ReviewReport.Emit(db, sessionId, logger, progress);
        }

        Assert.DoesNotContain("Review:", File.ReadAllText(_logPath));
        Assert.Equal("", sw.ToString());
    }

    [Fact]
    public void Emit_NullDatabaseOrNoSession_DoesNothing()
    {
        // SyncClient runs with _db == null on the binary-state fallback path, and leaves
        // sessionId at 0 whenever --delete is off (SyncClient.cs:116-122) — in both cases
        // nothing was ever logged, so there is nothing to read back.
        using var sw = new StringWriter();
        var progress = new JsonProgressWriter(sw);
        using var logger = new SyncLogger(verbose: false, logFile: _logPath, suppressConsole: true);

        ReviewReport.Emit(null, 1, logger, progress);
        using (var db = new SyncDatabase(_dbPath))
            ReviewReport.Emit(db, 0, logger, progress);

        Assert.Equal("", sw.ToString());
    }
}
