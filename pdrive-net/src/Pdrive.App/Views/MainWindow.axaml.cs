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
        Icon = StatusIconsSafe();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

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

    // ---- IToaster ----------------------------------------------------------------

    public void Show(string message, bool error = false) => Dispatcher.UIThread.Post(() =>
        _notifications?.Show(new Notification(error ? "Problem" : null, message,
            error ? NotificationType.Error : NotificationType.Information, TimeSpan.FromSeconds(error ? 8 : 4))));

    private static WindowIcon? StatusIconsSafe()
    {
        try
        {
            return StatusIcons.For(Pdrive.Ipc.DaemonState.Idle);
        }
        catch (Exception)
        {
            return null; // no rendering backend (headless tests)
        }
    }
}
