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
    // Real Skia rendering (not the headless stub), so screenshots look like the app.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
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

    private static List<string?> Texts(Window w)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
    }

    [AvaloniaFact]
    public async Task Main_window_navigates_between_pages_and_shows_status()
    {
        var window = new MainWindow();
        var daemon = new FakeDaemon();
        var toast = new FakeToaster();
        var dialogs = new FakeDialogs();
        var browser = new BrowserViewModel(new FakeDrive(), null, dialogs, toast);
        var backup = new BackupViewModel(daemon, dialogs, toast, a => a());
        var sync = new SyncViewModel(daemon, new FakeDrive(), toast, a => a(), _ => { });
        var vm = new MainWindowViewModel(browser, backup, sync);
        window.DataContext = vm;
        window.Show();

        await browser.RefreshAsync();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "EntryList");
        Assert.Equal(2, list.ItemCount);
        var texts = Texts(window);
        Assert.Contains("Docs", texts);
        Assert.Contains("My files", texts); // breadcrumb
        Assert.Contains("Service stopped", texts);

        daemon.Publish(FakeDaemon.With(DaemonState.Idle, "Up to date (3 uploaded)"));
        vm.PageIndex = 2;
        texts = Texts(window);
        Assert.Contains("Up to date (3 uploaded)", texts);
        Assert.Contains("/home/me/Docs", texts);
        Assert.Contains("Up to date", texts); // sidebar status

        daemon.Publish(FakeDaemon.WithSync(SyncState.Syncing, "Downloading Docs/a.txt"));
        vm.PageIndex = 1;
        texts = Texts(window);
        Assert.Contains("Syncing", texts);
        Assert.Contains("Downloading Docs/a.txt", texts);
        Assert.Contains("/home/me/proton-drive", texts);

        // the sidebar list drives the page too
        window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "Nav").SelectedIndex = 0;
        Assert.Same(browser, vm.CurrentPage);
        window.Close();
    }
}
