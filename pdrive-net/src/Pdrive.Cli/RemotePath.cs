namespace Pdrive.Cli;

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

    /// <summary>Parent folder, or "" at "/". Like posixpath.dirname.</summary>
    public static string Parent(string path)
    {
        var i = path.LastIndexOf('/');
        if (i < 0)
            return "";
        var head = path[..(i + 1)];
        return head.Trim('/').Length == 0 ? head : head.TrimEnd('/');
    }

    public static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];
}
