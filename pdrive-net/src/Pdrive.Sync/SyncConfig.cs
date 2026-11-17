using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Pdrive.Sync;

public sealed record FolderPair(string Local, string Remote)
{
    /// <summary>Absolute local path ("~" expanded, no trailing slash); also the state key.</summary>
    [JsonIgnore] public string LocalPath => Path.TrimEndingDirectorySeparator(Path.GetFullPath(PdrivePaths.ExpandHome(Local)));

    [JsonIgnore] public string RemoteRoot => Remote.TrimEnd('/');
}

/// <summary>The "sync" section: Drive folders mirrored two-way into a local folder.</summary>
public sealed record TwoWaySettings
{
    /// <summary>Local folder that mirrors /my-files (only the chosen folders exist in it).</summary>
    public string Local { get; init; } = "~/proton-drive";

    /// <summary>Drive folders to sync, relative to /my-files with raw names ("Photos", "Documents/Work").</summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    public double RemoteIntervalMinutes { get; init; } = 15;

    /// <summary>While checks of Drive keep finding nothing, the wait grows up to this (back to
    /// <see cref="RemoteIntervalMinutes"/> after any change). Set it equal to turn the back-off off.</summary>
    public double RemoteIntervalMaxMinutes { get; init; } = 60;
    public int ParallelListings { get; init; } = 4;
    public int MaxDeletesWithoutConfirm { get; init; } = 50;

    [JsonIgnore] public string LocalPath => Path.TrimEndingDirectorySeparator(Path.GetFullPath(PdrivePaths.ExpandHome(Local)));
    [JsonIgnore] public TimeSpan RemoteInterval => TimeSpan.FromMinutes(Math.Max(1, RemoteIntervalMinutes));

    /// <summary>The wait before the next check of Drive after <paramref name="quietChecks"/> checks in a row
    /// found nothing: the interval, doubled after every two quiet checks, at most remote_interval_max_minutes
    /// (default 15, 15, 30, 30, 60 … minutes).</summary>
    public TimeSpan NextCheckAfter(int quietChecks)
    {
        var max = TimeSpan.FromMinutes(Math.Max(RemoteIntervalMaxMinutes, Math.Max(1, RemoteIntervalMinutes)));
        var wait = RemoteInterval * Math.Pow(2, Math.Min(quietChecks, 20) / 2);
        return wait > max ? max : wait;
    }
}

/// <summary>config.json. Same shape as the Python prototype, plus <c>conflict_strategy</c>, <c>watch</c>
/// and the two-way <c>sync</c> section.</summary>
public sealed record SyncConfig
{
    public static readonly IReadOnlyList<string> DefaultExclude = [".*", "*~", "*.tmp", "*.part", "*.crdownload"];

    public double IntervalMinutes { get; init; } = 15;
    public double MinAgeSeconds { get; init; } = 5; // skip files still being written
    public IReadOnlyList<string> Exclude { get; init; } = DefaultExclude;
    public IReadOnlyList<FolderPair> Folders { get; init; } = [];
    /// <summary>Upload `-f` strategy for files that already exist on Drive; null means create-new-revision
    /// (keeps the old content in the file's version history).</summary>
    public string? ConflictStrategy { get; init; }

    [JsonIgnore] public string FileConflictStrategy => ConflictStrategy ?? Pdrive.Cli.UploadFileStrategies.Default;
    public bool Watch { get; init; } = true; // live change detection (inotify); the interval scan always runs too

    public TwoWaySettings Sync { get; init; } = new();

    [JsonIgnore] public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, IntervalMinutes));
    [JsonIgnore] public TimeSpan MinAge => TimeSpan.FromSeconds(MinAgeSeconds);
}

