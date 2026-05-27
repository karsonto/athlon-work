using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class ClaudeDesktopMcpConfigTests
{
    [Fact]
    public void Parse_ClaudeDesktopFormat_MapsToSettings()
    {
        const string json = """
            {
              "mcpServers": {
                "zai-mcp-server": {
                  "type": "stdio",
                  "command": "npx",
                  "args": ["-y", "@z_ai/mcp-server"],
                  "env": {
                    "Z_AI_API_KEY": "key",
                    "Z_AI_MODE": "ZAI"
                  }
                }
              }
            }
            """;

        Assert.True(ClaudeDesktopMcpConfigMapper.TryParse(json, out var config, out var error), error);
        var servers = ClaudeDesktopMcpConfigMapper.ToSettingsList(config!);
        Assert.Single(servers);
        Assert.Equal("zai-mcp-server", servers[0].Name);
        Assert.True(servers[0].Enabled);
        Assert.Equal("stdio", servers[0].TransportType);
        Assert.Equal("npx", servers[0].Command);
        Assert.Equal(new[] { "-y", "@z_ai/mcp-server" }, servers[0].Args);
        Assert.Equal("key", servers[0].Env["Z_AI_API_KEY"]);
    }

    [Fact]
    public void Parse_ClaudeDesktopFormat_MapsCwdToWorkingDirectory()
    {
        const string json = """
            {
              "mcpServers": {
                "qwen-vision": {
                  "command": "python",
                  "args": ["mcp_vision_server.py"],
                  "cwd": "C:/servers/qwen-vision"
                }
              }
            }
            """;

        Assert.True(ClaudeDesktopMcpConfigMapper.TryParse(json, out var config, out var error), error);
        var servers = ClaudeDesktopMcpConfigMapper.ToSettingsList(config!);
        Assert.Single(servers);
        Assert.Equal("C:/servers/qwen-vision", servers[0].WorkingDirectory);
    }

    [Fact]
    public void Serialize_ProducesClaudeDesktopShape()
    {
        var config = ClaudeDesktopMcpConfigMapper.FromSettingsList(new[]
        {
            new McpServerSettings
            {
                Name = "zai-mcp-server",
                Enabled = true,
                TransportType = "stdio",
                Command = "npx",
                Args = new List<string> { "-y", "@z_ai/mcp-server" },
                Env = new Dictionary<string, string> { ["Z_AI_API_KEY"] = "key" }
            }
        });

        var json = ClaudeDesktopMcpConfigMapper.Serialize(config);
        Assert.Contains("\"mcpServers\"", json, StringComparison.Ordinal);
        Assert.Contains("\"zai-mcp-server\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"disabled\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_StreamableHttpFormat_MapsUrlAndHeaders()
    {
        const string json = """
            {
              "mcpServers": {
                "remote": {
                  "type": "http",
                  "url": "http://127.0.0.1:8080/mcp",
                  "headers": {
                    "Authorization": "Bearer token"
                  }
                }
              }
            }
            """;

        Assert.True(ClaudeDesktopMcpConfigMapper.TryParse(json, out var config, out _));
        var servers = ClaudeDesktopMcpConfigMapper.ToSettingsList(config!);
        Assert.Single(servers);
        Assert.Equal("http", servers[0].TransportType);
        Assert.Equal("http://127.0.0.1:8080/mcp", servers[0].Url);
        Assert.Equal("Bearer token", servers[0].Headers["Authorization"]);
    }

    [Fact]
    public void Serialize_DisabledServer_WritesDisabledFlag()
    {
        var config = ClaudeDesktopMcpConfigMapper.FromSettingsList(new[]
        {
            new McpServerSettings { Name = "off", Enabled = false, Command = "npx" }
        });

        var json = ClaudeDesktopMcpConfigMapper.Serialize(config);
        Assert.Contains("\"disabled\": true", json, StringComparison.OrdinalIgnoreCase);
    }
}
