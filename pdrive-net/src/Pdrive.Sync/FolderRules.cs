using Pdrive.Cli;

namespace Pdrive.Sync;

/// <summary>Adding/removing backup folders, shared by the daemon (IPC) and pdrivectl (when the daemon is down).
/// Throws <see cref="ConfigException"/> with a message meant for the user.</summary>
public static class FolderRules
{
    public static SyncConfig Add(SyncConfig cfg, string local, string remote)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(PdrivePaths.ExpandHome(local)));
        if (!Directory.Exists(full))
            throw new ConfigException($"Not a folder: {full}");
        remote = remote.TrimEnd('/');
        if (!remote.StartsWith(RemotePath.Root + "/", StringComparison.Ordinal) && remote != RemotePath.Root)
            throw new ConfigException($"Remote path should start with {RemotePath.Root}");

        foreach (var f in cfg.Folders)
        {
            var other = f.LocalPath;
            if (other == full && f.RemoteRoot == remote)
                throw new ConfigException($"{full} is already backed up to {remote}");
            if (other == full)
                throw new ConfigException($"{full} is already backed up to {f.RemoteRoot}; remove it first to change the destination");
            if (IsInside(full, other) || IsInside(other, full))
                throw new ConfigException($"{full} overlaps {other}, which is already backed up");
        }
        return cfg with { Folders = [.. cfg.Folders, new FolderPair(full, remote)] };
    }

    public static SyncConfig Remove(SyncConfig cfg, string local)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(PdrivePaths.ExpandHome(local)));
        var kept = cfg.Folders.Where(f => f.LocalPath != full).ToList();
        if (kept.Count == cfg.Folders.Count)
            throw new ConfigException($"{full} isn't backed up");
        return cfg with { Folders = kept };
    }

    private static bool IsInside(string path, string folder) => path.StartsWith(folder + "/", StringComparison.Ordinal);
}
