using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NucleoDrive.Ipc;
using NucleoDrive.Sync;
using StreamJsonRpc;

namespace NucleoDrive.Daemon;

/// <summary>Serves <see cref="Rpc"/> on a Unix socket (mode 0600 in a 0700 runtime dir) and pushes
/// StatusChanged notifications to every connected client.</summary>
public sealed class RpcServer(NucleoDrivePaths paths, SyncWorker worker, TwoWaySyncWorker sync, IHostApplicationLifetime lifetime, ILogger<RpcServer> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await ServeForeverAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Without the socket nobody can control us: stop with a failure exit code so systemd notices.
            log.LogCritical(e, "Can't serve the control socket {Socket}: {Error}", paths.DaemonSocket, e.Message);
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
    }

    private async Task ServeForeverAsync(CancellationToken ct)
    {
        SocketPath.Validate(paths.DaemonSocket);
        NucleoDrivePaths.EnsurePrivateDir(paths.RuntimeDir);
        File.Delete(paths.DaemonSocket); // stale from a crash; safe because we hold the daemon lock

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(paths.DaemonSocket));
        File.SetUnixFileMode(paths.DaemonSocket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(16);
        log.LogInformation("Listening on {Socket}", paths.DaemonSocket);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var socket = await listener.AcceptAsync(ct);
                if (!PeerCredentials.IsSameUser(socket))
                {
                    log.LogWarning("Refused a control connection from another user");
                    socket.Dispose();
                    continue;
                }
                _ = Task.Run(() => ServeAsync(socket, ct), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            File.Delete(paths.DaemonSocket);
        }
    }

    private async Task ServeAsync(Socket socket, CancellationToken ct)
    {
        await using var stream = new NetworkStream(socket, ownsSocket: true);
        using var rpc = new JsonRpc(Rpc.CreateHandler(stream));
        rpc.AddLocalRpcTarget(new RpcTarget(worker, sync));

        void Push(DaemonStatus s) => rpc.NotifyAsync(Rpc.StatusChanged, s)
            .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

        worker.StatusChanged += Push;
        try
        {
            rpc.StartListening();
            await rpc.Completion.WaitAsync(ct);
        }
        catch (Exception e) when (e is OperationCanceledException or ConnectionLostException or IOException)
        {
        }
        finally
        {
            worker.StatusChanged -= Push;
        }
    }
}

/// <summary>The methods clients can call (names match <see cref="Rpc"/>).</summary>
public sealed class RpcTarget(SyncWorker worker, TwoWaySyncWorker sync)
{
    public DaemonStatus GetStatus() => worker.Status;

    public void SyncNow() => worker.SyncNow();

    public void Pause() => worker.Pause();

    public void Resume() => worker.Resume();

    public FolderInfo[] ListFolders() => worker.Folders.Select(f => new FolderInfo(f.Local, f.Remote)).ToArray();

    public void AddFolder(string local, string remote) => Refusable(() => worker.AddFolder(local, remote));

    public void RemoveFolder(string local) => Refusable(() => worker.RemoveFolder(local));

    public SyncSettingsInfo GetSyncSettings() => sync.GetSettings();

    public void SetSyncFolders(string[] folders) => Refusable(() => sync.SetFolders(folders));

    public void ConfirmDeletions() => sync.ConfirmDeletions();

    public void SetSyncLocal(string local) => Refusable(() => sync.SetLocal(local));

    public void SetSyncInterval(double minutes) => Refusable(() => sync.SetInterval(minutes));

    private static void Refusable(Action action)
    {
        try
        {
            action();
        }
        catch (ConfigException e)
        {
            throw new LocalRpcException(e.Message);
        }
    }
}
