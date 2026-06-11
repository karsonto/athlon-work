using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class McpSearchIndexTests
{
    [Fact]
    public void Search_finds_relevant_tool_for_query()
    {
        var catalog = new[]
        {
            new McpCatalogEntry("linear", "search_issues", "mcp_linear__search_issues", "Search Linear issues", "{\"query\":\"string\"}"),
            new McpCatalogEntry("github", "create_issue", "mcp_github__create_issue", "Create GitHub issue", "{}")
        };

        var results = McpSearchIndex.Search(catalog, "search linear issue", topK: 5, minScore: 0.1);

        Assert.NotEmpty(results);
        Assert.Contains("linear", results[0].Entry.ServerName, StringComparison.OrdinalIgnoreCase);
    }
}
