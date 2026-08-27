using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure.SkillHub;

public sealed class SkillHubClient(
    IHttpClientFactory httpClientFactory,
    AppSettings settings,
    ICredentialStore credentialStore) : ISkillHubClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(settings.BehaviorReport.BaseUrl);

    public async Task<IReadOnlyList<RemoteSkillDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var baseUrl = RequireBaseUrl();
        using var request = await CreateAuthorizedRequestAsync(
                HttpMethod.Get,
                $"{baseUrl}/agent/skills",
                cancellationToken)
            .ConfigureAwait(false);

        var client = httpClientFactory.CreateClient("SkillHub");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var payload = await response.Content
            .ReadFromJsonAsync<RemoteSkillListResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return payload?.Items ?? [];
    }

    public async Task<byte[]> DownloadAsync(string skillId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(skillId))
        {
            throw new ArgumentException("Skill id is required.", nameof(skillId));
        }

        var baseUrl = RequireBaseUrl();
        using var request = await CreateAuthorizedRequestAsync(
                HttpMethod.Get,
                $"{baseUrl}/agent/skills/download?id={Uri.EscapeDataString(skillId.Trim())}",
                cancellationToken)
            .ConfigureAwait(false);

        var client = httpClientFactory.CreateClient("SkillHub");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public static bool MatchesSha256(byte[] bytes, string? expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex) || bytes.Length == 0)
        {
            return true;
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return string.Equals(hash, expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private string RequireBaseUrl()
    {
        var baseUrl = settings.BehaviorReport.BaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Behavior report server is not configured.");
        }

        return baseUrl;
    }

    private async Task<HttpRequestMessage> CreateAuthorizedRequestAsync(
        HttpMethod method,
        string url,
        CancellationToken cancellationToken)
    {
        var apiKey = await ModelApiKeyResolver.ResolveAsync(credentialStore, settings, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Model API key is not configured.");
        }

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var detail = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body.Trim();
        throw new HttpRequestException(
            $"Skill Hub request failed ({(int)response.StatusCode}): {detail}");
    }
}
