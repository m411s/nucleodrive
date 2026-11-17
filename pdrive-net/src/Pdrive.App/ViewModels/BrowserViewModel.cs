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
    public bool IsFile => !Entry.IsDir;
    public string SizeText => Entry.IsDir ? "" : Entry.IsProtonDocument ? "Proton Doc" : Units.Bytes(Entry.Size);

    /// <summary>"Today 14:05", "Yesterday", "3 Nov", "3 Nov 2024".</summary>
    public string ModifiedText => Entry.ModifiedAt is not { } m ? "" : FormatDate(m.ToLocalTime(), DateTimeOffset.Now);

    internal static string FormatDate(DateTimeOffset t, DateTimeOffset now)
    {
        var c = System.Globalization.CultureInfo.CurrentCulture;
        if (t.Date == now.Date)
            return "Today " + t.ToString("HH:mm", c);
        if (t.Date == now.Date.AddDays(-1))
            return "Yesterday";
        return t.Year == now.Year ? t.ToString("d MMM", c) : t.ToString("d MMM yyyy", c);
    }

    /// <summary>The CLI skips Proton Docs/Sheets on download, so don't offer it.</summary>
    public bool CanDownload => !Entry.IsProtonDocument;
    public string Path => Entry.Path;
}

/// <summary>The Files page: browse, upload (button or drop), download, sign in. Port of app.py's Window.</summary>
public sealed partial class BrowserViewModel : ObservableObject
{
    private readonly DriveClientProvider _provider;
    private readonly IDialogs _dialogs;
    private readonly IToaster _toast;
    private readonly Stack<string> _history = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpCommand))]
    private string _currentPath = RemotePath.Root;

    [ObservableProperty] private bool _showSignIn;
    [ObservableProperty] private string? _placeholder;
    [ObservableProperty] private bool _isDragOver;

    /// <summary>Last listing of each folder visited: shown instantly when you go back, then refreshed.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<DriveEntry>> _cache = new(StringComparer.Ordinal);

    /// <summary>My files › Documents › Work, each clickable.</summary>
    public ObservableCollection<Crumb> Breadcrumbs { get; } = [new Crumb("My files", RemotePath.Root, true)];

    public string FolderTitle => Breadcrumbs[^1].Name;

    partial void OnCurrentPathChanged(string value)
    {
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new Crumb("My files", RemotePath.Root, value == RemotePath.Root));
        var path = RemotePath.Root;
        var rest = value.Length > RemotePath.Root.Length ? value[(RemotePath.Root.Length + 1)..] : "";
        // split on unescaped '/', keep escapes in paths, show unescaped names
        var segments = System.Text.RegularExpressions.Regex.Split(rest, @"(?<!\\)/").Where(s => s.Length > 0).ToList();
        for (var i = 0; i < segments.Count; i++)
        {
            path += "/" + segments[i];
            Breadcrumbs.Add(new Crumb(RemotePath.UnescapeName(segments[i]), path, i == segments.Count - 1));
        }
        OnPropertyChanged(nameof(FolderTitle));
    }

    [RelayCommand]
    private Task GoToAsync(Crumb? crumb) => crumb is null || crumb.Path == CurrentPath ? Task.CompletedTask : NavigateAsync(crumb.Path);

    private void Show(IReadOnlyList<DriveEntry> entries)
    {
        ShowSignIn = false;
        Entries.Clear();
        foreach (var e in entries)
            Entries.Add(new EntryViewModel(e));
        Placeholder = entries.Count == 0 ? "This folder is empty" : null;
    }

    private int _busyCount;

    /// <param name="client">Null if the CLI couldn't be found; <paramref name="clientError"/> says why.</param>
    public BrowserViewModel(IDriveClient? client, string? clientError, IDialogs dialogs, IToaster toast)
        : this(new DriveClientProvider(() => client ?? throw new DriveException(clientError ?? "proton-drive CLI not found.")), dialogs, toast)
    {
    }

    public BrowserViewModel(DriveClientProvider provider, IDialogs dialogs, IToaster toast)
    {
        _provider = provider;
        _dialogs = dialogs;
        _toast = toast;
        if (provider.Client is null)
            Placeholder = provider.Error ?? "proton-drive CLI not found.";
        provider.Changed += () =>
        {
            OnPropertyChanged(nameof(HasClient));
            Placeholder = provider.Client is null ? provider.Error : null;
            _ = RefreshAsync();
        };
    }

    private IDriveClient? _client => _provider.Client;

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
            _cache[path] = entries;
            if (path != CurrentPath)
                return; // stale: the user navigated away meanwhile
            Show(entries);
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
        if (_cache.TryGetValue(path, out var cached))
            Show(cached); // instant; the refresh below brings it up to date
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

/// <summary>One step of the breadcrumb path.</summary>
public sealed record Crumb(string Name, string Path, bool IsLast)
{
    public bool IsNotLast => !IsLast;
}
