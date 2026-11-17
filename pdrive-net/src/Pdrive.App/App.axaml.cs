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

        IDriveClient? client = null;
        string? clientError = null;
        try
        {
            client = new ProtonDriveCli();
        }
        catch (DriveException e)
        {
            clientError = e.Message;
        }

        _window = new MainWindow();
        var vm = new MainWindowViewModel(
            new BrowserViewModel(client, clientError, _window, _window),
            new BackupViewModel(daemon, _window, _window, a => Dispatcher.UIThread.Post(a)));
        _window.DataContext = vm;
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

        _tray = new TrayController(this, daemon, ShowWindow, () => Quit(desktop));
        _monitor.Start();
        _singleInstance = SingleInstance.Listen(paths.AppSocket, () => Dispatcher.UIThread.Post(ShowWindow));
        desktop.Exit += (_, _) => Cleanup();

        if (!_trayMode)
            ShowWindow();
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
