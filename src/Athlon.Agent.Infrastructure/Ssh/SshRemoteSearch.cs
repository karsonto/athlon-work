using System.Text;
using Athlon.Agent.Core;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Athlon.Agent.Infrastructure.Ssh;

/// <summary>Remote grep/glob via shell when available; callers should fall back to SFTP.</summary>
internal static class SshRemoteSearch
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(60);

    public static async Task<IReadOnlyList<string>?> TryGrepAsync(
        ISshWorkspaceClient client,
        string rootPath,
        string pattern,
        string glob,
        bool useRegex,
        int maxMatches,
        IReadOnlyList<string> ignorePatterns,
        CancellationToken cancellationToken)
    {
        if (await client.HasCommandAsync("rg", cancellationToken).ConfigureAwait(false))
        {
            return await GrepWithRipgrepAsync(
                    client,
                    rootPath,
                    pattern,
                    glob,
                    useRegex,
                    maxMatches,
                    ignorePatterns,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (await client.HasCommandAsync("grep", cancellationToken).ConfigureAwait(false))
        {
            return await GrepWithGnuGrepAsync(
                    client,
                    rootPath,
                    pattern,
                    glob,
                    useRegex,
                    maxMatches,
                    ignorePatterns,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return null;
    }

    public static async Task<IReadOnlyList<string>?> TryGlobAsync(
        ISshWorkspaceClient client,
        string rootPath,
        string pattern,
        int maxFiles,
        IReadOnlyList<string> ignorePatterns,
        CancellationToken cancellationToken)
    {
        if (await client.HasCommandAsync("rg", cancellationToken).ConfigureAwait(false))
        {
            var command = new StringBuilder();
            command.Append("rg --files --hidden --glob '!.git/**'");
            foreach (var ignore in ignorePatterns.Take(20))
            {
                command.Append(" --glob ").Append(SshWorkspaceClient.ShellQuote("!" + ignore));
                command.Append(" --glob ").Append(SshWorkspaceClient.ShellQuote("!**/" + ignore + "/**"));
            }

            command.Append(" -g ").Append(SshWorkspaceClient.ShellQuote(pattern.Replace('\\', '/')));
            command.Append(" .");

            var result = await client.ExecuteAsync(command.ToString(), rootPath, SearchTimeout, cancellationToken)
                .ConfigureAwait(false);
            // rg --files exits 0 even when empty; 2 = error
            if (result.ExitCode is 0 or 1)
            {
                return ParsePathList(result.StdOut, maxFiles, ignorePatterns, includeDirectories: false);
            }
        }

        if (await client.HasCommandAsync("find", cancellationToken).ConfigureAwait(false))
        {
            var result = await client.ExecuteAsync(
                    "find . -type f -print",
                    rootPath,
                    SearchTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
                matcher.AddInclude(pattern.Replace('\\', '/'));
                var matches = new List<string>();
                foreach (var relative in ParsePathList(result.StdOut, maxFiles * 4, ignorePatterns, includeDirectories: false))
                {
                    var normalized = relative.Replace('\\', '/').TrimStart('.', '/');
                    if (!matcher.Match(normalized).HasMatches)
                    {
                        continue;
                    }

                    matches.Add(normalized);
                    if (matches.Count >= maxFiles)
                    {
                        break;
                    }
                }

                return matches;
            }
        }

        return null;
    }

    private static async Task<IReadOnlyList<string>?> GrepWithRipgrepAsync(
        ISshWorkspaceClient client,
        string rootPath,
        string pattern,
        string glob,
        bool useRegex,
        int maxMatches,
        IReadOnlyList<string> ignorePatterns,
        CancellationToken cancellationToken)
    {
        var command = new StringBuilder();
        command.Append("rg -n --no-heading --hidden --glob '!.git/**' --max-filesize 2M");
        command.Append(" --max-count ").Append(Math.Max(1, maxMatches));
        if (!useRegex)
        {
            command.Append(" -F");
        }

        command.Append(" -i");
        if (!string.IsNullOrWhiteSpace(glob) && glob != "*")
        {
            command.Append(" -g ").Append(SshWorkspaceClient.ShellQuote(glob.Replace('\\', '/')));
        }

        foreach (var ignore in ignorePatterns.Take(20))
        {
            command.Append(" --glob ").Append(SshWorkspaceClient.ShellQuote("!" + ignore));
            command.Append(" --glob ").Append(SshWorkspaceClient.ShellQuote("!**/" + ignore + "/**"));
        }

        command.Append(' ').Append(SshWorkspaceClient.ShellQuote(pattern));
        command.Append(" .");

        var result = await client.ExecuteAsync(command.ToString(), rootPath, SearchTimeout, cancellationToken)
            .ConfigureAwait(false);
        // rg: 0 = matches, 1 = no matches, 2 = error
        if (result.ExitCode is not (0 or 1))
        {
            return null;
        }

        return ParseGrepLines(result.StdOut, maxMatches, ignorePatterns);
    }

    private static async Task<IReadOnlyList<string>?> GrepWithGnuGrepAsync(
        ISshWorkspaceClient client,
        string rootPath,
        string pattern,
        string glob,
        bool useRegex,
        int maxMatches,
        IReadOnlyList<string> ignorePatterns,
        CancellationToken cancellationToken)
    {
        var command = new StringBuilder();
        command.Append("grep -RIn");
        if (!useRegex)
        {
            command.Append("F");
        }

        command.Append(" --exclude-dir=.git");
        foreach (var ignore in ignorePatterns.Take(20))
        {
            command.Append(" --exclude-dir=").Append(SshWorkspaceClient.ShellQuote(ignore));
            command.Append(" --exclude=").Append(SshWorkspaceClient.ShellQuote(ignore));
        }

        if (!string.IsNullOrWhiteSpace(glob) && glob != "*")
        {
            command.Append(" --include=").Append(SshWorkspaceClient.ShellQuote(glob.Replace('\\', '/')));
        }

        command.Append(" -m ").Append(Math.Max(1, maxMatches));
        command.Append(' ').Append(SshWorkspaceClient.ShellQuote(pattern));
        command.Append(" .");

        var result = await client.ExecuteAsync(command.ToString(), rootPath, SearchTimeout, cancellationToken)
            .ConfigureAwait(false);
        // grep: 0 = matches, 1 = no matches, >=2 = error
        if (result.ExitCode >= 2)
        {
            return null;
        }

        return ParseGrepLines(result.StdOut, maxMatches, ignorePatterns);
    }

    private static List<string> ParseGrepLines(string stdout, int maxMatches, IReadOnlyList<string> ignorePatterns)
    {
        var matches = new List<string>();
        using var reader = new StringReader(stdout ?? string.Empty);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var normalized = NormalizeRelative(line);
            var pathPart = normalized;
            var firstColon = normalized.IndexOf(':');
            if (firstColon > 0)
            {
                pathPart = normalized[..firstColon];
            }

            if (SshWorkspaceToolHelper.ShouldIgnore(pathPart, ignorePatterns))
            {
                continue;
            }

            matches.Add(normalized);
            if (matches.Count >= maxMatches)
            {
                break;
            }
        }

        return matches;
    }

    private static List<string> ParsePathList(
        string stdout,
        int maxFiles,
        IReadOnlyList<string> ignorePatterns,
        bool includeDirectories)
    {
        var matches = new List<string>();
        using var reader = new StringReader(stdout ?? string.Empty);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var relative = NormalizeRelative(line);
            if (!includeDirectories && relative.EndsWith('/'))
            {
                continue;
            }

            if (SshWorkspaceToolHelper.ShouldIgnore(relative, ignorePatterns))
            {
                continue;
            }

            matches.Add(relative);
            if (matches.Count >= maxFiles)
            {
                break;
            }
        }

        return matches;
    }

    private static string NormalizeRelative(string raw)
    {
        var value = raw.Replace('\\', '/').Trim();
        if (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value.TrimStart('/');
    }
}
