using RemoteFileSync.Backup;
using RemoteFileSync.Models;
using RemoteFileSync.Security;

namespace RemoteFileSync.Sync;

/// <summary>The expanded plan plus the original-path -> conflict-name map, which the caller
/// needs to fill in ConflictDetail.RenamedTo when it logs the conflict.</summary>
public sealed record ConflictExpansion(
    List<SyncPlanEntry> Entries,
    IReadOnlyDictionary<string, string> RenamedTo);

/// <summary>Result of one peer's conflict rename pass. A non-empty <see cref="Failures"/> list is
/// fatal, not skippable. <see cref="NotArchived"/> is advisory: the rename succeeded but the
/// precautionary pre-rename snapshot did not.</summary>
public readonly record struct ConflictRenameOutcome(
    int Renamed,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> NotArchived);

/// <summary>
/// Executes SyncActionType.ConflictKeepBoth.
///
/// The plan is serialised once and BOTH peers iterate the identical list, so a conflict must not
/// require a peer-specific decision during any message-bearing phase. The client therefore
/// rewrites every ConflictKeepBoth entry into three entries before the plan goes on the wire:
///
///   1. ConflictKeepBoth(conflictName) — a purely LOCAL rename, performed only by the peer named
///      in the conflict name. It exchanges no frames.
///   2. SendToServer(...) and 3. SendToClient(...) — the loser copy in one direction and the
///      winner in the other, in whichever order the two sides happen to own them.
///
/// Because step 1 moves no bytes, a conflict adds exactly one file to each existing transfer
/// phase and cannot shift either peer's frame sequence.
///
/// Pure client-side expansion (never putting action 7 on the wire) was rejected: it can only
/// express the case where the CLIENT loses. When the server loses, the conflict-named file must
/// exist on the server's disk before FileTransferSender opens it, and the server's receive phase
/// overwrites the original before its send phase runs — so no reordering of existing actions can
/// produce it.
/// </summary>
public static class ConflictKeepBothExecutor
{
    /// <summary>
    /// Rewrites ConflictKeepBoth entries into the three-entry form. Runs on the client only,
    /// before the plan is serialised.
    /// </summary>
    public static ConflictExpansion Expand(
        IReadOnlyList<SyncPlanEntry> plan,
        FileManifest clientManifest,
        FileManifest serverManifest,
        ClockSkew skew,
        DateTime sessionStartUtc,
        string clientFolder)
    {
        var expanded = new List<SyncPlanEntry>(plan.Count);
        var renamedTo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in plan)
        {
            if (entry.Action != SyncActionType.ConflictKeepBoth)
            {
                expanded.Add(entry);
                continue;
            }

            var client = clientManifest.Get(entry.RelativePath);
            var server = serverManifest.Get(entry.RelativePath);
            if (client is null || server is null)
            {
                // ComputePlan only emits ConflictKeepBoth when both sides hold the file. If the
                // manifests disagree the state is not what the planner believed, and the safe
                // action is to touch nothing rather than rename on a guess.
                expanded.Add(new SyncPlanEntry(SyncActionType.Skip, entry.RelativePath));
                continue;
            }

            // Compare in client time: an unsynchronised server clock would otherwise decide the
            // winner by how wrong it is rather than by which copy is actually newer.
            // Tie goes to the server, so the choice is deterministic on repeat runs.
            var serverTime = skew.NormaliseServerTime(server.LastModifiedUtc);
            bool clientWins = serverTime < client.LastModifiedUtc;

            var losingSide = clientWins ? ConflictNamer.ServerSide : ConflictNamer.ClientSide;
            var conflictName = ConflictNamer.MakeUnique(
                clientFolder, entry.RelativePath, sessionStartUtc, losingSide);
            renamedTo[entry.RelativePath] = conflictName;

            expanded.Add(new SyncPlanEntry(SyncActionType.ConflictKeepBoth, conflictName));
            if (clientWins)
            {
                expanded.Add(new SyncPlanEntry(SyncActionType.SendToServer, entry.RelativePath));
                expanded.Add(new SyncPlanEntry(SyncActionType.SendToClient, conflictName));
            }
            else
            {
                expanded.Add(new SyncPlanEntry(SyncActionType.SendToServer, conflictName));
                expanded.Add(new SyncPlanEntry(SyncActionType.SendToClient, entry.RelativePath));
            }
        }

