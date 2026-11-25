namespace Athlon.Agent.Core;

/// <summary>POSIX shell quoting for remote interactive commands.</summary>
public static class PosixShell
{
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// Changes to <paramref name="remotePath"/> in the current shell.
    /// A failed <c>cd</c> prints one line and leaves the login directory in place.
    /// </summary>
    public static string ChangeDirectory(string remotePath) =>
        "cd " + Quote(remotePath) + " || echo 'athlon: failed to enter workspace directory'";
}
