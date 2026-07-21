using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

public static class SyncEngine
{
    /// <summary>
    /// Builds the sync plan by three-way merge against <paramref name="ancestor"/>.
    /// A null table, a missing row, a tombstoned row, or a row still marked 'new' all mean the
    /// same thing — we do not know which side changed — and route to the strictly additive
    /// fallback, which can never emit a deletion. Heuristics live only in that fallback and must
    /// not leak into the paths where the ancestor answers the question outright.
    /// This method is pure. It never opens the database: the resurrection and conflict rows it
    /// discovers are returned on <see cref="PlanResult"/> so the caller can write them only after
    /// the transfer phase has actually succeeded, leaving an aborted run with no recorded state.
    /// </summary>
    public static PlanResult ComputePlan(
        FileManifest clientManifest,
        FileManifest serverManifest,
        SyncMode mode,
        IReadOnlyDictionary<string, AncestorRow>? ancestor,
        bool deleteEnabled,
        bool mirrorDeletes,
        ClockSkew skew)
    {
        var result = new PlanResult();

        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in clientManifest.AllPaths) allPaths.Add(path);
        foreach (var path in serverManifest.AllPaths) allPaths.Add(path);

        // A path present in neither manifest is invisible unless the ancestor names it, and that
        // absence is the only evidence a deletion ever happened. Only 'exists' rows qualify:
        // tombstoned rows are settled history and would replan the same deletion on every run
        // forever, and 'new' rows never recorded a two-sided agreement to have deviated from.
        if (ancestor != null)
        {
            foreach (var row in ancestor.Values)
                if (row.Status == "exists") allPaths.Add(row.Path);
        }

        foreach (var path in allPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var client = clientManifest.Get(path);
            var server = serverManifest.Get(path);

            AncestorRow? row = null;
            if (ancestor != null && ancestor.TryGetValue(path, out var found)) row = found;

            SyncActionType? action = mode switch
            {
                SyncMode.Push => PlanPush(client, server, row, deleteEnabled, mirrorDeletes, skew),
                SyncMode.Pull => PlanPull(client, server, row, deleteEnabled, mirrorDeletes, skew),
                SyncMode.TwoWay => IsAncestor(row)
                    ? PlanTwoWayWithAncestor(path, client, server, row!, deleteEnabled, result)
                    : PlanNoAncestor(path, client, server, skew, result),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown sync mode."),
            };

            if (action.HasValue) result.Entries.Add(new SyncPlanEntry(action.Value, path));
        }

