using System.Text.Json;
using Pdrive.Cli;

namespace Pdrive.Tests;

/// <summary>A temp directory deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pdrive-test-" + Guid.NewGuid().ToString("N")[..12]);

    public string this[string relative] => System.IO.Path.Combine(Path, relative);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>The fake proton-drive CLI (tests/Pdrive.FakeCli), with per-test environment.</summary>
public sealed class FakeCli
{
    public static readonly string BinaryPath = Path.Combine(AppContext.BaseDirectory, "fake-proton-drive");

    private readonly Dictionary<string, string?> _env = new();

    public FakeCli(string logPath)
    {
        LogPath = logPath;
        _env["FAKE_LOG"] = logPath;
    }

    public string LogPath { get; }

    public FakeCli Set(string name, string? value)
    {
        _env[name] = value;
        return this;
    }

    /// <summary>Switches the fake to a persistent Drive stored in <paramref name="treeFile"/>.</summary>
    public FakeCli UseTree(string treeFile) => Set("FAKE_TREE", treeFile);

    public ProtonDriveCli Client(string? conflictStrategy = null) =>
        new(BinaryPath, new DriveCliOptions
        {
            Environment = _env,
            FileConflictStrategy = conflictStrategy ?? UploadFileStrategies.Default,
            KillAfter = TimeSpan.FromSeconds(2),
        });

    public IReadOnlyDictionary<string, string?> Environment => _env;

    /// <summary>Each logged call as a string array, e.g. ["upload", file..., remoteDir].</summary>
    public List<string[]> Log() => File.Exists(LogPath)
        ? File.ReadAllLines(LogPath).Select(l => JsonSerializer.Deserialize<string[]>(l)!).ToList()
        : [];

    public List<string[]> Uploads() => Log().Where(e => e[0] == "upload").ToList();

    public void ClearLog() => File.Delete(LogPath);
}
