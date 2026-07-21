using System.Globalization;
using RemoteFileSync.Logging;
using RemoteFileSync.Progress;
using RemoteFileSync.State;

namespace RemoteFileSync.Sync;

/// <summary>
/// The end-of-sync review. Everything the sync could not decide on the operator's behalf — a
/// two-sided conflict where both copies were kept, and a file that survived the peer's deletion
/// because this side had edited it — is listed here, after the summary, so it is the last thing
/// on screen instead of one INF line buried in a thousand.
/// </summary>
public static class ReviewReport
{
    private const string ConflictTag      = "CONFLICT";
    private const string ResurrectionTag  = "RESURRECTED";
    private const string OverwriteTag     = "FIRST-RUN OVERWRITE";
    private const string ConflictNote     = "both copies kept";
    private const string ResurrectionNote = "kept: modified after the peer deleted it";

    // The wire value of ProgressEvent.Kind. Deliberately not the same strings as the log tags:
    // the log is for humans, these are parsed by ExecRFS.
    private const string ConflictKind     = "conflict";
    private const string ResurrectionKind = "resurrection";
    private const string OverwriteKind    = "overwrite";

    // Mirrors ArchiveManager.ReasonFolder(ArchiveReason.Overwritten). The overwritten loser lands
    // at <archiveRoot>/<SessionFolderName>/overwritten/<relative path>; this is the layout the
    // report derives, not a copy of the enum.
    private const string OverwrittenReasonFolder = "overwritten";

    /// <summary>Sentinel size for a row whose detail could not be decoded. The GUI renders this
    /// as "unknown"; a 0 would be indistinguishable from a genuinely empty file.</summary>
    private const long UnknownSize = -1;

    /// <param name="overwrites">
    /// First-run overwrites, passed in from PlanResult rather than read from the database: they
    /// occur on the no-ancestor path, where the database may be null (a TwoWay run without
    /// --delete), so there is no DB row to read them back from and they always travel in memory.
    /// </param>
    /// <param name="archiveSessionRoot">
    /// This run's ArchiveManager.SessionRoot, used to derive the exact local path of an overwritten
    /// client copy. Null (or a server-side loser) falls back to the reason-folder convention.
    /// </param>
    public static IReadOnlyList<string> BuildLines(
        IReadOnlyList<ConflictEntry> conflicts,
        IReadOnlyList<ConflictEntry> resurrections,
        IReadOnlyList<OverwriteInfo>? overwrites = null,
        string? archiveSessionRoot = null)
    {
        overwrites ??= Array.Empty<OverwriteInfo>();

        var lines = new List<string>();
        var total = conflicts.Count + resurrections.Count + overwrites.Count;
        if (total == 0) return lines;

        lines.Add($"Review: {total} item(s) need attention");
        foreach (var entry in conflicts)
            AppendItem(lines, ConflictTag, entry, ConflictNote);
        foreach (var entry in resurrections)
            AppendItem(lines, ResurrectionTag, entry, ResurrectionNote);
        foreach (var overwrite in overwrites)
            AppendOverwrite(lines, overwrite, archiveSessionRoot);
        return lines;
    }

    /// <summary>
    /// Renders the session's review. Conflicts and resurrections are read back through their own
    /// readers, distinguished by the file_versions.action column that LogConflict and
    /// LogResurrection wrote — never by inspecting the detail string. Overwrites are supplied in
    /// memory because the no-ancestor path that produces them can run with a null database.
    /// </summary>
    public static void Emit(SyncDatabase? db, long sessionId, SyncLogger logger, JsonProgressWriter progress,
                            IReadOnlyList<OverwriteInfo>? overwrites = null,
                            string? archiveSessionRoot = null)
    {
        overwrites ??= Array.Empty<OverwriteInfo>();

        // Conflict/resurrection rows only exist when a session was opened (a database present and
        // --delete on, SyncClient.cs). Overwrites do not depend on that, so a null db still emits
        // whatever overwrites the planner handed back.
        var conflicts = db != null && sessionId > 0
            ? db.GetSessionConflicts(sessionId) : Array.Empty<ConflictEntry>();
        var resurrections = db != null && sessionId > 0
            ? db.GetSessionResurrections(sessionId) : Array.Empty<ConflictEntry>();

        if (conflicts.Count == 0 && resurrections.Count == 0 && overwrites.Count == 0) return;

        foreach (var line in BuildLines(conflicts, resurrections, overwrites, archiveSessionRoot))
            logger.Summary(line);

        foreach (var entry in conflicts)
            WriteEvent(progress, ConflictKind, entry);
        foreach (var entry in resurrections)
            WriteEvent(progress, ResurrectionKind, entry);
        foreach (var overwrite in overwrites)
            WriteOverwriteEvent(progress, overwrite);
    }

