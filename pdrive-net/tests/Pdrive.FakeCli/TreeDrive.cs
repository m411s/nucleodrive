using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>A persistent fake Drive stored in a JSON file (FAKE_TREE), mirroring the commands and
/// output shapes of cli-drive 0.9.0: list/info/create-folder/upload/download/trash/rename/move.
/// Content is kept inline (base64); every content change gets a new revision uid.</summary>
internal static class TreeDrive
{
    private sealed class Node
    {
        public string Uid { get; set; } = "";
        public string? Parent { get; set; }
        public string Name { get; set; } = "";
        public string Type { get; set; } = "folder";
        public string? Rev { get; set; }
        public long Size { get; set; }
        public DateTimeOffset MTime { get; set; }
        public string? Sha1 { get; set; }
        public string? Content { get; set; }
        public string? MediaType { get; set; }
        public bool Trashed { get; set; }
    }

    private sealed class Store
    {
        public int Next { get; set; } = 1;
        public List<Node> Nodes { get; set; } = [];
    }

    private sealed class CliError(string message, int code = 1) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static int Run(List<string> a, string treeFile, Action<string[]> rec)
    {
        using var lockFile = AcquireLock(treeFile + ".lock");
        var store = Load(treeFile);
        try
        {
            var code = Dispatch(a, store, rec);
            Save(treeFile, store);
            return code;
        }
        catch (CliError e)
        {
            Save(treeFile, store); // partial changes (e.g. some files uploaded) persist, like the real CLI
            Console.Error.WriteLine(e.Message);
            return e.Code;
        }
    }

    private static int Dispatch(List<string> a, Store s, Action<string[]> rec)
    {
        // Like cli-drive 0.9.0: "--" ends the options; without it, a positional argument starting with
        // '-' is an unknown option.
        var dashes = a.IndexOf("--");
        if (dashes >= 0)
            a.RemoveAt(dashes);
        else if (a.Skip(2).FirstOrDefault(x => x.StartsWith('-') && x is not ("-f" or "-d" or "--file-conflict-strategy"
                     or "--folder-conflict-strategy" or "--json" or "-j")) is { } bad
                 && a.Count >= 2 && a[1] is "create-folder" or "rename")
            throw new CliError($"Unknown option '{bad}'. To specify a positional argument starting with a '-', place it at the end of the command after '--'", 2);
        string Arg(int i) => i < a.Count ? a[i] : throw new CliError("missing argument", 2);
        switch (a.Count >= 2 ? a[1] : "")
        {
            case "list":
            {
                if (Environment.GetEnvironmentVariable("FAKE_LIST_FAIL") is { Length: > 0 } failList && Arg(2).Contains(failList))
                    throw new CliError("fake: listing failed");
                var node = Resolve(s, Arg(2));
                if (node.Type != "folder")
                    throw new CliError($"Not a folder: {node.Name}");
                Console.WriteLine(new JsonArray(Children(s, node).Select(c => Describe(c)).ToArray()).ToJsonString());
                return 0;
            }
            case "info":
                Console.WriteLine(Describe(Resolve(s, Arg(2))).ToJsonString());
                return 0;
            case "create-folder":
            {
                var parent = Resolve(s, Arg(2));
                if (Children(s, parent).Any(c => c.Name == Arg(3)))
                    throw new CliError("A file or folder with that name already exists");
                var folder = NewNode(s, parent, Arg(3), "folder");
                rec(["mkdir", PathOf(s, folder)]);
                Console.WriteLine(Describe(folder).ToJsonString());
                return 0;
            }
            case "upload":
                return Upload(a, s, rec);
            case "download":
                return Download(a, s, rec);
            case "trash":
            {
                foreach (var path in a.Skip(2))
                {
                    var node = Resolve(s, path);
                    node.Trashed = true;
                    rec(["trash", path]);
                    Console.WriteLine($"✅ {node.Name}");
                }
                return 0;
            }
            case "rename":
            {
                var node = Resolve(s, Arg(2));
                var parent = s.Nodes.Single(n => n.Uid == node.Parent);
                if (Children(s, parent).Any(c => c.Name == Arg(3) && c != node))
                    throw new CliError("A file or folder with that name already exists");
                rec(["rename", Arg(2), Arg(3)]);
                node.Name = Arg(3);
                return 0;
            }
            case "move":
            {
                var target = Resolve(s, a[^1]);
                foreach (var path in a.Skip(2).SkipLast(1))
                {
                    var node = Resolve(s, path);
                    if (Children(s, target).Any(c => c.Name == node.Name))
                        throw new CliError("A file or folder with that name already exists");
                    rec(["move", path, a[^1]]);
                    node.Parent = target.Uid;
                }
                return 0;
            }
            default:
                throw new CliError($"Command not found: {string.Join(' ', a)}");
        }
    }

