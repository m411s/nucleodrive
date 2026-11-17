using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pdrive.App.Services;
using Pdrive.Cli;
using Pdrive.Ipc;

namespace Pdrive.App.ViewModels;

public sealed class EntryViewModel(DriveEntry entry)
{
    public DriveEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public bool IsDir => Entry.IsDir;
    public string SizeText => Entry.IsProtonDocument ? "Proton Docs/Sheets (open on the web)" : Units.Bytes(Entry.Size);

    /// <summary>The CLI skips Proton Docs/Sheets on download, so don't offer it.</summary>
    public bool CanDownload => !Entry.IsProtonDocument;
    public string Path => Entry.Path;
}

/// <summary>The Files tab: browse, upload (button or drop), download, sign in. Port of app.py's Window.</summary>
public sealed partial class BrowserViewModel : ObservableObject
{
    private readonly IDriveClient? _client;
    private readonly IDialogs _dialogs;
    private readonly IToaster _toast;
    private readonly Stack<string> _history = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpCommand))]
    private string _currentPath = RemotePath.Root;

    [ObservableProperty] private bool _showSignIn;
    [ObservableProperty] private string? _placeholder;

    private int _busyCount;

    /// <param name="client">Null if the CLI couldn't be found; <paramref name="clientError"/> says why.</param>
    public BrowserViewModel(IDriveClient? client, string? clientError, IDialogs dialogs, IToaster toast)
    {
        _client = client;
        _dialogs = dialogs;
        _toast = toast;
        if (client is null)
            Placeholder = clientError ?? "proton-drive CLI not found.";
    }

    public ObservableCollection<EntryViewModel> Entries { get; } = [];

    /// <summary>Operations in flight (a counter, so parallel operations don't hide each other).</summary>
    public int BusyCount => Volatile.Read(ref _busyCount);

    public bool IsBusy => BusyCount > 0;

    public bool HasClient => _client is not null;

    private bool CanGoBack => _history.Count > 0;
    private bool CanGoUp => CurrentPath != RemotePath.Root;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_client is null)
            return;
        var path = CurrentPath;
        try
        {
            var entries = await Busy(() => _client.ListAsync(path));
            if (path != CurrentPath)
                return; // stale: the user navigated away meanwhile
            ShowSignIn = false;
            Entries.Clear();
            foreach (var e in entries)
                Entries.Add(new EntryViewModel(e));
            Placeholder = entries.Count == 0 ? "This folder is empty" : null;
        }
        catch (AuthException)
        {
            if (path != CurrentPath)
                return;
            Entries.Clear();
            Placeholder = null;
            ShowSignIn = true;
        }
        catch (DriveException e)
        {
            if (path != CurrentPath)
                return;
            Entries.Clear();
            Placeholder = e.FirstLine;
            _toast.Show(e.FirstLine, error: true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task BackAsync() => _history.TryPop(out var prev) ? NavigateAsync(prev, push: false) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private Task UpAsync() => NavigateAsync(RemotePath.Parent(CurrentPath) is { Length: > 0 } p ? p : RemotePath.Root);

    [RelayCommand]
    public Task OpenAsync(EntryViewModel? entry) => entry is { IsDir: true } ? NavigateAsync(entry.Path) : Task.CompletedTask;

    public Task NavigateAsync(string path, bool push = true)
    {
        if (push && path != CurrentPath)
            _history.Push(CurrentPath);
        CurrentPath = path;
        BackCommand.NotifyCanExecuteChanged();
        return RefreshAsync();
    }

    [RelayCommand]
    private async Task UploadAsync()
    {
        var files = await _dialogs.PickFilesAsync("Upload to " + CurrentPath);
        await UploadPathsAsync(files);
    }

    /// <summary>Uploads local files/folders into the current folder (button or drag-and-drop).</summary>
    public async Task UploadPathsAsync(IReadOnlyList<string> paths)
    {
        if (_client is null || paths.Count == 0)
            return;
        var dest = CurrentPath;
        _toast.Show($"Uploading {paths.Count} item(s)…");
        try
        {
            await Busy(async () =>
            {
                await _client.UploadAsync(paths, dest);
                return true;
            });
            _toast.Show("Upload finished");
            if (dest == CurrentPath)
                await RefreshAsync();
        }
        catch (AuthException)
        {
            ShowSignIn = true;
            _toast.Show("Upload failed: sign in required", error: true);
        }
        catch (DriveException e)
        {
            _toast.Show("Upload failed: " + e.FirstLine, error: true);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DownloadAsync(EntryViewModel? entry)
    {
        if (_client is null || entry is not { CanDownload: true })
            return;
        var folder = await _dialogs.PickFolderAsync($"Download “{entry.Name}” to…");
        if (folder is null)
            return;
        _toast.Show($"Downloading {entry.Name}…");
        try
        {
            await Busy(async () =>
            {
                await _client.DownloadAsync(entry.Path, folder, entry.Entry.Size);
                return true;
            });
            _toast.Show($"Saved to {folder}");
        }
        catch (DriveException e)
        {
            _toast.Show("Download failed: " + e.FirstLine, error: true);
        }
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (_client is null)
            return;
        _toast.Show("Complete sign-in in your browser…");
        try
        {
            await Busy(async () =>
            {
                await _client.LoginAsync();
                return true;
            });
            await RefreshAsync();
        }
        catch (DriveException e)
        {
            _toast.Show(e.FirstLine, error: true);
        }
    }

    private async Task<T> Busy<T>(Func<Task<T>> work)
    {
        Interlocked.Increment(ref _busyCount);
        OnPropertyChanged(nameof(BusyCount));
        OnPropertyChanged(nameof(IsBusy));
        try
        {
            return await work();
        }
        finally
        {
            Interlocked.Decrement(ref _busyCount);
            OnPropertyChanged(nameof(BusyCount));
            OnPropertyChanged(nameof(IsBusy));
        }
    }
}
