using System.Net;
using System.Security.Cryptography;
using Pdrive.Tests;

namespace Pdrive.Cli.Tests;

public sealed class CliInstallerTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly StubHandler _http = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Shaped like https://proton.me/download/drive/cli/index.html (a table: platform, URL, SHA-512).
    private static string Index(string linuxX64Sha) => $"""
        <html><body><h1>Proton Drive CLI 9.9.9</h1><p>Release date: 2026-12-01</p>
        <table><tr><th>Platform</th><th>URL</th><th>Checksum (SHA-512)</th></tr>
        <tr><td>linux/arm64</td><td><a href="https://proton.me/download/drive/cli/9.9.9/linux-arm64/proton-drive">https://proton.me/download/drive/cli/9.9.9/linux-arm64/proton-drive</a></td><td>{new string('a', 128)}</td></tr>
        <tr><td>linux/x64</td><td><a href="https://proton.me/download/drive/cli/9.9.9/linux-x64/proton-drive">https://proton.me/download/drive/cli/9.9.9/linux-x64/proton-drive</a></td><td>{linuxX64Sha}</td></tr>
        </table></body></html>
        """;

    private static readonly byte[] Elf = [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, 42, 42, 42];
    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA512.HashData(b));

    private CliInstaller Installer(string? version = "9.9.9") =>
        new(new HttpClient(_http), _tmp["data"], _tmp["bin"], (_, _) => Task.FromResult(version));

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Parses_the_release_index()
    {
        var r = CliInstaller.ParseIndex(Index(new string('b', 128)), "linux-x64");
        Assert.Equal("9.9.9", r.Version);
        Assert.Equal("https://proton.me/download/drive/cli/9.9.9/linux-x64/proton-drive", r.Url.AbsoluteUri);
        Assert.Equal(new string('b', 128), r.Sha512);
        Assert.Throws<DriveException>(() => CliInstaller.ParseIndex(Index("x"), "freebsd-x64"));
    }

    [Fact]
    public async Task Installs_a_verified_download_and_links_it()
    {
        _http.Files["/download/drive/cli/index.html"] = System.Text.Encoding.UTF8.GetBytes(Index(Sha(Elf)));
        _http.Files["/download/drive/cli/9.9.9/linux-x64/proton-drive"] = Elf;
        var installer = Installer();
        var release = await installer.GetLatestAsync("linux-x64", Ct);
        var asked = 0;
        var path = await installer.InstallAsync(release, _ => { asked++; return Task.FromResult(true); }, ct: Ct);

        Assert.Equal(_tmp["data/pdrive/cli/proton-drive"], path);
        Assert.Equal(Elf, File.ReadAllBytes(path));
        Assert.True((File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0);
        Assert.Equal(path, new FileInfo(_tmp["bin/proton-drive"]).LinkTarget);
        Assert.Equal(0, asked);
        Assert.Single(Directory.GetFiles(_tmp["data/pdrive/cli"])); // no leftovers

        await installer.InstallAsync(release, _ => { asked++; return Task.FromResult(true); }, ct: Ct);
        Assert.Equal(0, asked); // our own link: nothing to ask
    }

    [Fact]
    public async Task Asks_before_replacing_a_cli_put_there_by_hand()
    {
        _http.Files["/download/drive/cli/9.9.9/linux-x64/proton-drive"] = Elf;
        Directory.CreateDirectory(_tmp["bin"]);
        File.WriteAllText(_tmp["elsewhere"], "mine");
        File.CreateSymbolicLink(_tmp["bin/proton-drive"], _tmp["elsewhere"]);
        var release = new CliRelease("9.9.9", "linux-x64", new Uri("https://proton.me/download/drive/cli/9.9.9/linux-x64/proton-drive"), Sha(Elf));

        string? question = null;
        await Installer().InstallAsync(release, what => { question = what; return Task.FromResult(false); }, ct: Ct);
        Assert.Contains(_tmp["elsewhere"], question);
        Assert.Equal(_tmp["elsewhere"], new FileInfo(_tmp["bin/proton-drive"]).LinkTarget); // left alone
        Assert.True(File.Exists(_tmp["data/pdrive/cli/proton-drive"])); // but installed

        await Installer().InstallAsync(release, _ => Task.FromResult(true), ct: Ct);
        Assert.Equal(_tmp["data/pdrive/cli/proton-drive"], new FileInfo(_tmp["bin/proton-drive"]).LinkTarget);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("not-elf")]
    [InlineData("does-not-run")]
    [InlineData("wrong-host")]
    public async Task Bad_downloads_are_never_installed(string problem)
    {
        byte[] body = problem == "not-elf" ? "#!/bin/sh\necho hi\n"u8.ToArray() : Elf;
        _http.Files["/download/drive/cli/9.9.9/linux-x64/proton-drive"] = body;
        var url = problem == "wrong-host" ? "https://example.com/download/drive/cli/9.9.9/linux-x64/proton-drive"
            : "https://proton.me/download/drive/cli/9.9.9/linux-x64/proton-drive";
        var release = new CliRelease("9.9.9", "linux-x64", new Uri(url), problem == "checksum" ? new string('0', 128) : Sha(body));
        var installer = Installer(problem == "does-not-run" ? null : "9.9.9");

        await Assert.ThrowsAsync<DriveException>(() => installer.InstallAsync(release, _ => Task.FromResult(true), ct: Ct));
        Assert.False(File.Exists(_tmp["data/pdrive/cli/proton-drive"]));
        Assert.False(File.Exists(_tmp["bin/proton-drive"]));
        if (Directory.Exists(_tmp["data/pdrive/cli"]))
            Assert.Empty(Directory.GetFiles(_tmp["data/pdrive/cli"], "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("0.10.0", "0.9.1", 1)]
    [InlineData("0.9.0", "0.9.0", 0)]
    [InlineData("1.0", "1.0.1", -1)]
    public void Compares_versions_numerically(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(CliInstaller.CompareVersions(a, b)));

    private sealed class StubHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Files.TryGetValue(request.RequestUri!.AbsolutePath, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