        return new ConflictExpansion(expanded, renamedTo);
    }

    /// <summary>
    /// How many conflict entries this peer owns would land on a name a local file already
    /// occupies — i.e. how many existing local files the rename pass would archive and remove.
    /// The server calls this BEFORE renaming: the plan arrives from a peer we do not
    /// authenticate, and a plan full of conflict names pointing at real local files is a way to
    /// destroy a folder without ever sending a DeleteFile frame. Occupancy is established by
    /// probing THIS machine's filesystem, never by anything the plan asserts.
    /// </summary>
    public static int CountOccupiedTargets(
        IReadOnlyList<SyncPlanEntry> plan, string side, string syncFolder)
    {
        int occupied = 0;
        foreach (var entry in plan)
        {
            if (entry.Action != SyncActionType.ConflictKeepBoth) continue;
            if (!ConflictNamer.TryParse(entry.RelativePath, out _, out var losingSide)) continue;
            if (losingSide != side) continue;
            if (!PathGuard.TryResolveWithinRoot(syncFolder, entry.RelativePath, out var full)) continue;
            if (File.Exists(full)) occupied++;
        }
        return occupied;
    }

    /// <summary>
    /// Runs on both peers before their transfer phases. <paramref name="side"/> is this peer's
    /// identity; only the peer the conflict name blames touches its disk, so the two sides never
    /// both rename and the per-direction file counts stay symmetric.
    ///
    /// Every entry this peer owns but cannot complete lands in Failures. Callers MUST abort the
    /// session on a non-empty list: the plan already promises the peer a transfer under the
    /// conflict name, and FileTransferSender throws while opening a missing source — before the
    /// first frame is written — leaving the peer blocked on a frame that never arrives.
    /// </summary>
    public static ConflictRenameOutcome ApplyLocalRenames(
        IReadOnlyList<SyncPlanEntry> plan, string side, string syncFolder, ArchiveManager archive)
    {
        int renamed = 0;
        var failures = new List<string>();
        var notArchived = new List<string>();

        foreach (var entry in plan)
        {
            if (entry.Action != SyncActionType.ConflictKeepBoth) continue;

            if (!ConflictNamer.TryParse(entry.RelativePath, out var originalPath, out var losingSide))
            {
                // Cannot tell whose entry this is, so both peers fail it and both abort — which
                // is still symmetric, and better than one side silently ignoring it.
                failures.Add($"{entry.RelativePath}: malformed conflict name");
                continue;
            }
            if (losingSide != side) continue;

            // The plan arrives from a peer we do not authenticate. Both names must be proven
            // inside the root before either reaches the filesystem.
            if (!PathGuard.TryResolveWithinRoot(syncFolder, originalPath, out var originalFull)
                || !PathGuard.TryResolveWithinRoot(syncFolder, entry.RelativePath, out var conflictFull))
            {
                failures.Add($"{entry.RelativePath}: resolves outside the sync root");
                continue;
            }

            if (!File.Exists(originalFull))
            {
                failures.Add($"{originalPath}: no longer exists");
                continue;
            }

            try
            {
                // File.Exists on OUR disk, not a claim carried in the plan: whether a local file
                // is about to be destroyed is a fact about this filesystem and must be derived
                // here.
                if (File.Exists(conflictFull))
                {
                    // NEVER destroy an existing file on an unproven archive. TryArchive reports
                    // Failed WITHOUT throwing when it finds the file and cannot preserve it, so
                    // a `bool` that was merely discarded would license the one path on which the
                    // user's file is destroyed with no copy anywhere. Record and skip instead;
                    // the caller turns a non-empty Failures list into an abort before any frame
                    // moves.
                    if (archive.TryArchive(entry.RelativePath, ArchiveReason.Conflict, removeOriginal: true)
                        == ArchiveOutcome.Failed)
                    {
                        failures.Add($"{entry.RelativePath}: could not archive the file already " +
                                     "occupying the conflict name; refusing to overwrite it");
                        continue;
                    }

                    // Archived-and-removed leaves nothing here; NothingToArchive means the file
                    // vanished under us, which also leaves nothing here. A survivor therefore
                    // means the archive did not preserve this file, and File.Move onto it would
                    // destroy a copy we cannot vouch for.
                    if (File.Exists(conflictFull))
                    {
                        failures.Add($"{entry.RelativePath}: still present after archiving; " +
                                     "refusing to overwrite it");
                        continue;
                    }
                }

                // Precautionary pre-rename snapshot. Deliberately NOT gated the way the squatter
                // archive above is: a failure here costs only a redundant copy, because File.Move
                // preserves the bytes under the new name either way. Aborting the whole session
                // over a belt-and-braces snapshot would strand the peer mid-plan for no gain.
                if (archive.TryArchive(originalPath, ArchiveReason.Conflict, removeOriginal: false)
                    != ArchiveOutcome.Archived)
                    notArchived.Add(originalPath);

                // Move, not copy-then-delete: the mtime must survive so the copy the peer
                // receives compares equal on the next scan instead of transferring forever.
                File.Move(originalFull, conflictFull);
                renamed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{originalPath}: {ex.Message}");
            }
        }

        return new ConflictRenameOutcome(renamed, failures, notArchived);
    }
}
