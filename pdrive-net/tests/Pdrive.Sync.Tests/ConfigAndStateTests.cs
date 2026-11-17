using Pdrive.Tests;

namespace Pdrive.Sync.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly PdrivePaths _paths;
    private readonly ConfigStore _store;

    public ConfigStoreTests()
    {
        _paths = PdrivePaths.Under(_tmp.Path);
        _store = new ConfigStore(_paths);
    }

    public void Dispose() => _tmp.Dispose();

    private void Write(string text)
    {
        Directory.CreateDirectory(_paths.ConfigDir);
        File.WriteAllText(_paths.ConfigFile, text);
    }

    [Fact]
    public void Defaults_when_missing()
    {
        var cfg = _store.Load();
        Assert.Equal(15, cfg.IntervalMinutes);
        Assert.Empty(cfg.Folders);
        Assert.True(cfg.Watch);
        Assert.False(File.Exists(_paths.ConfigFile));
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("[]")]
    [InlineData("""{"interval_minutes": "15"}""")]
    [InlineData("""{"interval_minutes": 0}""")]
    [InlineData("""{"folders": [{"local": "/x"}]}""")]
    [InlineData("""{"folders": [{"local": "/x", "remote": "/elsewhere"}]}""")]
    [InlineData("""{"exclude": "*.tmp"}""")]
    [InlineData("""{"folders": null}""")]
    [InlineData("""{"conflict_strategy": "overwrite"}""")]
    public void Bad_json_and_bad_types_raise_config_error(string text)
    {
        Write(text);
        var e = Assert.Throws<ConfigException>(() => _store.Load());
        Assert.Contains(_paths.ConfigFile, e.Message);
    }

    [Fact]
    public void Roundtrip_keeps_python_shape()
    {
        _store.Save(new SyncConfig { Folders = [new FolderPair("/home/x/Docs", "/my-files/Docs")], ConflictStrategy = "replace" });
        var json = File.ReadAllText(_paths.ConfigFile);
        Assert.Contains("\"interval_minutes\"", json);
        Assert.Contains("\"local\": \"/home/x/Docs\"", json);
        Assert.DoesNotContain("local_path", json);
        Assert.DoesNotContain("\"interval\":", json);
        var back = _store.Load();
        Assert.Equal("replace", back.ConflictStrategy);
        Assert.Equal(new FolderPair("/home/x/Docs", "/my-files/Docs"), back.Folders.Single());
    }

    [Fact]
    public void Imports_python_config_once()
    {
        Directory.CreateDirectory(_paths.LegacyConfigDir);
        File.WriteAllText(_paths.LegacyConfigFile, """
            {"interval_minutes": 30, "min_age_seconds": 5, "exclude": [".*"], "folders": [{"local": "/home/x/Docs", "remote": "/my-files/Docs"}]}
            """);
        Assert.Equal(30, _store.Load().IntervalMinutes);
        Assert.True(File.Exists(_paths.ConfigFile));
        File.Delete(_paths.LegacyConfigFile);
        Assert.Equal(30, _store.Load().IntervalMinutes);
    }

    [Fact]
    public void Update_refuses_to_overwrite_a_broken_file()
    {
        Write("{bad");
        Assert.Throws<ConfigException>(() => _store.Update(c => c with { IntervalMinutes = 5 }));
        Assert.Equal("{bad", File.ReadAllText(_paths.ConfigFile));
    }
}

public sealed class StateStoreTests
{
    [Fact]
    public void Records_and_meta_survive_reopen()
    {
        using var tmp = new TempDir();
        using (var s = new StateStore(tmp["s.db"]))
        {
            s.Record("/l", "/my-files/r", [KeyValuePair.Create("a/b.txt", new FileSig(5, 6))]);
            s.Record("/l", "/my-files/r", [KeyValuePair.Create("a/b.txt", new FileSig(7, 8))]);
            s.Paused = true;
            s.LastSync = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        }
        using var again = new StateStore(tmp["s.db"]);
        Assert.Equal(new FileSig(7, 8), again.GetFolder("/l", "/my-files/r")["a/b.txt"]);
        Assert.Empty(again.GetFolder("/l", "/my-files/other"));
        Assert.True(again.Paused);
        Assert.Equal(1_700_000_000, again.LastSync!.Value.ToUnixTimeSeconds());
        again.Paused = false;
        Assert.False(again.Paused);
    }
}

public sealed class ChangeWatcherTests
{
    private static async Task<bool> WaitFor(Func<bool> pred, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (pred())
                return true;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        return false;
    }

    [Fact]
    public async Task Reports_changed_folder_after_quiet_period_and_ignores_excluded_names()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp["watched/sub"]);
        var fired = new List<IReadOnlyCollection<string>>();
        using var w = new ChangeWatcher([tmp["watched"]], SyncConfig.DefaultExclude,
            TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5), c => { lock (fired) fired.Add(c); });
        Assert.Empty(w.Unwatched);

        File.WriteAllText(tmp["watched/.hidden"], "x");
        File.WriteAllText(tmp["watched/sub/draft.tmp"], "x");
        await Task.Delay(600, TestContext.Current.CancellationToken);
        lock (fired)
            Assert.Empty(fired);

        File.WriteAllText(tmp["watched/sub/real.txt"], "x");
        Assert.True(await WaitFor(() => { lock (fired) return fired.Count > 0; }, TimeSpan.FromSeconds(5)));
        lock (fired)
            Assert.Equal([tmp["watched"]], fired[0]);
    }

    [Fact]
    public void Missing_folder_is_reported_as_unwatched()
    {
        using var tmp = new TempDir();
        using var w = new ChangeWatcher([tmp["nope"]], [], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), _ => { });
        Assert.Contains(tmp["nope"], w.Unwatched.Keys);
    }
}
