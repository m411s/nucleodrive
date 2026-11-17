using System.Net.Sockets;
using StreamJsonRpc;

namespace Pdrive.Ipc;

/// <summary>A connection to the running daemon over its Unix socket.</summary>
public sealed class DaemonClient : IAsyncDisposable
{
    private readonly JsonRpc _rpc;

    private DaemonClient(Stream stream)
    {
        _rpc = new JsonRpc(Rpc.CreateHandler(stream));
        _rpc.AddLocalRpcMethod(Rpc.StatusChanged, new Action<DaemonStatus>(s => StatusChanged?.Invoke(s)));
        _rpc.StartListening();
    }

    /// <summary>Raised (on a thread-pool thread) whenever the daemon's status changes.</summary>
    public event Action<DaemonStatus>? StatusChanged;

    /// <summary>Completes when the connection ends (daemon stopped or crashed).</summary>
    public Task Completion => _rpc.Completion;

    /// <summary>Connects, or returns null if no daemon is listening.</summary>
    /// <exception cref="DaemonException">The socket path is unusable (too long).</exception>
    public static async Task<DaemonClient?> TryConnectAsync(string socketPath, CancellationToken ct = default)
    {
        SocketPath.Validate(socketPath);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
            return new DaemonClient(new NetworkStream(socket, ownsSocket: true));
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }

    public Task<DaemonStatus> GetStatusAsync(CancellationToken ct = default) => Call<DaemonStatus>(Rpc.GetStatus, [], ct);
    public Task SyncNowAsync(CancellationToken ct = default) => Call(Rpc.SyncNow, [], ct);
    public Task PauseAsync(CancellationToken ct = default) => Call(Rpc.Pause, [], ct);
    public Task ResumeAsync(CancellationToken ct = default) => Call(Rpc.Resume, [], ct);
    public Task<FolderInfo[]> ListFoldersAsync(CancellationToken ct = default) => Call<FolderInfo[]>(Rpc.ListFolders, [], ct);
    public Task AddFolderAsync(string local, string remote, CancellationToken ct = default) => Call(Rpc.AddFolder, [local, remote], ct);
    public Task RemoveFolderAsync(string local, CancellationToken ct = default) => Call(Rpc.RemoveFolder, [local], ct);

    private async Task<T> Call<T>(string method, object?[] args, CancellationToken ct)
    {
        try
        {
            return await _rpc.InvokeWithCancellationAsync<T>(method, args, ct);
        }
        catch (RemoteInvocationException e)
        {
            throw new DaemonException(e.Message, e);
        }
        catch (ConnectionLostException e)
        {
            throw new DaemonException("Lost connection to the backup daemon.", e);
        }
    }

    private async Task Call(string method, object?[] args, CancellationToken ct)
    {
        try
        {
            await _rpc.InvokeWithCancellationAsync(method, args, ct);
        }
        catch (RemoteInvocationException e)
        {
            throw new DaemonException(e.Message, e);
        }
        catch (ConnectionLostException e)
        {
            throw new DaemonException("Lost connection to the backup daemon.", e);
        }
    }

    public ValueTask DisposeAsync()
    {
        _rpc.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Keeps a connection to the daemon, reconnecting every few seconds, and reports its status,
/// or <see cref="DaemonStatus.NotRunning"/> while it can't be reached. Used by the app and tray.</summary>
public sealed class DaemonMonitor : IAsyncDisposable
{
    private readonly string _socketPath;
    private readonly TimeSpan _retry;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private volatile DaemonClient? _client;

    public DaemonMonitor(string socketPath, TimeSpan? retry = null)
    {
        _socketPath = socketPath;
        _retry = retry ?? TimeSpan.FromSeconds(3);
    }

    public DaemonStatus Status { get; private set; } = DaemonStatus.NotRunning;

    /// <summary>The live connection, or null while the daemon isn't running.</summary>
    public DaemonClient? Client => _client;

    public event Action<DaemonStatus>? StatusChanged;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    private void Publish(DaemonStatus s)
    {
        Status = s;
        StatusChanged?.Invoke(s);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DaemonClient? client;
            try
            {
                client = await DaemonClient.TryConnectAsync(_socketPath, ct).ConfigureAwait(false);
            }
            catch (DaemonException e)
            {
                Publish(DaemonStatus.NotRunning with { Message = e.Message });
                return; // misconfiguration; retrying won't help
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (client is not null)
            {
                try
                {
                    client.StatusChanged += Publish;
                    _client = client;
                    Publish(await client.GetStatusAsync(ct).ConfigureAwait(false));
                    await client.Completion.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is DaemonException or ConnectionLostException or ObjectDisposedException)
                {
                    // daemon went away; fall through to "not running" and retry
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                finally
                {
                    _client = null;
                    client.StatusChanged -= Publish;
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }
            if (Status.State != DaemonState.Stopped)
                Publish(DaemonStatus.NotRunning);
            try
            {
                await Task.Delay(_retry, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
            await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
