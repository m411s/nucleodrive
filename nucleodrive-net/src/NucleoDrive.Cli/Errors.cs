namespace NucleoDrive.Cli;

/// <summary>The proton-drive CLI failed, timed out, or couldn't be started.</summary>
public class DriveException : Exception
{
    public DriveException(string message) : base(message) { }
    public DriveException(string message, Exception inner) : base(message, inner) { }

    /// <summary>First line of the message, for status lines and toasts.</summary>
    public string FirstLine => Message.Split('\n', 2)[0].Trim();
}

/// <summary>The CLI says we're not signed in, or the session expired.</summary>
public sealed class AuthException(string message) : DriveException(message);

/// <summary>Classifies CLI error text. The CLI's wording isn't documented, so this is a
/// heuristic kept in one place (see docs/cli-notes.md once checked against the real CLI).</summary>
public static class AuthHeuristics
{
    private static readonly string[] Hints =
    [
        "not logged in", "not signed in", "log in", "login", "sign in", "unauthorized",
        "unauthenticated", "authentication", "session expired", "session has expired",
        "no session", "invalid session",
    ];

    public static bool LooksLikeAuthError(string? message) =>
        !string.IsNullOrEmpty(message) && Hints.Any(h => message.Contains(h, StringComparison.OrdinalIgnoreCase));
}
