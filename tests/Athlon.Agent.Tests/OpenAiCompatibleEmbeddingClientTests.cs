using System.Net;
using System.Net.Http;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Infrastructure.Knowledge;

namespace Athlon.Agent.Tests;

public sealed class OpenAiCompatibleEmbeddingClientTests
{
    [Fact]
    public async Task EmbedAsync_OmitsAuthorizationHeader_WhenApiKeyMissing()
    {
        var previousEmbedding = Environment.GetEnvironmentVariable("ATHLON_KNOWLEDGE_EMBEDDING_API_KEY");
        var previousOpenAi = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("ATHLON_KNOWLEDGE_EMBEDDING_API_KEY", null);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);

        HttpRequestMessage? capturedRequest = null;
        var handler = new CaptureHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "data": [
                        { "index": 0, "embedding": [0.1, 0.2] }
                      ]
                    }
                    """)
            };
        });

        try
        {
            var settings = new AppSettings
            {
                Knowledge = new KnowledgeSettings
                {
                    Embedding = new KnowledgeEmbeddingSettings
                    {
                        Endpoint = "https://example.com/v1",
                        Model = "text-embedding-3-small",
                        Dimension = 2,
                        BatchSize = 1
                    }
                }
            };

            var client = new OpenAiCompatibleEmbeddingClient(
                new HttpClient(handler),
                settings,
                new EmptyCredentialStore(),
                new NoOpLogger());

            var result = await client.EmbedAsync(["hello"]);

            Assert.NotNull(capturedRequest);
            Assert.Null(capturedRequest!.Headers.Authorization);
            Assert.Single(result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ATHLON_KNOWLEDGE_EMBEDDING_API_KEY", previousEmbedding);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previousOpenAi);
        }
    }

    private sealed class CaptureHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class EmptyCredentialStore : ICredentialStore
    {
        public Task SaveSecretAsync(string name, string secret, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<bool> HasSecretAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
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
