using NucleoDrive.Tests;

namespace NucleoDrive.Sync.Tests;

public sealed class FreedesktopTrashTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly FreedesktopTrash _trash;

    public FreedesktopTrashTests() =>
        _trash = new FreedesktopTrash(_tmp["data"], () => new DateTime(2026, 11, 17, 10, 0, 0), () => ["/"]);

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Trashes_files_with_trashinfo_and_unique_names()
    {
        Directory.CreateDirectory(_tmp["docs"]);
        File.WriteAllText(_tmp["docs/my file.txt"], "1");
        var first = _trash.Trash(_tmp["docs/my file.txt"]);
        File.WriteAllText(_tmp["docs/my file.txt"], "2");
        var second = _trash.Trash(_tmp["docs/my file.txt"]);

        Assert.False(File.Exists(_tmp["docs/my file.txt"]));
        Assert.Equal(_tmp["data/Trash/files/my file.txt"], first);
        Assert.Equal(_tmp["data/Trash/files/my file.txt.2"], second);
        Assert.Equal("2", File.ReadAllText(second));
        var info = File.ReadAllText(_tmp["data/Trash/info/my file.txt.2.trashinfo"]);
        Assert.Contains("[Trash Info]", info);
        Assert.Contains("Path=" + _tmp["docs"].Replace(" ", "%20") + "/my%20file.txt", info);
        Assert.Contains("DeletionDate=2026-11-17T10:00:00", info);
    }

    [Fact]
    public void Trashes_whole_folders()
    {
        Directory.CreateDirectory(_tmp["docs/sub"]);
        File.WriteAllText(_tmp["docs/sub/a.txt"], "a");
        var target = _trash.Trash(_tmp["docs/sub"]);
        Assert.False(Directory.Exists(_tmp["docs/sub"]));
        Assert.True(File.Exists(Path.Combine(target, "a.txt")));
    }

    [Fact]
    public void Missing_items_are_an_error() =>
        Assert.Throws<FileNotFoundException>(() => _trash.Trash(_tmp["nope"]));
}
