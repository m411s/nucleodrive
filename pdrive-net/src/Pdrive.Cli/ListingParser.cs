using System.Text.Json;

namespace Pdrive.Cli;

public sealed record DriveEntry(string Name, bool IsDir, long? Size, string Path);

/// <summary>Tolerant parser for `filesystem list --json`: the schema isn't documented,
/// so it accepts a bare list or an object wrapping one, and several key spellings.</summary>
public static class ListingParser
{
    private static readonly string[] ListKeys = ["items", "entries", "children", "nodes", "files", "data", "result"];

    public static IReadOnlyList<DriveEntry> Parse(string raw, string parent)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException e)
        {
            throw new DriveException($"Could not parse CLI output as JSON: {e.Message}\n{Truncate(raw, 300)}", e);
        }

        using (doc)
        {
            var list = doc.RootElement;
            if (list.ValueKind == JsonValueKind.Object)
            {
                var wrapped = ListKeys
                    .Select(k => list.TryGetProperty(k, out var v) ? v : default)
                    .FirstOrDefault(v => v.ValueKind == JsonValueKind.Array);
                if (wrapped.ValueKind != JsonValueKind.Array)
                    throw new DriveException($"Unrecognised listing format from the CLI:\n{Truncate(raw, 300)}");
                list = wrapped;
            }
            if (list.ValueKind != JsonValueKind.Array)
                throw new DriveException($"Unrecognised listing format from the CLI:\n{Truncate(raw, 300)}");

            var entries = new List<DriveEntry>();
            foreach (var item in list.EnumerateArray())
            {
                if (ParseItem(item, parent) is { } entry)
                    entries.Add(entry);
            }
            return entries
                .OrderBy(e => !e.IsDir)
                .ThenBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal)
                .ToList();
        }
    }

    private static DriveEntry? ParseItem(JsonElement item, string parent)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            var bare = item.GetString();
            return string.IsNullOrEmpty(bare) ? null : new DriveEntry(bare, false, null, RemotePath.Join(parent, RemotePath.EscapeName(bare)));
        }
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        var name = First(item, "name", "Name", "title")?.ToString();
        if (string.IsNullOrEmpty(name))
            return null;

        var kind = (First(item, "type", "kind", "nodeType", "node_type")?.ToString() ?? "").ToLowerInvariant();
        var isDir = Truthy(First(item, "isFolder", "is_folder", "isDirectory", "is_dir"))
                    || kind.Contains("folder") || kind.Contains("dir");

        long? size = First(item, "size", "Size", "claimedSize", "totalStorageSize") is { ValueKind: JsonValueKind.Number } s
                     && s.TryGetInt64(out var n) ? n : null;

        var path = First(item, "path", "fullPath") is { ValueKind: JsonValueKind.String } p && p.GetString() is { Length: > 0 } ps
            ? ps
            : RemotePath.Join(parent, RemotePath.EscapeName(name));

        return new DriveEntry(name, isDir, size, path);
    }

    /// <summary>First key that is present and not null.</summary>
    private static JsonElement? First(JsonElement obj, params ReadOnlySpan<string> keys)
    {
        foreach (var k in keys)
        {
            if (obj.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null)
                return v;
        }
        return null;
    }

    private static bool Truthy(JsonElement? v) => v?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => v.Value.TryGetDouble(out var d) && d != 0,
        JsonValueKind.String => bool.TryParse(v.Value.GetString(), out var b) && b,
        _ => false,
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
