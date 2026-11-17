using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Pdrive.App.Services;
using Pdrive.App.ViewModels;

namespace Pdrive.App.Views;

public sealed partial class MainWindow : Window, IDialogs, IToaster
{
    private WindowNotificationManager? _notifications;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Icon = LoadAppIcon();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Load settings (CLI version, update check, sign-in state) when the tab is first opened.
        var settingsLoaded = false;
        this.FindControl<TabItem>("SettingsTab")!.PropertyChanged += (_, e) =>
        {
            if (e.Property == TabItem.IsSelectedProperty && e.NewValue is true && !settingsLoaded && Vm?.Settings is { } s)
            {
                settingsLoaded = true;
                _ = s.LoadAsync();
            }
        };

        var list = this.FindControl<ListBox>("EntryList")!;
        list.DoubleTapped += (_, _) => OpenSelected(list);
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                OpenSelected(list);
        };
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _notifications ??= new WindowNotificationManager(this) { MaxItems = 3, Position = NotificationPosition.BottomRight };
    }

    private void OpenSelected(ListBox list)
    {
        if (list.SelectedItem is EntryViewModel { IsDir: true } entry && Vm is { } vm)
            _ = vm.Browser.OpenAsync(entry);
    }

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
        if (paths.Count > 0 && Vm is { } vm)
            _ = vm.Browser.UploadPathsAsync(paths);
        e.Handled = true;
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
