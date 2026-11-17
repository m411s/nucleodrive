using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pdrive.Ipc;
using Pdrive.Sync;
using Pdrive.Tests;

namespace Pdrive.Daemon.Tests;

/// <summary>A daemon host running in-process against temp dirs and the fake CLI.</summary>
public sealed class DaemonFixture : IAsyncDisposable
{
    private DaemonLock? _lock;

    public TempDir Tmp { get; } = new();
    public PdrivePaths Paths { get; }
    public FakeCli Fake { get; }
    public string Src { get; }
    public IHost? Host { get; private set; }
    public DaemonClient? Client { get; private set; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DaemonFixture()
    {
        Paths = PdrivePaths.Under(Tmp.Path);
        Fake = new FakeCli(Tmp["fake.log"]);
        Src = Tmp["src"];
        Directory.CreateDirectory(Path.Combine(Src, "sub"));
        WriteFile("a.txt");
        WriteFile("sub/b.txt");
    }

    public void WriteFile(string rel, bool old = true)
    {
        var p = Path.Combine(Src, rel);
        File.WriteAllText(p, rel + Guid.NewGuid());
        if (old)
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddHours(-1));
    }

    public void WriteConfig(SyncConfig cfg) => new ConfigStore(Paths).Save(cfg);

    public SyncConfig BackupSrc(double minAge = 0, bool watch = true) =>
        new() { Folders = [new FolderPair(Src, "/my-files/Backup")], MinAgeSeconds = minAge, Watch = watch };

    public async Task<DaemonFixture> StartAsync()
    {
        _lock = DaemonLock.TryAcquire(Paths.DaemonLock) ?? throw new InvalidOperationException("lock held");
        Host = DaemonHost.Build([], Paths, new DaemonOptions
        {
            ClientFactory = cfg => Fake.Client(cfg.ConflictStrategy),
            WatchQuiet = TimeSpan.FromMilliseconds(300),
            ProgressThrottle = TimeSpan.Zero,
            MinLogLevel = LogLevel.Warning,
        });
        await Host.StartAsync(Ct);
        Client = await ConnectAsync();
        return this;
    }

    public async Task<DaemonClient> ConnectAsync()
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < end)
        {
            if (await DaemonClient.TryConnectAsync(Paths.DaemonSocket, Ct) is { } c)
                return c;
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException("daemon socket never came up");
    }

    public async Task<DaemonStatus> WaitForStatus(Func<DaemonStatus, bool> pred, int seconds = 10)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        DaemonStatus s;
        do
        {
            s = await Client!.GetStatusAsync(Ct);
            if (pred(s))
                return s;
            await Task.Delay(50, Ct);
        } while (DateTime.UtcNow < end);
        throw new Xunit.Sdk.XunitException($"status never matched; last: {s.State}: {s.Message}");
    }

    public static async Task<bool> WaitFor(Func<bool> pred, int seconds = 10)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (pred())
                return true;
            await Task.Delay(50, Ct);
        }
        return false;
    }

    public async Task StopAsync()
    {
        if (Client is not null)
            await Client.DisposeAsync();
        Client = null;
        if (Host is not null)
        {
            await Host.StopAsync(CancellationToken.None);
            Host.Dispose();
        }
        Host = null;
        _lock?.Dispose();
        _lock = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Tmp.Dispose();
    }
}
