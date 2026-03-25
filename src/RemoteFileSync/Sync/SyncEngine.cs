using RemoteFileSync.Models;

namespace RemoteFileSync.Sync;

public static class SyncEngine
{
    public static List<SyncPlanEntry> ComputePlan(FileManifest clientManifest, FileManifest serverManifest, bool bidirectional)
    {
        var plan = new List<SyncPlanEntry>();
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in clientManifest.AllPaths) allPaths.Add(path);
        foreach (var path in serverManifest.AllPaths) allPaths.Add(path);

        foreach (var path in allPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
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
}
