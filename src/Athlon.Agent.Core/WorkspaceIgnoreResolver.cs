namespace Athlon.Agent.Core;

public static class WorkspaceIgnoreResolver
{
    /// <summary>
    /// Resolves effective ignore directory names.
    /// Priority: session scope &gt; workspace-specific &gt; global app config &gt; built-in defaults.
    /// </summary>
    public static IReadOnlyList<string> Resolve(
        IReadOnlyList<string>? sessionPatterns = null,
        IReadOnlyList<string>? workspacePatterns = null,
        IReadOnlyList<string>? globalPatterns = null)
    {
        if (sessionPatterns is { Count: > 0 })
        {
            return Deduplicate(sessionPatterns);
        }

        if (workspacePatterns is { Count: > 0 })
        {
            return Deduplicate(workspacePatterns);
        }

        if (globalPatterns is { Count: > 0 })
        {
            return Deduplicate(globalPatterns);
        }

        return WorkspaceIgnoreDefaults.BuiltIn;
    }

    private static string[] Deduplicate(IReadOnlyList<string> patterns)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(patterns.Count);
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            var trimmed = pattern.Trim();
            if (seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result.ToArray();
    }
}
