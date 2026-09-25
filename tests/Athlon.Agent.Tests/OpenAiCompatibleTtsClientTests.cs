using System.Net;
using System.Net.Http;
using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Audio;
using Athlon.Agent.Infrastructure.Audio;

namespace Athlon.Agent.Tests;

public sealed class OpenAiCompatibleTtsClientTests
{
    private const int ReadBufferBytes = 16 * 1024;

    [Fact]
    public async Task StreamAsync_yields_pcm_chunks_with_the_reported_sample_rate()
    {
        // Larger than one read buffer so the client must split the body into multiple chunks.
        var pcm = new byte[ReadBufferBytes + 512];
        Random.Shared.NextBytes(pcm);

        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(pcm))
            };
            response.Headers.Add("X-Sample-Rate", "24000");
            return response;
        });

        var client = CreateClient(handler);

        var chunks = new List<TtsAudioChunk>();
        await foreach (var chunk in client.StreamAsync(new TtsRequest("你好")))
        {
            chunks.Add(chunk);
        }

        Assert.NotNull(captured);
        Assert.All(chunks, chunk => Assert.Equal(24_000, chunk.SampleRate));
        Assert.All(chunks, chunk => Assert.InRange(chunk.Pcm.Length, 1, ReadBufferBytes));
        Assert.Equal(pcm, chunks.SelectMany(chunk => chunk.Pcm).ToArray());

        Assert.Equal(
            "https://tts.example.com/v1/audio/speech?stream=true",
            captured!.RequestUri!.ToString());
        var body = await captured.Content!.ReadAsStringAsync();
        Assert.Contains("\"response_format\":\"pcm\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamAsync_defaults_sample_rate_when_header_missing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3, 4])
        });

        var client = CreateClient(handler);

        var chunks = new List<TtsAudioChunk>();
        await foreach (var chunk in client.StreamAsync(new TtsRequest("hi")))
        {
            chunks.Add(chunk);
        }

        var single = Assert.Single(chunks);
        Assert.Equal(24_000, single.SampleRate);
        Assert.Equal(4, single.Pcm.Length);
    }

    [Fact]
    public async Task StreamAsync_sends_bearer_token_when_a_key_is_stored()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0])
            };
        });

        var client = CreateClient(handler, storedKey: "tts-secret");

        await foreach (var _ in client.StreamAsync(new TtsRequest("hi")))
        {
            // Drain the stream.
        }

        Assert.NotNull(captured);
        Assert.Equal("Bearer", captured!.Headers.Authorization?.Scheme);
        Assert.Equal("tts-secret", captured.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task StreamAsync_maps_401_to_a_readable_authentication_message()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                """{"error":{"message":"Invalid API key provided.","code":"invalid_api_key"}}""",
                Encoding.UTF8,
                "application/json")
        });

        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<TtsClientException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(new TtsRequest("hi")))
            {
                // The failure surfaces before any chunk is produced.
            }
        });

        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Invalid API key provided.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamAsync_omits_optional_fields_when_not_configured()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([])
            };
        });

        var settings = new AppSettings();
        settings.Tts.Instructions = string.Empty;
        var client = CreateClient(handler, settings: settings);

        await foreach (var _ in client.StreamAsync(new TtsRequest("hi")))
        {
        }

        var body = await captured!.Content!.ReadAsStringAsync();
        Assert.DoesNotContain("instructions", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeAsync_parses_voices_sample_rate_and_formats()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "status": "ok",
                  "model": "qwen3-tts",
                  "model_loaded": true,
                  "sample_rate": 24000,
                  "ffmpeg": false,
                  "available_formats": ["pcm", "wav"],
                  "voices": ["Vivian", "Cherry"]
                }
                """,
                Encoding.UTF8,
                "application/json")
        });

        var client = CreateClient(handler);

        var result = await client.ProbeAsync(CancellationToken.None);

        Assert.Equal("qwen3-tts", result.Model);
        Assert.Equal(24_000, result.SampleRate);
        Assert.False(result.Ffmpeg);
        Assert.Equal(["Vivian", "Cherry"], result.Voices);
        Assert.Equal(["pcm", "wav"], result.Formats);
    }

    [Fact]
    public async Task ProbeAsync_surfaces_connection_failures()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<TtsClientException>(
            () => client.ProbeAsync(CancellationToken.None));

        Assert.Contains("无法连接语音合成服务", exception.Message, StringComparison.Ordinal);
    }

    private static OpenAiCompatibleTtsClient CreateClient(
        HttpMessageHandler handler,
        string? storedKey = null,
        AppSettings? settings = null)
    {
        settings ??= new AppSettings();
        settings.Tts.Endpoint = "https://tts.example.com/v1";
        return new OpenAiCompatibleTtsClient(
            new HttpClient(handler),
            settings,
            new StubCredentialStore(storedKey),
            new NoOpLogger());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class StubCredentialStore(string? secret) : ICredentialStore
    {
        public Task SaveSecretAsync(string name, string value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(secret);

        public Task<bool> HasSecretAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(secret is not null);

        public Task DeleteSecretAsync(string name, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
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
