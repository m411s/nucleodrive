using System.Text.Json;

namespace NucleoDrive.Cli;

public sealed record DriveEntry(string Name, bool IsDir, long? Size, string Path, string? MediaType = null)
{
    /// <summary>Proton Docs/Sheets live only on Drive; `filesystem download` skips them.</summary>
    public bool IsProtonDocument => MediaType?.StartsWith("application/vnd.proton.", StringComparison.Ordinal) == true;

    /// <summary>Node id (stable across renames and moves).</summary>
    public string? Uid { get; init; }

    /// <summary>Id of the current revision; changes whenever the content changes.</summary>
    public string? RevisionUid { get; init; }

    /// <summary>The file's own modification time as uploaded (claimedModificationTime), else the node's.</summary>
    public DateTimeOffset? ModifiedAt { get; init; }

    /// <summary>SHA-1 of the content as claimed by the uploader (lowercase hex), if known.</summary>
    public string? Sha1 { get; init; }

    /// <summary>The name couldn't be decrypted; <see cref="Name"/> is a placeholder and <see cref="Path"/> uses the uid.</summary>
    public bool NameUnavailable { get; init; }
}

/// <summary>Parser for `filesystem list --json`. The real schema (cli-drive 0.9.0, see
/// tests/NucleoDrive.Cli.Tests/Fixtures/list-folder.json) is a bare list of nodes with
/// name {ok, value}, type file|folder, uid, mediaType and activeRevision.claimedSize. It stays
/// tolerant of other shapes and spellings in case the undocumented format changes.</summary>
public static class ListingParser
{
    private static readonly string[] ListKeys = ["items", "entries", "children", "nodes", "files", "data", "result"];

    /// <summary>Parses `filesystem info PATH --json` (a single node object).</summary>
    public static DriveEntry ParseInfo(string raw, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var entry = ParseItem(doc.RootElement, RemotePath.Parent(path))
                ?? throw new DriveException($"Unrecognised node format from the CLI:\n{Truncate(raw, 300)}");
            return entry with { Path = path };
        }
        catch (JsonException e)
        {
            throw new DriveException($"Could not parse CLI output as JSON: {e.Message}\n{Truncate(raw, 300)}", e);
        }
    }

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

        // cli-drive 0.9.0: "name": {"ok": true, "value": "file.txt"}; ok=false when it can't be decrypted.
        var uid = First(item, "uid") is { ValueKind: JsonValueKind.String } u ? u.GetString() : null;
        string? name;
        var nameOk = true;
        if (First(item, "name", "Name", "title") is { ValueKind: JsonValueKind.Object } nameObj)
        {
            nameOk = !(nameObj.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False);
            name = nameObj.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        else
        {
            name = First(item, "name", "Name", "title")?.ToString();
        }
        string segment;
        var unavailable = false;
        if (nameOk && !string.IsNullOrEmpty(name))
        {
            segment = RemotePath.EscapeName(name);
        }
        else if (uid is not null)
        {
            // The CLI accepts node UIDs in paths where names can't be used.
            segment = uid;
            unavailable = true;
            var nodeId = uid.Split('~')[^1];
            name = $"(name unavailable) {nodeId[..Math.Min(8, nodeId.Length)]}";
        }
        else
        {
            return null;
        }

        var kind = (First(item, "type", "kind", "nodeType", "node_type")?.ToString() ?? "").ToLowerInvariant();
        var isDir = Truthy(First(item, "isFolder", "is_folder", "isDirectory", "is_dir"))
                    || kind.Contains("folder") || kind.Contains("dir");

        // Real content size is activeRevision.claimedSize. totalStorageSize counts every revision
        // plus encryption overhead, so it's deliberately not used.
        var size = Int(First(item, "claimedSize", "size", "Size"))
                   ?? (First(item, "activeRevision") is { ValueKind: JsonValueKind.Object } rev ? Int(First(rev, "claimedSize", "size")) : null);

        var path = First(item, "path", "fullPath") is { ValueKind: JsonValueKind.String } p && p.GetString() is { Length: > 0 } ps
            ? ps
            : RemotePath.Join(parent, segment);

        var mediaType = First(item, "mediaType", "mimeType") is { ValueKind: JsonValueKind.String } m ? m.GetString() : null;
        var revision = First(item, "activeRevision") is { ValueKind: JsonValueKind.Object } r ? r : (JsonElement?)null;
        var modified = Time(revision is { } r1 ? First(r1, "claimedModificationTime") : null)
                       ?? Time(First(item, "folder") is { ValueKind: JsonValueKind.Object } f ? First(f, "claimedModificationTime") : null)
                       ?? Time(First(item, "modificationTime"));
        string? sha1 = null;
        if (revision is { } r2 && First(r2, "claimedDigests") is { ValueKind: JsonValueKind.Object } digests
            && First(digests, "sha1") is { ValueKind: JsonValueKind.String } s1)
            sha1 = s1.GetString()?.ToLowerInvariant();

        return new DriveEntry(name!, isDir, size, path, mediaType)
        {
            NameUnavailable = unavailable,
            Uid = uid,
            RevisionUid = revision is { } r3 && First(r3, "uid") is { ValueKind: JsonValueKind.String } ru ? ru.GetString() : null,
            ModifiedAt = modified,
            Sha1 = sha1,
        };
    }

    private static DateTimeOffset? Time(JsonElement? v) =>
        v is { ValueKind: JsonValueKind.String } s && DateTimeOffset.TryParse(s.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static long? Int(JsonElement? v) =>
        v is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var i) ? i : null;

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