    private static int Upload(List<string> a, Store s, Action<string[]> rec)
    {
        var fileStrategy = Take(a, "-f", "--file-conflict-strategy");
        var folderStrategy = Take(a, "-d", "--folder-conflict-strategy");
        if (fileStrategy is null || folderStrategy is null)
            throw new CliError("fake: upload without -f/-d would prompt for conflict resolution");
        if (double.TryParse(Environment.GetEnvironmentVariable("FAKE_SLOW"), out var slow))
            Thread.Sleep(TimeSpan.FromSeconds(slow));
        var locals = a.Skip(2).SkipLast(1).ToList();
        var parent = Resolve(s, a[^1]);
        foreach (var l in locals)
        {
            if (!File.Exists(l) && !Directory.Exists(l))
            {
                // The real CLI crashes before uploading anything, with the cause on stdout.
                Console.WriteLine("Transfer summary:\n  Uploaded: 0 items (0 B)");
                Console.WriteLine($"ENOENT: no such file or directory, lstat '{l}'");
                throw new CliError("===============================================");
            }
        }

        var failOn = Environment.GetEnvironmentVariable("FAKE_FAIL");
        int uploaded = 0;
        long bytes = 0;
        var skipped = new List<string>();
        var failed = new List<string>();

        void UploadOne(string local, Node into)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(local));
            var existing = Children(s, into).FirstOrDefault(c => c.Name == name);
            if (Directory.Exists(local))
            {
                Node folder;
                if (existing is { Type: "folder" } && folderStrategy == "merge")
                    folder = existing;
                else if (existing is not null && folderStrategy == "skip")
                {
                    skipped.Add(name);
                    return;
                }
                else
                {
                    if (existing is not null)
                        existing.Trashed = true; // replace (rename isn't needed by our tests)
                    folder = NewNode(s, into, name, "folder");
                }
                foreach (var child in Directory.EnumerateFileSystemEntries(local).Order(StringComparer.Ordinal))
                    UploadOne(child, folder);
                return;
            }
            if (!string.IsNullOrEmpty(failOn) && local.Contains(failOn))
            {
                failed.Add($"{name}: Error: boom");
                return;
            }
            byte[] content;
            try
            {
                content = File.ReadAllBytes(local);
            }
            catch (UnauthorizedAccessException)
            {
                failed.Add($"{name}: Error: EACCES: permission denied, open '{local}'");
                return;
            }
            var sha1 = Convert.ToHexStringLower(SHA1.HashData(content));
            var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(local), TimeSpan.Zero);
            if (existing is { Type: "file" } && existing.Sha1 == sha1)
            {
                skipped.Add(name); // "Files with the same content are automatically skipped."
                return;
            }
            Node target;
            if (existing is null)
                target = NewNode(s, into, name, "file");
            else if (fileStrategy == "skip")
            {
                skipped.Add(name);
                return;
            }
            else if (fileStrategy == "replace")
            {
                existing.Trashed = true;
                target = NewNode(s, into, name, "file");
            }
            else if (fileStrategy == "rename")
                target = NewNode(s, into, UniqueName(Children(s, into).Select(c => c.Name).ToHashSet(), name), "file");
            else
                target = existing; // create-new-revision
            target.Rev = $"REV{s.Next++:D4}";
            target.Content = Convert.ToBase64String(content);
            target.Size = content.Length;
            target.Sha1 = sha1;
            target.MTime = mtime;
            target.MediaType = "application/octet-stream";
            uploaded++;
            bytes += content.Length;
        }

        foreach (var l in locals)
            UploadOne(l, parent);
        rec(["upload", .. locals, a[^1]]);
        rec(["strategy", fileStrategy, folderStrategy]);

        Console.WriteLine("Transfer summary:");
        Console.WriteLine($"  Uploaded: {uploaded} items ({bytes} B)");
        if (skipped.Count > 0)
        {
            Console.WriteLine($"  Skipped: {skipped.Count} items");
            foreach (var n in skipped)
                Console.WriteLine($"  - {n}");
        }
        if (failed.Count > 0)
        {
            Console.WriteLine($"  Failed: {failed.Count} items");
            foreach (var f in failed)
                Console.WriteLine($"  - {f}");
            throw new CliError($"{failed.Count} item(s) failed to upload");
        }
        return 0;
    }

    private static int Download(List<string> a, Store s, Action<string[]> rec)
    {
        var fileStrategy = Take(a, "-f", "--file-conflict-strategy");
        var folderStrategy = Take(a, "-d", "--folder-conflict-strategy");
        if (fileStrategy is null || folderStrategy is null)
            throw new CliError("fake: download without -f/-d would prompt for conflict resolution");
        var localFolder = a[^1];
        int downloaded = 0;
        long bytes = 0;

        void DownloadOne(Node node, string into)
        {
            var target = Path.Combine(into, node.Name);
            if (node.Type == "folder")
            {
                Directory.CreateDirectory(target); // merge
                foreach (var child in Children(s, node))
                    DownloadOne(child, target);
                return;
            }
            if (File.Exists(target))
            {
                if (fileStrategy == "skip")
                    return;
                if (fileStrategy == "rename")
                    target = Path.Combine(into, UniqueName(Directory.EnumerateFileSystemEntries(into).Select(Path.GetFileName).OfType<string>().ToHashSet(), node.Name));
            }
            var content = Convert.FromBase64String(node.Content ?? "");
            File.WriteAllBytes(target, content); // like the real CLI (as far as we know), mtime is "now"
            downloaded++;
            bytes += content.Length;
        }

        foreach (var path in a.Skip(2).SkipLast(1))
        {
            DownloadOne(Resolve(s, path), localFolder);
            rec(["download", fileStrategy, folderStrategy, path, localFolder]);
        }
        Console.WriteLine("Transfer summary:");
        Console.WriteLine($"  Downloaded: {downloaded} items ({bytes} B)");
        return 0;
    }

    // ---- tree helpers ------------------------------------------------------------------------

    private static IEnumerable<Node> Children(Store s, Node folder) =>
        s.Nodes.Where(n => n.Parent == folder.Uid && !n.Trashed).OrderBy(n => n.Name, StringComparer.Ordinal);

    private static Node NewNode(Store s, Node parent, string name, string type)
    {
        var node = new Node { Uid = $"VOL~N{s.Next++:D4}", Parent = parent.Uid, Name = name, Type = type, MTime = DateTimeOffset.UtcNow };
        s.Nodes.Add(node);
        return node;
    }

    private static Node Resolve(Store s, string path)
    {
        var root = s.Nodes.Single(n => n.Uid == "ROOT");
        if (path == "/my-files" || path == "/my-files/")
            return root;
        if (!path.StartsWith("/my-files/", StringComparison.Ordinal))
            throw new CliError($"Node not found: {path}");
        var node = root;
        foreach (var segment in SplitEscaped(path["/my-files/".Length..]))
        {
            node = Children(s, node).FirstOrDefault(c => c.Name == segment || c.Uid == segment)
                   ?? throw new CliError($"Node not found: {segment}");
        }
        return node;
    }

    private static IEnumerable<string> SplitEscaped(string rel)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < rel.Length; i++)
        {
            if (rel[i] == '\\' && i + 1 < rel.Length && rel[i + 1] == '/')
            {
                sb.Append('/');
                i++;
            }
            else if (rel[i] == '/')
            {
                if (sb.Length > 0)
                    yield return sb.ToString();
                sb.Clear();
            }
            else
            {
                sb.Append(rel[i]);
            }
        }
        if (sb.Length > 0)
            yield return sb.ToString();
    }

    private static string PathOf(Store s, Node node)
    {
        var parts = new List<string>();
        for (var n = node; n.Uid != "ROOT"; n = s.Nodes.Single(p => p.Uid == n.Parent))
            parts.Add(n.Name.Replace("/", "\\/", StringComparison.Ordinal));
        parts.Reverse();
        return "/my-files" + (parts.Count > 0 ? "/" + string.Join('/', parts) : "");
    }

    private static string UniqueName(HashSet<string> taken, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    private static JsonObject Describe(Node n)
    {
        var o = new JsonObject
        {
            ["uid"] = n.Uid,
            ["parentUid"] = n.Parent,
            ["name"] = new JsonObject { ["ok"] = true, ["value"] = n.Name },
            ["type"] = n.Type,
            ["isShared"] = false,
            ["creationTime"] = n.MTime.ToString("O"),
            ["modificationTime"] = n.MTime.ToString("O"),
        };
        if (n.Type == "file")
        {
            o["mediaType"] = n.MediaType ?? "application/octet-stream";
            o["totalStorageSize"] = n.Size + 64;
            o["activeRevision"] = new JsonObject
            {
                ["uid"] = n.Rev,
                ["state"] = "active",
                ["storageSize"] = n.Size + 64,
                ["claimedSize"] = n.Size,
                ["claimedModificationTime"] = n.MTime.ToString("O"),
                ["claimedDigests"] = new JsonObject { ["sha1"] = n.Sha1, ["sha1Verified"] = false },
            };
        }
        else
        {
            o["folder"] = new JsonObject { ["claimedModificationTime"] = n.MTime.ToString("O"), ["isImported"] = false };
        }
        return o;
    }

    private static string? Take(List<string> a, string shortName, string longName)
    {
        var i = a.FindIndex(x => x == shortName || x == longName);
        if (i < 0 || i + 1 >= a.Count)
            return null;
        var value = a[i + 1];
        a.RemoveRange(i, 2);
        return value;
    }

    private static Store Load(string file)
    {
        if (File.Exists(file) && new FileInfo(file).Length > 0)
            return JsonSerializer.Deserialize<Store>(File.ReadAllText(file), Json)!;
        var store = new Store();
        store.Nodes.Add(new Node { Uid = "ROOT", Name = "my-files", Type = "folder", MTime = DateTimeOffset.UtcNow });
        return store;
    }

    private static void Save(string file, Store s)
    {
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
        File.Move(tmp, file, overwrite: true);
    }

    private static FileStream AcquireLock(string path)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (i < 600)
            {
                Thread.Sleep(10);
            }
        }
    }
}
