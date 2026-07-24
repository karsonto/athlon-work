using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class McpServersJsonConverterTests
{
    [Fact]
    public void Deserialize_AthlonArray_PreservesServers()
    {
        const string json = """
            {
              "mcpServers": [
                {
                  "name": "filesystem",
                  "enabled": true,
                  "transportType": "stdio",
                  "command": "npx",
                  "args": ["-y", "@modelcontextprotocol/server-filesystem"]
                }
              ]
            }
            """;

        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonFileStore.Options)!;
        var server = Assert.Single(settings.McpServers);
        Assert.Equal("filesystem", server.Name);
        Assert.Equal("stdio", server.TransportType);
        Assert.Equal("npx", server.Command);
    }

    [Fact]
    public void Deserialize_ClaudeDesktopObject_MapsNamedServers()
    {
        const string json = """
            {
              "mcpServers": {
                "local-sse": {
                  "type": "sse",
                  "url": "http://localhost:3100/sse"
                }
              }
            }
            """;

        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonFileStore.Options)!;
        var server = Assert.Single(settings.McpServers);
        Assert.Equal("local-sse", server.Name);
        Assert.Equal("sse", server.TransportType);
        Assert.Equal("http://localhost:3100/sse", server.Url);
        Assert.True(server.Enabled);
    }

    [Fact]
    public void Deserialize_HybridArray_WithClaudeDesktopSingleton_MapsEntry()
    {
        const string json = """
            {
              "mcpServers": [
                {
                  "local-sse": {
                    "type": "sse",
                    "url": "http://localhost:3100/sse"
                  }
                },
                {
                  "name": "filesystem",
                  "enabled": true,
                  "transportType": "stdio",
                  "command": "npx"
                }
              ]
            }
            """;

        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonFileStore.Options)!;
        Assert.Equal(2, settings.McpServers.Count);
        Assert.Equal("local-sse", settings.McpServers[0].Name);
        Assert.Equal("sse", settings.McpServers[0].TransportType);
        Assert.Equal("http://localhost:3100/sse", settings.McpServers[0].Url);
        Assert.Equal("filesystem", settings.McpServers[1].Name);
    }

    [Fact]
    public void Serialize_AlwaysWritesAthlonArray()
    {
        var settings = new AppSettings
        {
            McpServers =
            [
                new McpServerSettings
                {
                    Name = "local-sse",
                    Enabled = true,
                    TransportType = "sse",
                    Url = "http://localhost:3100/sse"
                }
            ]
        };

        var json = JsonSerializer.Serialize(settings, JsonFileStore.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("mcpServers").ValueKind);
        Assert.Equal(
            "local-sse",
            doc.RootElement.GetProperty("mcpServers")[0].GetProperty("name").GetString());
        Assert.Equal(
            "sse",
            doc.RootElement.GetProperty("mcpServers")[0].GetProperty("transportType").GetString());
    }
}
