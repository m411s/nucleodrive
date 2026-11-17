using System.IO.Enumeration;

namespace Pdrive.Sync;

public sealed record ScanResult(Dictionary<string, FileSig> Files, DateTimeOffset? NextMatureAt);

public static class Scanner
{
    /// <summary>Regular files under <paramref name="root"/> keyed by '/'-separated relative path.
    /// Skips excluded names (files and folders), symlinks, unreadable entries, and files modified
    /// less than <paramref name="minAge"/> ago; <see cref="ScanResult.NextMatureAt"/> says when the
    /// earliest of those becomes old enough, so the caller can scan again then.</summary>
    public static ScanResult Scan(string root, IReadOnlyList<string> exclude, TimeSpan minAge, DateTimeOffset? now = null)
    {
        var files = new Dictionary<string, FileSig>(StringComparer.Ordinal);
        var t = now ?? DateTimeOffset.UtcNow;
        DateTimeOffset? nextMature = null;
        var pending = new Stack<(DirectoryInfo Dir, string Rel)>();
        pending.Push((new DirectoryInfo(root), ""));
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };

        while (pending.TryPop(out var current))
        {
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = current.Dir.EnumerateFileSystemInfos("*", opts).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                if (IsExcluded(entry.Name, exclude))
                    continue;
                try
                {
                    if (entry.LinkTarget is not null)
                        continue; // never follow or upload symlinks
                    var rel = current.Rel.Length == 0 ? entry.Name : current.Rel + "/" + entry.Name;
                    if (entry is DirectoryInfo dir)
                    {
                        pending.Push((dir, rel));
                    }
                    else if (entry is FileInfo file)
                    {
                        var mtime = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                        if (t - mtime < minAge)
                        {
                            var mature = mtime + minAge;
                            nextMature = nextMature is { } n && n < mature ? n : mature;
                            continue;
                        }
                        files[rel] = FileSig.Of(file);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // vanished or unreadable since the listing; next scan will see it
                }
            }
        }
        return new ScanResult(files, nextMature);
    }

    public static bool IsExcluded(string name, IReadOnlyList<string> exclude)
    {
        foreach (var pattern in exclude)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: false))
                return true;
        }
        return false;
    }

    /// <summary>True if any segment of a path relative to the folder root is excluded.</summary>
    public static bool IsExcludedPath(string relativePath, IReadOnlyList<string> exclude) =>
        relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(seg => IsExcluded(seg, exclude));
}

public static class Chunker
{
    public const int MaxFiles = 50; // files per CLI call, keeps argv short
    public const long MaxBytes = 256L * 1024 * 1024; // bigger files go in a call of their own

    /// <summary>Groups files into CLI calls of at most <paramref name="maxFiles"/> files and (unless a
    /// single file is bigger) <paramref name="maxBytes"/> bytes, so one large file can't drag a whole
    /// batch into a timeout.</summary>
    public static IEnumerable<List<string>> Chunks(IEnumerable<string> rels, Func<string, long> size,
        int maxFiles = MaxFiles, long maxBytes = MaxBytes)
    {
        var chunk = new List<string>();
        long total = 0;
        foreach (var r in rels)
        {
            var s = size(r);
            if (chunk.Count > 0 && (chunk.Count >= maxFiles || total + s > maxBytes))
            {
                yield return chunk;
                chunk = [];
                total = 0;
            }
            chunk.Add(r);
            total += s;
        }
        if (chunk.Count > 0)
            yield return chunk;
    }
}
