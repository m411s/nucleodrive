using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Pdrive.App.ViewModels;
using Pdrive.App.Views;
using Pdrive.Ipc;

[assembly: AvaloniaTestApplication(typeof(Pdrive.App.Tests.TestAppBuilder))]

namespace Pdrive.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class HeadlessTests
{
    [AvaloniaFact]
    public void App_icon_is_embedded()
    {
        var uri = new Uri("avares://pdrive/Assets/pdrive.png");
        Assert.True(Avalonia.Platform.AssetLoader.Exists(uri));
        using var png = Avalonia.Platform.AssetLoader.Open(uri);
        Span<byte> magic = stackalloc byte[4];
        png.ReadExactly(magic);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], magic.ToArray());
    }

    [AvaloniaFact]
    public async Task Main_window_shows_entries_and_backup_status()
    {
        var window = new MainWindow();
        var daemon = new FakeDaemon();
        var browser = new BrowserViewModel(new FakeDrive(), null, window, window);
        var backup = new BackupViewModel(daemon, window, window, a => a());
        window.DataContext = new MainWindowViewModel(browser, backup);
        window.Show();

        await browser.RefreshAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var list = window.FindControl<ListBox>("EntryList")!;
        Assert.Equal(2, list.ItemCount);
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Docs", texts);
        Assert.Contains("/my-files", texts);

        daemon.Publish(FakeDaemon.With(DaemonState.Idle, "Up to date (3 uploaded)"));
        var tabs = window.GetLogicalDescendants().OfType<TabControl>().Single();
        tabs.SelectedIndex = 1;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Up to date (3 uploaded)", texts);
        Assert.Contains("/home/me/Docs", texts);
        window.Close();
    }
}
