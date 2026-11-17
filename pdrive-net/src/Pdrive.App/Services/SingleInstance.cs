using System.Net.Sockets;
using System.Text;
using Pdrive.Ipc;

namespace Pdrive.App.Services;

/// <summary>One app instance per user: later launches send "show" over a Unix socket and exit.</summary>
public static class SingleInstance
{
    public static bool TrySignalExisting(string socketPath)
    {
        try
        {
            SocketPath.Validate(socketPath);
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(socketPath));
            s.Send(Encoding.ASCII.GetBytes("show\n"));
            return true;
        }
        catch (Exception e) when (e is SocketException or DaemonException)
        {
            return false;
        }
    }

    /// <summary>Listens for "show" from later launches. Returns null if the socket can't be created
    /// (the app still works, just without single-instance behaviour).</summary>
    public static IDisposable? Listen(string socketPath, Action onShow)
    {
        try
        {
            SocketPath.Validate(socketPath);
            Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
            File.Delete(socketPath); // stale: TrySignalExisting just failed to connect to it
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(4);
            var cts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                var buf = new byte[16];
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        using var client = await listener.AcceptAsync(cts.Token);
                        var n = await client.ReceiveAsync(buf, SocketFlags.None, cts.Token);
                        if (Encoding.ASCII.GetString(buf, 0, n).StartsWith("show", StringComparison.Ordinal))
                            onShow();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                    }
                }
            });
            return new Listener(listener, cts, socketPath);
        }
        catch (Exception e) when (e is SocketException or IOException or DaemonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class Listener(Socket socket, CancellationTokenSource cts, string path) : IDisposable
    {
        public void Dispose()
        {
            cts.Cancel();
            socket.Dispose();
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
