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

    /// <summary>Row for a file both sides agreed on at <paramref name="mtime"/>.</summary>
    private static AncestorRow Row(string path, long size, DateTime mtime) =>
        new(path, size, mtime.Ticks, size, mtime.Ticks, "exists", T1.Ticks, null);

    /// <summary>Row for a file already tombstoned — must behave exactly like "no row".</summary>
    private static AncestorRow Tombstoned(string path, long size, DateTime mtime) =>
        new(path, size, mtime.Ticks, size, mtime.Ticks, "deleted", T1.Ticks, T1.Ticks);

    /// <summary>
    /// Row for a file discovered on one side but never confirmed as synced to the other. No
    /// two-sided agreement it records ever happened, so it must route exactly like a missing row.
    /// </summary>
    private static AncestorRow NewRow(string path, long size, DateTime mtime) =>
        new(path, size, mtime.Ticks, size, mtime.Ticks, "new", T1.Ticks, null);

    private static Dictionary<string, AncestorRow> Ancestor(params AncestorRow[] rows) =>
        rows.ToDictionary(r => r.Path, r => r, StringComparer.OrdinalIgnoreCase);

    private static PlanResult Plan(
        FileManifest client,
        FileManifest server,
        SyncMode mode,
        IReadOnlyDictionary<string, AncestorRow>? ancestor,
        bool deleteEnabled = true,
        bool mirrorDeletes = false,
        ClockSkew? skew = null) =>
        SyncEngine.ComputePlan(client, server, mode, ancestor, deleteEnabled, mirrorDeletes,
                               skew ?? ClockSkew.None);

    private static Dictionary<string, SyncActionType> Actions(PlanResult result) =>
        result.Entries.ToDictionary(p => p.RelativePath, p => p.Action,
                                    StringComparer.OrdinalIgnoreCase);

    // ── TwoWay, row present and Status == "exists" ────────────────────────────

    [Fact]
    public void TwoWay_UnchangedBothSides_Skip()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.Skip, result.Entries[0].Action);
    }

    [Fact]
    public void TwoWay_ClientChangedOnly_SendToServer()
    {
        var client = MakeManifest(new FileEntry("f.txt", 150, T2));
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[0].Action);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void TwoWay_ServerChangedOnly_SendToClient()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = MakeManifest(new FileEntry("f.txt", 150, T2));
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToClient, result.Entries[0].Action);
    }

    [Fact]
    public void TwoWay_BothChanged_ConflictKeepBothAndRecordsBothSides()
    {
        // Both sides edited since the ancestor. Neither edit may be silently discarded, so the
        // plan keeps both and the executor renames the loser. The conflict must also reach
        // PlanResult with each side's real size/mtime, because that is the only place the
        // review report can learn what the two copies were.
        var client = MakeManifest(new FileEntry("f.txt", 150, T2));
        var server = MakeManifest(new FileEntry("f.txt", 220, T2.AddMinutes(5)));
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));

        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.ConflictKeepBoth, result.Entries[0].Action);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("f.txt", conflict.Path);
        Assert.Equal(150, conflict.ClientSize);
        Assert.Equal(T2.Ticks, conflict.ClientMtimeTicks);
        Assert.Equal(220, conflict.ServerSize);
        Assert.Equal(T2.AddMinutes(5).Ticks, conflict.ServerMtimeTicks);
    }

    [Fact]
    public void TwoWay_ClientAbsent_ServerUnchanged_DeleteOnServer()
    {
        // The client deleted it and nobody touched the server copy, so the deletion is the only
        // edit in play and it propagates.
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.DeleteOnServer, result.Entries[0].Action);
        Assert.Equal("f.txt", result.Entries[0].RelativePath);
        Assert.Empty(result.Resurrections);
    }

    [Fact]
    public void TwoWay_ClientAbsent_ServerChanged_SendToClientAndRecordsResurrection()
    {
        // A delete on one side loses to a real edit on the other: losing the edit is
        // unrecoverable, an unwanted resurrection costs one more delete. The kept copy is
        // recorded so the review report can tell the user why the file came back.
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("f.txt", 220, T2));
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));

        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToClient, result.Entries[0].Action);

        var res = Assert.Single(result.Resurrections);
        Assert.Equal("f.txt", res.Path);
        Assert.False(res.KeptClientCopy);
        Assert.Equal(220, res.KeptSize);
        Assert.Equal(T2.Ticks, res.KeptMtimeTicks);
    }

    [Fact]
    public void TwoWay_ServerAbsent_ClientUnchanged_DeleteOnClient()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = new FileManifest();
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.DeleteOnClient, result.Entries[0].Action);
        Assert.Empty(result.Resurrections);
    }

    [Fact]
    public void TwoWay_ServerAbsent_ClientChanged_SendToServerAndRecordsResurrection()
    {
        var client = MakeManifest(new FileEntry("f.txt", 220, T2));
        var server = new FileManifest();
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));

        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[0].Action);

        var res = Assert.Single(result.Resurrections);
        Assert.Equal("f.txt", res.Path);
        Assert.True(res.KeptClientCopy);
        Assert.Equal(220, res.KeptSize);
        Assert.Equal(T2.Ticks, res.KeptMtimeTicks);
    }

    [Fact]
    public void TwoWay_AbsentBothSides_NoPlanEntry()
    {
        // Both sides already removed it. There is nothing to transfer and nothing to delete;
        // the caller tombstones the row. ComputePlan stays free of database writes.
        var result = Plan(new FileManifest(), new FileManifest(), SyncMode.TwoWay,
                          Ancestor(Row("f.txt", 100, T1)));
        Assert.Empty(result.Entries);
        Assert.Empty(result.Resurrections);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void TwoWay_SizeChangedMtimeIdentical_CountsAsChanged()
    {
        // An in-place rewrite keeps the mtime inside tolerance. Comparing mtimes alone returns
        // Skip here and the larger client copy is never pushed.
        var client = MakeManifest(new FileEntry("f.txt", 250, T1));
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[0].Action);
    }

    [Fact]
    public void TwoWay_DeleteDisabled_ReCopiesInsteadOfDeleting()
    {
        // Without --delete the deletion must not propagate, but dropping the path from the plan
        // would leave the two sides permanently divergent with no record of why.
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Row("f.txt", 100, T1)),
                          deleteEnabled: false);
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToClient, result.Entries[0].Action);
    }

    [Fact]
    public void TwoWay_NewFileWithNoRow_TakesAdditivePath()
    {
        var ancestor = Ancestor(Row("existing.txt", 100, T1));
        var client = MakeManifest(
            new FileEntry("existing.txt", 100, T1),
            new FileEntry("brand-new.txt", 50, T2));
        var server = MakeManifest(new FileEntry("existing.txt", 100, T1));
        var actions = Actions(Plan(client, server, SyncMode.TwoWay, ancestor));
        Assert.Equal(SyncActionType.Skip, actions["existing.txt"]);
        Assert.Equal(SyncActionType.SendToServer, actions["brand-new.txt"]);
    }

    // ── Status == "new" is a discovery, never an ancestor ─────────────────────

    [Fact]
    public void TwoWay_StatusNewRow_TreatedAsNoAncestor_NeverDeletes()
    {
        // A 'new' row records that one side was seen holding the file, not that both sides ever
        // agreed on it — no sync it attests to happened. Dispatching on Status != "deleted"
        // routes it down the delete-capable path, where client-present/server-absent reads as
        // "the server deleted it" and the client copy is destroyed on the strength of a sync
        // that never ran.
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = new FileManifest();
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(NewRow("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[0].Action);
    }

    [Fact]
    public void Pull_StatusNewRow_DoesNotLicenseDeletingClientFile()
    {
        // The Pull mirror, and the dangerous direction: this branch deletes out of the user's own
        // local folder, so a 'new' row must not satisfy the "the server had it too" gate.
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = new FileManifest();
        var result = Plan(client, server, SyncMode.Pull, Ancestor(NewRow("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.Skip, result.Entries[0].Action);
    }

    [Fact]
    public void Push_StatusNewRow_DoesNotLicenseDeletingServerFile()
    {
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var result = Plan(client, server, SyncMode.Push, Ancestor(NewRow("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.Skip, result.Entries[0].Action);
    }

    [Fact]
    public void StatusNewRow_AbsentBothSides_EmitsNothing()
    {
        // A 'new' row must not be seeded into the path set as though it were an agreed ancestor:
        // that would plan an action for a path neither side holds.
        var result = Plan(new FileManifest(), new FileManifest(), SyncMode.TwoWay,
                          Ancestor(NewRow("f.txt", 100, T1)));
        Assert.Empty(result.Entries);
    }

    // ── No ancestor (null table, missing row, or tombstoned row) ──────────────

    [Fact]
    public void NoAncestor_AdditiveOnly_NeverEmitsDelete()
    {
        var client = MakeManifest(new FileEntry("c-only.txt", 50, T1));
        var server = MakeManifest(new FileEntry("s-only.txt", 50, T1));
        var result = Plan(client, server, SyncMode.TwoWay, ancestor: null, deleteEnabled: true);
        var actions = Actions(result);
        Assert.Equal(SyncActionType.SendToServer, actions["c-only.txt"]);
        Assert.Equal(SyncActionType.SendToClient, actions["s-only.txt"]);
        Assert.DoesNotContain(result.Entries, p => p.Action == SyncActionType.DeleteOnServer);
        Assert.DoesNotContain(result.Entries, p => p.Action == SyncActionType.DeleteOnClient);
    }

    [Fact]
    public void NoAncestor_BothPresent_NewestWins()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T2));
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var result = Plan(client, server, SyncMode.TwoWay, ancestor: null);
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[0].Action);
    }

    [Fact]
    public void NoAncestor_SameMtime_LargerWins()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = MakeManifest(new FileEntry("f.txt", 200, T1));
        var result = Plan(client, server, SyncMode.TwoWay, ancestor: null);
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToClient, result.Entries[0].Action);
    }

    [Fact]
    public void TombstonedRow_TreatedAsNoAncestor_NeverDeletes()
    {
        // A "deleted" row is settled history. Reading it as an ancestor would turn a file the
        // user deliberately re-created into an immediate re-deletion.
        var client = MakeManifest(new FileEntry("f.txt", 100, T2));
        var server = new FileManifest();
        var result = Plan(client, server, SyncMode.TwoWay, Ancestor(Tombstoned("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[0].Action);
    }

    [Fact]
    public void BothEmpty_EmptyPlan()
    {
        var result = Plan(new FileManifest(), new FileManifest(), SyncMode.TwoWay, ancestor: null);
        Assert.Empty(result.Entries);
    }

    // ── Clock skew ───────────────────────────────────────────────────────────

    [Fact]
    public void ClockSkew_ServerOneHourFast_DoesNotWin()
    {
        // The server's clock is +1h. Its file is byte-identical and was written at the same real
        // instant, but its raw mtime is an hour "newer", so it wins newest-wins forever and every
        // run pulls the same bytes down again. The first half of this test proves the second half
        // bites: with ClockSkew.None the engine really does return SendToClient.
        var client = MakeManifest(new FileEntry("f.txt", 100, T2));
        var server = MakeManifest(new FileEntry("f.txt", 100, T2.AddHours(1)));

        var withoutSkew = Plan(client, server, SyncMode.TwoWay, ancestor: null,
                               skew: ClockSkew.None);
        Assert.Equal(SyncActionType.SendToClient, withoutSkew.Entries[0].Action);

        var withSkew = Plan(client, server, SyncMode.TwoWay, ancestor: null,
                            skew: new ClockSkew(TimeSpan.FromHours(1)));
        Assert.Single(withSkew.Entries);
        Assert.Equal(SyncActionType.Skip, withSkew.Entries[0].Action);
    }

    // ── Push (client authoritative) ──────────────────────────────────────────

    [Fact]
    public void Push_NeverEmitsClientSideActions()
    {
        var ancestor = Ancestor(Row("keep.txt", 100, T1), Row("gone.txt", 100, T1));
        var client = MakeManifest(
            new FileEntry("keep.txt", 100, T1),
            new FileEntry("push-me.txt", 50, T2));
        var server = MakeManifest(
            new FileEntry("keep.txt", 100, T1),
            new FileEntry("gone.txt", 100, T1),
            new FileEntry("server-extra.txt", 100, T2));

        var result = Plan(client, server, SyncMode.Push, ancestor);
        var actions = Actions(result);

        Assert.Equal(SyncActionType.Skip, actions["keep.txt"]);
        Assert.Equal(SyncActionType.SendToServer, actions["push-me.txt"]);
        Assert.Equal(SyncActionType.DeleteOnServer, actions["gone.txt"]);
        // No row proves the client ever had it, so it is left alone rather than wiped.
        Assert.Equal(SyncActionType.Skip, actions["server-extra.txt"]);

        Assert.DoesNotContain(result.Entries, p => p.Action == SyncActionType.SendToClient);
        Assert.DoesNotContain(result.Entries, p => p.Action == SyncActionType.DeleteOnClient);
    }

    [Fact]
    public void Push_UnknownDeletion_ServerEditedSinceAncestor_Skip()
    {
        // The client copy is gone and a row proves the client once had it — but the server copy
        // has been edited since that agreement. Deleting it destroys the only surviving version
        // of that edit, and Push has no resurrection path to bring it back. CONTRACT.md's Push
        // table requires the row to say the client had it AND that it is unchanged; checking
        // only Status == "exists" is what this test catches.
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("f.txt", 220, T2));
        var result = Plan(client, server, SyncMode.Push, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.Skip, result.Entries[0].Action);
    }

    [Fact]
    public void Push_UnknownDeletion_ServerEditedButMirror_DeleteOnServer()
    {
        // --mirror is the explicit "make the server match, whatever it is holding" opt-in, so it
        // overrides the unchanged check as well as the row check.
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("f.txt", 220, T2));
        var result = Plan(client, server, SyncMode.Push, Ancestor(Row("f.txt", 100, T1)),
                          mirrorDeletes: true);
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.DeleteOnServer, result.Entries[0].Action);
    }

    [Fact]
    public void Push_ServerChangedUnderneath_StillSendToServer()
    {
        // Push means the server does not get a vote, even when its copy is the newer one.
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = MakeManifest(new FileEntry("f.txt", 220, T2));
        var result = Plan(client, server, SyncMode.Push, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[0].Action);
    }

    [Fact]
    public void Push_ServerLostFile_RePushed()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = new FileManifest();
        var result = Plan(client, server, SyncMode.Push, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToServer, result.Entries[0].Action);
    }

    [Fact]
    public void Push_UnknownServerFile_WithMirror_DeleteOnServer()
    {
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("stray.txt", 100, T1));
        var result = Plan(client, server, SyncMode.Push, ancestor: null, mirrorDeletes: true);
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.DeleteOnServer, result.Entries[0].Action);
    }

    [Fact]
    public void Push_DeleteDisabled_KeepsServerFile()
    {
        var client = new FileManifest();
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var result = Plan(client, server, SyncMode.Push, Ancestor(Row("f.txt", 100, T1)),
                          deleteEnabled: false);
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.Skip, result.Entries[0].Action);
    }

    // ── Pull (server authoritative) — the exact mirror of Push ────────────────

    [Fact]
    public void Pull_NeverEmitsServerSideActions()
    {
        var ancestor = Ancestor(Row("keep.txt", 100, T1), Row("gone.txt", 100, T1));
        var client = MakeManifest(
            new FileEntry("keep.txt", 100, T1),
            new FileEntry("gone.txt", 100, T1),
            new FileEntry("client-extra.txt", 100, T2));
        var server = MakeManifest(
            new FileEntry("keep.txt", 100, T1),
            new FileEntry("pull-me.txt", 50, T2));

        var result = Plan(client, server, SyncMode.Pull, ancestor);
        var actions = Actions(result);

        Assert.Equal(SyncActionType.Skip, actions["keep.txt"]);
        Assert.Equal(SyncActionType.SendToClient, actions["pull-me.txt"]);
        Assert.Equal(SyncActionType.DeleteOnClient, actions["gone.txt"]);
        Assert.Equal(SyncActionType.Skip, actions["client-extra.txt"]);

        Assert.DoesNotContain(result.Entries, p => p.Action == SyncActionType.SendToServer);
        Assert.DoesNotContain(result.Entries, p => p.Action == SyncActionType.DeleteOnServer);
    }

    [Fact]
    public void Pull_UnknownDeletion_ClientEditedSinceAncestor_Skip()
    {
        // Mirror of Push_UnknownDeletion_ServerEditedSinceAncestor_Skip. This is the branch that
        // destroys the user's own local files, so it gets its own test rather than relying on
        // symmetry with the Push case.
        var client = MakeManifest(new FileEntry("f.txt", 220, T2));
        var server = new FileManifest();
        var result = Plan(client, server, SyncMode.Pull, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.Skip, result.Entries[0].Action);
    }

    [Fact]
    public void Pull_UnknownDeletion_ClientUnchanged_DeleteOnClient()
    {
        var client = MakeManifest(new FileEntry("f.txt", 100, T1));
        var server = new FileManifest();
        var result = Plan(client, server, SyncMode.Pull, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.DeleteOnClient, result.Entries[0].Action);
    }

    [Fact]
    public void Pull_ClientChangedUnderneath_StillSendToClient()
    {
        var client = MakeManifest(new FileEntry("f.txt", 220, T2));
        var server = MakeManifest(new FileEntry("f.txt", 100, T1));
        var result = Plan(client, server, SyncMode.Pull, Ancestor(Row("f.txt", 100, T1)));
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.SendToClient, result.Entries[0].Action);
    }

    [Fact]
    public void Pull_UnknownClientFile_WithoutMirror_Skip()
    {
        var client = MakeManifest(new FileEntry("stray.txt", 100, T1));
        var server = new FileManifest();
        var result = Plan(client, server, SyncMode.Pull, ancestor: null, mirrorDeletes: false);
        Assert.Single(result.Entries);
        Assert.Equal(SyncActionType.Skip, result.Entries[0].Action);
    }

    // ── BuildMergedManifest ──────────────────────────────────────────────────

    [Fact]
    public void BuildMergedManifest_ConflictKeepBoth_KeepsClientEntry()
    {
        // The renamed loser is written into the sync folder and picked up by the next scan; the
        // merged manifest records the winner so the path is not dropped from tracking entirely.
        var client = MakeManifest(new FileEntry("f.txt", 150, T2));
        var server = MakeManifest(new FileEntry("f.txt", 220, T2.AddMinutes(5)));
        var plan = new List<SyncPlanEntry> { new(SyncActionType.ConflictKeepBoth, "f.txt") };
        var merged = SyncEngine.BuildMergedManifest(client, server, plan);
        Assert.Equal(150, merged.Get("f.txt")!.FileSize);
    }
}
