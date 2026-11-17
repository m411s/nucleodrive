using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Pdrive.Daemon;

/// <summary>Checks who is on the other end of the control socket (SO_PEERCRED). The socket file is
/// already 0600 in a 0700 folder; this is defence in depth in case those permissions are ever wrong.</summary>
public static partial class PeerCredentials
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    [LibraryImport("libc")]
    private static partial uint getuid();

    public static uint CurrentUid => getuid();

    /// <summary>struct ucred { pid_t pid; uid_t uid; gid_t gid; } → uid, or null if unavailable.</summary>
    public static uint? PeerUid(Socket socket)
    {
        try
        {
            Span<byte> cred = stackalloc byte[12];
            return socket.GetRawSocketOption(SolSocket, SoPeerCred, cred) >= 8 ? BitConverter.ToUInt32(cred[4..8]) : null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    public static bool IsSameUser(Socket socket) => PeerUid(socket) == CurrentUid;
}
