namespace NucleoDrive.Cli;

/// <summary>Drive paths are always '/'-separated, whatever the local OS uses.</summary>
public static class RemotePath
{
    public const string Root = "/my-files";

    public static string Join(string parent, params ReadOnlySpan<string> parts)
    {
        var result = parent;
        foreach (var part in parts)
        {
            if (part.Length == 0)
                continue;
            if (part.StartsWith('/'))
                result = part;
            else
                result = result.EndsWith('/') ? result + part : result + "/" + part;
        }
        return result;
    }

    /// <summary>Parent folder, or "" at "/". Like posixpath.dirname, but skips escaped "\/" in names.</summary>
    public static string Parent(string path)
    {
        var i = LastSeparator(path);
        if (i < 0)
            return "";
        var head = path[..(i + 1)];
        return head.Trim('/').Length == 0 ? head : head.TrimEnd('/');
    }

    /// <summary>Last segment (still escaped). Ignores an escaped "\/" inside the name.</summary>
    public static string Name(string path) => path[(LastSeparator(path) + 1)..];

    /// <summary>The CLI escapes '/' inside node names as "\/" in paths.</summary>
    public static string EscapeName(string name) => name.Replace("/", "\\/", StringComparison.Ordinal);

    public static string UnescapeName(string name) => name.Replace("\\/", "/", StringComparison.Ordinal);

    private static int LastSeparator(string path)
    {
        for (var i = path.Length - 1; i >= 0; i--)
        {
            if (path[i] == '/' && (i == 0 || path[i - 1] != '\\'))
                return i;
        }
        return -1;
    }
}
