using RemoteFileSync.Models;
using RemoteFileSync.State;
using RemoteFileSync.Sync;

namespace RemoteFileSync.Tests.Sync;

public class SyncEngineTests
{
    private static readonly DateTime T1 = new(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime LastSync = new(2026, 3, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BeforeSync = new(2026, 3, 26, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AfterSync = new(2026, 3, 27, 8, 0, 0, DateTimeKind.Utc);

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

    [Fact]
    public void DeletedOnClient_UntouchedOnServer_ProducesDeleteOnServer()
    {
        var snapshot = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));
        var state = new RemoteFileSync.State.SyncState(snapshot, LastSync);
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));
        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, previousState: state, deleteEnabled: true);
        Assert.Single(plan);
        Assert.Equal(SyncActionType.DeleteOnServer, plan[0].Action);
        Assert.Equal("file.txt", plan[0].RelativePath);
    }

    [Fact]
    public void DeletedOnClient_ModifiedOnServer_ProducesSendToClient()
    {
        var snapshot = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));
        var state = new RemoteFileSync.State.SyncState(snapshot, LastSync);
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("file.txt", 200, AfterSync));
        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, previousState: state, deleteEnabled: true);
        Assert.Single(plan);
        Assert.Equal(SyncActionType.SendToClient, plan[0].Action);
    }

    [Fact]
    public void DeletedOnServer_UntouchedOnClient_ProducesDeleteOnClient()
    {
        var snapshot = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));
        var state = new RemoteFileSync.State.SyncState(snapshot, LastSync);
        var client = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));
        var server = new FileManifest();
        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, previousState: state, deleteEnabled: true);
        Assert.Single(plan);
        Assert.Equal(SyncActionType.DeleteOnClient, plan[0].Action);
    }

    [Fact]
    public void DeletedOnServer_ModifiedOnClient_ProducesSendToServer()
    {
        var snapshot = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));
        var state = new RemoteFileSync.State.SyncState(snapshot, LastSync);
        var client = MakeManifest(new FileEntry("file.txt", 200, AfterSync));
        var server = new FileManifest();
        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, previousState: state, deleteEnabled: true);
        Assert.Single(plan);
        Assert.Equal(SyncActionType.SendToServer, plan[0].Action);
    }

    [Fact]
    public void BothDeleted_NoAction()
    {
        var snapshot = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));
        var state = new RemoteFileSync.State.SyncState(snapshot, LastSync);
        var client = new FileManifest();
        var server = new FileManifest();
        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, previousState: state, deleteEnabled: true);
        Assert.Empty(plan);
    }

    [Fact]
    public void NoState_FullyAdditive()
    {
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));
        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, previousState: null, deleteEnabled: true);
        Assert.Single(plan);
        Assert.Equal(SyncActionType.ServerOnly, plan[0].Action);
    }

    [Fact]
    public void UniDirectional_OnlyClientDeletionsPropagate()
    {
        var snapshot = MakeManifest(
            new FileEntry("client-deleted.txt", 100, BeforeSync),
            new FileEntry("server-deleted.txt", 100, BeforeSync));
        var state = new RemoteFileSync.State.SyncState(snapshot, LastSync);
        var client = MakeManifest(new FileEntry("server-deleted.txt", 100, BeforeSync));
        var server = MakeManifest(new FileEntry("client-deleted.txt", 100, BeforeSync));
        var plan = SyncEngine.ComputePlan(client, server, bidirectional: false, previousState: state, deleteEnabled: true);
        Assert.Single(plan);
        Assert.Equal(SyncActionType.DeleteOnServer, plan[0].Action);
        Assert.Equal("client-deleted.txt", plan[0].RelativePath);
    }

    [Fact]
    public void NewFileNotInSnapshot_NormalCopyBehavior()
    {
        var snapshot = MakeManifest(new FileEntry("existing.txt", 100, BeforeSync));
        var state = new RemoteFileSync.State.SyncState(snapshot, LastSync);
        var client = MakeManifest(
            new FileEntry("existing.txt", 100, BeforeSync),
            new FileEntry("brand-new.txt", 50, AfterSync));
        var server = MakeManifest(new FileEntry("existing.txt", 100, BeforeSync));
        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, previousState: state, deleteEnabled: true);
        var actions = plan.ToDictionary(p => p.RelativePath, p => p.Action);
        Assert.Equal(SyncActionType.Skip, actions["existing.txt"]);
        Assert.Equal(SyncActionType.ClientOnly, actions["brand-new.txt"]);
    }

    [Fact]
    public void TimestampTolerance_WithinTwoSeconds_TreatedAsUntouched()
    {
        // File mod time is 1 second after lastSync — within ±2s tolerance → treat as untouched → delete
        var withinTolerance = LastSync.AddSeconds(1);
        var snapshot = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));
        var state = new RemoteFileSync.State.SyncState(snapshot, LastSync);
        var client = new FileManifest(); // deleted on client
        var server = MakeManifest(new FileEntry("file.txt", 100, withinTolerance));
        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, previousState: state, deleteEnabled: true);
        Assert.Single(plan);
        Assert.Equal(SyncActionType.DeleteOnServer, plan[0].Action);
    }

    [Fact]
    public void UniDirectional_ServerDeletionsIgnored()
    {
        // In uni-directional mode, server deletions must not propagate to the client
        var snapshot = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));
        var state = new RemoteFileSync.State.SyncState(snapshot, LastSync);
        var client = MakeManifest(new FileEntry("file.txt", 100, BeforeSync)); // untouched
        var server = new FileManifest(); // deleted on server
        var plan = SyncEngine.ComputePlan(client, server, bidirectional: false, previousState: state, deleteEnabled: true);
        // Server deletion ignored in uni mode — no DeleteOnClient, no action at all
        Assert.Empty(plan);
    }

    [Fact]
    public void DeleteEnabled_False_IgnoresDeletions()
    {
        var snapshot = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));
        var state = new RemoteFileSync.State.SyncState(snapshot, LastSync);
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));
        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, previousState: state, deleteEnabled: false);
        Assert.Single(plan);
        Assert.Equal(SyncActionType.ServerOnly, plan[0].Action);
    }

    // ── SyncDatabase-backed deletion detection (new overload) ─────────────────

    private static SyncDatabase CreateTestDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rfs_engine_db_{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        return new SyncDatabase(Path.Combine(dir, "sync.db"));
    }

    [Fact]
    public void Db_DeletedFile_InDb_ProducesDeleteAction()
    {
        // File in db with status='exists', missing from client, present on server (untouched) → DeleteOnServer
        using var db = CreateTestDb();
        long sessionId = db.StartSession("bidi", "c:\\local", "server", 5000);
        db.MarkSynced("file.txt", 100, BeforeSync, sessionId, "client→server");

        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));

        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, db: db, deleteEnabled: true);

        Assert.Single(plan);
        Assert.Equal(SyncActionType.DeleteOnServer, plan[0].Action);
        Assert.Equal("file.txt", plan[0].RelativePath);
    }

    [Fact]
    public void Db_NewFile_NotInDb_ProducesCopyAction()
    {
        // File NOT in db, on client only → ClientOnly (genuinely new)
        using var db = CreateTestDb();

        var client = MakeManifest(new FileEntry("brand-new.txt", 50, AfterSync));
        var server = new FileManifest();

        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, db: db, deleteEnabled: true);

        Assert.Single(plan);
        Assert.Equal(SyncActionType.ClientOnly, plan[0].Action);
        Assert.Equal("brand-new.txt", plan[0].RelativePath);
    }

    [Fact]
    public void Db_PreviouslyDeleted_Reappeared_CopiesAgain()
    {
        // File in db with status='deleted', re-appears on client → ClientOnly
        using var db = CreateTestDb();
        long sessionId = db.StartSession("bidi", "c:\\local", "server", 5000);
        db.MarkSynced("file.txt", 100, BeforeSync, sessionId, "client→server");
        db.MarkDeleted("file.txt", sessionId, null);

        var client = MakeManifest(new FileEntry("file.txt", 100, AfterSync));
        var server = new FileManifest();

        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, db: db, deleteEnabled: true);

        Assert.Single(plan);
        Assert.Equal(SyncActionType.ClientOnly, plan[0].Action);
    }

    [Fact]
    public void Db_UniDirectional_ServerLostFile_RePushed()
    {
        // Bug #4 fix: uni mode, db status='exists', server missing, client has → ClientOnly (not silently dropped)
        using var db = CreateTestDb();
        long sessionId = db.StartSession("uni", "c:\\local", "server", 5000);
        db.MarkSynced("file.txt", 100, BeforeSync, sessionId, "client→server");

        var client = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));
        var server = new FileManifest(); // server lost the file

        var plan = SyncEngine.ComputePlan(client, server, bidirectional: false, db: db, deleteEnabled: true);

        Assert.Single(plan);
        Assert.Equal(SyncActionType.ClientOnly, plan[0].Action);
        Assert.Equal("file.txt", plan[0].RelativePath);
    }

    [Fact]
    public void Db_PerFileTimestamp_UsedForDeletion()
    {
        // File synced now (LastSynced = UtcNow via MarkSynced), server has a version modified
        // in the future (T2 > LastSynced), client deleted → SendToClient (restore)
        using var db = CreateTestDb();
        long sessionId = db.StartSession("bidi", "c:\\local", "server", 5000);
        db.MarkSynced("file.txt", 100, BeforeSync, sessionId, "client→server");

        // Server file modified after the MarkSynced call (guaranteed future timestamp)
        var serverModifiedAfterSync = DateTime.UtcNow.AddDays(1);

        var client = new FileManifest(); // deleted on client
        var server = MakeManifest(new FileEntry("file.txt", 200, serverModifiedAfterSync)); // modified on server after sync

        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, db: db, deleteEnabled: true);

        Assert.Single(plan);
        Assert.Equal(SyncActionType.SendToClient, plan[0].Action);
    }

    [Fact]
    public void Db_DeleteEnabled_False_NormalBehavior()
    {
        // deleteEnabled=false, db has data → ignored, normal ServerOnly behavior
        using var db = CreateTestDb();
        long sessionId = db.StartSession("bidi", "c:\\local", "server", 5000);
        db.MarkSynced("file.txt", 100, BeforeSync, sessionId, "client→server");

        var client = new FileManifest(); // no file on client
        var server = MakeManifest(new FileEntry("file.txt", 100, BeforeSync));

        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, db: db, deleteEnabled: false);

        Assert.Single(plan);
        Assert.Equal(SyncActionType.ServerOnly, plan[0].Action);
    }

    [Fact]
    public void Db_BothDeletedFromDb_NoAction()
    {
        // File in db status='exists', missing from both manifests → no plan entry
        using var db = CreateTestDb();
        long sessionId = db.StartSession("bidi", "c:\\local", "server", 5000);
        db.MarkSynced("file.txt", 100, BeforeSync, sessionId, "client→server");

        var client = new FileManifest(); // not present
        var server = new FileManifest(); // not present

        var plan = SyncEngine.ComputePlan(client, server, bidirectional: true, db: db, deleteEnabled: true);

        Assert.Empty(plan);
    }
}
