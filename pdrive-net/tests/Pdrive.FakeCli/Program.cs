// Stand-in for the proton-drive CLI in tests. Mirrors the interface and messages of the real
// cli-drive 0.9.0 (see docs/cli-notes.md); only the behaviour tests need is simulated.
// Env switches:
//   FAKE_LOG=path        append each upload/create-folder/download call as a JSON array line
//   FAKE_FAIL=text       uploads fail when any argument contains text
//   FAKE_SIGNED_OUT=1    every filesystem command fails like the real CLI when signed out
//   FAKE_OFFLINE=1       every filesystem command fails with a network error
//   FAKE_SLOW=seconds    uploads sleep this long first (for cancellation/timeout tests)
//   FAKE_TREE=file.json  a persistent fake Drive (TreeDrive.cs) instead of the fixed listings below
using System.Text.Json;

var a = args.ToList();
var log = Environment.GetEnvironmentVariable("FAKE_LOG");

void Rec(params string[] x)
{
    if (!string.IsNullOrEmpty(log))
        File.AppendAllText(log, JsonSerializer.Serialize(x) + "\n");
}

int Fail(string msg, int code = 1)
{
    Console.Error.WriteLine(msg);
    return code;
}

bool Cmd(string a0, string a1) => a.Count >= 2 && a[0] == a0 && a[1] == a1;

// Pulls "-x VALUE" / "--long VALUE" out of the argument list.
string? TakeOption(string shortName, string longName)
{
    var i = a.FindIndex(x => x == shortName || x == longName);
    if (i < 0 || i + 1 >= a.Count)
        return null;
    var value = a[i + 1];
    a.RemoveRange(i, 2);
    return value;
}

a.Remove("--json");
a.Remove("-j");

if (a.Count >= 1 && a[0] == "filesystem")
{
    if (Environment.GetEnvironmentVariable("FAKE_SIGNED_OUT") == "1")
        return Fail("You need to login first");
    if (Environment.GetEnvironmentVariable("FAKE_OFFLINE") == "1")
        return Fail("network error: connection refused");
    // Persistent fake Drive (used by the two-way sync tests); see TreeDrive.cs.
    if (Environment.GetEnvironmentVariable("FAKE_TREE") is { Length: > 0 } tree)
        return TreeDrive.Run(a, tree, x => Rec(x));
}

if (Cmd("filesystem", "list"))
{
    switch (a[2])
    {
        case "/my-files":
            // Shape of cli-drive 0.9.0 (trimmed; see tests/Pdrive.Cli.Tests/Fixtures/list-folder.json).
            Console.WriteLine("""
                [
                  {"uid": "V~F1", "parentUid": "V~R", "name": {"ok": true, "value": "b.txt"}, "type": "file",
                   "mediaType": "text/plain", "totalStorageSize": 9999,
                   "activeRevision": {"uid": "V~R1", "state": "active", "storageSize": 2100, "claimedSize": 2048}},
                  {"uid": "V~D1", "parentUid": "V~R", "name": {"ok": true, "value": "Documents"}, "type": "folder",
                   "folder": {"isImported": false}}
                ]
                """);
            return 0;
        case "/nope":
            return Fail("You need to login first");
        case "/offline":
            return Fail("network error: connection refused");
        default:
            Console.WriteLine("""{"items": []}""");
            return 0;
    }
}

if (Cmd("filesystem", "upload"))
{
    var fileStrategy = TakeOption("-f", "--file-conflict-strategy");
    var folderStrategy = TakeOption("-d", "--folder-conflict-strategy");
    var rest = a[2..];
    // The real CLI prompts on conflicts without a strategy; a non-interactive caller would stall.
    if (fileStrategy is null || folderStrategy is null)
        return Fail("fake: upload without -f/-d would prompt for conflict resolution");
    if (double.TryParse(Environment.GetEnvironmentVariable("FAKE_SLOW"), out var slow))
        Thread.Sleep(TimeSpan.FromSeconds(slow));
    // Like the real CLI: the good files in a batch are uploaded, failures are listed in the summary
    // on stdout, stderr says how many failed, and the exit code is 1.
    var failOn = Environment.GetEnvironmentVariable("FAKE_FAIL");
    var dest = rest[^1];
    var files = rest[..^1];
    var failed = string.IsNullOrEmpty(failOn) ? [] : files.Where(f => f.Contains(failOn)).ToList();
    var ok = files.Except(failed).ToList();
    if (ok.Count > 0)
    {
        Rec(["upload", .. ok, dest]);
        Rec("strategy", fileStrategy, folderStrategy);
    }
    Console.WriteLine("Transfer summary:");
    Console.WriteLine($"  Uploaded: {ok.Count} items ({ok.Count} B)");
    if (failed.Count == 0)
        return 0;
    Console.WriteLine($"  Failed: {failed.Count} items");
    foreach (var f in failed)
        Console.WriteLine($"  - {Path.GetFileName(f)}: Error: boom");
    return Fail($"{failed.Count} item(s) failed to upload");
}

if (Cmd("filesystem", "create-folder"))
{
    if (a.Count != 4)
        return Fail("Usage: filesystem create-folder parentPath name", 2);
    var parent = a[2];
    Rec("mkdir", parent.EndsWith('/') ? parent + a[3] : parent + "/" + a[3]);
    Console.WriteLine("ok");
    return 0;
}

if (Cmd("filesystem", "download"))
{
    var fileStrategy = TakeOption("-f", "--file-conflict-strategy");
    var folderStrategy = TakeOption("-d", "--folder-conflict-strategy");
    if (fileStrategy is null || folderStrategy is null)
        return Fail("fake: download without -f/-d would prompt for conflict resolution");
    Rec(["download", fileStrategy, folderStrategy, .. a[2..]]);
    Console.WriteLine($"downloaded {string.Join(' ', a[2..])}");
    return 0;
}

if (Cmd("auth", "login"))
{
    Console.WriteLine("ok");
    return 0;
}

return Fail($"Command not found: {string.Join(' ', args)}", 1);
