using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using NucleoDrive.App.ViewModels;
using NucleoDrive.App.Views;
using NucleoDrive.Cli;
using NucleoDrive.Ipc;

namespace NucleoDrive.App.Tests;

/// <summary>Renders every screen to PNGs for reviewing the design:
/// NUCLEODRIVE_SCREENSHOTS=/some/dir ./tests/NucleoDrive.App.Tests/bin/Debug/net10.0/NucleoDrive.App.Tests -class "NucleoDrive.App.Tests.Screenshots"
/// Does nothing unless NUCLEODRIVE_SCREENSHOTS is set.</summary>
public sealed class Screenshots
{
    private static string? Dir => Environment.GetEnvironmentVariable("NUCLEODRIVE_SCREENSHOTS");

    private static void Save(Window w, string name)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()?.Save(Path.Combine(Dir!, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static (MainWindow Window, FakeDaemon Daemon, MainWindowViewModel Vm) CreateMain(ThemeVariant theme)
    {
        Avalonia.Application.Current!.RequestedThemeVariant = theme;
        var window = new MainWindow { Width = 900, Height = 620 };
        var drive = new FakeDrive();
        drive.Tree["/my-files"] =
        [
            new("Documents", true, null, "/my-files/Documents"), new("Photos", true, null, "/my-files/Photos"),
            new("Projects", true, null, "/my-files/Projects"),
            new("Budget 2026.ods", false, 48_213, "/my-files/Budget 2026.ods") { ModifiedAt = DateTimeOffset.Now.AddDays(-2) },
            new("Holiday.mp4", false, 734_003_200, "/my-files/Holiday.mp4") { ModifiedAt = DateTimeOffset.Now.AddDays(-40) },
            new("Notes", false, null, "/my-files/Notes", "application/vnd.proton.doc"),
            new("README.md", false, 2_048, "/my-files/README.md") { ModifiedAt = DateTimeOffset.Now.AddHours(-3) },
        ];
        drive.Tree["/my-files/Projects"] =
        [
            new("Website", true, null, "/my-files/Projects/Website"), new("Thesis", true, null, "/my-files/Projects/Thesis"),
            new("Archive", true, null, "/my-files/Projects/Archive"),
        ];
        drive.Tree["/my-files/Photos"] = [new("2025", true, null, "/my-files/Photos/2025"), new("2026", true, null, "/my-files/Photos/2026")];
        var daemon = new FakeDaemon();
        daemon.SyncFolders = ["Documents", "Projects/Thesis", "Projects/Website"];
        daemon.Folders.Add(new FolderInfo("/home/me/Pictures/Camera", "/my-files/Camera backup"));
        var vm = new MainWindowViewModel(
            new BrowserViewModel(drive, null, window, window),
            new BackupViewModel(daemon, window, window, a => a()),
            new SyncViewModel(daemon, drive, window, a => a(), _ => { }, TimeSpan.FromSeconds(1)),
            new SettingsViewModel(new NoCli(), new NucleoDrive.App.Services.DriveClientProvider(() => drive), new NoStartup(), daemon, window, window, new FixedAppearance()));
        window.DataContext = vm;
        window.Show();
        return (window, daemon, vm);
    }

    [AvaloniaFact]
    public async Task Render_all_screens()
    {
        if (Dir is null)
            return;
        Directory.CreateDirectory(Dir);
        foreach (var (theme, suffix) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            var (w, daemon, vm) = CreateMain(theme);
            await vm.Browser.RefreshAsync();
            var idle = FakeDaemon.WithSync(SyncState.Idle, "Up to date", 0, "Documents", "Photos");
            daemon.Publish(idle with
            {
                State = DaemonState.Idle, Message = "Up to date (3 uploaded)", LastSync = DateTimeOffset.Now.AddMinutes(-4),
                Sync = idle.Sync! with
                {
                    LastFullScan = DateTimeOffset.Now.AddMinutes(-12), LastCheckSeconds = 26, NextCheck = DateTimeOffset.Now.AddMinutes(48),
                },
            });
            await vm.Sync.LoadChoicesAsync();
            await vm.Sync.Choices.Single(c => c.Path == "Documents").EnsureLoadedAsync();
            await vm.Backup.ReloadFoldersAsync();
            await vm.Settings!.LoadAsync();
            for (var i = 0; i < 4; i++)
            {
                vm.PageIndex = i;
                await Task.Delay(250, TestContext.Current.CancellationToken); // let the cross-fade finish
                Save(w, $"main-{i}-{suffix}");
            }
            vm.PageIndex = 3;
            await Task.Delay(250, TestContext.Current.CancellationToken);
            if (w.FindDescendantOfType<Expander>() is { } limits)
            {
                limits.IsExpanded = true;
                await Task.Delay(300, TestContext.Current.CancellationToken);
                limits.BringIntoView();
                await Task.Delay(100, TestContext.Current.CancellationToken);
                Save(w, $"settings-limits-{suffix}");
                limits.IsExpanded = false;
            }
            vm.PageIndex = 1;
            daemon.Publish(FakeDaemon.WithSync(SyncState.NeedsConfirmation, "62 files were deleted in Photos. Move them to the Trash on Drive too?", 62, "Documents", "Photos"));
            await Task.Delay(250, TestContext.Current.CancellationToken);
            Save(w, $"sync-confirm-{suffix}");
            vm.PageIndex = 0;
            vm.Browser.IsDragOver = true;
            await Task.Delay(250, TestContext.Current.CancellationToken);
            Save(w, $"files-drop-{suffix}");
            w.Close();

            var setup = new SetupWindow { DataContext = new SetupViewModel(null!, null!, null!, null!, null!, null!, null!) };
            setup.Show();
            Save(setup, $"setup-{suffix}");
            setup.Close();
            var about = new AboutWindow(_ => { });
            about.Show();
            Save(about, $"about-{suffix}");
            about.Close();
        }
    }
}

internal sealed class NoCli : NucleoDrive.App.Services.ICliSetup
{
    public string? FindInstalled() => "/home/me/.local/bin/proton-drive";
    public Task<string?> VersionOfAsync(string path) => Task.FromResult<string?>("0.9.0");
    public Task<CliRelease> GetLatestAsync() =>
        Task.FromResult(new CliRelease("0.9.0", "linux-x64", new Uri("https://proton.me/download/drive/cli/0.9.0/linux-x64/proton-drive"), new string('a', 128)));
    public Task<string> InstallAsync(CliRelease release, Func<string, Task<bool>> confirmReplace, IProgress<string>? progress) =>
        throw new NotSupportedException();
}

internal sealed class NoStartup : NucleoDrive.App.Services.IStartupSettings
{
    public bool TrayAtLogin { get; private set; } = true;
    public void SetTrayAtLogin(bool enabled) => TrayAtLogin = enabled;
    public Task<bool?> IsServiceEnabledAsync() => Task.FromResult<bool?>(true);
    public Task SetServiceEnabledAsync(bool enabled) => Task.CompletedTask;
}

internal sealed class FixedAppearance : NucleoDrive.App.Services.IAppearance
{
    public NucleoDrive.App.Services.AppTheme Theme { get; set; }
}

internal static class VisualExtensions
{
    public static T? FindDescendantOfType<T>(this Avalonia.Visual v) where T : class =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(v).OfType<T>().FirstOrDefault();
}