    private static void AppendItem(List<string> lines, string tag, ConflictEntry entry, string note)
    {
        lines.Add($"  [{tag}] {entry.Path}");

        var detail = ConflictDetail.Decode(entry.Detail);
        if (detail == null)
        {
            // Written by a build that predates ConflictDetail, or hand-edited. Print it raw:
            // a dropped row hides precisely the case this report exists to surface.
            lines.Add($"      detail: {entry.Detail}");
        }
        else
        {
            lines.Add($"      client: {detail.ClientSize} bytes  {Stamp(detail.ClientMtimeTicks)}");
            lines.Add($"      server: {detail.ServerSize} bytes  {Stamp(detail.ServerMtimeTicks)}");
            if (detail.RenamedTo != null)
                lines.Add($"      kept as: {detail.RenamedTo}");
        }

        lines.Add($"      {note}");
    }

    private static void WriteEvent(JsonProgressWriter progress, string kind, ConflictEntry entry)
    {
        var detail = ConflictDetail.Decode(entry.Detail);
        if (detail == null)
        {
            progress.WriteReview(kind, entry.Path, UnknownSize, string.Empty, UnknownSize, string.Empty);
            return;
        }

        progress.WriteReview(kind, entry.Path,
            detail.ClientSize, Iso(detail.ClientMtimeTicks),
            detail.ServerSize, Iso(detail.ServerMtimeTicks),
            detail.RenamedTo);
    }

    private static void AppendOverwrite(List<string> lines, OverwriteInfo ow, string? archiveSessionRoot)
    {
        lines.Add($"  [{OverwriteTag}] {ow.Path}");

        // The record stores kept/replaced; map them back onto client/server so these two lines
        // read exactly like the conflict and resurrection sections above.
        var (clientSize, clientTicks, serverSize, serverTicks) = SidesOf(ow);
        lines.Add($"      client: {clientSize} bytes  {Stamp(clientTicks)}");
        lines.Add($"      server: {serverSize} bytes  {Stamp(serverTicks)}");
        lines.Add($"      kept: {(ow.KeptClientCopy ? "client" : "server")} copy");
        lines.Add($"      replaced: {ReplacedLine(ow, archiveSessionRoot)}");
    }

    /// <summary>
    /// Where the overwritten loser can be found. Only the client's own overwritten copy is
    /// archived locally (by the receive loop's pre-overwrite hook) at a path this side can name;
    /// when the client copy won, the loser is the SERVER's copy, archived on the server, so the
    /// convention is printed rather than a path this side cannot know — a wrong exact path would
    /// be worse than naming the folder.
    /// </summary>
    private static string ReplacedLine(OverwriteInfo ow, string? archiveSessionRoot)
    {
        if (ow.KeptClientCopy)
            return $"server copy (archived on the server under {OverwrittenReasonFolder}/{ow.Path})";

        if (archiveSessionRoot == null)
            return $"client copy (archived locally under {OverwrittenReasonFolder}/{ow.Path})";

        // Same layout ArchiveManager writes: <SessionRoot>/overwritten/<relative path>. One
        // overwrite per path per session, so there is no _N collision suffix to account for here.
        var archived = Path.Combine(archiveSessionRoot, OverwrittenReasonFolder,
            ow.Path.Replace('/', Path.DirectorySeparatorChar));
        return $"client copy archived at {archived}";
    }

    private static void WriteOverwriteEvent(JsonProgressWriter progress, OverwriteInfo ow)
    {
        // Both sides are real files here, so there is no decode sentinel. renamed_to is omitted:
        // an overwrite renames nothing, and the archive path is derivable by the GUI from the
        // session folder + reason + path, exactly as this report derives it.
        var (clientSize, clientTicks, serverSize, serverTicks) = SidesOf(ow);
        progress.WriteReview(OverwriteKind, ow.Path,
            clientSize, Iso(clientTicks),
            serverSize, Iso(serverTicks));
    }

    // Projects an OverwriteInfo's kept/replaced pair back onto (client, server), the axis the
    // report and the wire event both render.
    private static (long ClientSize, long ClientTicks, long ServerSize, long ServerTicks) SidesOf(OverwriteInfo ow) =>
        ow.KeptClientCopy
            ? (ow.KeptSize, ow.KeptMtimeTicks, ow.ReplacedSize, ow.ReplacedMtimeTicks)
            : (ow.ReplacedSize, ow.ReplacedMtimeTicks, ow.KeptSize, ow.KeptMtimeTicks);

    // A tick count outside DateTime's range would make new DateTime(ticks) throw
    // ArgumentOutOfRangeException and abort the entire review over one corrupt row.
    private static bool TryUtc(long ticks, out DateTime utc)
    {
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            utc = default;
            return false;
        }
        utc = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    // InvariantCulture because ':' is the culture's time separator inside a custom format
    // string — on a de-DE console this would otherwise print 14.30.52.
    private static string Stamp(long ticks) =>
        TryUtc(ticks, out var utc)
            ? utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z"
            : "unknown";

    // Empty string, not "unknown", so the GUI's -1 size sentinel and an empty mtime always
    // travel together and mean exactly one thing.
    private static string Iso(long ticks) =>
        TryUtc(ticks, out var utc) ? utc.ToString("O", CultureInfo.InvariantCulture) : string.Empty;
}
