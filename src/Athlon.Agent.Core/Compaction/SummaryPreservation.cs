using System.Text;

namespace Athlon.Agent.Core.Compaction;

/// <summary>
/// Appends must-preserve facts the summary model dropped. No second model call.
/// </summary>
public static class SummaryPreservation
{
    public const int FactProbeLength = 40;

    public static string EnsureFacts(string summary, string? mustPreserve)
    {
        var missing = CollectMissing(summary, mustPreserve);
        if (missing.Count == 0)
        {
            return summary;
        }

        var builder = new StringBuilder(summary.TrimEnd());
        if (builder.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine();
        }

        builder.AppendLine("Missing from summary:");
        foreach (var fact in missing)
        {
            builder.AppendLine(fact);
        }

        return builder.ToString().TrimEnd();
    }

    public static IReadOnlyList<string> CollectMissing(string summary, string? mustPreserve)
    {
        if (string.IsNullOrWhiteSpace(mustPreserve))
        {
            return [];
        }

        var missing = new List<string>();
        foreach (var raw in mustPreserve.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("- ", StringComparison.Ordinal))
            {
                continue;
            }

            var probeLength = Math.Min(FactProbeLength, line.Length);
            var probe = line[..probeLength];
            if (summary.Contains(probe, StringComparison.Ordinal))
            {
                continue;
            }

            missing.Add(line);
        }

        return missing;
    }
}
