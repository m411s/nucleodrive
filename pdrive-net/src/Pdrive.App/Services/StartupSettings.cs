using System.Diagnostics;

namespace Pdrive.App.Services;

/// <summary>"Start at login" (XDG autostart entry for the tray) and the background service
/// (the pdrive systemd user unit that does the syncing and backups).</summary>
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
    public const string ServiceName = "pdrive.service";

    public static StartupSettings ForCurrentUser() => new(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        Environment.ProcessPath ?? "pdrive");

    public string AutostartFile => Path.Combine(configHome, "autostart", "pdrive-tray.desktop");

    public bool TrayAtLogin
    {
        get
        {
            if (!File.Exists(AutostartFile))
                return false;
            var lines = File.ReadAllLines(AutostartFile).Select(l => l.Trim()).ToList();
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
            Name=Proton Drive (tray)
            Exec="{appExecutable}" --tray
            Icon=pdrive
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
            "enabled" => true,
            "disabled" => false,
            _ => code == 0 ? true : null, // not installed, or no systemd
        };
    }

    public async Task SetServiceEnabledAsync(bool enabled)
    {
        var (code, output) = await SystemctlAsync(enabled ? "enable" : "disable", "--now", ServiceName);
        if (code != 0)
            throw new InvalidOperationException($"systemctl failed: {output.Trim()}");
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
