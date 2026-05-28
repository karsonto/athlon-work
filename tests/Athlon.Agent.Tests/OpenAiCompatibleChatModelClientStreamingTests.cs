using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

        var client = CreateClient(handler, enableStreaming: true);

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
    public async Task CompleteAsync_StreamResponse_EmitsReasoningDeltas()
    {
        var response = string.Join(
            "\n",
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"think\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\" hard\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}",
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

        var client = CreateClient(handler, enableStreaming: true);

        var reasoningDeltas = new StringBuilder();
        var result = await client.CompleteAsync(
            new AgentModelRequest(
                new[] { new AgentModelMessage("user", "hi") },
                Array.Empty<ToolDefinition>()),
            onTextDelta: null,
            onReasoningDelta: token =>
            {
                reasoningDeltas.Append(token);
                return Task.CompletedTask;
            });

        Assert.Equal("think hard", result.ReasoningContent);
        Assert.Equal("think hard", reasoningDeltas.ToString());
        Assert.Equal("answer", result.Content);
    }

    [Fact]
    public async Task CompleteAsync_NonStreaming_EmitsReasoningDeltaOnce()
    {
        var response =
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"answer\",\"reasoning_content\":\"thought\"}}]}";
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") });

        var client = CreateClient(handler, enableStreaming: false);

        var reasoningDeltas = new List<string>();
        var result = await client.CompleteAsync(
            new AgentModelRequest(new[] { new AgentModelMessage("user", "hi") }, Array.Empty<ToolDefinition>()),
            onTextDelta: null,
            onReasoningDelta: token =>
            {
                reasoningDeltas.Add(token);
                return Task.CompletedTask;
            });

        Assert.Equal("thought", result.ReasoningContent);
        Assert.Single(reasoningDeltas);
        Assert.Equal("thought", reasoningDeltas[0]);
    }

    [Fact]
    public async Task CompleteAsync_NonStreaming_ParsesReasoningContent()
    {
        var response =
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"answer\",\"reasoning_content\":\"thought\"}}]}";
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") });

        var client = CreateClient(handler, enableStreaming: false);
        var result = await client.CompleteAsync(
            new AgentModelRequest(new[] { new AgentModelMessage("user", "hi") }, Array.Empty<ToolDefinition>()));

        Assert.Equal("answer", result.Content);
        Assert.Equal("thought", result.ReasoningContent);
    }

    [Fact]
    public async Task CompleteAsync_ReplaysReasoningContentInFollowUpRequest()
    {
        var callCount = 0;
        string? capturedRequestBody = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            callCount++;
            if (callCount == 1)
            {
                var response =
                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"answer\",\"reasoning_content\":\"thought\"}}]}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response, Encoding.UTF8, "application/json")
                };
            }

            capturedRequestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}", Encoding.UTF8, "application/json")
            };
        });

        var client = CreateClient(handler, enableStreaming: false);
        var first = await client.CompleteAsync(
            new AgentModelRequest(new[] { new AgentModelMessage("user", "hi") }, Array.Empty<ToolDefinition>()));

        await client.CompleteAsync(
            new AgentModelRequest(
                new[]
                {
                    new AgentModelMessage("user", "hi"),
                    new AgentModelMessage("assistant", first.Content, ReasoningContent: first.ReasoningContent),
                    new AgentModelMessage("user", "continue")
                },
                Array.Empty<ToolDefinition>()));

        Assert.NotNull(capturedRequestBody);
        using var json = JsonDocument.Parse(capturedRequestBody);
        var assistant = json.RootElement.GetProperty("messages")[1];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        Assert.Equal("thought", assistant.GetProperty("reasoning_content").GetString());
    }

    [Fact]
    public async Task CompleteAsync_WhenStreamingDisabled_InvokesDeltaOnceWithFullText()
    {
        var response = "{\"choices\":[{\"message\":{\"content\":\"Final text\"}}]}";
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") });

        var client = CreateClient(handler, enableStreaming: false);

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

    [Fact]
    public async Task CompleteAsync_WhenStreamingFails_FallsBackToNonStreaming()
    {
        var callCount = 0;
        var streamFlags = new List<bool>();
        var handler = new StubHttpMessageHandler(request =>
        {
            callCount++;
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var json = JsonDocument.Parse(body);
            streamFlags.Add(json.RootElement.GetProperty("stream").GetBoolean());

            if (callCount == 1)
            {
                throw new HttpRequestException("stream failed");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"fallback-ok\"}}]}", Encoding.UTF8, "application/json")
            };
        });

        var client = CreateClient(handler, enableStreaming: true);
        var tokens = new List<string>();
        var result = await client.CompleteAsync(
            new AgentModelRequest(new[] { new AgentModelMessage("user", "hi") }, Array.Empty<ToolDefinition>()),
            token =>
            {
                tokens.Add(token);
                return Task.CompletedTask;
            });

        Assert.Equal("fallback-ok", result.Content);
        Assert.Equal(new[] { true, false }, streamFlags);
        Assert.Single(tokens);
        Assert.Equal("fallback-ok", tokens[0]);
    }

    [Fact]
    public async Task CompleteAsync_StreamRequested_WithNonSseContentType_FallsBackToJsonParsing()
    {
        var streamFlags = new List<bool>();
        var handler = new StubHttpMessageHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var json = JsonDocument.Parse(body);
            streamFlags.Add(json.RootElement.GetProperty("stream").GetBoolean());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"json-ok\"}}]}", Encoding.UTF8, "application/json")
            };
        });

        var client = CreateClient(handler, enableStreaming: true);
        var result = await client.CompleteAsync(
            new AgentModelRequest(new[] { new AgentModelMessage("user", "hi") }, Array.Empty<ToolDefinition>()),
            _ => Task.CompletedTask);

        Assert.Equal("json-ok", result.Content);
        Assert.Single(streamFlags);
        Assert.True(streamFlags[0]);
    }

    [Fact]
    public async Task CompleteAsync_StreamRequested_WithoutSseDataLines_UsesFallbackBodyParsing()
    {
        var body = "{\"choices\":[{\"message\":{\"content\":\"fallback-body\"}}]}\n";
        var handler = new StubHttpMessageHandler(_ =>
        {
            var http = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8)
            };
            http.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return http;
        });

        var client = CreateClient(handler, enableStreaming: true);
        var result = await client.CompleteAsync(
            new AgentModelRequest(new[] { new AgentModelMessage("user", "hi") }, Array.Empty<ToolDefinition>()),
            _ => Task.CompletedTask);

        Assert.Equal("fallback-body", result.Content);
    }

    [Fact]
    public async Task CompleteAsync_WhenAllowToolCallsFalse_StillUsesStreaming()
    {
        var streamFlags = new List<bool>();
        var response = string.Join(
            "\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"sum\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\"mary\"}}]}",
            "data: [DONE]",
            string.Empty);

        var handler = new StubHttpMessageHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var json = JsonDocument.Parse(body);
            streamFlags.Add(json.RootElement.GetProperty("stream").GetBoolean());

            var http = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8)
            };
            http.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return http;
        });

        var client = CreateClient(handler, enableStreaming: true);
        var deltas = new StringBuilder();
        var result = await client.CompleteAsync(
            new AgentModelRequest(
                new[] { new AgentModelMessage("user", "summarize") },
                Array.Empty<ToolDefinition>(),
                AllowToolCalls: false,
                MaxTokens: 64),
            token =>
            {
                deltas.Append(token);
                return Task.CompletedTask;
            });

        Assert.Equal("summary", result.Content);
        Assert.Equal("summary", deltas.ToString());
        Assert.Single(streamFlags);
        Assert.True(streamFlags[0]);
    }

    [Fact]
    public async Task CompleteAsync_CompactionLikeRequest_UsesStreamingWithoutDeltaCallbacks()
    {
        var streamFlags = new List<bool>();
        var response = string.Join(
            "\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"compact\"}}]}",
            "data: [DONE]",
            string.Empty);

        var handler = new StubHttpMessageHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var json = JsonDocument.Parse(body);
            streamFlags.Add(json.RootElement.GetProperty("stream").GetBoolean());

            var http = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8)
            };
            http.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return http;
        });

        var client = CreateClient(handler, enableStreaming: true);
        var result = await client.CompleteAsync(
            new AgentModelRequest(
                new[] { new AgentModelMessage("user", "summary prompt") },
                Array.Empty<ToolDefinition>(),
                AllowToolCalls: false,
                MaxTokens: 64));

        Assert.Equal("compact", result.Content);
        Assert.Single(streamFlags);
        Assert.True(streamFlags[0]);
    }

    private static OpenAiCompatibleChatModelClient CreateClient(HttpMessageHandler handler, bool enableStreaming)
    {
        var settings = new AppSettings
        {
            Model = new ModelSettings
            {
                Endpoint = "https://example.com/v1",
                ModelName = "demo",
                EnableStreaming = enableStreaming
            }
        };

        return new OpenAiCompatibleChatModelClient(
            new HttpClient(handler),
            new NoOpLogger(),
            settings,
            new FixedCredentialStore("test-key"),
            new CaptureHttpLogService(),
            new ActiveAgentSessionContext());
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
