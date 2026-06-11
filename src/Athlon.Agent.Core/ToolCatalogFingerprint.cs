using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Athlon.Agent.Core;

public static class ToolCatalogFingerprint
{
    public static string Compute(IReadOnlyList<ToolDefinition> tools)
    {
        var canonical = tools
            .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .Select(tool => new
            {
                tool.Name,
                tool.Description,
                tool.Source,
                Parameters = tool.Parameters
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            })
            .ToArray();

        var json = JsonSerializer.Serialize(canonical);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public static bool IsBreakingChange(string? previousFingerprint, string currentFingerprint) =>
        !string.IsNullOrWhiteSpace(previousFingerprint)
        && !string.Equals(previousFingerprint, currentFingerprint, StringComparison.Ordinal);
}
