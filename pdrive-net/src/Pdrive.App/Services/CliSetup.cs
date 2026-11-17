using Pdrive.Cli;

namespace Pdrive.App.Services;

/// <summary>What the Settings page needs to find, check and install the CLI (fakeable in tests).</summary>
public interface ICliSetup
{
    string? FindInstalled();
    Task<string?> VersionOfAsync(string path);
    Task<CliRelease> GetLatestAsync();
    Task<string> InstallAsync(CliRelease release, Func<string, Task<bool>> confirmReplace, IProgress<string>? progress);
}

public sealed class CliSetup(CliInstaller installer) : ICliSetup
{
    public string? FindInstalled() => BinaryLocator.Find();
    public Task<string?> VersionOfAsync(string path) => CliInstaller.RunVersionAsync(path);
    public Task<CliRelease> GetLatestAsync() => installer.GetLatestAsync();

    public Task<string> InstallAsync(CliRelease release, Func<string, Task<bool>> confirmReplace, IProgress<string>? progress) =>
        installer.InstallAsync(release, confirmReplace, progress);
}
