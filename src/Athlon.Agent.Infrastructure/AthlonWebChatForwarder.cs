using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

/// <summary>
/// Forwards Athlon Web chat completions to the agent-configured model.
/// The page sends messages without an API key; this adds the stored key and model name.
/// </summary>
public sealed class AthlonWebChatForwarder : IDisposable
{
    public const string ChatCompletionsPath = "/v1/chat/completions";

    private readonly AppSettings _settings;
    private readonly ICredentialStore _credentialStore;
    private readonly HttpClient _httpClient;

    public AthlonWebChatForwarder(AppSettings settings, ICredentialStore credentialStore)
        : this(settings, credentialStore, CreateClient())
    {
    }

    internal AthlonWebChatForwarder(AppSettings settings, ICredentialStore credentialStore, HttpClient httpClient)
    {
        _settings = settings;
        _credentialStore = credentialStore;
        _httpClient = httpClient;
    }

    public static bool IsChatCompletionRequest(string? httpMethod, Uri? url)
    {
        if (!string.Equals(httpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = url?.AbsolutePath.TrimEnd('/');
        return string.Equals(path, ChatCompletionsPath, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<HttpResponseMessage> SendAsync(Stream requestBody, CancellationToken cancellationToken = default)
    {
        string json;
        using (var reader = new StreamReader(requestBody, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        {
            json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return Text(HttpStatusCode.BadRequest, "Empty chat request.");
        }

        JsonObject payload;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj)
            {
                return Text(HttpStatusCode.BadRequest, "Chat request must be a JSON object.");
            }

            payload = obj;
        }
        catch (JsonException)
        {
            return Text(HttpStatusCode.BadRequest, "Invalid JSON.");
        }

        var modelName = _settings.Model.ModelName?.Trim();
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return Text(HttpStatusCode.ServiceUnavailable, "Model is not configured.");
        }

        var endpoint = _settings.Model.Endpoint?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return Text(HttpStatusCode.ServiceUnavailable, "Model endpoint is not configured.");
        }

        payload["model"] = modelName;
        var apiKey = await ModelApiKeyResolver.ResolveAsync(_credentialStore, _settings, cancellationToken)
            .ConfigureAwait(false);

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/chat/completions")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", "Athlon-Agent");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        try
        {
            return await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            request.Dispose();
            throw;
        }
        catch (HttpRequestException ex)
        {
            request.Dispose();
            return Text(HttpStatusCode.BadGateway, ex.Message);
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private static HttpClient CreateClient()
    {
        return new HttpClient(ModelHttpClientHandler.Create())
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static HttpResponseMessage Text(HttpStatusCode statusCode, string message)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(message, Encoding.UTF8, "text/plain"),
        };
    }
}
