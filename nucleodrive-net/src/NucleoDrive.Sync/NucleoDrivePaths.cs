namespace NucleoDrive.Sync;

/// <summary>Where config, state and sockets live. Built from XDG variables (overridable with
/// NUCLEODRIVE_CONFIG_DIR / NUCLEODRIVE_STATE_DIR / NUCLEODRIVE_RUNTIME_DIR), or under one root for tests.</summary>
public sealed record NucleoDrivePaths(string ConfigDir, string StateDir, string RuntimeDir, string LegacyConfigDir, string LegacyStateDir)
{
    public string ConfigFile => Path.Combine(ConfigDir, "config.json");
    public string StateDb => Path.Combine(StateDir, "state.db");
    public string DaemonLock => Path.Combine(StateDir, "daemon.lock");
    public string DaemonSocket => Path.Combine(RuntimeDir, "daemon.sock");
    public string AppSocket => Path.Combine(RuntimeDir, "app.sock");

    /// <summary>The Python prototype's files (proton-drive-gui), imported once.</summary>
    public string LegacyConfigFile => Path.Combine(LegacyConfigDir, "config.json");
    public string LegacyStateFile => Path.Combine(LegacyStateDir, "state.json");

    public static NucleoDrivePaths FromEnvironment(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var home = getEnv("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Xdg(string var, string fallback) => getEnv(var) is { Length: > 0 } v ? v : Path.Combine(home, fallback);

        var configHome = Xdg("XDG_CONFIG_HOME", ".config");
        var stateHome = Xdg("XDG_STATE_HOME", ".local/state");
        var stateDir = getEnv("NUCLEODRIVE_STATE_DIR") is { Length: > 0 } s ? s : Path.Combine(stateHome, "nucleodrive");
        var runtimeDir = getEnv("NUCLEODRIVE_RUNTIME_DIR") is { Length: > 0 } r ? r
            : getEnv("XDG_RUNTIME_DIR") is { Length: > 0 } xr ? Path.Combine(xr, "nucleodrive")
            : Path.Combine(stateDir, "run");
        return new NucleoDrivePaths(
            getEnv("NUCLEODRIVE_CONFIG_DIR") is { Length: > 0 } c ? c : Path.Combine(configHome, "nucleodrive"),
            stateDir,
            runtimeDir,
            Path.Combine(configHome, "proton-drive-gui"),
            Path.Combine(stateHome, "proton-drive-gui"));
    }

    /// <summary>NucleoDrive was called pdrive up to 0.2.1. Moves its config, state and data folders
    /// (~/.config/pdrive, ~/.local/state/pdrive, ~/.local/share/pdrive) to the new names, once, and
    /// re-points ~/.local/bin/proton-drive if it linked to the CLI in the old data folder. A folder is
    /// only moved when the new one doesn't exist yet and no NUCLEODRIVE_*_DIR override is set.
    /// Safe to call from the daemon and the app at the same time. Returns the folders it moved.</summary>
    public static IReadOnlyList<string> MigrateFromPdrive(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var home = getEnv("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Xdg(string var, string fallback) => getEnv(var) is { Length: > 0 } v ? v : Path.Combine(home, fallback);
        var moved = new List<string>();

        void Move(string? overrideVar, string baseDir)
        {
            if (overrideVar is not null && getEnv(overrideVar) is { Length: > 0 })
                return;
            var from = Path.Combine(baseDir, "pdrive");
            var to = Path.Combine(baseDir, "nucleodrive");
            if (!Directory.Exists(from) || Directory.Exists(to) || File.Exists(to))
                return;
            try
            {
                Directory.Move(from, to);
                moved.Add(from);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the other process won the race, or it isn't ours to move; the old folder stays
            }
        }

        Move("NUCLEODRIVE_CONFIG_DIR", Xdg("XDG_CONFIG_HOME", ".config"));
        Move("NUCLEODRIVE_STATE_DIR", Xdg("XDG_STATE_HOME", ".local/state"));
        var dataHome = Xdg("XDG_DATA_HOME", ".local/share");
        Move(null, dataHome);
        // install.sh and the AppImage write icons into the new data folder before the app first runs,
        // so move what's left (the CLI in cli/) entry by entry.
        var oldData = Path.Combine(dataHome, "pdrive");
        var newData = Path.Combine(dataHome, "nucleodrive");
        if (Directory.Exists(oldData) && Directory.Exists(newData))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(oldData))
            {
                var to = Path.Combine(newData, Path.GetFileName(entry));
                if (Directory.Exists(to) || File.Exists(to))
                    continue;
                try
                {
                    if (Directory.Exists(entry))
                        Directory.Move(entry, to);
                    else
                        File.Move(entry, to);
                    moved.Add(entry);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // raced with the other process, or not ours; leave it
                }
            }
            try
            {
                Directory.Delete(oldData, recursive: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // something stayed behind (it already existed in the new folder)
            }
        }

        // Settings › Install links ~/.local/bin/proton-drive to the CLI in the data folder.
        var link = Path.Combine(home, ".local/bin/proton-drive");
        var oldCli = Path.Combine(dataHome, "pdrive/cli/proton-drive");
        var newCli = Path.Combine(dataHome, "nucleodrive/cli/proton-drive");
        try
        {
            if (new FileInfo(link).LinkTarget == oldCli && File.Exists(newCli))
            {
                var tmp = $"{link}.{Guid.NewGuid():N}.tmp";
                File.CreateSymbolicLink(tmp, newCli);
                File.Move(tmp, link, overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // leave the link alone; the app finds the CLI in the new folder anyway
        }
        return moved;
    }

    public static NucleoDrivePaths Under(string root) => new(
        Path.Combine(root, "config"), Path.Combine(root, "state"), Path.Combine(root, "run"),
        Path.Combine(root, "legacy-config"), Path.Combine(root, "legacy-state"));

    public static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/")
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~').TrimStart('/'))
            : path;

    public const UnixFileMode PrivateDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    public const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Creates a folder only you can read (0700), or tightens an existing one.</summary>
    public static void EnsurePrivateDir(string dir)
    {
        Directory.CreateDirectory(dir, PrivateDir);
        try
        {
            File.SetUnixFileMode(dir, PrivateDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // not ours to change (e.g. a shared folder); keep going
        }
    }

    /// <summary>Writes a file only you can read (0600) atomically.</summary>
    internal static void AtomicWrite(string path, string contents)
    {
        EnsurePrivateDir(Path.GetDirectoryName(path)!);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp"; // unique: two writers must not share a temp file
        using (var f = new FileStream(tmp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = PrivateFile }))
        using (var w = new StreamWriter(f))
            w.Write(contents);
        File.Move(tmp, path, overwrite: true);
    }
}
