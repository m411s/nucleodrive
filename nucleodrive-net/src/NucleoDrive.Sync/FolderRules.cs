using NucleoDrive.Cli;

namespace NucleoDrive.Sync;

/// <summary>Adding/removing backup folders, shared by the daemon (IPC) and nucleodrivectl (when the daemon is down).
/// Throws <see cref="ConfigException"/> with a message meant for the user.</summary>
public static class FolderRules
{
    public static SyncConfig Add(SyncConfig cfg, string local, string remote)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(NucleoDrivePaths.ExpandHome(local)));
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
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(NucleoDrivePaths.ExpandHome(local)));
        var kept = cfg.Folders.Where(f => f.LocalPath != full).ToList();
        if (kept.Count == cfg.Folders.Count)
            throw new ConfigException($"{full} isn't backed up");
        return cfg with { Folders = kept };
    }

    private static bool IsInside(string path, string folder) => path.StartsWith(folder + "/", StringComparison.Ordinal);
}

/// <summary>Where the two-way sync folder lives (the "sync.local" setting).</summary>
public static class SyncRootRules
{
    /// <summary>Moves the sync folder to <paramref name="local"/> ("~" allowed). Refused while any Drive
    /// folder is synced: their baselines describe files under the current location.</summary>
    /// <exception cref="ConfigException">Not usable, or folders are still synced.</exception>
    public static SyncConfig SetLocal(SyncConfig cfg, string local)
    {
        if (string.IsNullOrWhiteSpace(local))
            throw new ConfigException("Choose a folder for syncing.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(NucleoDrivePaths.ExpandHome(local.Trim())));
        if (full == "/" || full == Path.TrimEndingDirectorySeparator(Path.GetFullPath(NucleoDrivePaths.ExpandHome("~"))))
            throw new ConfigException("Pick a dedicated folder (like ~/proton-drive), not / or your whole home folder.");
        if (File.Exists(full))
            throw new ConfigException($"{full} is a file, not a folder.");
        if (cfg.Sync.Folders.Count > 0 && full != cfg.Sync.LocalPath)
            throw new ConfigException($"Stop syncing all folders before moving the sync folder (it's {cfg.Sync.LocalPath} now).");
        return cfg with { Sync = cfg.Sync with { Local = full } };
    }
}
