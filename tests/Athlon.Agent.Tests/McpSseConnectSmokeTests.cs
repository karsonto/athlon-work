using Athlon.Agent.Core;
using Athlon.Agent.Mcp;
using ModelContextProtocol.Client;

namespace Athlon.Agent.Tests;

public sealed class McpSseConnectSmokeTests
{
    [Fact]
    public void ResolveMode_ForLocalSseUrl_IsSse()
    {
        Assert.Equal(
            HttpTransportMode.Sse,
            McpTransportKinds.ResolveHttpTransportMode("sse", "http://localhost:3100/sse"));
        Assert.Equal(
            HttpTransportMode.Sse,
            McpTransportKinds.ResolveHttpTransportMode("http", "http://localhost:3100/sse"));
    }

    [Fact]
    public async Task ConnectAsync_LocalSse_WhenServerListening_ListsTools()
    {
        if (!await IsLocalSseReachableAsync())
        {
            return; // optional smoke: skip when the local MCP server is not running
        }

        var server = new McpServerSettings
        {
            Name = "local-sse",
            Enabled = true,
            TransportType = "sse",
            Url = "http://localhost:3100/sse"
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var client = await McpSdkClientFactory.ConnectAsync(
            "local-sse",
            server,
            workspaceRoot: null,
            cancellationToken: cts.Token);

        var tools = await client.ListToolsAsync(cts.Token);
        Assert.Equal(McpConnectionState.Connected, client.Status.State);
        Assert.Equal("sse", client.Status.Transport);
        Assert.True(tools.Count >= 0);
    }

    private static async Task<bool> IsLocalSseReachableAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:3100/sse");
            request.Headers.Accept.ParseAdd("text/event-stream");
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                CancellationToken.None);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
