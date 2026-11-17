using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Pdrive.Ipc;

namespace Pdrive.App.Services;

/// <summary>Tray icon (StatusNotifierItem on Linux) mirroring the daemon status. Port of tray.py.</summary>
public sealed class TrayController : IDisposable
{
    private readonly IDaemonControl _daemon;
    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _status = new() { IsEnabled = false };
    private readonly NativeMenuItem _lastSync = new() { IsEnabled = false };
    private readonly NativeMenuItem _syncNow = new("Sync now");
    private readonly NativeMenuItem _pause = new("Pause");
    private readonly NativeMenuItem _openSync = new("Open sync folder");
    private readonly DispatcherTimer _clock;

    public TrayController(Application app, IDaemonControl daemon, Action showWindow, Action quit, Action<string> openFolder)
    {
        _daemon = daemon;
        _openSync.Click += (_, _) =>
        {
            if (_daemon.Status.Sync is { LocalRoot.Length: > 0 } sync)
                openFolder(sync.LocalRoot);
        };
        var open = new NativeMenuItem("Open Proton Drive");
        var quitItem = new NativeMenuItem("Quit");
        open.Click += (_, _) => showWindow();
        quitItem.Click += (_, _) => quit();
        _syncNow.Click += async (_, _) => await Try(daemon.SyncNowAsync);
        _pause.Click += async (_, _) => await Try(daemon.Status.State == DaemonState.Paused ? daemon.ResumeAsync : daemon.PauseAsync);

        _icon = new TrayIcon
        {
            Icon = StatusIcons.For(DaemonState.Stopped),
            ToolTipText = "Proton Drive",
            Menu = new NativeMenu { _status, _lastSync, new NativeMenuItemSeparator(), _syncNow, _pause, _openSync, open, new NativeMenuItemSeparator(), quitItem },
        };
        _icon.Clicked += (_, _) => showWindow();
        TrayIcon.SetIcons(app, [_icon]);

        daemon.StatusChanged += s => Dispatcher.UIThread.Post(() => Update(s));
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Update(_daemon.Status));
        _clock.Start();
        Update(daemon.Status);
    }

    private DaemonState? _shown;

    private void Update(DaemonStatus s)
    {
        var (state, message, last) = TrayState.Combine(s);
        if (_shown != state)
        {
            _icon.Icon = StatusIcons.For(state);
            _shown = state;
        }
        var tip = $"Proton Drive – {message}";
        _icon.ToolTipText = tip.Length > 120 ? tip[..120] : tip;
        _status.Header = message.Length > 80 ? message[..80] + "…" : message;
        _lastSync.Header = "Last sync: " + Units.Ago(last);
        _openSync.IsEnabled = s.Sync is { Folders.Count: > 0 };
        var running = s.State != DaemonState.Stopped;
        _syncNow.IsEnabled = running && s.State != DaemonState.Paused;
        _pause.IsEnabled = running;
        _pause.Header = s.State == DaemonState.Paused ? "Resume" : "Pause";
    }

    private static async Task Try(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (DaemonException)
        {
            // the status (and icon) already show why
        }
    }

    public void Dispose()
    {
        _clock.Stop();
        _icon.Dispose();
    }
}

/// <summary>Tray icons per state: the icons install.sh generates (app icon + status dot, in
/// $XDG_DATA_HOME/pdrive/icons), or, if they're missing, a drawn coloured disc with a white "P".</summary>
public static class StatusIcons
{
    private static readonly Dictionary<DaemonState, WindowIcon> Cache = [];

    public static string IconDir => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share"),
        "pdrive", "icons");

    /// <summary>File name stem for a state: synced = idle, attention = sign in / confirm deletions.</summary>
    public static string NameFor(DaemonState state) => state switch
    {
        DaemonState.Idle => "idle",
        DaemonState.Syncing or DaemonState.Starting => "syncing",
        DaemonState.Paused => "paused",
        DaemonState.Error => "error",
        DaemonState.Login => "attention",
        _ => "stopped",
    };

    /// <summary>The app icon install.sh prepared (Proton's badge, if downloaded), or null.</summary>
    public static WindowIcon? InstalledAppIcon() => Load(Path.Combine(IconDir, "app-256.png"));

    private static WindowIcon? Load(string file)
    {
        try
        {
            return File.Exists(file) ? new WindowIcon(file) : null;
        }
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    public static Color ColorFor(DaemonState state) => Color.Parse(state switch
    {
        DaemonState.Idle => "#3ba55d",
        DaemonState.Syncing or DaemonState.Starting => "#4a7fe0",
        DaemonState.Paused => "#9aa0a6",
        DaemonState.Error => "#d9534f",
        DaemonState.Login => "#e0a030",
        _ => "#5f6368", // not running
    });

    public static WindowIcon For(DaemonState state)
    {
        if (Cache.TryGetValue(state, out var icon))
            return icon;
        if (Load(Path.Combine(IconDir, $"tray-{NameFor(state)}.png")) is { } installed)
            return Cache[state] = installed;
        var color = new SolidColorBrush(ColorFor(state));
        var bmp = new RenderTargetBitmap(new PixelSize(64, 64));
        using (var ctx = bmp.CreateDrawingContext())
        {
            ctx.DrawEllipse(color, null, new Rect(4, 4, 56, 56));
            ctx.DrawRectangle(Brushes.White, null, new Rect(24, 18, 7, 28));          // stem
            ctx.DrawGeometry(Brushes.White, null, HalfDisc(new Point(35, 28), 11, 10)); // bowl
            ctx.DrawGeometry(color, null, HalfDisc(new Point(35, 28), 5.5, 5));        // counter
        }
        return Cache[state] = new WindowIcon(bmp);
    }

    private static StreamGeometry HalfDisc(Point center, double rx, double ry)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(new Point(center.X, center.Y - ry), true);
        c.ArcTo(new Point(center.X, center.Y + ry), new Size(rx, ry), 0, false, SweepDirection.Clockwise);
        c.EndFigure(true);
        return g;
    }
}
