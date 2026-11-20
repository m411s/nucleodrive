using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NucleoDrive.App.Services;
using NucleoDrive.Cli;
using NucleoDrive.Ipc;

namespace NucleoDrive.App.ViewModels;

/// <summary>One Drive folder in the "what to sync" tree. Its subfolders are listed from Drive only when
/// it's first expanded (one CLI call per level). Switching a folder on includes everything under it.</summary>
public sealed partial class SyncFolderChoice : ObservableObject
{
    private readonly Func<SyncFolderChoice, Task>? _loadChildren;
    private bool _loaded;
    private bool _loading;

    /// <param name="loadChildren">Lists this folder's subfolders into <see cref="Merge"/>; null for a flat list.</param>
    public SyncFolderChoice(string path, bool isChecked, SyncFolderChoice? parent = null, Func<SyncFolderChoice, Task>? loadChildren = null)
    {
        Path = path;
        Parent = parent;
        _isChecked = isChecked;
        _loadChildren = loadChildren;
        if (loadChildren is not null)
            Children.Add(LoadingRow()); // shows the expander until we know
    }

    private SyncFolderChoice(string path) => (Path, IsLoadingRow) = (path, true);

    private static SyncFolderChoice LoadingRow() => new("Loading…");

    public string Path { get; }
    public string Name => IsLoadingRow ? Path : Path[(Path.LastIndexOf('/') + 1)..];
    public SyncFolderChoice? Parent { get; }
    public bool IsLoadingRow { get; }
    public bool IsFolderRow => !IsLoadingRow;
    public ObservableCollection<SyncFolderChoice> Children { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOn), nameof(PartialText))]
    private bool _isChecked;

    [ObservableProperty] private bool _isExpanded;

    /// <summary>A switched-on folder above this one already syncs it.</summary>
    public bool IncludedByParent => Parent is { } p && (p.IsChecked || p.IncludedByParent);
    public bool CanToggle => !IncludedByParent;

    /// <summary>What the switch shows: on if this folder or one above it syncs.</summary>
    public bool IsOn
    {
        get => IsChecked || IncludedByParent;
        set
        {
            if (!IncludedByParent)
                IsChecked = value;
        }
    }

    public IEnumerable<SyncFolderChoice> Folders => Children.Where(c => !c.IsLoadingRow);

    /// <summary>Switched-on subfolders somewhere below (among those listed so far).</summary>
    public int SyncedBelow => Folders.Sum(c => (c.IsChecked ? 1 : 0) + c.SyncedBelow);

    /// <summary>"2 subfolders sync" on a folder that's off but partly synced.</summary>
    public string? PartialText => IsOn || SyncedBelow == 0 ? null
        : SyncedBelow == 1 ? "1 subfolder syncs" : $"{SyncedBelow} subfolders sync";

    partial void OnIsCheckedChanged(bool value)
    {
        foreach (var c in Folders)
            c.InheritedChanged();
        for (var p = Parent; p is not null; p = p.Parent)
            p.OnPropertyChanged(nameof(PartialText));
    }

    private void InheritedChanged()
    {
        OnPropertyChanged(nameof(IncludedByParent));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(PartialText));
        foreach (var c in Folders)
            c.InheritedChanged();
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
            _ = TryLoadAsync();
    }

    private async Task TryLoadAsync()
    {
        try
        {
            await EnsureLoadedAsync();
        }
        catch (DriveException)
        {
            // the loader already told the user; opening it again retries
        }
    }

    public async Task EnsureLoadedAsync()
    {
        if (_loaded || _loading || _loadChildren is null)
            return;
        _loading = true;
        try
        {
            await _loadChildren(this);
            _loaded = true;
        }
        finally
        {
            _loading = false;
            foreach (var row in Children.Where(c => c.IsLoadingRow).ToList())
                Children.Remove(row);
            if (!_loaded && Children.Count == 0)
                Children.Add(LoadingRow()); // failed: try again next time it's opened
        }
    }

    /// <summary>Adds a subfolder unless it's already there (it may have been added by path before listing).</summary>
    public SyncFolderChoice Merge(SyncFolderChoice child)
    {
        if (Folders.FirstOrDefault(c => c.Name == child.Name) is { } existing)
            return existing;
        var i = 0;
        while (i < Children.Count && !Children[i].IsLoadingRow
               && string.Compare(Children[i].Name, child.Name, StringComparison.OrdinalIgnoreCase) < 0)
            i++;
        Children.Insert(i, child);
        OnPropertyChanged(nameof(PartialText));
        return child;
    }
}

