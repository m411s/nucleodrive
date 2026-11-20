using System.Diagnostics;

namespace NucleoDrive.App.Services;

/// <summary>"Start at login" (XDG autostart entry for the tray) and the background service
/// (the NucleoDrive systemd user unit that does the syncing and backups).</summary>
public interface IStartupSettings
{
    bool TrayAtLogin { get; }
    void SetTrayAtLogin(bool enabled);

    /// <summary>True/false if the service is installed; null if it isn't (or systemd isn't available).</summary>
    Task<bool?> IsServiceEnabledAsync();

    /// <summary>Enables and starts, or disables and stops, the background service.</summary>
    Task SetServiceEnabledAsync(bool enabled);
}

public sealed class StartupSettings(string configHome, string appExecutable) : IStartupSettings
{
    public const string ServiceName = "nucleodrive.service";

    /// <param name="systemConfigDirs">$XDG_CONFIG_DIRS: where a system-wide install (the .deb) puts
    /// its autostart entry; a user entry with the same name overrides it.</param>
    public StartupSettings(string configHome, string appExecutable, IReadOnlyList<string>? systemConfigDirs = null)
        : this(configHome, appExecutable)
    {
        _systemConfigDirs = systemConfigDirs ?? [];
    }

    private readonly IReadOnlyList<string> _systemConfigDirs = [];

    public static StartupSettings ForCurrentUser() => new(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        // Inside an AppImage the process runs from a temporary mount; autostart must point at the AppImage file.
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage ? appImage : Environment.ProcessPath ?? "nucleodrive",
        (Environment.GetEnvironmentVariable("XDG_CONFIG_DIRS") is { Length: > 0 } x ? x : "/etc/xdg").Split(':', StringSplitOptions.RemoveEmptyEntries));

    public string AutostartFile => Path.Combine(configHome, "autostart", "nucleodrive-tray.desktop");

    public bool TrayAtLogin
    {
        get
        {
            var file = File.Exists(AutostartFile) ? AutostartFile
                : _systemConfigDirs.Select(d => Path.Combine(d, "autostart", "nucleodrive-tray.desktop")).FirstOrDefault(File.Exists);
            if (file is null)
                return false;
            var lines = File.ReadAllLines(file).Select(l => l.Trim()).ToList();
            return !lines.Contains("Hidden=true") && !lines.Contains("X-GNOME-Autostart-enabled=false");
        }
    }

    /// <summary>Off writes Hidden=true (the XDG way to switch an autostart entry off), which
    /// install.sh keeps on reinstall.</summary>
    public void SetTrayAtLogin(bool enabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
        var contents = $"""
            [Desktop Entry]
            Type=Application
            Name=NucleoDrive (tray)
            Exec="{appExecutable}" --tray
            Icon=io.github.m411s.nucleodrive
            X-GNOME-Autostart-enabled=true
            NoDisplay=true
            {(enabled ? "" : "Hidden=true")}

            """;
        var tmp = AutostartFile + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, AutostartFile, overwrite: true);
    }

    public async Task<bool?> IsServiceEnabledAsync()
    {
        var (code, output) = await SystemctlAsync("is-enabled", ServiceName);
        return output.Trim() switch
        {
            "enabled" or "enabled-runtime" or "static" or "alias" => true,
            "disabled" or "masked" or "masked-runtime" => false,
            _ => code == 0 ? true : null, // not installed, or no systemd
        };
    }

    /// <summary>On: unmask, enable and start. Off: disable and stop; if it's still enabled (the .deb
    /// enables it for all users), mask it for this user so it stays off.</summary>
    public async Task SetServiceEnabledAsync(bool enabled)
    {
        if (enabled)
        {
            await SystemctlAsync("unmask", ServiceName);
            await Run("enable", "--now", ServiceName);
            return;
        }
        await Run("disable", "--now", ServiceName);
        if (await IsServiceEnabledAsync() == true)
            await Run("mask", "--now", ServiceName);

        static async Task Run(params string[] args)
        {
            var (code, output) = await SystemctlAsync(args);
            if (code != 0)
                throw new InvalidOperationException($"systemctl failed: {output.Trim()}");
        }
    }

    private static async Task<(int Code, string Output)> SystemctlAsync(params string[] args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("systemctl", ["--user", .. args])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            return (p.ExitCode, await stdout + await stderr);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return (-1, e.Message);
        }
    }
}
