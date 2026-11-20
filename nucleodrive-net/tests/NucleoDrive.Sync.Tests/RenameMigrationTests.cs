using NucleoDrive.Sync;
using NucleoDrive.Sync.TwoWay;
using NucleoDrive.Tests;

namespace NucleoDrive.Sync.Tests;

/// <summary>NucleoDrive was called pdrive up to 0.2.1: its folders move over once.</summary>
public sealed class RenameMigrationTests : IDisposable
{
    private readonly TempDir _home = new();

    public void Dispose() => _home.Dispose();

    private Func<string, string?> Env(Dictionary<string, string>? extra = null) => name =>
        name == "HOME" ? _home.Path : extra is not null && extra.TryGetValue(name, out var v) ? v : null;

    private void Write(string relative, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_home[relative])!);
        File.WriteAllText(_home[relative], contents);
    }

    [Fact]
    public void MovesConfigStateAndData()
    {
        Write(".config/pdrive/config.json", "{}");
        Write(".local/state/pdrive/state.db", "db");
        Write(".local/share/pdrive/cli/proton-drive", "cli");

        var moved = NucleoDrivePaths.MigrateFromPdrive(Env());

        Assert.Equal(3, moved.Count);
        Assert.Equal("{}", File.ReadAllText(_home[".config/nucleodrive/config.json"]));
        Assert.Equal("db", File.ReadAllText(_home[".local/state/nucleodrive/state.db"]));
        Assert.Equal("cli", File.ReadAllText(_home[".local/share/nucleodrive/cli/proton-drive"]));
        Assert.False(Directory.Exists(_home[".config/pdrive"]));
        Assert.Empty(NucleoDrivePaths.MigrateFromPdrive(Env())); // only once
    }

    [Fact]
    public void KeepsAnExistingNewFolder()
    {
        Write(".config/pdrive/config.json", "old");
        Write(".config/nucleodrive/config.json", "new");

        Assert.Empty(NucleoDrivePaths.MigrateFromPdrive(Env()));

        Assert.Equal("new", File.ReadAllText(_home[".config/nucleodrive/config.json"]));
        Assert.Equal("old", File.ReadAllText(_home[".config/pdrive/config.json"]));
    }

    [Fact]
    public void MergesTheDataFolderWhenInstallAlreadyWroteIcons()
    {
        Write(".local/share/pdrive/cli/proton-drive", "cli");
        Write(".local/share/pdrive/icons/tray-idle.png", "old");
        Write(".local/share/nucleodrive/icons/tray-idle.png", "new");

        NucleoDrivePaths.MigrateFromPdrive(Env());

        Assert.Equal("cli", File.ReadAllText(_home[".local/share/nucleodrive/cli/proton-drive"]));
        Assert.Equal("new", File.ReadAllText(_home[".local/share/nucleodrive/icons/tray-idle.png"]));
    }

    [Fact]
    public void LeavesFoldersAloneWhenOverridden()
    {
        Write(".config/pdrive/config.json", "{}");

        NucleoDrivePaths.MigrateFromPdrive(Env(new() { ["NUCLEODRIVE_CONFIG_DIR"] = _home["elsewhere"] }));

        Assert.True(File.Exists(_home[".config/pdrive/config.json"]));
        Assert.False(Directory.Exists(_home[".config/nucleodrive"]));
    }

    [Fact]
    public void RepointsTheCliLink()
    {
        Write(".local/share/pdrive/cli/proton-drive", "cli");
        Directory.CreateDirectory(_home[".local/bin"]);
        File.CreateSymbolicLink(_home[".local/bin/proton-drive"], _home[".local/share/pdrive/cli/proton-drive"]);

        NucleoDrivePaths.MigrateFromPdrive(Env());

        Assert.Equal(_home[".local/share/nucleodrive/cli/proton-drive"], new FileInfo(_home[".local/bin/proton-drive"]).LinkTarget);
        Assert.Equal("cli", File.ReadAllText(_home[".local/bin/proton-drive"]));
    }

    [Fact]
    public void LeavesSomeoneElsesCliLinkAlone()
    {
        Write(".local/share/pdrive/cli/proton-drive", "cli");
        Write("other/proton-drive", "other");
        Directory.CreateDirectory(_home[".local/bin"]);
        File.CreateSymbolicLink(_home[".local/bin/proton-drive"], _home["other/proton-drive"]);

        NucleoDrivePaths.MigrateFromPdrive(Env());

        Assert.Equal(_home["other/proton-drive"], new FileInfo(_home[".local/bin/proton-drive"]).LinkTarget);
    }

    [Fact]
    public void TheOldStagingFolderIsNeverSynced() =>
        Assert.True(SyncPaths.IsExcludedPath(".pdrive-staging/abc/file.txt", []));
}
