using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class OpenAiCompatibleChatModelClientStreamingTests
{
    [Fact]
    public async Task CompleteAsync_StreamResponse_EmitsDeltasAndBuildsToolCalls()
    {
        var response = string.Join(
            "\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"file_read\",\"arguments\":\"{\\\"path\\\":\\\"a\"}}]}}]}",
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\".txt\\\"}\"}}]}}]}",
            "data: [DONE]",
            string.Empty);

        var handler = new StubHttpMessageHandler(_ =>
        {
            var http = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8)
            };
            http.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return http;
        });

        var settings = new AppSettings
        {
            Model = new ModelSettings
            {
                Endpoint = "https://example.com/v1",
                ModelName = "demo",
                EnableStreaming = true
            }
        };

        var client = new OpenAiCompatibleChatModelClient(
            new HttpClient(handler),
            new NoOpLogger(),
            settings,
            new FixedCredentialStore("test-key"),
            new CaptureHttpLogService(),
            new ActiveAgentSessionContext());

        var deltas = new StringBuilder();
        var result = await client.CompleteAsync(
            new AgentModelRequest(
                new[] { new AgentModelMessage("user", "hi") },
                Array.Empty<ToolDefinition>()),
            token =>
            {
                deltas.Append(token);
                return Task.CompletedTask;
            });

        Assert.Equal("Hello", result.Content);
        Assert.Equal("Hello", deltas.ToString());
        Assert.Single(result.ToolCalls);
        Assert.Equal("call_1", result.ToolCalls[0].Id);
        Assert.Equal("file_read", result.ToolCalls[0].Name);
        Assert.Equal("a.txt", result.ToolCalls[0].Arguments["path"]);
    }

    [Fact]
    public async Task CompleteAsync_WhenStreamingDisabled_InvokesDeltaOnceWithFullText()
    {
        var response = "{\"choices\":[{\"message\":{\"content\":\"Final text\"}}]}";
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") });

        var settings = new AppSettings
        {
            Model = new ModelSettings
            {
                Endpoint = "https://example.com/v1",
                ModelName = "demo",
                EnableStreaming = false
            }
        };

        var client = new OpenAiCompatibleChatModelClient(
            new HttpClient(handler),
            new NoOpLogger(),
            settings,
            new FixedCredentialStore("test-key"),
            new CaptureHttpLogService(),
            new ActiveAgentSessionContext());

        var tokens = new List<string>();
        var result = await client.CompleteAsync(
            new AgentModelRequest(new[] { new AgentModelMessage("user", "hi") }, Array.Empty<ToolDefinition>()),
            token =>
            {
                tokens.Add(token);
                return Task.CompletedTask;
            });

        Assert.Equal("Final text", result.Content);
        Assert.Single(tokens);
        Assert.Equal("Final text", tokens[0]);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class FixedCredentialStore(string value) : ICredentialStore
    {
        public Task SaveSecretAsync(string name, string secret, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult<string?>(value);
        public Task<bool> HasSecretAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class CaptureHttpLogService : ISessionHttpLogService
    {
        public Task LogInteractionAsync(string? sessionId, SessionHttpInteractionLog entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }
}
