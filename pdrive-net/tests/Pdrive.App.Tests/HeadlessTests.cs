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
    public void About_shows_version_licence_and_project_link()
    {
        var opened = new List<string>();
        var about = new AboutWindow(opened.Add);
        about.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var texts = about.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.Contains(texts, t => t.StartsWith("Version ", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("MIT License", StringComparison.Ordinal) && t.Contains("m411s"));
        Assert.Contains("https://github.com/m411s/pdrive", texts);
        about.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Project on GitHub")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(["https://github.com/m411s/pdrive"], opened);
        about.Close();
    }

    [AvaloniaFact]
    public void Setup_window_renders_the_current_step()
    {
        var w = new SetupWindow { DataContext = new SetupViewModel(null!, null!, null!, null!, null!, null!, null!) };
        w.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Contains(w.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Install the Proton Drive CLI");
        w.Close();
    }

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
        var sync = new SyncViewModel(daemon, new FakeDrive(), window, a => a(), _ => { });
        window.DataContext = new MainWindowViewModel(browser, backup, sync);
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
        tabs.SelectedIndex = 2;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Up to date (3 uploaded)", texts);
        Assert.Contains("/home/me/Docs", texts);

        daemon.Publish(FakeDaemon.WithSync(SyncState.Syncing, "Downloading Docs/a.txt"));
        tabs.SelectedIndex = 1;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Syncing", texts);
        Assert.Contains("Downloading Docs/a.txt", texts);
        Assert.Contains("Local folder: /home/me/proton-drive", texts);
        window.Close();
    }
}
