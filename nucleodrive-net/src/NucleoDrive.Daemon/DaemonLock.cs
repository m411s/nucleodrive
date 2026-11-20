using System.Globalization;

namespace NucleoDrive.Daemon;

/// <summary>Single-instance lock. .NET takes an flock(LOCK_EX) for FileShare.None on Unix, which the
/// kernel releases when the process dies, so a crash never leaves a stale lock behind.</summary>
public sealed class DaemonLock : IDisposable
{
    private readonly FileStream _file;

    private DaemonLock(FileStream file) => _file = file;

    /// <summary>The lock, or null if another daemon holds it.</summary>
    public static DaemonLock? TryAcquire(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileStream fs;
        try
        {
            fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        fs.SetLength(0);
        var pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        fs.Write(System.Text.Encoding.ASCII.GetBytes(pid));
        fs.Flush();
        return new DaemonLock(fs);
    }

    public void Dispose() => _file.Dispose();
}