public sealed class ConfigException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class ConfigStore(PdrivePaths paths)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string FilePath => paths.ConfigFile;

    /// <summary>Defaults merged with config.json; imports the Python prototype's config once if
    /// ours doesn't exist. Throws <see cref="ConfigException"/> if the file is unreadable or malformed.</summary>
    public SyncConfig Load()
    {
        if (!File.Exists(paths.ConfigFile) && File.Exists(paths.LegacyConfigFile))
        {
            var imported = Parse(paths.LegacyConfigFile);
            Save(imported);
            return imported;
        }
        return File.Exists(paths.ConfigFile) ? Parse(paths.ConfigFile) : new SyncConfig();
    }

    public void Save(SyncConfig cfg) => PdrivePaths.AtomicWrite(paths.ConfigFile, JsonSerializer.Serialize(cfg, Json));

    /// <summary>Load, change, validate and save; refuses to touch a broken file.</summary>
    public SyncConfig Update(Func<SyncConfig, SyncConfig> change)
    {
        var updated = change(Load());
        Validate(updated, paths.ConfigFile);
        Save(updated);
        return updated;
    }

    private static SyncConfig Parse(string file)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ConfigException($"Can't read {file}: {e.Message}", e);
        }
        if (root is not JsonObject obj)
            throw new ConfigException($"{file}: expected a JSON object");

        var cfg = new SyncConfig();
        try
        {
            cfg = obj.Deserialize<SyncConfig>(Json) ?? cfg;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw new ConfigException($"{file}: {Describe(obj) ?? e.Message}", e);
        }
        Validate(cfg, file);
        return cfg;
    }

    /// <summary>A friendlier message than the serializer's for the common type mistakes.</summary>
    private static string? Describe(JsonObject obj)
    {
        static bool IsNumber(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number;
        if (obj["interval_minutes"] is { } i && !IsNumber(i)) return "'interval_minutes' must be a positive number";
        if (obj["min_age_seconds"] is { } m && !IsNumber(m)) return "'min_age_seconds' must be a number >= 0";
        if (obj["exclude"] is { } x && (x is not JsonArray xa || xa.Any(p => p is not JsonValue pv || pv.GetValueKind() != JsonValueKind.String)))
            return "'exclude' must be a list of patterns";
        if (obj["folders"] is { } f && f is not JsonArray) return "'folders' must be a list";
        if (obj["folders"] is JsonArray fa)
        {
            foreach (var item in fa)
            {
                if (item is not JsonObject fo || fo["local"] is not JsonValue l || l.GetValueKind() != JsonValueKind.String
                    || fo["remote"] is not JsonValue r || r.GetValueKind() != JsonValueKind.String)
                    return $"each folder needs string 'local' and 'remote' (got {item?.ToJsonString() ?? "null"})";
            }
        }
        if (obj["watch"] is { } w && w.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) return "'watch' must be true or false";
        return null;
    }

    private static void Validate(SyncConfig cfg, string file)
    {
        ConfigException Bad(string msg) => new($"{file}: {msg}");
        if (!(cfg.IntervalMinutes > 0)) throw Bad("'interval_minutes' must be a positive number");
        if (!(cfg.MinAgeSeconds >= 0)) throw Bad("'min_age_seconds' must be a number >= 0");
        if (cfg.Exclude is null || cfg.Exclude.Any(p => p is null)) throw Bad("'exclude' must be a list of patterns");
        if (cfg.Folders is null) throw Bad("'folders' must be a list");
        if (cfg.Sync is null || cfg.Sync.Folders is null || string.IsNullOrWhiteSpace(cfg.Sync.Local))
            throw Bad("'sync' needs a 'local' folder and a list of 'folders'");
        foreach (var f in cfg.Sync.Folders)
        {
            if (string.IsNullOrWhiteSpace(f) || f.StartsWith('/') || f.EndsWith('/') || f.Split('/').Any(s => s is "" or "." or ".."))
                throw Bad($"sync folder '{f}' must be a path relative to /my-files, like \"Photos\" or \"Documents/Work\"");
        }
        if (cfg.Sync.Folders.Distinct(StringComparer.Ordinal).Count() != cfg.Sync.Folders.Count
            || cfg.Sync.Folders.Any(a => cfg.Sync.Folders.Any(b => a != b && a.StartsWith(b + "/", StringComparison.Ordinal))))
            throw Bad("sync folders must not repeat or contain each other");
        if (cfg.Sync.ParallelListings is < 1 or > 16) throw Bad("'sync.parallel_listings' must be between 1 and 16");
        if (cfg.Sync.MaxDeletesWithoutConfirm < 0) throw Bad("'sync.max_deletes_without_confirm' must be >= 0");
        if (!(cfg.Sync.RemoteIntervalMinutes > 0)) throw Bad("'sync.remote_interval_minutes' must be a positive number");
        if (!(cfg.Sync.RemoteIntervalMaxMinutes > 0)) throw Bad("'sync.remote_interval_max_minutes' must be a positive number");
        var syncRoot = cfg.Sync.LocalPath;
        foreach (var f in cfg.Folders)
        {
            var backup = f.LocalPath;
            if (backup == syncRoot || backup.StartsWith(syncRoot + "/", StringComparison.Ordinal) || syncRoot.StartsWith(backup + "/", StringComparison.Ordinal))
                throw Bad($"backup folder {backup} overlaps the sync folder {syncRoot}; sync already covers it");
        }
        if (cfg.ConflictStrategy is { } cs && !Pdrive.Cli.UploadFileStrategies.All.Contains(cs))
            throw Bad($"'conflict_strategy' must be one of: {string.Join(", ", Pdrive.Cli.UploadFileStrategies.All)}");
        foreach (var f in cfg.Folders)
        {
            if (f?.Local is null || f.Remote is null)
                throw Bad($"each folder needs string 'local' and 'remote' (got local={f?.Local ?? "null"}, remote={f?.Remote ?? "null"})");
            if (!f.Remote.StartsWith(Pdrive.Cli.RemotePath.Root, StringComparison.Ordinal))
                throw Bad($"remote '{f.Remote}' should start with {Pdrive.Cli.RemotePath.Root}");
        }
    }
}
