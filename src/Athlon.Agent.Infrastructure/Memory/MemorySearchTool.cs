using System.Text.RegularExpressions;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Infrastructure.Memory;

public sealed class MemorySearchTool(ILongTermMemory longTermMemory, IAppLogger logger) : IAgentTool
{
    private readonly IAppLogger _logger = logger.ForContext("MemorySearchTool");

    public ToolDefinition Definition => new(
        Name: "memory_search",
        Description: "Search through long-term memory files (MEMORY.md and memory/*.md) for relevant information. Use before answering questions about prior work, decisions, dates, people, preferences, or todos.",
        Parameters: new Dictionary<string, string>
        {
            ["query"] = "Keywords to search for in memory files"
        });

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!invocation.Arguments.TryGetValue("query", out var query) || string.IsNullOrWhiteSpace(query))
            return ToolResult.Failure("No query provided", "query parameter is required");

        try
        {
            var memoryPaths = await longTermMemory.ListAllMemoryFilePathsAsync(cancellationToken);
            var pattern = new Regex(Regex.Escape(query), RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var results = new List<string>();
            var matchCount = 0;

            foreach (var relativePath in memoryPaths)
            {
                string fileContent;
                if (relativePath.EndsWith("MEMORY.md", StringComparison.OrdinalIgnoreCase))
                {
                    fileContent = await longTermMemory.ReadCuratedAsync(cancellationToken);
                }
                else
                {
                    var fileName = relativePath.Split('/')[^1];
                    fileContent = await longTermMemory.ReadDailyFileAsync(fileName, cancellationToken);
                }

                if (string.IsNullOrEmpty(fileContent))
                    continue;

                var lines = fileContent.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (pattern.IsMatch(lines[i]))
                    {
                        results.Add($"Source: {relativePath}#{i + 1}: {lines[i].Trim()}");
                        matchCount++;
                    }
                }
            }

            if (matchCount == 0)
                return ToolResult.Success("Search completed", $"No matching memories found for: {query}");

            var summary = matchCount <= 30
                ? $"Found {matchCount} matches"
                : $"Found {matchCount} matches (showing first 30)";

            var content = string.Join("\n", results.Take(30));
            return ToolResult.Success(summary, content);
        }
        catch (Exception ex)
        {
            _logger.Warning("Memory search failed: {Error}", ex.Message);
            return ToolResult.Failure("Search failed", ex.Message);
        }
    }
}
