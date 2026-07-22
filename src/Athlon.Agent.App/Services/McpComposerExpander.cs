using System.Text;
using System.Text.RegularExpressions;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Mcp;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Expands //mcp:serverOrTool references in user composer text before sending to the agent.
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

        var catalog = registry.ListCatalogEntries();
        var knownToolIds = catalog
            .Select(entry => entry.EncodedName)
            .ToHashSet(StringComparer.Ordinal);
        var knownServerNames = catalog
            .Select(entry => entry.ServerName)
            .Concat(registry.GetStatuses()
                .Where(status => status.State == McpConnectionState.Connected)
                .Select(status => status.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matches = McpReferencePattern().Matches(userInput);
        if (matches.Count == 0)
        {
            return userInput;
        }

        var blocks = new List<string>();
        var warnings = new List<string>();

        foreach (Match match in matches)
        {
            var reference = match.Groups[1].Value;
            if (knownToolIds.Contains(reference))
            {
                blocks.Add(
                    $"[MCP reference: {reference}]{Environment.NewLine}"
                    + $"Use mcp_call(toolId=\"{reference}\", arguments={{}}) "
                    + "to invoke this MCP tool when needed.");
                continue;
            }

            if (knownServerNames.Contains(reference))
            {
                blocks.Add(
                    $"[MCP server reference: {reference}]{Environment.NewLine}"
                    + $"Prefer MCP tools from server \"{reference}\" when needed. "
                    + "Use the advertised function schemas / mcp_call for tools on that server.");
                continue;
            }

            warnings.Add($"Unknown MCP reference '{reference}'; connect the MCP server first.");
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
