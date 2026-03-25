using RemoteFileSync.Models;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class SyncEngineTests
{
    private static readonly DateTime T1 = new(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);

    private static FileManifest MakeManifest(params FileEntry[] entries)
    {
        var m = new FileManifest();
        foreach (var e in entries) m.Add(e);
        return m;
    }

    [Fact]
    public void BothEmpty_EmptyPlan()
    {
        var plan = SyncEngine.ComputePlan(new FileManifest(), new FileManifest(), bidirectional: true);
        Assert.Empty(plan);
    }

    [Fact]
    public void IdenticalFiles_AllSkipped()
    {
        var client = MakeManifest(new FileEntry("a.txt", 100, T1));
        var server = MakeManifest(new FileEntry("a.txt", 100, T1));
        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true);
        Assert.All(plan, p => Assert.Equal(SyncActionType.Skip, p.Action));
    }

    [Fact]
    public void ClientOnly_Unidirectional_ProducesClientOnlyAction()
    {
        var client = MakeManifest(new FileEntry("new.txt", 50, T1));
        var server = new FileManifest();
        var plan = SyncEngine.ComputePlan(client, server, bidirectional: false);
        Assert.Single(plan);
        Assert.Equal(SyncActionType.ClientOnly, plan[0].Action);
    }

    [Fact]
    public void ServerOnly_Unidirectional_Ignored()
    {
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("only-server.txt", 50, T1));
        var plan = SyncEngine.ComputePlan(client, server, bidirectional: false);
        Assert.Empty(plan.Where(p => p.Action != SyncActionType.Skip));
    }

    [Fact]
    public void ServerOnly_Bidirectional_ProducesServerOnlyAction()
    {
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("only-server.txt", 50, T1));
        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true);
        Assert.Single(plan);
        Assert.Equal(SyncActionType.ServerOnly, plan[0].Action);
    }

    [Fact]
    public void ClientNewer_SendToServer()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T2));
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true);
        Assert.Single(plan);
        Assert.Equal(SyncActionType.SendToServer, plan[0].Action);
    }

    [Fact]
    public void ServerNewer_SendToClient()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = MakeManifest(new FileEntry("f.txt", 100, T2));
        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true);
        Assert.Single(plan);
        Assert.Equal(SyncActionType.SendToClient, plan[0].Action);
    }

    [Fact]
    public void MixedScenario_CorrectPlan()
    {
        var client = MakeManifest(
            new FileEntry("same.txt", 100, T1),
            new FileEntry("client-newer.txt", 100, T2),
            new FileEntry("client-only.txt", 50, T1));
        var server = MakeManifest(
            new FileEntry("same.txt", 100, T1),
            new FileEntry("client-newer.txt", 100, T1),
            new FileEntry("server-only.txt", 50, T1));
        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true);
        var actions = plan.ToDictionary(p => p.RelativePath, p => p.Action);
        Assert.Equal(SyncActionType.Skip, actions["same.txt"]);
        Assert.Equal(SyncActionType.SendToServer, actions["client-newer.txt"]);
        Assert.Equal(SyncActionType.ClientOnly, actions["client-only.txt"]);
        Assert.Equal(SyncActionType.ServerOnly, actions["server-only.txt"]);
    }
}
