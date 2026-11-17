namespace Pdrive.Sync.TwoWay;

// Paths in the two-way sync are relative to the sync root (~/proton-drive ⇄ /my-files),
// '/'-separated, with raw (unescaped) names: "Photos/2024/a.jpg".

/// <summary>A local file or folder.</summary>
/// <param name="Mtime">Last write time in 100-ns units since the Unix epoch.</param>
/// <param name="Ignored">Present but must not be synced right now (still being written, a symlink,
/// unreadable): the planner leaves it alone and never treats it as deleted.</param>
public sealed record LocalItem(string Path, bool IsDir, long Size, long Mtime, long Inode, bool Ignored = false);

/// <summary>A remote file or folder from a listing.</summary>
public sealed record RemoteItem(string Path, bool IsDir, string Uid, string? RevisionUid, long Size,
    DateTimeOffset? ModifiedAt, string? Sha1, bool IsProtonDocument = false);

/// <summary>What both sides looked like when the item was last in sync.</summary>
public sealed record BaseItem(string Path, bool IsDir, string? Uid, string? RevisionUid, long LocalSize, long LocalMtime, long Inode);

public abstract record SyncOp(string Path);

/// <summary>Upload the local file (as a new revision if it exists remotely).</summary>
public sealed record UploadOp(string Path) : SyncOp(Path);

/// <summary>Download the remote file; if <paramref name="TrashLocalFirst"/>, the existing local file
/// lost a conflict and goes to the desktop Trash first.</summary>
public sealed record DownloadOp(string Path, RemoteItem Remote, bool TrashLocalFirst) : SyncOp(Path);

public sealed record CreateRemoteFolderOp(string Path) : SyncOp(Path);

public sealed record CreateLocalFolderOp(string Path) : SyncOp(Path);

/// <summary>Move to the Drive trash. <paramref name="Count"/> includes everything inside a folder.</summary>
public sealed record TrashRemoteOp(string Path, bool IsDir, int Count) : SyncOp(Path);

/// <summary>Move to the desktop Trash. <paramref name="Count"/> includes everything inside a folder.</summary>
public sealed record TrashLocalOp(string Path, bool IsDir, int Count) : SyncOp(Path);

/// <summary>Rename/move the remote node to follow a local rename.</summary>
public sealed record MoveRemoteOp(string From, string To) : SyncOp(To);

/// <summary>Rename/move the local item to follow a remote rename.</summary>
public sealed record MoveLocalOp(string From, string To) : SyncOp(To);

/// <summary>Both sides already match (e.g. same content created on both); just record the baseline.</summary>
public sealed record RecordOp(string Path) : SyncOp(Path);

/// <summary>Gone on both sides; drop it from the baseline.</summary>
public sealed record ForgetOp(string Path) : SyncOp(Path);

public sealed record SyncPlan(IReadOnlyList<SyncOp> Ops, int LocalDeletes, int RemoteDeletes, bool DeletionsHeld)
{
    public bool IsEmpty => Ops.Count == 0;
}

public sealed record PlanOptions
{
    /// <summary>More deletions than this (on either side, counting folder contents) are held back
    /// until <see cref="DeletionsConfirmed"/>.</summary>
    public int MaxDeletesWithoutConfirm { get; init; } = 50;

    public bool DeletionsConfirmed { get; init; }

    /// <summary>SHA-1 (lowercase hex) of a local file, computed on demand to settle conflicts; null if unreadable.</summary>
    public Func<string, string?> LocalSha1 { get; init; } = _ => null;
}