        return result;
    }

    /// <summary>
    /// True only for a row that records a completed two-sided agreement. 'deleted' is settled
    /// history and 'new' is a one-sided discovery — no sync either attests to ever happened — so
    /// both must behave exactly like a missing row. Testing <c>Status != "deleted"</c> instead
    /// would let a 'new' row license a deletion on the strength of a sync that never ran.
    /// </summary>
    private static bool IsAncestor(AncestorRow? row) => row is not null && row.Status == "exists";

    /// <summary>
    /// The only path where we KNOW what happened: the row records what each side looked like when
    /// they last agreed, so "changed" is a recorded fact rather than a timestamp guess. No
    /// newest-wins comparison may appear in this method — guessing from timestamps is the bug
    /// being fixed. Clock skew is irrelevant here: the current server mtime and the stored server
    /// mtime both come from the server's clock, so any constant offset cancels.
    /// </summary>
    private static SyncActionType? PlanTwoWayWithAncestor(
        string path, FileEntry? client, FileEntry? server, AncestorRow row,
        bool deleteEnabled, PlanResult result)
    {
        bool clientChanged = client != null
            && !ChangeDetector.Unchanged(client, row.ClientSize, row.ClientMtimeTicks);
        bool serverChanged = server != null
            && !ChangeDetector.Unchanged(server, row.ServerSize, row.ServerMtimeTicks);

        if (client != null && server != null)
        {
            if (!clientChanged && !serverChanged) return SyncActionType.Skip;
            if (clientChanged && !serverChanged) return SyncActionType.SendToServer;
            if (!clientChanged && serverChanged) return SyncActionType.SendToClient;

            result.Conflicts.Add(new ConflictInfo(path,
                client.FileSize, client.LastModifiedUtc.Ticks,
                server.FileSize, server.LastModifiedUtc.Ticks));
            return SyncActionType.ConflictKeepBoth;
        }

        if (client != null && server == null)
        {
            // A delete loses to an edit: losing the edit is unrecoverable, an unwanted
            // resurrection costs one more delete. Record it so the review report can explain
            // why a file the user deleted on the server came back.
            if (clientChanged)
            {
                result.Resurrections.Add(new ResurrectionInfo(
                    path, KeptClientCopy: true, client.FileSize, client.LastModifiedUtc.Ticks));
                return SyncActionType.SendToServer;
            }

            // Without --delete the deletion does not propagate. Re-push rather than emit nothing,
            // which would leave the two sides divergent with no record of why.
            return deleteEnabled ? SyncActionType.DeleteOnClient : SyncActionType.SendToServer;
        }

        if (client == null && server != null)
        {
            if (serverChanged)
            {
                result.Resurrections.Add(new ResurrectionInfo(
                    path, KeptClientCopy: false, server.FileSize, server.LastModifiedUtc.Ticks));
                return SyncActionType.SendToClient;
            }

            return deleteEnabled ? SyncActionType.DeleteOnServer : SyncActionType.SendToClient;
        }

        // Gone from both sides. Nothing to transfer and nothing to delete; the caller tombstones
        // the row. ComputePlan has no sessionId in scope and must not write to the database.
        return null;
    }

    /// <summary>
    /// No usable row: we cannot tell an edit from a deletion, so this path is strictly additive
    /// and must never return DeleteOnServer or DeleteOnClient. Only TwoWay reaches it — Push and
    /// Pull have their own tables and handle the missing-row case themselves.
    /// When both sides hold a differing copy, newest-wins overwrites the loser: the ACTION is
    /// unchanged from before, but an <see cref="OverwriteInfo"/> is recorded on
    /// <paramref name="result"/> so the review report can tell the user which copy was replaced.
    /// A Skip (identical or skew-equal copies) replaces nothing and records nothing, and a
    /// one-sided file is a pure add with no loser — so only SendToServer / SendToClient on the
    /// both-present branch produce an overwrite.
    /// </summary>
    private static SyncActionType? PlanNoAncestor(
        string path, FileEntry? client, FileEntry? server, ClockSkew skew, PlanResult result)
    {
        if (client != null && server != null)
        {
            var action = ResolveNoAncestor(client, server, skew);

            if (action == SyncActionType.SendToServer)
                result.Overwrites.Add(new OverwriteInfo(path, KeptClientCopy: true,
                    KeptSize: client.FileSize, KeptMtimeTicks: client.LastModifiedUtc.Ticks,
                    ReplacedSize: server.FileSize, ReplacedMtimeTicks: server.LastModifiedUtc.Ticks));
            else if (action == SyncActionType.SendToClient)
                result.Overwrites.Add(new OverwriteInfo(path, KeptClientCopy: false,
                    KeptSize: server.FileSize, KeptMtimeTicks: server.LastModifiedUtc.Ticks,
                    ReplacedSize: client.FileSize, ReplacedMtimeTicks: client.LastModifiedUtc.Ticks));

            return action;
        }

        if (client != null) return SyncActionType.SendToServer;
        if (server != null) return SyncActionType.SendToClient;
        return null;
    }

    /// <summary>
    /// Newest wins, tie broken by size — but only after the server's mtime is pulled back into
    /// the client's clock domain. A server running an hour fast otherwise wins every comparison
    /// forever and the same bytes are re-downloaded on every run.
    /// </summary>
    private static SyncActionType ResolveNoAncestor(FileEntry client, FileEntry server, ClockSkew skew)
    {
        var normalised = new FileEntry(server.RelativePath, server.FileSize,
                                       skew.NormaliseServerTime(server.LastModifiedUtc));
        return ConflictResolver.Resolve(client, normalised);
    }

    /// <summary>
    /// Client authoritative: the server is made to match and nothing is ever written to the
    /// client, so this method may only return SendToServer, DeleteOnServer or Skip.
    /// </summary>
    private static SyncActionType? PlanPush(
        FileEntry? client, FileEntry? server, AncestorRow? row,
        bool deleteEnabled, bool mirrorDeletes, ClockSkew skew)
    {
        if (client != null && server == null) return SyncActionType.SendToServer;

        if (client != null && server != null)
        {
            // Deliberately not "newest wins": in Push the server does not get a vote, so a newer
            // server copy is still overwritten. Same-content is compared skew-normalised so a
            // clock offset alone does not re-upload every file on every run.
            return SameContent(client, server, skew)
                ? SyncActionType.Skip
                : SyncActionType.SendToServer;
        }

        if (client == null && server != null)
        {
            if (!deleteEnabled) return SyncActionType.Skip;

            // Two independent conditions, both required.
            // An 'exists' row proves the client once held this path AND that both sides agreed on
            // it: without one, an absent client file is indistinguishable from one the client
            // never had, and deleting would wipe the server on the first run against a repointed
            // or unrelated folder.
            // Unchanged proves nobody edited the server copy since that agreement: deleting an
            // edited copy destroys the only surviving version of that edit, and unlike TwoWay,
            // Push has no resurrection branch to bring it back.
            // --mirror is the explicit opt-in to skipping both checks.
            bool clientHadItAndPeerUntouched =
                IsAncestor(row)
                && ChangeDetector.Unchanged(server, row!.ServerSize, row.ServerMtimeTicks);

            return (clientHadItAndPeerUntouched || mirrorDeletes)
                ? SyncActionType.DeleteOnServer
                : SyncActionType.Skip;
        }

        return null;
    }

    /// <summary>
    /// Server authoritative: the exact mirror of <see cref="PlanPush"/>. May only return
    /// SendToClient, DeleteOnClient or Skip.
    /// </summary>
    private static SyncActionType? PlanPull(
        FileEntry? client, FileEntry? server, AncestorRow? row,
        bool deleteEnabled, bool mirrorDeletes, ClockSkew skew)
    {
        if (server != null && client == null) return SyncActionType.SendToClient;

        if (server != null && client != null)
        {
            return SameContent(client, server, skew)
                ? SyncActionType.Skip
                : SyncActionType.SendToClient;
        }

        if (server == null && client != null)
        {
            if (!deleteEnabled) return SyncActionType.Skip;

            // Mirror of the Push gate, and the more dangerous of the two: this branch deletes
            // files out of the user's own local folder, so a row alone is not enough — the local
            // copy must also be unchanged since the last agreement.
            bool serverHadItAndPeerUntouched =
                IsAncestor(row)
                && ChangeDetector.Unchanged(client, row!.ClientSize, row.ClientMtimeTicks);

            return (serverHadItAndPeerUntouched || mirrorDeletes)
                ? SyncActionType.DeleteOnClient
                : SyncActionType.Skip;
        }

        return null;
    }

    /// <summary>
    /// Same bytes as far as metadata can tell, with the server mtime normalised into the client's
    /// clock domain first.
    /// </summary>
    private static bool SameContent(FileEntry client, FileEntry server, ClockSkew skew)
    {
        if (client.FileSize != server.FileSize) return false;
        var normalised = skew.NormaliseServerTime(server.LastModifiedUtc);
        return Math.Abs((client.LastModifiedUtc - normalised).TotalSeconds)
               <= ChangeDetector.Tolerance.TotalSeconds;
    }

    public static FileManifest BuildMergedManifest(
        FileManifest clientManifest,
        FileManifest serverManifest,
        List<SyncPlanEntry> syncPlan)
    {
        var merged = new FileManifest();
        foreach (var entry in syncPlan)
        {
            switch (entry.Action)
            {
                case SyncActionType.Skip:
                case SyncActionType.SendToServer:
                case SyncActionType.ClientOnly:
                    var clientEntry = clientManifest.Get(entry.RelativePath);
                    if (clientEntry != null) merged.Add(clientEntry);
                    break;
                case SyncActionType.SendToClient:
                case SyncActionType.ServerOnly:
                    var serverEntry = serverManifest.Get(entry.RelativePath);
                    if (serverEntry != null) merged.Add(serverEntry);
                    break;
                case SyncActionType.ConflictKeepBoth:
                    // Not reachable from this method's only caller: BuildMergedManifest runs
                    // solely on SyncClient's `_db == null` fallback, where ComputePlan routes
                    // TwoWay through PlanNoAncestor — which never emits ConflictKeepBoth — and
                    // Push/Pull never emit it either. Kept as a defensive no-op (and to pin the
                    // direct unit test below) rather than deleted, since a lookup here would
                    // return null anyway: a surviving ConflictKeepBoth entry's RelativePath is
                    // already the CONFLICT name after ConflictKeepBothExecutor.Expand, not the
                    // original path this switch is keyed on.
                    var conflictEntry = clientManifest.Get(entry.RelativePath);
                    if (conflictEntry != null) merged.Add(conflictEntry);
                    break;
                case SyncActionType.DeleteOnServer:
                case SyncActionType.DeleteOnClient:
                    break;
            }
        }
        return merged;
    }
}
