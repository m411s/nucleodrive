using System.Globalization;
using System.Text;

namespace Pdrive.Sync;

/// <summary>Moves files and folders to the desktop Trash (freedesktop.org Trash spec 1.0), so the
/// user can restore them from the file manager. Uses the home trash ($XDG_DATA_HOME/Trash) when the
/// item is on the same filesystem, otherwise $topdir/.Trash-$uid on the item's own filesystem.</summary>
public sealed class FreedesktopTrash
{
    private readonly string _homeTrash;
    private readonly Func<DateTime> _now;
    private readonly Func<IEnumerable<string>> _mountPoints;

    public FreedesktopTrash(string? dataHome = null, Func<DateTime>? now = null, Func<IEnumerable<string>>? mountPoints = null)
    {
        dataHome ??= Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share");
        _homeTrash = Path.Combine(dataHome, "Trash");
        _now = now ?? (() => DateTime.Now);
        _mountPoints = mountPoints ?? ReadMountPoints;
    }

    /// <summary>Trashes <paramref name="path"/> and returns where it went.</summary>
    /// <exception cref="IOException">It couldn't be trashed (it is left in place).</exception>
    public string Trash(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException("Nothing to trash", path);

        try
        {
            return MoveInto(_homeTrash, path, Uri.EscapeDataString(path).Replace("%2F", "/", StringComparison.Ordinal));
        }
        catch (IOException) when (TopDirFor(path) is { } topDir && !IsUnder(_homeTrash, topDir))
        {
            // Different filesystem: use the trash at the top of that filesystem.
            var trash = Path.Combine(topDir, $".Trash-{GetUid()}");
            var relative = Path.GetRelativePath(topDir, path);
            return MoveInto(trash, path, Uri.EscapeDataString(relative).Replace("%2F", "/", StringComparison.Ordinal));
        }
    }

    private string MoveInto(string trashDir, string path, string encodedPath)
    {
        var files = Path.Combine(trashDir, "files");
        var info = Path.Combine(trashDir, "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);
        try
        {
            File.SetUnixFileMode(trashDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // not ours to change; keep going
        }

        var baseName = Path.GetFileName(path);
        var contents = Encoding.UTF8.GetBytes(
            $"[Trash Info]\nPath={encodedPath}\nDeletionDate={_now().ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}\n");
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? baseName : $"{baseName}.{n}";
            var infoFile = Path.Combine(info, name + ".trashinfo");
            var target = Path.Combine(files, name);
            FileStream stream;
            try
            {
                stream = new FileStream(infoFile, FileMode.CreateNew, FileAccess.Write); // reserves the name atomically
            }
            catch (IOException) when (File.Exists(infoFile))
            {
                continue;
            }
            using (stream)
                stream.Write(contents);
            if (File.Exists(target) || Directory.Exists(target))
            {
                File.Delete(infoFile);
                continue;
            }
            try
            {
                if (Directory.Exists(path))
                    Directory.Move(path, target);
                else
                    File.Move(path, target);
                return target;
            }
            catch
            {
                File.Delete(infoFile);
                throw;
            }
        }
    }

    /// <summary>The mount point containing <paramref name="path"/> (longest matching prefix).</summary>
    private string? TopDirFor(string path) =>
        _mountPoints().Where(m => IsUnder(path, m)).OrderByDescending(m => m.Length).FirstOrDefault();

    private static bool IsUnder(string path, string dir) =>
        dir == "/" || path == dir || path.StartsWith(dir.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static IEnumerable<string> ReadMountPoints()
    {
        try
        {
            // /proc/self/mounts: "device mountpoint fstype ..."; spaces in paths are octal-escaped.
            return File.ReadLines("/proc/self/mounts")
                .Select(l => l.Split(' '))
                .Where(f => f.Length > 1)
                .Select(f => f[1].Replace("\\040", " ", StringComparison.Ordinal))
                .ToList();
        }
        catch (IOException)
        {
            return ["/"];
        }
    }

    private static string GetUid()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                    return line.Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries)[1];
            }
        }
        catch (IOException)
        {
        }
        return Environment.UserName;
    }
}
