using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace NucleoDrive.Cli;

/// <summary>One downloadable build from Proton's CLI release index.</summary>
public sealed record CliRelease(string Version, string Platform, Uri Url, string Sha512);

/// <summary>Finds, downloads, verifies and installs the official proton-drive CLI.
/// <list type="bullet">
/// <item>Release index: https://proton.me/download/drive/cli/index.html (per-platform URL + SHA-512).</item>
/// <item>Downloads only from https://proton.me/download/drive/cli/, and only installs a file whose
/// SHA-512 matches the index, that is an ELF executable and whose --version runs.</item>
/// <item>Installs to $XDG_DATA_HOME/nucleodrive/cli/proton-drive (survives NucleoDrive reinstalls) and links
/// ~/.local/bin/proton-drive to it, asking before replacing anything that isn't already that link.</item>
/// </list></summary>
public sealed partial class CliInstaller(HttpClient http, string dataHome, string binDir,
    Func<string, CancellationToken, Task<string?>>? versionOf = null)
{
    private readonly Func<string, CancellationToken, Task<string?>> _versionOf = versionOf ?? RunVersionAsync;

    public static readonly Uri IndexUrl = new("https://proton.me/download/drive/cli/index.html");
    private const string AllowedPrefix = "https://proton.me/download/drive/cli/";

    public static CliInstaller ForCurrentUser(HttpClient http)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d : Path.Combine(home, ".local/share");
        return new CliInstaller(http, data, Path.Combine(home, ".local/bin"));
    }

    /// <summary>Where installed CLIs go.</summary>
    public string InstallPath => Path.Combine(dataHome, "nucleodrive", "cli", "proton-drive");

    public string LinkPath => Path.Combine(binDir, "proton-drive");

    /// <summary>Proton's platform name for this machine, e.g. linux-x64 or linux-arm64-musl.</summary>
    public static string CurrentPlatform()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            var a => throw new DriveException($"Proton doesn't publish the CLI for {a} Linux."),
        };
        var musl = Directory.Exists("/lib") && Directory.EnumerateFiles("/lib", "ld-musl-*").Any();
        return $"linux-{arch}{(musl ? "-musl" : "")}";
    }

    /// <summary>The latest release for <paramref name="platform"/> from Proton's index.</summary>
    public async Task<CliRelease> GetLatestAsync(string? platform = null, CancellationToken ct = default)
    {
        platform ??= CurrentPlatform();
        string page;
        try
        {
            page = await http.GetStringAsync(IndexUrl, ct);
        }
        catch (HttpRequestException e)
        {
            throw new DriveException($"Couldn't reach Proton's download page: {e.Message}", e);
        }
        return ParseIndex(page, platform);
    }

    /// <summary>Parses the release index: rows of platform URL and SHA-512 (HTML tags in between are fine).</summary>
    public static CliRelease ParseIndex(string html, string platform)
    {
        var text = System.Net.WebUtility.HtmlDecode(TagRegex().Replace(html, " "));
        foreach (Match m in RowRegex().Matches(text))
        {
            if (m.Groups["platform"].Value == platform)
                return new CliRelease(m.Groups["version"].Value, platform, new Uri(m.Groups["url"].Value), m.Groups["sha"].Value.ToLowerInvariant());
        }
        throw new DriveException($"Proton's download page has no CLI build for {platform}.");
    }

    /// <summary>Downloads, verifies and installs <paramref name="release"/>, then links it into ~/.local/bin.</summary>
    /// <param name="confirmReplace">Asked before replacing something at ~/.local/bin/proton-drive that isn't
    /// already our link (e.g. a CLI you put there yourself). Answering no leaves it alone.</param>
    /// <returns>The installed path.</returns>
    public async Task<string> InstallAsync(CliRelease release, Func<string, Task<bool>> confirmReplace,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!release.Url.AbsoluteUri.StartsWith(AllowedPrefix, StringComparison.Ordinal))
            throw new DriveException($"Refusing to download the CLI from {release.Url} (not Proton's download site).");

        var dir = Path.GetDirectoryName(InstallPath)!;
        Directory.CreateDirectory(dir);
        var partial = Path.Combine(dir, $".proton-drive.{Guid.NewGuid():N}.download");
        try
        {
            progress?.Report($"Downloading proton-drive {release.Version}…");
            using (var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write);
                await source.CopyToAsync(file, ct);
            }

            progress?.Report("Checking the download…");
            string sha;
            await using (var f = File.OpenRead(partial))
                sha = Convert.ToHexStringLower(await SHA512.HashDataAsync(f, ct));
            if (sha != release.Sha512)
                throw new DriveException("The download doesn't match the checksum Proton publishes; it was discarded.");
            if (!IsElfExecutable(partial))
                throw new DriveException("The download isn't a Linux executable; it was discarded.");

            File.SetUnixFileMode(partial, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            var version = await _versionOf(partial, ct);
            if (version is null)
                throw new DriveException("The downloaded CLI doesn't run on this computer; it was discarded.");

            File.Move(partial, InstallPath, overwrite: true);
        }
        catch (HttpRequestException e)
        {
            throw new DriveException($"Download failed: {e.Message}", e);
        }
        finally
        {
            File.Delete(partial);
        }

        await LinkAsync(confirmReplace);
        progress?.Report($"Installed proton-drive {release.Version}");
        return InstallPath;
    }

    private async Task LinkAsync(Func<string, Task<bool>> confirmReplace)
    {
        Directory.CreateDirectory(binDir);
        var existing = new FileInfo(LinkPath);
        if (existing.LinkTarget == InstallPath)
            return;
        if (existing.Exists || existing.LinkTarget is not null)
        {
            var what = existing.LinkTarget is { } target ? $"{LinkPath} (a link to {target})" : LinkPath;
            if (!await confirmReplace(what))
                return;
            File.Delete(LinkPath);
        }
        File.CreateSymbolicLink(LinkPath, InstallPath);
    }

    /// <summary>"0.9.0" from `proton-drive --version` ("Proton Drive CLI cli-drive@0.9.0+…"), or null if it doesn't run.</summary>
    public static async Task<string?> RunVersionAsync(string binary, CancellationToken ct = default)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(binary, ["--version"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            })!;
            p.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var output = await p.StandardOutput.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            return VersionRegex().Match(output) is { Success: true } m ? m.Groups[1].Value : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    /// <summary>Compares dotted versions numerically ("0.10.0" > "0.9.1").</summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        var pb = b.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var c = (i < pa.Length ? pa[i] : 0).CompareTo(i < pb.Length ? pb[i] : 0);
            if (c != 0)
                return c;
        }
        return 0;
    }

    private static bool IsElfExecutable(string path)
    {
        Span<byte> head = stackalloc byte[4];
        using var f = File.OpenRead(path);
        return f.Read(head) == 4 && head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F';
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"(?<url>https://proton\.me/download/drive/cli/(?<version>\d+(?:\.\d+)+)/(?<platform>[a-z0-9-]+)/proton-drive(?:\.exe)?)\s+(?<sha>[0-9a-fA-F]{128})\b")]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"cli-drive@(\d+(?:\.\d+)+)")]
    private static partial Regex VersionRegex();
}
