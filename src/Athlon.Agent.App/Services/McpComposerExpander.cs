using System.Text;
using System.Text.RegularExpressions;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Expands //mcp:encodedName references in user composer text before sending to the agent.
/// </summary>
public static partial class McpComposerExpander
{
    [GeneratedRegex(@"//mcp:([^\s]+)", RegexOptions.None)]
    private static partial Regex McpReferencePattern();

    public static string Expand(string userInput, IMcpRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(userInput))
        {
            return userInput;
        }

        var knownToolIds = registry.ListCatalogEntries()
            .Select(entry => entry.EncodedName)
            .ToHashSet(StringComparer.Ordinal);

        var matches = McpReferencePattern().Matches(userInput);
        if (matches.Count == 0)
        {
            return userInput;
        }

        var blocks = new List<string>();
        var warnings = new List<string>();

        foreach (Match match in matches)
        {
            var toolId = match.Groups[1].Value;
            if (knownToolIds.Contains(toolId))
            {
                blocks.Add(
                    $"[MCP reference: {toolId}]{Environment.NewLine}"
                    + $"Use mcp_call(toolId=\"{toolId}\", arguments={{}}) "
                    + "to invoke this MCP tool when needed.");
            }
            else
            {
                warnings.Add($"Unknown MCP tool '{toolId}' in //mcp reference; connect the MCP server first.");
            }
        }

        var builder = new StringBuilder();
        if (blocks.Count > 0)
        {
            builder.AppendLine(string.Join(Environment.NewLine + Environment.NewLine, blocks.Distinct(StringComparer.Ordinal)));
            builder.AppendLine();
        }

        builder.Append(userInput);

        if (warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine(string.Join(Environment.NewLine, warnings.Distinct(StringComparer.Ordinal)));
        }

        return builder.ToString();
    }
}
