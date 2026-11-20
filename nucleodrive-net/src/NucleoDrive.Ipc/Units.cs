using System.Globalization;

namespace NucleoDrive.Ipc;

public static class Units
{
    public static string Bytes(long? n)
    {
        if (n is not { } v)
            return "";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double d = v;
        var i = 0;
        while (d >= 1024 && i < units.Length - 1)
        {
            d /= 1024;
            i++;
        }
        return i == 0 ? string.Create(CultureInfo.InvariantCulture, $"{d:0} B") : string.Create(CultureInfo.InvariantCulture, $"{d:0.0} {units[i]}");
    }

    public static string Ago(DateTimeOffset? t, DateTimeOffset? now = null)
    {
        if (t is not { } ts)
            return "never";
        var secs = (int)((now ?? DateTimeOffset.UtcNow) - ts).TotalSeconds;
        return secs switch
        {
            < 90 => "just now",
            < 5400 => $"{secs / 60} min ago",
            < 172800 => $"{secs / 3600} h ago",
            _ => $"{secs / 86400} days ago",
        };
    }
}
