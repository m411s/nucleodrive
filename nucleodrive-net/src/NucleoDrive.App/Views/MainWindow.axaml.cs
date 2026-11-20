using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NucleoDrive.App.Services;
using NucleoDrive.App.ViewModels;

namespace NucleoDrive.App.Views;

public sealed partial class MainWindow : Window, IDialogs, IToaster
{
    private WindowNotificationManager? _notifications;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Icon = LoadAppIcon();
        try
        {
            using var png = Avalonia.Platform.AssetLoader.Open(new Uri("avares://nucleodrive/Assets/nucleodrive.png"));
            this.FindControl<Image>("Logo")!.Source = new Avalonia.Media.Imaging.Bitmap(png);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            // no logo; the text is enough
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _notifications ??= new WindowNotificationManager(this) { MaxItems = 3, Position = NotificationPosition.BottomRight };
    }

    // ---- IDialogs ----------------------------------------------------------------

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = true });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string yes = "Yes", string no = "Cancel")
    {
        var yesButton = new Button { Content = yes, Classes = { "accent" }, IsDefault = true };
        var noButton = new Button { Content = no, IsCancel = true };
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = Icon,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { noButton, yesButton },
                    },
                },
            },
        };
        yesButton.Click += (_, _) => dialog.Close(true);
        noButton.Click += (_, _) => dialog.Close(false);
        if (!IsVisible)
        {
            Show();
            Activate();
        }
        return await dialog.ShowDialog<bool?>(this) == true;
    }

    // ---- IToaster ----------------------------------------------------------------

    public void Show(string message, bool error = false) => Dispatcher.UIThread.Post(() =>
        _notifications?.Show(new Notification(error ? "Problem" : null, message,
            error ? NotificationType.Error : NotificationType.Information, TimeSpan.FromSeconds(error ? 8 : 4))));

    private static WindowIcon? LoadAppIcon() => StatusIcons.InstalledAppIconSafe();
}
