using System.Net;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class AthlonWebChatForwarderTests
{
    [Fact]
    public async Task SendAsync_RewritesModel_AndAttachesStoredKey()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "data: ok\n\n");
        var credentials = new MemoryCredentialStore();
        await credentials.SaveSecretAsync(ModelSettings.ApiKeySecretName, "sk-stored");
        var settings = new AppSettings
        {
            Model = new ModelSettings
            {
                Endpoint = "http://model.example/v1",
                ModelName = "agent-model",
            },
        };

        using var http = new HttpClient(handler);
        using var forwarder = new AthlonWebChatForwarder(settings, credentials, http);
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"model":"athlon-coder","temperature":0.3,"messages":[{"role":"user","content":"hi"}],"stream":true}
            """));

        using var response = await forwarder.SendAsync(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("http://model.example/v1/chat/completions", handler.RequestUri?.ToString());
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("sk-stored", handler.AuthorizationParameter);
        using var document = JsonDocument.Parse(handler.Body ?? "");
        Assert.Equal("agent-model", document.RootElement.GetProperty("model").GetString());
        Assert.Equal(0.3, document.RootElement.GetProperty("temperature").GetDouble());
        Assert.True(document.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("hi", document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task SendAsync_Returns503_WhenEndpointMissing()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "unused");
        var settings = new AppSettings
        {
            Model = new ModelSettings
            {
                Endpoint = " ",
                ModelName = "agent-model",
            },
        };

        using var http = new HttpClient(handler);
        using var forwarder = new AthlonWebChatForwarder(settings, new MemoryCredentialStore(), http);
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("""{"model":"athlon-coder","messages":[]}"""));

        using var response = await forwarder.SendAsync(body);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public void IsChatCompletionRequest_MatchesPostPathOnly()
    {
        Assert.True(AthlonWebChatForwarder.IsChatCompletionRequest(
            "POST", new Uri("http://127.0.0.1:6888/v1/chat/completions")));
        Assert.False(AthlonWebChatForwarder.IsChatCompletionRequest(
            "GET", new Uri("http://127.0.0.1:6888/v1/chat/completions")));
        Assert.False(AthlonWebChatForwarder.IsChatCompletionRequest(
            "POST", new Uri("http://127.0.0.1:6888/v1/models")));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;

        public RecordingHandler(HttpStatusCode statusCode, string responseBody)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        public Uri? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public Task SaveSecretAsync(string name, string secret, CancellationToken cancellationToken = default)
        {
            _secrets[name] = secret;
            return Task.CompletedTask;
        }

        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets.TryGetValue(name, out var secret) ? secret : null);

        public Task<bool> HasSecretAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets.ContainsKey(name));

        public Task DeleteSecretAsync(string name, CancellationToken cancellationToken = default)
        {
            _secrets.Remove(name);
            return Task.CompletedTask;
        }
    }
}
