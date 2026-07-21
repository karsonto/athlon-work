using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class ToolSchemaTests
{
    [Fact]
    public void Object_BuildsRequiredAndTypedProperties()
    {
        var schema = ToolSchema.Object()
            .String("path", "workspace path", required: true)
            .Integer("offset", "start line")
            .Boolean("regex", "use regex")
            .Build();

        var json = schema.ToCanonicalJson();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("string", root.GetProperty("properties").GetProperty("path").GetProperty("type").GetString());
        Assert.Equal("integer", root.GetProperty("properties").GetProperty("offset").GetProperty("type").GetString());
        Assert.Equal("boolean", root.GetProperty("properties").GetProperty("regex").GetProperty("type").GetString());
        Assert.Contains("path", root.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void ToCanonicalJson_returns_cached_string()
    {
        var schema = ToolSchema.Object()
            .String("path", "workspace path", required: true)
            .Build();

        var first = schema.ToCanonicalJson();
        var second = schema.ToCanonicalJson();

        Assert.Same(first, second);
        Assert.Equal(first, schema.ToJsonElement().GetRawText());
    }

    [Fact]
    public void FromMcp_PreservesObjectSchema()
    {
        const string mcpSchema = """
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "description": "search" }
              },
              "required": ["query"]
            }
            """;

        var schema = ToolSchema.FromMcp(mcpSchema);
        var json = schema.ToCanonicalJson();

        Assert.Contains("\"query\"", json, StringComparison.Ordinal);
        Assert.Contains("\"required\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void FromLegacyParameters_MapsOptionalPrefix()
    {
        var schema = ToolSchema.FromLegacyParameters(new Dictionary<string, string>
        {
            ["path"] = "Workspace path",
            ["glob"] = "Optional file glob"
        });

        var json = schema.ToCanonicalJson();
        using var document = JsonDocument.Parse(json);
        var required = document.RootElement.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray();

        Assert.Contains("path", required);
        Assert.DoesNotContain("glob", required);
    }
}

public sealed class OpenAiChatRequestFactoryTests
{
    [Fact]
    public void BuildPayload_UsesJsonSchemaTypesForNativeTools()
    {
        var fileRead = new ToolDefinition(
            "file_read",
            "Read file",
            ToolSchema.Object()
                .String("path", ToolPathDescriptions.WorkspaceRelativePath, required: true)
                .Integer("offset", "0-indexed start line")
                .Integer("limit", "Max lines")
                .Build());

        var request = new AgentModelRequest([], [fileRead]);
        var payload = OpenAiChatRequestFactory.BuildPayload(request, new AppSettings(), stream: false);
        var tools = Assert.IsType<object[]>(payload["tools"]);
        var toolJson = JsonSerializer.Serialize(tools[0]);
        using var document = JsonDocument.Parse(toolJson);

        var parameters = document.RootElement
            .GetProperty("function")
            .GetProperty("parameters");

        Assert.Equal("string", parameters.GetProperty("properties").GetProperty("path").GetProperty("type").GetString());
        Assert.Equal("integer", parameters.GetProperty("properties").GetProperty("offset").GetProperty("type").GetString());
        Assert.Contains(
            "path",
            parameters.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void BuildPayload_UsesBooleanForFileEditReplaceAll()
    {
        var fileEdit = new ToolDefinition(
            "file_edit",
            "Edit file",
            ToolSchema.Object()
                .String("path", ToolPathDescriptions.WorkspaceRelativePath, required: true)
                .String("old_text", "old", required: true)
                .String("new_text", "new", required: true)
                .Boolean("replace_all", "Replace all occurrences")
                .Build());

        var request = new AgentModelRequest([], [fileEdit]);
        var payload = OpenAiChatRequestFactory.BuildPayload(request, new AppSettings(), stream: false);
        var toolJson = JsonSerializer.Serialize(((object[])payload["tools"]!)[0]);
        using var document = JsonDocument.Parse(toolJson);

        var replaceAll = document.RootElement
            .GetProperty("function")
            .GetProperty("parameters")
            .GetProperty("properties")
            .GetProperty("replace_all");

        Assert.Equal("boolean", replaceAll.GetProperty("type").GetString());
    }

    [Fact]
    public void BuildPayload_McpTool_UsesNativeSchemaWithoutArgumentsJsonWrapper()
    {
        const string mcpSchema = """
            {
              "type": "object",
              "properties": {
                "message": { "type": "string" }
              },
              "required": ["message"]
            }
            """;

        var tool = new ToolDefinition(
            "mcp_demo__echo",
            "echo",
            ToolSchema.FromMcp(mcpSchema),
            Source: "mcp");

        var payload = OpenAiChatRequestFactory.BuildPayload(new AgentModelRequest([], [tool]), new AppSettings(), false);
        var toolJson = JsonSerializer.Serialize(((object[])payload["tools"]!)[0]);

        Assert.DoesNotContain("argumentsJson", toolJson, StringComparison.Ordinal);
        Assert.Contains("\"message\"", toolJson, StringComparison.Ordinal);
    }
}
