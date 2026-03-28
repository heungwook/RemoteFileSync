using RemoteFileSync.Models;
using RemoteFileSync.State;

namespace RemoteFileSync.Sync;

public static class SyncEngine
{
    public static List<SyncPlanEntry> ComputePlan(FileManifest clientManifest, FileManifest serverManifest, bool bidirectional)
    {
        return ComputePlan(clientManifest, serverManifest, bidirectional, previousState: null, deleteEnabled: false);
    }

    public static List<SyncPlanEntry> ComputePlan(
        FileManifest clientManifest,
        FileManifest serverManifest,
        bool bidirectional,
        SyncState? previousState,
        bool deleteEnabled)
    {
        var plan = new List<SyncPlanEntry>();
        var deletionHandled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Phase 1: Deletion detection
        if (deleteEnabled && previousState != null)
        {
            foreach (var path in previousState.Manifest.AllPaths)
            {
                var clientHas = clientManifest.Contains(path);
                var serverHas = serverManifest.Contains(path);

                if (clientHas && serverHas) continue;

                if (!clientHas && !serverHas)
                {
                    deletionHandled.Add(path);
                    continue;
                }

                bool deletedOnClient = !clientHas && serverHas;

                if (deletedOnClient)
                {
                    var serverEntry = serverManifest.Get(path)!;
                    var action = ConflictResolver.ResolveDeleteConflict(true, serverEntry, previousState.LastSyncUtc);
                    plan.Add(new SyncPlanEntry(action, path));
                    deletionHandled.Add(path);
                }
                else // deletedOnServer
                {
                    if (bidirectional)
                    {
                        var clientEntry = clientManifest.Get(path)!;
                        var action = ConflictResolver.ResolveDeleteConflict(false, clientEntry, previousState.LastSyncUtc);
                        plan.Add(new SyncPlanEntry(action, path));
                    }
                    deletionHandled.Add(path);
                }
            }
        }

        // Phase 2: Standard comparison
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in clientManifest.AllPaths) allPaths.Add(path);
        foreach (var path in serverManifest.AllPaths) allPaths.Add(path);

        foreach (var path in allPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (deletionHandled.Contains(path)) continue;

            var clientEntry = clientManifest.Get(path);
            var serverEntry = serverManifest.Get(path);

            if (clientEntry != null && serverEntry != null)
            {
                var action = ConflictResolver.Resolve(clientEntry, serverEntry);
                plan.Add(new SyncPlanEntry(action, path));
            }
            else if (clientEntry != null && serverEntry == null)
            {
                plan.Add(new SyncPlanEntry(SyncActionType.ClientOnly, path));
            }
            else if (clientEntry == null && serverEntry != null)
            {
                if (bidirectional)
                    plan.Add(new SyncPlanEntry(SyncActionType.ServerOnly, path));
            }
        }

        return plan;
    }

    public static List<SyncPlanEntry> ComputePlan(
        FileManifest clientManifest,
        FileManifest serverManifest,
        bool bidirectional,
        SyncDatabase? db,
        bool deleteEnabled)
    {
        var plan = new List<SyncPlanEntry>();

        // Collect all paths: both manifests + DB tracked files with status='exists'
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in clientManifest.AllPaths) allPaths.Add(path);
        foreach (var path in serverManifest.AllPaths) allPaths.Add(path);
        var dbIndex = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        if (deleteEnabled && db != null)
        {
            foreach (var fs in db.GetAllTrackedFiles())
            {
                dbIndex[fs.Path] = fs;
                if (fs.Status == "exists")
                    allPaths.Add(fs.Path);
            }
        }

        foreach (var path in allPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var clientEntry = clientManifest.Get(path);
            var serverEntry = serverManifest.Get(path);

            if (clientEntry != null && serverEntry != null)
            {
                // Both sides have the file — normal conflict resolution
                var action = ConflictResolver.Resolve(clientEntry, serverEntry);
                plan.Add(new SyncPlanEntry(action, path));
            }
            else if (clientEntry != null && serverEntry == null)
            {
                // Client has, server doesn't
                if (deleteEnabled && db != null)
                {
                    var dbState = dbIndex.GetValueOrDefault(path);
                    if (dbState == null)
                    {
                        // Genuinely new — ClientOnly
                        plan.Add(new SyncPlanEntry(SyncActionType.ClientOnly, path));
                    }
                    else if (dbState.Status == "exists")
                    {
                        // Server deleted it
                        if (bidirectional)
                        {
                            var action = ConflictResolver.ResolveDeleteConflict(
                                deletedOnClient: false, clientEntry, dbState.LastSynced);
                            plan.Add(new SyncPlanEntry(action, path));
                        }
                        else
                        {
                            // Bug #4 fix: uni-directional, client authoritative — re-push
                            plan.Add(new SyncPlanEntry(SyncActionType.ClientOnly, path));
                        }
                    }
                    else
                    {
                        // dbState.Status == "deleted" or "new" — re-appeared or never synced → ClientOnly
                        plan.Add(new SyncPlanEntry(SyncActionType.ClientOnly, path));
                    }
                }
                else
                {
                    // No DB — ClientOnly
                    plan.Add(new SyncPlanEntry(SyncActionType.ClientOnly, path));
                }
            }
            else if (clientEntry == null && serverEntry != null)
            {
                // Server has, client doesn't
                if (deleteEnabled && db != null)
                {
                    var dbState = dbIndex.GetValueOrDefault(path);
                    if (dbState == null)
                    {
                        // Genuinely new on server — ServerOnly if bidi
                        if (bidirectional)
                            plan.Add(new SyncPlanEntry(SyncActionType.ServerOnly, path));
                    }
                    else if (dbState.Status == "exists")
                    {
                        // Client deleted it — resolve conflict using per-file timestamp
                        var action = ConflictResolver.ResolveDeleteConflict(
                            deletedOnClient: true, serverEntry, dbState.LastSynced);
                        plan.Add(new SyncPlanEntry(action, path));
                    }
                    else
                    {
                        // dbState.Status == "deleted" or "new" — re-appeared on server → ServerOnly if bidi
                        if (bidirectional)
                            plan.Add(new SyncPlanEntry(SyncActionType.ServerOnly, path));
                    }
                }
                else
                {
                    // No DB — ServerOnly if bidi
                    if (bidirectional)
                        plan.Add(new SyncPlanEntry(SyncActionType.ServerOnly, path));
                }
            }
            else
            {
                // Neither has it (from DB tracked paths) — both deleted, no action
            }
        }

        return plan;
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
                case SyncActionType.DeleteOnServer:
                case SyncActionType.DeleteOnClient:
                    break;
            }
        }
        return merged;
    }
}
