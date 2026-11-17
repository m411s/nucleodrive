using Avalonia;
using Pdrive.App.Services;
using Pdrive.Sync;

namespace Pdrive.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // A second launch (e.g. from the app menu while the tray instance runs) just shows the first one;
        // a second `--tray` (login autostart, reinstall) does nothing at all.
        var tray = args.Contains("--tray");
        if (SingleInstance.TrySignalExisting(PdrivePaths.FromEnvironment().AppSocket, show: !tray))
            return 0;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    // Also used by the designer and headless tests.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
