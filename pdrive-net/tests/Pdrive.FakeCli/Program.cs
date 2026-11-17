// Stand-in for the proton-drive CLI in tests (C# port of proton-drive-gui/fake-proton-drive).
// Env switches:
//   FAKE_LOG=path        append each upload/mkdir call as a JSON array line
//   FAKE_FAIL=text       uploads fail when any argument contains text
//   FAKE_SIGNED_OUT=1    every filesystem command fails with a sign-in error
//   FAKE_OFFLINE=1       every filesystem command fails with a network error
//   FAKE_SLOW=seconds    uploads sleep this long first (for cancellation/timeout tests)
using System.Text.Json;

var a = args;
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

bool Cmd(string a0, string a1) => a.Length >= 2 && a[0] == a0 && a[1] == a1;

if (a.Length >= 1 && a[0] == "filesystem")
{
    if (Environment.GetEnvironmentVariable("FAKE_SIGNED_OUT") == "1")
        return Fail("not logged in: run auth login");
    if (Environment.GetEnvironmentVariable("FAKE_OFFLINE") == "1")
        return Fail("network error: connection refused");
}

if (Cmd("filesystem", "list"))
{
    switch (a[2])
    {
        case "/my-files":
            Console.WriteLine("""[{"name": "b.txt", "type": "file", "size": 2048}, {"name": "Documents", "type": "folder"}]""");
            return 0;
        case "/nope":
            return Fail("not logged in: run auth login");
        case "/offline":
            return Fail("network error: connection refused");
        default:
            Console.WriteLine("""{"items": []}""");
            return 0;
    }
}

if (Cmd("filesystem", "upload"))
{
    var rest = a[2..].ToList();
    var i = rest.IndexOf("--conflict-strategy");
    string? strategy = null;
    if (i >= 0)
    {
        strategy = rest[i + 1];
        rest.RemoveRange(i, 2);
    }
    if (double.TryParse(Environment.GetEnvironmentVariable("FAKE_SLOW"), out var slow))
        Thread.Sleep(TimeSpan.FromSeconds(slow));
    var failOn = Environment.GetEnvironmentVariable("FAKE_FAIL");
    if (!string.IsNullOrEmpty(failOn) && rest.Any(x => x.Contains(failOn)))
        return Fail("boom");
    Rec(["upload", .. rest]);
    if (strategy is not null)
        Rec("conflict-strategy", strategy);
    Console.WriteLine($"uploaded {string.Join(' ', rest)}");
    return 0;
}

if (Cmd("filesystem", "mkdir"))
{
    Rec("mkdir", a[2]);
    Console.WriteLine("ok");
    return 0;
}

if (Cmd("filesystem", "download"))
{
    Console.WriteLine($"downloaded {string.Join(' ', a[2..])}");
    return 0;
}

if (Cmd("auth", "login"))
{
    Console.WriteLine("ok");
    return 0;
}

return Fail("unknown", 2);
