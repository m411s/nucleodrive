using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Pdrive.Sync;

/// <summary>What we last uploaded for a file: mtime in 100-ns units since the Unix epoch
/// (.NET's resolution) and size.</summary>
public readonly record struct FileSig(long Mtime, long Size)
{
    public static FileSig Of(FileInfo f) => new(f.LastWriteTimeUtc.Ticks - DateTime.UnixEpoch.Ticks, f.Length);
}

/// <summary>SQLite state: what was uploaded where, plus a few daemon settings (pause, last sync).
/// Thread-safe; one connection guarded by a lock.</summary>
public sealed class StateStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly Lock _gate = new();

    public StateStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        _db.Open();
        Exec("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS files (
                local_root  TEXT NOT NULL,
                remote_root TEXT NOT NULL,
                rel_path    TEXT NOT NULL,
                mtime       INTEGER NOT NULL,
                size        INTEGER NOT NULL,
                uploaded_at INTEGER NOT NULL,
                PRIMARY KEY (local_root, remote_root, rel_path)
            );
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
    }

    public void Dispose()
    {
        lock (_gate)
            _db.Dispose();
    }

    /// <summary>Everything recorded for one folder pair, keyed by '/'-separated relative path.
    /// The remote is part of the key, so pointing a folder somewhere new uploads it again.</summary>
    public Dictionary<string, FileSig> GetFolder(string localRoot, string remoteRoot)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT rel_path, mtime, size FROM files WHERE local_root = $l AND remote_root = $r",
                ("$l", localRoot), ("$r", remoteRoot));
            using var reader = cmd.ExecuteReader();
            var result = new Dictionary<string, FileSig>(StringComparer.Ordinal);
            while (reader.Read())
                result[reader.GetString(0)] = new FileSig(reader.GetInt64(1), reader.GetInt64(2));
            return result;
        }
    }

    public void Record(string localRoot, string remoteRoot, IEnumerable<KeyValuePair<string, FileSig>> files)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = Command("""
                INSERT INTO files (local_root, remote_root, rel_path, mtime, size, uploaded_at)
                VALUES ($l, $r, $p, $m, $s, $t)
                ON CONFLICT DO UPDATE SET mtime = excluded.mtime, size = excluded.size, uploaded_at = excluded.uploaded_at
                """, ("$l", localRoot), ("$r", remoteRoot), ("$p", ""), ("$m", 0L), ("$s", 0L), ("$t", now));
            cmd.Transaction = tx;
            foreach (var (rel, sig) in files)
            {
                cmd.Parameters["$p"].Value = rel;
                cmd.Parameters["$m"].Value = sig.Mtime;
                cmd.Parameters["$s"].Value = sig.Size;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public int Count()
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT COUNT(*) FROM files");
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    public string? GetMeta(string key)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT value FROM meta WHERE key = $k", ("$k", key));
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string? value)
    {
        lock (_gate)
        {
            using var cmd = value is null
                ? Command("DELETE FROM meta WHERE key = $k", ("$k", key))
                : Command("INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT DO UPDATE SET value = excluded.value",
                    ("$k", key), ("$v", value));
            cmd.ExecuteNonQuery();
        }
    }

    public bool Paused
    {
        get => GetMeta("paused") == "1";
        set => SetMeta("paused", value ? "1" : null);
    }

    public DateTimeOffset? LastSync
    {
        get => long.TryParse(GetMeta("last_sync"), CultureInfo.InvariantCulture, out var s) ? DateTimeOffset.FromUnixTimeSeconds(s) : null;
        set => SetMeta("last_sync", value?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Imports the Python prototype's state.json once ({"local::rel": [mtime_ns, size]}).
    /// mtimes are cut to 100-ns steps so unchanged files compare equal and aren't uploaded again.
    /// Entries whose folder isn't in the config are skipped (we don't know their remote).
    /// Returns the number of imported entries, or null if there was nothing to import.</summary>
    public int? ImportLegacy(string stateJsonPath, SyncConfig cfg)
    {
        if (GetMeta("legacy_imported") is not null || !File.Exists(stateJsonPath))
            return null;

        Dictionary<string, long[]>? legacy;
        try
        {
            legacy = JsonSerializer.Deserialize<Dictionary<string, long[]>>(File.ReadAllText(stateJsonPath));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            SetMeta("legacy_imported", "unreadable: " + e.Message);
            return 0;
        }

        var byFolder = new Dictionary<FolderPair, List<KeyValuePair<string, FileSig>>>();
        foreach (var (key, sig) in legacy ?? [])
        {
            var sep = key.IndexOf("::", StringComparison.Ordinal);
            if (sep < 0 || sig is not { Length: 2 })
                continue;
            var local = key[..sep];
            var folder = cfg.Folders.FirstOrDefault(f => SamePath(f.LocalPath, local));
            if (folder is null)
                continue;
            var rel = key[(sep + 2)..].Replace(Path.DirectorySeparatorChar, '/');
            var entries = byFolder.TryGetValue(folder, out var list) ? list : byFolder[folder] = [];
            entries.Add(new(rel, new FileSig(sig[0] / 100, sig[1])));
        }

        foreach (var (folder, entries) in byFolder)
            Record(folder.LocalPath, folder.RemoteRoot, entries);
        var count = byFolder.Values.Sum(l => l.Count);
        SetMeta("legacy_imported", count.ToString(CultureInfo.InvariantCulture));
        return count;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.Ordinal);

    private void Exec(string sql)
    {
        using var cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }
}