/// <summary>One choice in "Check Drive for changes made elsewhere".</summary>
public sealed record IntervalChoice(string Label, double Minutes)
{
    public override string ToString() => Label;

    public static IntervalChoice For(double minutes) => minutes switch
    {
        0 => new("Only when I click Sync now", 0),
        30 => new("Every 30 minutes", 30),
        60 => new("Every hour (recommended)", 60),
        120 => new("Every 2 hours", 120),
        240 => new("Every 4 hours", 240),
        _ => new($"Every {minutes:0} minutes (set in config.json)", minutes),
    };
}

/// <summary>The Sync page: two-way sync of chosen Drive folders into the local sync folder.</summary>
public sealed partial class SyncViewModel : ObservableObject
{
    private readonly IDaemonControl _daemon;
    private readonly DriveClientProvider _provider;
    private readonly IToaster _toast;
    private readonly Action<string> _openFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleText), nameof(StateText), nameof(Message), nameof(Detail), nameof(LastScanText), nameof(LocalRoot), nameof(Errors),
        nameof(NeedsConfirmation), nameof(IsRunning), nameof(HasFolders))]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand), nameof(ConfirmDeletionsCommand), nameof(ApplyCommand),
        nameof(LoadChoicesCommand), nameof(AddPathCommand), nameof(OpenFolderCommand))]
    private DaemonStatus _status;

    [ObservableProperty] private string _newPath = "";
    [ObservableProperty] private bool _choicesDirty;

    private readonly TimeSpan? _autoApply;
    private CancellationTokenSource? _pendingApply;

    /// <summary>Toggling a folder takes effect by itself after a short pause (no Apply button).</summary>
    public bool AutoApply => _autoApply is not null;
    public bool ShowApply => !AutoApply;

    /// <param name="onUi">Runs an action on the UI thread.</param>
    /// <param name="openFolder">Opens a local folder in the file manager.</param>
    public SyncViewModel(IDaemonControl daemon, IDriveClient? drive, IToaster toast, Action<Action> onUi, Action<string> openFolder,
        TimeSpan? autoApply = null)
        : this(daemon, new DriveClientProvider(() => drive ?? throw new DriveException("proton-drive CLI not found.")), toast, onUi, openFolder, autoApply)
    {
    }

    /// <param name="autoApply">If set, folder choices are applied this long after the last change.</param>
    public SyncViewModel(IDaemonControl daemon, DriveClientProvider provider, IToaster toast, Action<Action> onUi, Action<string> openFolder,
        TimeSpan? autoApply = null)
    {
        _autoApply = autoApply;
        _daemon = daemon;
        _provider = provider;
        _toast = toast;
        _openFolder = openFolder;
        _status = daemon.Status;
        daemon.StatusChanged += s => onUi(() => Apply(s));
    }

    public ObservableCollection<SyncFolderChoice> Choices { get; } = [];

    private SyncInfo? Info => Status.Sync;

    public bool IsRunning => Status.State != DaemonState.Stopped && Info is not null;
    public bool HasFolders => Info is { Folders.Count: > 0 };
    public bool NeedsConfirmation => Info?.State == SyncState.NeedsConfirmation;

    public string StateText => !IsRunning ? "Not running" : Info!.State switch
    {
        SyncState.Off => "Off",
        SyncState.Idle => "Up to date",
        SyncState.Syncing => "Syncing",
        SyncState.Paused => "Paused",
        SyncState.Login => "Sign in required",
        SyncState.NeedsConfirmation => "Waiting for you",
        _ => "Problem",
    };

    public string Message => IsRunning ? Info!.Message : "Start the daemon with: systemctl --user start nucleodrive";

    /// <summary>The message, unless it only repeats the state ("Up to date").</summary>
    public string? Detail => string.Equals(Message.TrimEnd('.'), StateText, StringComparison.OrdinalIgnoreCase) ? null : Message;
    public string LastScanText => "Last check of Drive: " + Units.Ago(Info?.LastFullScan);
    public string LocalRoot => Info?.LocalRoot ?? "";
    public IReadOnlyList<string> Errors => Info?.Errors ?? [];

    private void Apply(DaemonStatus s)
    {
        var wasRunning = IsRunning;
        Status = s;
        if (IsRunning && !wasRunning && Choices.Count == 0)
            _ = LoadChoicesAsync();
    }

    // ---- how often Drive is checked ----------------------------------------------------------

    public ObservableCollection<IntervalChoice> IntervalChoices { get; } =
        [.. NucleoDrive.Sync.TwoWaySettings.IntervalChoices.Select(IntervalChoice.For)];

    private IntervalChoice? _selectedInterval;
    private bool _settingInterval;

    /// <summary>Changing it applies straight away (the service checks at the new pace from now on).</summary>
    public IntervalChoice? SelectedInterval
    {
        get => _selectedInterval;
        set
        {
            if (value is null || value == _selectedInterval)
                return;
            var previous = _selectedInterval;
            SetProperty(ref _selectedInterval, value);
            if (!_settingInterval && previous is not null)
                _ = ApplyIntervalAsync(value, previous);
        }
    }

    private void ShowInterval(double minutes)
    {
        var choice = IntervalChoices.FirstOrDefault(c => c.Minutes == minutes);
        if (choice is null)
        {
            choice = IntervalChoice.For(minutes); // a custom value from config.json
            IntervalChoices.Insert(0, choice);
        }
        _settingInterval = true;
        SelectedInterval = choice;
        _settingInterval = false;
    }

    private async Task ApplyIntervalAsync(IntervalChoice choice, IntervalChoice previous)
    {
        try
        {
            await _daemon.SetSyncIntervalAsync(choice.Minutes);
        }
        catch (DaemonException e)
        {
            _toast.Show(e.Message, error: true);
            ShowInterval(previous.Minutes);
        }
    }

    /// <summary>"Last check took 26 s · next check around 15:40".</summary>
    public string ScheduleText
    {
        get
        {
            if (!IsRunning || Info!.Folders.Count == 0)
                return "";
            var took = Info.LastCheckSeconds is { } s ? $"Last check took {Math.Max(1, Math.Round(s)):0} s" : null;
            var next = Info.IntervalMinutes == 0 ? "Drive is checked only when you click Sync now"
                : Info.NextCheck is { } n ? $"next check around {n.ToLocalTime():HH:mm}" : null;
            var text = string.Join(" · ", new[] { took, next }.Where(t => t is not null));
            return text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : "";
        }
    }

    private HashSet<string> _synced = new(StringComparer.Ordinal);

    /// <summary>Lists the top-level Drive folders as a tree, switched on if synced. Folders synced deeper
    /// down (e.g. "Documents/Work") are listed and their parents opened, so you see them.</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    public async Task LoadChoicesAsync()
    {
        try
        {
            var settings = await _daemon.GetSyncSettingsAsync();
            ShowInterval(settings.RemoteIntervalMinutes);
            _synced = new HashSet<string>(settings.Folders, StringComparer.Ordinal);
            var names = new List<string>();
            if (_provider.Client is { } drive)
            {
                try
                {
                    names.AddRange((await drive.ListAsync(RemotePath.Root)).Where(e => e.IsDir && !e.NameUnavailable).Select(e => e.Name));
                }
                catch (DriveException e)
                {
                    _toast.Show("Couldn't list Drive folders: " + e.FirstLine, error: true);
                }
            }
            _loadingChoices = true;
            Choices.Clear();
            foreach (var n in names.Concat(_synced.Select(f => f.Split('/')[0])).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase))
                Choices.Add(NewChoice(n, null));
            foreach (var nested in _synced.Where(f => f.Contains('/')).Order(StringComparer.Ordinal))
                await RevealAsync(nested);
            _loadingChoices = false;
            ChoicesDirty = false;
            OnPropertyChanged(nameof(SyncedCount));
        }
        catch (DaemonException e)
        {
            _toast.Show(e.Message, error: true);
        }
        finally
        {
            _loadingChoices = false;
        }
    }

    private bool _loadingChoices;

    private SyncFolderChoice NewChoice(string path, SyncFolderChoice? parent)
    {
        var choice = new SyncFolderChoice(path, _synced.Contains(path), parent, LoadSubfoldersAsync);
        choice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SyncFolderChoice.IsChecked) && !_loadingChoices)
                Changed();
        };
        return choice;
    }

    private async Task LoadSubfoldersAsync(SyncFolderChoice folder)
    {
        if (_provider.Client is not { } drive)
            throw new DriveException("proton-drive CLI not found.");
        try
        {
            var path = RemotePath.Join(RemotePath.Root, string.Join('/', folder.Path.Split('/').Select(RemotePath.EscapeName)));
            foreach (var e in (await drive.ListAsync(path)).Where(e => e.IsDir && !e.NameUnavailable))
                folder.Merge(NewChoice(folder.Path + "/" + e.Name, folder));
        }
        catch (DriveException e)
        {
            _toast.Show($"Couldn't list {folder.Path}: {e.FirstLine}", error: true);
            throw;
        }
    }

    /// <summary>Finds or adds the node for "A/B/C", listing and opening A and A/B on the way.</summary>
    private async Task<SyncFolderChoice> RevealAsync(string path)
    {
        var parts = path.Split('/');
        var node = Choices.FirstOrDefault(c => c.Path == parts[0]);
        if (node is null)
        {
            node = NewChoice(parts[0], null);
            Choices.Add(node);
        }
        for (var i = 1; i < parts.Length; i++)
        {
            try
            {
                await node.EnsureLoadedAsync();
            }
            catch (DriveException)
            {
                // added by path below; the toast already said why
            }
            node.IsExpanded = true;
            node = node.Merge(NewChoice(string.Join('/', parts[..(i + 1)]), node));
        }
        return node;
    }

    /// <summary>Adds a nested Drive folder (e.g. "Documents/Work") to the tree, switched on.</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task AddPathAsync()
    {
        var path = NewPath.Trim().Trim('/');
        if (path.StartsWith("my-files/", StringComparison.Ordinal))
            path = path["my-files/".Length..];
        if (path.Length == 0 || path.Split('/').Any(p => p.Length == 0 || p is "." or ".."))
            return;
        NewPath = "";
        var node = await RevealAsync(path);
        if (node.IsChecked)
            Changed();
        else
            node.IsChecked = true;
    }

    /// <summary>The folders to sync: each switched-on folder, without the ones below it (already included).</summary>
    public IReadOnlyList<string> SelectedFolders()
    {
        var result = new List<string>();
        void Walk(IEnumerable<SyncFolderChoice> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.IsChecked)
                    result.Add(n.Path);
                else
                    Walk(n.Folders);
            }
        }
        Walk(Choices);
        return result;
    }

    private async void Changed()
    {
        ChoicesDirty = true;
        if (_autoApply is not { } delay)
            return;
        _pendingApply?.Cancel();
        var cts = _pendingApply = new CancellationTokenSource();
        try
        {
            await Task.Delay(delay, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // another toggle came in; that one applies
        }
        if (ApplyCommand.CanExecute(null))
            await ApplyCommand.ExecuteAsync(null);
    }

    public int SyncedCount => SelectedFolders().Count;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task ApplyAsync()
    {
        var folders = SelectedFolders().ToArray();
        try
        {
            await _daemon.SetSyncFoldersAsync(folders);
            ChoicesDirty = false;
            OnPropertyChanged(nameof(SyncedCount));
            _toast.Show(folders.Length == 0 ? "Sync turned off (files are kept)" : $"Syncing {folders.Length} folder(s) into {LocalRoot}");
        }
        catch (DaemonException e)
        {
            _toast.Show(e.Message, error: true);
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private Task SyncNowAsync() => Try(_daemon.SyncNowAsync);

    [RelayCommand(CanExecute = nameof(NeedsConfirmation))]
    private Task ConfirmDeletionsAsync() => Try(_daemon.ConfirmDeletionsAsync);

    [RelayCommand(CanExecute = nameof(HasFolders))]
    private void OpenFolder()
    {
        if (LocalRoot.Length > 0)
            _openFolder(LocalRoot);
    }

    private async Task Try(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (DaemonException e)
        {
            _toast.Show(e.Message, error: true);
        }
    }
}
