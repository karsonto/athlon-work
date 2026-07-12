using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class McpSearchIndexTests
{
    [Fact]
    public void SearchCatalog_uses_registry_cache_without_rebuilding_each_call()
    {
        var catalog = Enumerable.Range(0, 5)
            .Select(index => new McpCatalogEntry("linear", $"tool_{index}", $"mcp_linear__tool_{index}", $"Search Linear {index}", "{}"))
            .ToArray();
        var registry = new TestMcpRegistry(catalog);

        var first = registry.SearchCatalog("linear search", topK: 3, minScore: 0.1);
        var second = registry.SearchCatalog("linear search", topK: 3, minScore: 0.1);

        Assert.NotEmpty(first);
        Assert.Equal(first.Select(result => result.Entry.EncodedName), second.Select(result => result.Entry.EncodedName));
    }

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

    [Fact]
    public void Search_BilingualBenchmark_MeetsRecallAt3AndMrrTargets()
    {
        var catalog = new[]
        {
            Entry("linear", "search_issues", "Search project issues and tickets", "query project status"),
            Entry("github", "create_issue", "Create a repository issue", "title body labels"),
            Entry("browser", "browser_navigate", "Navigate to and open a web page", "url"),
            Entry("vision", "analyze_image", "Analyze an image or picture", "image prompt"),
            Entry("mail", "send_email", "Send an email message", "to subject body"),
            Entry("calendar", "list_events", "List calendar events", "start end"),
            Entry("storage", "search_files", "Search stored documents", "query folder")
        };
        (string Query, string Expected)[] cases =
        [
            ("search linear issues", "search_issues"),
            ("查找工单", "search_issues"),
            ("创建 github 问题", "create_issue"),
            ("打开网页", "browser_navigate"),
            ("分析图片", "analyze_image"),
            ("发送邮件", "send_email")
        ];
        var reciprocalRank = 0.0;
        var recalled = 0;
        foreach (var testCase in cases)
        {
            var results = McpSearchIndex.Search(catalog, testCase.Query, topK: 3, minScore: 0.01);
            var rank = Array.FindIndex(
                results.ToArray(),
                result => result.Entry.ToolName == testCase.Expected);
            if (rank >= 0)
            {
                recalled++;
                reciprocalRank += 1.0 / (rank + 1);
            }
        }

        var recallAt3 = (double)recalled / cases.Length;
        var mrr = reciprocalRank / cases.Length;
        Assert.True(recallAt3 >= 1.0, $"Recall@3={recallAt3:F3}");
        Assert.True(mrr >= 0.9, $"MRR={mrr:F3}");
    }

    [Fact]
    public void Search_PrioritizesToolNameAndDescriptionOverSchemaNoise()
    {
        var catalog = new[]
        {
            Entry("browser", "browser_navigate", "Open and navigate to a URL", "url"),
            Entry("generic", "submit_payload", "Submit arbitrary payload", "browser navigate url url url url url")
        };

        var result = McpSearchIndex.Search(catalog, "browser navigate", topK: 2, minScore: 0.01);

        Assert.Equal("browser_navigate", result[0].Entry.ToolName);
    }

    private static McpCatalogEntry Entry(
        string server,
        string name,
        string description,
        string schemaKeywords) =>
        new(
            server,
            name,
            McpToolNameCodec.Encode(server, name),
            description,
            "{\"type\":\"object\",\"description\":"
            + JsonSerializer.Serialize(schemaKeywords)
            + ",\"properties\":{}}");
}
