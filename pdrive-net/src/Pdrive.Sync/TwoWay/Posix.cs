using System.Runtime.InteropServices;

namespace Pdrive.Sync.TwoWay;

/// <summary>File metadata .NET doesn't expose (inode, nanosecond mtime, symlink-ness), via statx(2).
/// struct statx has the same layout on every Linux architecture.</summary>
public static partial class Posix
{
    public readonly record struct Stat(long Inode, long Device, long Size, long Mtime100ns, bool IsDir, bool IsSymlink, bool IsRegular);

    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x7ff;

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int StatxNative(int dirfd, string path, int flags, uint mask, byte* buffer);

    /// <summary>Stats <paramref name="path"/> without following a final symlink; null if it doesn't exist or can't be read.</summary>
    public static unsafe Stat? TryStat(string path)
    {
        byte* buf = stackalloc byte[256];
        if (StatxNative(AtFdCwd, path, AtSymlinkNoFollow, StatxBasicStats, buf) != 0)
            return null;
        var mode = *(ushort*)(buf + 28);
        var inode = *(long*)(buf + 32);
        var size = *(long*)(buf + 40);
        var mtimeSec = *(long*)(buf + 112);
        var mtimeNsec = *(uint*)(buf + 120);
        var devMajor = *(uint*)(buf + 136);
        var devMinor = *(uint*)(buf + 140);
        var type = mode & 0xF000;
        return new Stat(inode, ((long)devMajor << 32) | devMinor, size, mtimeSec * 10_000_000 + mtimeNsec / 100,
            IsDir: type == 0x4000, IsSymlink: type == 0xA000, IsRegular: type == 0x8000);
    }
}
