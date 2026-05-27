using System.Net;
using System.Text;
using Athlon.Agent.Mcp;

namespace Athlon.Agent.Tests;

public sealed class McpStreamableHttpClientTests
{
    [Fact]
    public async Task InitializeAndListTools_JsonResponse_Works()
    {
        var handler = new ScriptableHttpHandler();
        handler.EnqueueJson("""
            {"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-03-26","capabilities":{}}}
            """, sessionId: "sess-1");
        handler.EnqueueJson("""
            {"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"echo","description":"echo tool","inputSchema":{"type":"object"}}]}}
            """);

        await using var client = new McpStreamableHttpClient("remote", "http://127.0.0.1/mcp", handler: handler);
        await client.InitializeAsync();
        var tools = await client.ListToolsAsync();

        Assert.Single(tools);
        Assert.Equal("echo", tools[0].Name);
        Assert.Equal("sess-1", handler.LastSessionIdSent);
    }

    [Fact]
    public async Task CallTool_SseResponse_ParsesMatchingEvent()
    {
        var handler = new ScriptableHttpHandler();
        handler.EnqueueJson("""{"jsonrpc":"2.0","id":1,"result":{}}""");
        handler.EnqueueSse("""
            event: message
            data: {"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"ok"}]}}
            """);

        await using var client = new McpStreamableHttpClient("remote", "http://127.0.0.1/mcp", handler: handler);
        await client.InitializeAsync();
        var raw = await client.CallToolAsync("echo", "{\"message\":\"hi\"}");

        Assert.Contains("ok", raw, StringComparison.Ordinal);
    }

    private sealed class ScriptableHttpHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

        public string? LastSessionIdSent { get; private set; }

        public void EnqueueJson(string body, string? sessionId = null) =>
            _responses.Enqueue(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    response.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
                }

                return response;
            });

        public void EnqueueSse(string body) =>
            _responses.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
            });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.TryGetValues("Mcp-Session-Id", out var values))
            {
                LastSessionIdSent = values.FirstOrDefault();
            }

            if (_responses.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            return Task.FromResult(_responses.Dequeue()(request));
        }
    }
}
