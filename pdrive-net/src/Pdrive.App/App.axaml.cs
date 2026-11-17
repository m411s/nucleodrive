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

        // The window is built when first shown and released when closed to the tray (it holds
        // ~150 MB of rendering resources); the view models and their state stay.
        var ui = new WindowServices(() => ShowWindow(), () => _window);
        var settings = new SettingsViewModel(cli, drive, startup, daemon, ui, ui);
        _vm = new MainWindowViewModel(
            new BrowserViewModel(drive, ui, ui),
            new BackupViewModel(daemon, ui, ui, a => Dispatcher.UIThread.Post(a)),
            new SyncViewModel(daemon, drive, ui, a => Dispatcher.UIThread.Post(a), OpenInFileManager, TimeSpan.FromSeconds(1.5)),
            settings);
        _desktop = desktop;
        _showSetup = () => ShowSetup(new SetupViewModel(cli, drive, syncSetup, startup, setupState, daemon, ui), _vm);
        settings.ShowSetup = _showSetup;
        settings.ShowAbout = ShowAbout;

        _tray = new TrayController(this, daemon, () => ShowWindow(), () => Quit(desktop), OpenInFileManager, ShowAbout);
        _monitor.Start();
        _singleInstance = SingleInstance.Listen(paths.AppSocket, () => Dispatcher.UIThread.Post(() => ShowWindow()));
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
            _window?.Show("Setup finished. Syncing starts now.", false);
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
    private MainWindowViewModel? _vm;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    private MainWindow ShowWindow()
    {
        if (_window is null)
        {
            var window = new MainWindow { DataContext = _vm };
            window.Closing += (_, _) =>
            {
                if (!_trayMode && !_quitting && _desktop is not null)
                    Quit(_desktop);
            };
            window.Closed += (_, _) =>
            {
                window.DataContext = null;
                if (ReferenceEquals(_window, window))
                    _window = null;
                // Give the rendering memory back while only the tray runs.
                Dispatcher.UIThread.Post(() =>
                {
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                }, DispatcherPriority.Background);
            };
            _window = window;
        }
        _window.Show();
        _window.Activate();
        if (!_browserLoaded && _vm is not null)
        {
            _browserLoaded = true; // first CLI call only once the window is actually shown
            _ = _vm.Browser.RefreshAsync();
        }
        return _window;
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
