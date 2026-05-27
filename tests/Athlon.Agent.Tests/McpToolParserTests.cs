using System.Text.Json;
using System.Text.Json.Nodes;
using Athlon.Agent.Mcp;

namespace Athlon.Agent.Tests;

public sealed class McpToolParserTests
{
    [Fact]
    public void ParseTools_ReturnsToolsWithSchemaJson()
    {
        using var document = JsonDocument.Parse("""
            {
              "tools": [
                {
                  "name": "echo",
                  "description": "Echo input",
                  "inputSchema": { "type": "object", "properties": { "message": { "type": "string" } } }
                }
              ]
            }
            """);

        var tools = McpToolParser.ParseTools(document.RootElement);

        Assert.Single(tools);
        Assert.Equal("echo", tools[0].Name);
        Assert.Equal("Echo input", tools[0].Description);
        Assert.Contains("\"message\"", tools[0].InputSchemaJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseArgumentsNode_UsesJsonObjectWhenValid()
    {
        var node = McpToolParser.ParseArgumentsNode("{\"x\":1}");

        Assert.IsType<JsonObject>(node);
        Assert.Equal(1, node!["x"]!.GetValue<int>());
    }

    [Fact]
    public void ParseArgumentsNode_UsesRawStringWhenInvalidJson()
    {
        var node = McpToolParser.ParseArgumentsNode("not-json");

        Assert.Equal("not-json", node!.GetValue<string>());
    }
}
