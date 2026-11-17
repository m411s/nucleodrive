using Pdrive.Sync.TwoWay;

namespace Pdrive.Sync.Tests;

/// <summary>The planner's decision table, renames, folders, safety rules and ordering.</summary>
public sealed class SyncPlannerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // 100-ns ticks since the epoch for T0 + seconds
    private static long Ticks(int seconds) => (T0.AddSeconds(seconds).UtcTicks - DateTime.UnixEpoch.Ticks);

    private static BaseItem BF(string p, string rev = "r1", long size = 1, int mtime = 0, long inode = 0) =>
        new(p, false, "u:" + p, rev, size, Ticks(mtime), inode);
    private static BaseItem BD(string p, long inode = 0) => new(p, true, "u:" + p, null, 0, 0, inode);
    private static LocalItem LF(string p, long size = 1, int mtime = 0, long inode = 0, bool ignored = false) =>
        new(p, false, size, Ticks(mtime), inode, ignored);
    private static LocalItem LD(string p, long inode = 0) => new(p, true, 0, 0, inode);
    private static RemoteItem RF(string p, string rev = "r1", long size = 1, int mtime = 0, string? sha = null, string? uid = null, bool doc = false) =>
        new(p, false, uid ?? "u:" + p, rev, size, T0.AddSeconds(mtime), sha, doc);
    private static RemoteItem RD(string p, string? uid = null) => new(p, true, uid ?? "u:" + p, null, 0, T0, null);

    private static SyncPlan Plan(BaseItem[] b, LocalItem[] l, RemoteItem[] r, PlanOptions? o = null) =>
        SyncPlanner.Plan(b.ToDictionary(x => x.Path), l.ToDictionary(x => x.Path), r.ToDictionary(x => x.Path), o ?? new PlanOptions());

    private static SyncOp Single(SyncPlan plan) => Assert.Single(plan.Ops);

    // ---- the file table ----

    [Fact]
    public void Nothing_changed_means_nothing_to_do() =>
        Assert.True(Plan([BF("a")], [LF("a")], [RF("a")]).IsEmpty);

    [Fact]
    public void New_local_file_is_uploaded() => Assert.Equal(new UploadOp("a"), Single(Plan([], [LF("a")], [])));

    [Fact]
    public void New_remote_file_is_downloaded() =>
        Assert.Equal(new DownloadOp("a", RF("a"), false), Single(Plan([], [], [RF("a")])));

    [Fact]
    public void Local_edit_is_uploaded() => Assert.Equal(new UploadOp("a"), Single(Plan([BF("a")], [LF("a", mtime: 5)], [RF("a")])));

    [Fact]
    public void Remote_edit_is_downloaded_without_trashing_the_old_local_copy() =>
        Assert.Equal(new DownloadOp("a", RF("a", "r2"), false), Single(Plan([BF("a")], [LF("a")], [RF("a", "r2")])));

    [Fact]
    public void Edited_on_both_sides_newest_wins()
    {
        Assert.Equal(new UploadOp("a"), Single(Plan([BF("a")], [LF("a", mtime: 20)], [RF("a", "r2", mtime: 10)])));
        Assert.Equal(new DownloadOp("a", RF("a", "r2", mtime: 30), true), Single(Plan([BF("a")], [LF("a", mtime: 20)], [RF("a", "r2", mtime: 30)])));
    }

    [Fact]
    public void Edited_on_both_sides_to_the_same_content_is_just_recorded()
    {
        var o = new PlanOptions { LocalSha1 = _ => "abc" };
        Assert.Equal(new RecordOp("a"), Single(Plan([BF("a")], [LF("a", size: 3, mtime: 20)], [RF("a", "r2", size: 3, mtime: 10, sha: "abc")], o)));
    }

    [Fact]
    public void Created_on_both_sides_compares_content_then_time()
    {
        var same = new PlanOptions { LocalSha1 = _ => "abc" };
        Assert.Equal(new RecordOp("a"), Single(Plan([], [LF("a", size: 3)], [RF("a", size: 3, sha: "abc")], same)));
        Assert.Equal(new DownloadOp("a", RF("a", sha: "def", mtime: 9), true), Single(Plan([], [LF("a", mtime: 1)], [RF("a", sha: "def", mtime: 9)], same)));
    }

    [Fact]
    public void Local_delete_trashes_on_drive() => Assert.Equal(new TrashRemoteOp("a", false, 1), Single(Plan([BF("a")], [], [RF("a")])));

    [Fact]
    public void Remote_delete_trashes_locally() => Assert.Equal(new TrashLocalOp("a", false, 1), Single(Plan([BF("a")], [LF("a")], [])));

    [Fact]
    public void An_edit_beats_a_deletion()
    {
        Assert.Equal(new DownloadOp("a", RF("a", "r2"), false), Single(Plan([BF("a")], [], [RF("a", "r2")])));
        Assert.Equal(new UploadOp("a"), Single(Plan([BF("a")], [LF("a", mtime: 5)], [])));
    }

    [Fact]
    public void Deleted_on_both_sides_is_forgotten() => Assert.Equal(new ForgetOp("a"), Single(Plan([BF("a")], [], [])));

    [Fact]
    public void Replaced_remote_node_counts_as_a_remote_change() =>
        Assert.Equal(new DownloadOp("a", RF("a", uid: "other"), false), Single(Plan([BF("a")], [LF("a")], [RF("a", uid: "other")])));

    // ---- things that must be left alone ----

    [Fact]
    public void Settling_or_ignored_local_files_are_never_read_as_deleted()
    {
        Assert.True(Plan([BF("a")], [LF("a", mtime: 5, ignored: true)], [RF("a")]).IsEmpty);
        Assert.True(Plan([BF("a")], [LF("a", ignored: true)], [RF("a", "r2")]).IsEmpty);
    }

    [Fact]
    public void Proton_documents_stay_online_only() => Assert.True(Plan([], [], [RF("doc", doc: true)]).IsEmpty);

    // ---- folders ----

    [Fact]
    public void New_local_folder_is_created_before_its_files_are_uploaded()
    {
        var ops = Plan([], [LD("d"), LD("d/e"), LF("d/e/f")], []).Ops;
        Assert.Equal<SyncOp>([new CreateRemoteFolderOp("d"), new CreateRemoteFolderOp("d/e"), new UploadOp("d/e/f")], ops);
    }

    [Fact]
    public void Folder_deleted_locally_is_trashed_on_drive_as_one_item()
    {
        var plan = Plan([BD("d"), BF("d/a"), BF("d/b")], [], [RD("d"), RF("d/a"), RF("d/b")]);
        Assert.Equal(new TrashRemoteOp("d", true, 3), Single(plan));
        Assert.Equal(3, plan.RemoteDeletes);
    }

    [Fact]
    public void Folder_deleted_locally_keeps_what_changed_on_drive()
    {
        var ops = Plan([BD("d"), BF("d/a"), BF("d/b")], [], [RD("d"), RF("d/a", "r2"), RF("d/b")]).Ops;
        Assert.Equal<SyncOp>([new CreateLocalFolderOp("d"), new DownloadOp("d/a", RF("d/a", "r2"), false), new TrashRemoteOp("d/b", false, 1)], ops);
    }

    [Fact]
    public void Folder_deleted_on_drive_keeps_new_local_files()
    {
        var ops = Plan([BD("d"), BF("d/a")], [LD("d"), LF("d/a"), LF("d/new")], []).Ops;
        Assert.Equal<SyncOp>([new CreateRemoteFolderOp("d"), new UploadOp("d/new"), new TrashLocalOp("d/a", false, 1)], ops);
    }

    [Fact]
    public void Folder_deleted_on_drive_is_kept_while_a_file_inside_is_settling()
    {
        var ops = Plan([BD("d"), BF("d/a")], [LD("d"), LF("d/a", ignored: true)], []).Ops;
        Assert.Equal<SyncOp>([new CreateRemoteFolderOp("d")], ops);
    }

    [Fact]
    public void File_here_and_folder_there_lets_drive_win()
    {
        var ops = Plan([], [LF("x")], [RD("x")]).Ops;
        Assert.Equal<SyncOp>([new CreateLocalFolderOp("x"), new TrashLocalOp("x", false, 1)], ops);
    }

    // ---- renames and moves ----

    [Fact]
    public void Remote_rename_moves_the_local_file()
    {
        var plan = Plan([BF("a")], [LF("a")], [RF("b", uid: "u:a")]);
        Assert.Equal(new MoveLocalOp("a", "b"), Single(plan));
    }

    [Fact]
    public void Remote_folder_move_moves_the_local_folder_once()
    {
        var plan = Plan([BD("d"), BF("d/a"), BD("x")], [LD("d"), LF("d/a"), LD("x")],
            [RD("x"), RD("x/d", uid: "u:d"), RF("x/d/a", uid: "u:d/a")]);
        Assert.Equal(new MoveLocalOp("d", "x/d"), Single(plan));
    }

    [Fact]
    public void Remote_rename_with_new_content_moves_then_downloads()
    {
        var ops = Plan([BF("a")], [LF("a")], [RF("b", "r2", uid: "u:a")]).Ops;
        Assert.Equal<SyncOp>([new MoveLocalOp("a", "b"), new DownloadOp("b", RF("b", "r2", uid: "u:a"), false)], ops);
    }

    [Fact]
    public void Local_rename_renames_on_drive()
    {
        Assert.Equal(new MoveRemoteOp("a", "b"), Single(Plan([BF("a", inode: 7)], [LF("b", inode: 7)], [RF("a")])));
        Assert.Equal(new MoveRemoteOp("d", "e"),
            Single(Plan([BD("d", inode: 3), BF("d/f", inode: 4)], [LD("e", inode: 3), LF("e/f", inode: 4)], [RD("d"), RF("d/f")])));
    }

    [Fact]
    public void Local_rename_plus_edit_is_trash_and_upload()
    {
        var ops = Plan([BF("a", inode: 7)], [LF("b", size: 9, inode: 7)], [RF("a")]).Ops;
        Assert.Equal<SyncOp>([new UploadOp("b"), new TrashRemoteOp("a", false, 1)], ops);
    }

    // ---- deletion guard ----

    [Fact]
    public void Too_many_deletions_are_held_until_confirmed()
    {
        BaseItem[] b = [BF("a"), BF("b"), BF("c"), BF("d")];
        LocalItem[] l = [LF("d", mtime: 9)];
        RemoteItem[] r = [RF("a"), RF("b"), RF("c"), RF("d")];
        var held = Plan(b, l, r, new PlanOptions { MaxDeletesWithoutConfirm = 2 });
        Assert.True(held.DeletionsHeld);
        Assert.Equal(3, held.RemoteDeletes);
        Assert.Equal<SyncOp>([new UploadOp("d")], held.Ops); // other changes still go through

        var confirmed = Plan(b, l, r, new PlanOptions { MaxDeletesWithoutConfirm = 2, DeletionsConfirmed = true });
        Assert.False(confirmed.DeletionsHeld);
        Assert.Equal(3, confirmed.Ops.OfType<TrashRemoteOp>().Count());
    }

    [Fact]
    public void Trashing_happens_children_first()
    {
        var ops = Plan([BD("d"), BF("d/a"), BF("top")], [LD("d"), LF("d/a")], [RD("d"), RF("d/a", "r2"), RF("top")], new PlanOptions()).Ops;
        Assert.Equal<SyncOp>([new DownloadOp("d/a", RF("d/a", "r2"), false), new TrashRemoteOp("top", false, 1)], ops);
        var deep = Plan([BF("a/b/c"), BF("a/x")], [], [RF("a/b/c"), RF("a/x")]).Ops;
        Assert.Equal(["a/b/c", "a/x"], deep.Select(o => o.Path));
    }
}
