namespace Pdrive.Sync;

/// <summary>Where config, state and sockets live. Built from XDG variables (overridable with
/// PDRIVE_CONFIG_DIR / PDRIVE_STATE_DIR / PDRIVE_RUNTIME_DIR), or under one root for tests.</summary>
public sealed record PdrivePaths(string ConfigDir, string StateDir, string RuntimeDir, string LegacyConfigDir, string LegacyStateDir)
{
    public string ConfigFile => Path.Combine(ConfigDir, "config.json");
    public string StateDb => Path.Combine(StateDir, "state.db");
    public string DaemonLock => Path.Combine(StateDir, "daemon.lock");
    public string DaemonSocket => Path.Combine(RuntimeDir, "daemon.sock");
    public string AppSocket => Path.Combine(RuntimeDir, "app.sock");

    /// <summary>The Python prototype's files (proton-drive-gui), imported once.</summary>
    public string LegacyConfigFile => Path.Combine(LegacyConfigDir, "config.json");
    public string LegacyStateFile => Path.Combine(LegacyStateDir, "state.json");

    public static PdrivePaths FromEnvironment(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var home = getEnv("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Xdg(string var, string fallback) => getEnv(var) is { Length: > 0 } v ? v : Path.Combine(home, fallback);

        var configHome = Xdg("XDG_CONFIG_HOME", ".config");
        var stateHome = Xdg("XDG_STATE_HOME", ".local/state");
        var stateDir = getEnv("PDRIVE_STATE_DIR") is { Length: > 0 } s ? s : Path.Combine(stateHome, "pdrive");
        var runtimeDir = getEnv("PDRIVE_RUNTIME_DIR") is { Length: > 0 } r ? r
            : getEnv("XDG_RUNTIME_DIR") is { Length: > 0 } xr ? Path.Combine(xr, "pdrive")
            : Path.Combine(stateDir, "run");
        return new PdrivePaths(
            getEnv("PDRIVE_CONFIG_DIR") is { Length: > 0 } c ? c : Path.Combine(configHome, "pdrive"),
            stateDir,
            runtimeDir,
            Path.Combine(configHome, "proton-drive-gui"),
            Path.Combine(stateHome, "proton-drive-gui"));
    }

    public static PdrivePaths Under(string root) => new(
        Path.Combine(root, "config"), Path.Combine(root, "state"), Path.Combine(root, "run"),
        Path.Combine(root, "legacy-config"), Path.Combine(root, "legacy-state"));

    public static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/")
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~').TrimStart('/'))
            : path;

    internal static void AtomicWrite(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }
}
