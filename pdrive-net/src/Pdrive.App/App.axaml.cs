using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Pdrive.App.Services;
using Pdrive.App.ViewModels;
using Pdrive.App.Views;
using Pdrive.Cli;
using Pdrive.Ipc;
using Pdrive.Sync;

namespace Pdrive.App;

public sealed class App : Application
{
    private DaemonMonitor? _monitor;
    private TrayController? _tray;
    private IDisposable? _singleInstance;
    private MainWindow? _window;
    private bool _trayMode;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            Start(desktop);
        base.OnFrameworkInitializationCompleted();
    }

    private void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // --tray (used by the login autostart entry): start hidden, closing the window hides it.
        // Otherwise closing the window quits, so you're never left without a way back if the
        // desktop doesn't show tray icons (GNOME without the AppIndicator extension).
        _trayMode = desktop.Args?.Contains("--tray") == true;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var paths = PdrivePaths.FromEnvironment();

        _monitor = new DaemonMonitor(paths.DaemonSocket);
        var daemon = new MonitorDaemonControl(_monitor);

        var drive = new DriveClientProvider(() => new ProtonDriveCli());
        var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("pdrive");

        var cli = new CliSetup(CliInstaller.ForCurrentUser(http));
        var startup = StartupSettings.ForCurrentUser();
        var setupState = new SetupState(paths.ConfigDir);
        var syncSetup = new SyncSetup(daemon, new ConfigStore(paths));

        _window = new MainWindow();
        var settings = new SettingsViewModel(cli, drive, startup, daemon, _window, _window);
        var vm = new MainWindowViewModel(
            new BrowserViewModel(drive, _window, _window),
            new BackupViewModel(daemon, _window, _window, a => Dispatcher.UIThread.Post(a)),
            new SyncViewModel(daemon, drive, _window, a => Dispatcher.UIThread.Post(a), OpenInFileManager),
            settings);
        _window.DataContext = vm;
        _showSetup = () => ShowSetup(new SetupViewModel(cli, drive, syncSetup, startup, setupState, daemon, _window), vm);
        settings.ShowSetup = _showSetup;
        settings.ShowAbout = ShowAbout;
        _window.Closing += (_, e) =>
        {
            if (_trayMode && !_quitting)
            {
                e.Cancel = true;
                _window.Hide();
            }
            else
            {
                Quit(desktop);
            }
        };

        _tray = new TrayController(this, daemon, ShowWindow, () => Quit(desktop), OpenInFileManager, ShowAbout);
        _monitor.Start();
        _singleInstance = SingleInstance.Listen(paths.AppSocket, () => Dispatcher.UIThread.Post(ShowWindow));
        desktop.Exit += (_, _) => Cleanup();

        if (!_trayMode)
            ShowWindow();

        // First run: the setup wizard (also from the login autostart, so a fresh install greets you).
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (setupState.ShouldRun(await syncSetup.CurrentAsync()))
                _showSetup();
        });
    }

    private Action _showSetup = () => { };
    private SetupWindow? _setupWindow;

    private async void ShowSetup(SetupViewModel setup, MainWindowViewModel main)
    {
        if (_setupWindow is not null)
        {
            _setupWindow.Activate();
            return;
        }
        _setupWindow = new SetupWindow { DataContext = setup };
        _setupWindow.Closed += (_, _) => _setupWindow = null;
        setup.Completed += () =>
        {
            _setupWindow?.Close();
            _ = main.Browser.RefreshAsync();
            _ = main.Sync.LoadChoicesAsync();
            if (main.Settings is { } s)
                _ = s.LoadAsync();
            ((IToaster?)_window)?.Show("Setup finished. Syncing starts now.");
        };
        _setupWindow.Show();
        _setupWindow.Activate();
        try
        {
            await setup.InitializeAsync();
        }
        catch (Exception e)
        {
            setup.Error = "Couldn't prepare the setup: " + e.Message;
        }
    }

    private void ShowAbout()
    {
        var about = new AboutWindow(OpenInFileManager);
        if (_window is { IsVisible: true })
            about.ShowDialog(_window);
        else
            about.Show();
    }

    private bool _quitting;
    private bool _browserLoaded;

    private void ShowWindow()
    {
        if (_window is null)
            return;
        _window.Show();
        _window.Activate();
        if (!_browserLoaded && _window.DataContext is MainWindowViewModel vm)
        {
            _browserLoaded = true; // first CLI call only once the window is actually shown
            _ = vm.Browser.RefreshAsync();
        }
    }

    private static void OpenInFileManager(string folder)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", [folder]) { UseShellExecute = false })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // no xdg-open; nothing sensible to do
        }
    }

    private void Quit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_quitting)
            return;
        _quitting = true;
        _window?.Close();
        desktop.Shutdown();
    }

    private void Cleanup()
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        _monitor?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
    }
}
