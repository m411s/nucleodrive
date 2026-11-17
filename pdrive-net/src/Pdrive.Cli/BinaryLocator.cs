namespace Pdrive.Cli;

public static class BinaryLocator
{
    public const string EnvVar = "PROTON_DRIVE_BIN";
    private static readonly string[] Fallbacks = ["~/.local/bin/proton-drive", "~/bin/proton-drive", "~/Downloads/proton-drive"];

    /// <summary>$PROTON_DRIVE_BIN, then $PATH, then a few usual download locations.</summary>
    public static string? Find(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        if (getEnv(EnvVar) is { Length: > 0 } env && IsExecutable(env))
            return env;

        foreach (var dir in (getEnv("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, "proton-drive");
            if (IsExecutable(candidate))
                return candidate;
        }

        var home = getEnv("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var data = getEnv("XDG_DATA_HOME") is { Length: > 0 } d ? d : Path.Combine(home, ".local/share");
        var installed = Path.Combine(data, "pdrive/cli/proton-drive"); // where Settings › Install puts it
        return Fallbacks.Select(f => Path.Combine(home, f[2..])).Append(installed).FirstOrDefault(IsExecutable);
    }

    public static bool IsExecutable(string path)
    {
        if (!File.Exists(path))
            return false;
        if (OperatingSystem.IsWindows())
            return true;
        const UnixFileMode anyExec = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & anyExec) != 0;
    }
}
