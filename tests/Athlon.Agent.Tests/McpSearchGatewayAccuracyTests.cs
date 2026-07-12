using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class McpSearchGatewayAccuracyTests
{
    private const string SimpleSchema =
        """{"type":"object","properties":{"query":{"type":"string","minLength":1},"limit":{"type":"integer","minimum":1}},"required":["query"],"additionalProperties":false}""";

    [Fact]
    public async Task Search_ReturnsCompleteSimpleSchemaAndStableFingerprint()
    {
        var entry = Entry("search_issues", "Search issues", SimpleSchema);
        var registry = new TestMcpRegistry([entry]);
        var tools = McpSearchGatewayTools.Create(registry, Settings(), () => Task.CompletedTask);
        var search = tools.Single(tool => tool.Definition.Name == McpSearchGatewayTools.SearchToolName);

        var first = await search.InvokeAsync(new ToolInvocation(
            search.Definition.Name,
            ToolCallArgumentsParser.ParseJson("""{"query":"search issues","topK":1}""")));
        var second = await search.InvokeAsync(new ToolInvocation(
            search.Definition.Name,
            ToolCallArgumentsParser.ParseJson("""{"query":"search issues","topK":1}""")));

        using var firstJson = JsonDocument.Parse(first.Content!);
        using var secondJson = JsonDocument.Parse(second.Content!);
        var result = firstJson.RootElement.GetProperty("results")[0];
        Assert.False(result.GetProperty("requiresDescribe").GetBoolean());
        Assert.False(result.GetProperty("schemaTruncated").GetBoolean());
        Assert.Equal("object", result.GetProperty("inputSchema").GetProperty("type").GetString());
        Assert.Equal(
            result.GetProperty("schemaFingerprint").GetString(),
            secondJson.RootElement.GetProperty("results")[0].GetProperty("schemaFingerprint").GetString());
    }

    [Fact]
    public async Task Search_ComplexSchemaRequiresDescribe_AndDescribeReturnsFullSchema()
    {
        var complexSchema =
            """{"type":"object","properties":{"target":{"oneOf":[{"type":"string"},{"type":"object","properties":{"id":{"type":"string"}}}]}},"required":["target"]}""";
        var entry = Entry("complex_tool", "Complex target operation", complexSchema);
        var registry = new TestMcpRegistry([entry]);
        var tools = McpSearchGatewayTools.Create(registry, Settings(), () => Task.CompletedTask);
        var search = tools.Single(tool => tool.Definition.Name == McpSearchGatewayTools.SearchToolName);
        var describe = tools.Single(tool => tool.Definition.Name == McpSearchGatewayTools.DescribeToolName);

        var searchResult = await search.InvokeAsync(new ToolInvocation(
            search.Definition.Name,
            ToolCallArgumentsParser.ParseJson("""{"query":"complex target"}""")));
        var describeResult = await describe.InvokeAsync(new ToolInvocation(
            describe.Definition.Name,
            ToolCallArgumentsParser.ParseJson($$"""{"toolId":"{{entry.EncodedName}}"}""")));

        using var searchJson = JsonDocument.Parse(searchResult.Content!);
        using var describeJson = JsonDocument.Parse(describeResult.Content!);
        var found = searchJson.RootElement.GetProperty("results")[0];
        Assert.True(found.GetProperty("requiresDescribe").GetBoolean());
        Assert.True(found.GetProperty("schemaTruncated").GetBoolean());
        Assert.Contains("mcp_describe", found.GetProperty("guidance").GetString(), StringComparison.Ordinal);
        Assert.True(describeJson.RootElement.GetProperty("schemaComplete").GetBoolean());
        Assert.True(
            describeJson.RootElement.GetProperty("inputSchema")
                .GetProperty("properties")
                .GetProperty("target")
                .TryGetProperty("oneOf", out _));
    }

    [Fact]
    public async Task Call_ForwardsNativeArgumentsWithoutDoubleEncoding()
    {
        var entry = Entry("search_issues", "Search issues", SimpleSchema);
        var registry = new TestMcpRegistry([entry]);
        var call = McpSearchGatewayTools.Create(registry, Settings(), () => Task.CompletedTask)
            .Single(tool => tool.Definition.Name == McpSearchGatewayTools.CallToolName);
        var invocation = new ToolInvocation(
            call.Definition.Name,
            ToolCallArgumentsParser.ParseJson(
                JsonSerializer.Serialize(new
                {
                    toolId = entry.EncodedName,
                    arguments = new { query = "bug", limit = 2 }
                })));

        var result = await call.InvokeAsync(invocation);

        Assert.True(result.Succeeded);
        Assert.Equal(1, registry.InvocationCount);
        Assert.Equal("bug", registry.LastArguments!.GetString("query"));
        Assert.Equal(2, registry.LastArguments.GetInt32("limit"));
        Assert.False(registry.LastArguments.ContainsKey("argumentsJson"));
    }

    [Fact]
    public void RegistrySecondaryValidation_UsesFullCatalogSchema()
    {
        var invalid = McpRegistry.ValidateArgumentsAgainstSchema(
            SimpleSchema,
            ToolCallArgumentsParser.ParseJson("""{"query":"","limit":0}"""));
        var valid = McpRegistry.ValidateArgumentsAgainstSchema(
            SimpleSchema,
            ToolCallArgumentsParser.ParseJson("""{"query":"bug","limit":1}"""));

        Assert.NotNull(invalid);
        Assert.Contains("schema.min_length", invalid!.Error, StringComparison.Ordinal);
        Assert.Null(valid);
    }

    private static McpCatalogEntry Entry(string name, string description, string schema) =>
        new("linear", name, McpToolNameCodec.Encode("linear", name), description, schema);

    private static AppSettings Settings() => new()
    {
        McpSearch = new McpSearchSettings
        {
            Enabled = true,
            Mode = "search",
            TopKDefault = 5,
            TopKMax = 10,
            MinScore = 0.01
        }
    };
}
