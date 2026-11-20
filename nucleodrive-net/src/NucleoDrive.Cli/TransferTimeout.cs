namespace NucleoDrive.Cli;

public static class TransferTimeout
{
    /// <summary>Bytes/s; a transfer slower than this is treated as stuck.</summary>
    public const long MinRate = 256 * 1024;

    /// <summary>10 minutes of slack plus the time the transfer takes at <see cref="MinRate"/>.</summary>
    public static TimeSpan For(long bytes) => TimeSpan.FromSeconds(600 + Math.Floor(bytes / (double)MinRate));

    /// <summary>Total size of files and folders (recursively); unreadable entries count as 0.</summary>
    public static long LocalSize(IEnumerable<string> paths)
    {
        long total = 0;
        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p))
                {
                    var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
                    foreach (var f in new DirectoryInfo(p).EnumerateFiles("*", opts))
                    {
                        if (f.LinkTarget is null)
                            total += f.Length;
                    }
                }
                else if (File.Exists(p))
                {
                    total += new FileInfo(p).Length;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // best effort: an unreadable file just doesn't extend the timeout
            }
        }
        return total;
    }
}
