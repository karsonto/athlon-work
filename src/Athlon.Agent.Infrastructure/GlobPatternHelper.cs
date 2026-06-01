using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Athlon.Agent.Infrastructure;

internal static class GlobPatternHelper
{
    /// <summary>
    /// Expands brace alternates such as <c>**/*.{png,jpg}</c> into multiple glob patterns.
    /// </summary>
    public static IEnumerable<string> ExpandBraces(string pattern)
    {
        var start = pattern.IndexOf('{');
        if (start < 0)
        {
            yield return pattern;
            yield break;
        }

        var end = pattern.IndexOf('}', start + 1);
        if (end < 0)
        {
            yield return pattern;
            yield break;
        }

        var prefix = pattern[..start];
        var suffix = pattern[(end + 1)..];
        var alternatives = pattern[(start + 1)..end]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (alternatives.Length == 0)
        {
            yield return pattern;
            yield break;
        }

        foreach (var alternative in alternatives)
        {
            foreach (var expanded in ExpandBraces(prefix + alternative + suffix))
            {
                yield return expanded;
            }
        }
    }

    public static IEnumerable<string> EnumerateMatches(
        string rootDirectory,
        string pattern,
        IReadOnlyList<string> ignorePatterns,
        int maxResults = 200)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        foreach (var expanded in ExpandBraces(pattern))
        {
            matcher.AddInclude(NormalizePattern(expanded));
        }

        var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(rootDirectory)));
        var count = 0;

        foreach (var file in result.Files)
        {
            var fullPath = Path.GetFullPath(Path.Combine(rootDirectory, file.Path));
            if (WorkspacePathFilter.ShouldIgnorePath(fullPath, ignorePatterns))
            {
                continue;
            }

            yield return fullPath;
            if (++count >= maxResults)
            {
                yield break;
            }
        }
    }

    private static string NormalizePattern(string pattern) =>
        pattern.Replace('\\', '/');
}
