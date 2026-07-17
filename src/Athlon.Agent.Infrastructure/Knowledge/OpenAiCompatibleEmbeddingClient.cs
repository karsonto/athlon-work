using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Infrastructure.BehaviorReport;

namespace Athlon.Agent.Infrastructure.Knowledge;

public sealed class OpenAiCompatibleEmbeddingClient(
    HttpClient httpClient,
    AppSettings settings,
    ICredentialStore credentialStore,
    IAppLogger logger) : IEmbeddingClient
{
    private readonly IAppLogger _logger = logger.ForContext("EmbeddingGateway");

    public async Task<IReadOnlyList<EmbeddingVector>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0)
        {
            return Array.Empty<EmbeddingVector>();
        }

        var cfg = settings.Knowledge.Embedding;
        var apiKey = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);
        var endpoint = cfg.Endpoint.TrimEnd('/') + "/embeddings";
        var result = new List<EmbeddingVector>(texts.Count);
        var sw = Stopwatch.StartNew();

        try
        {
            foreach (var batch in texts.Chunk(Math.Max(1, cfg.BatchSize)))
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = JsonContent.Create(new
                    {
                        model = cfg.Model,
                        input = batch
                    })
                };
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
                }

                using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warning("Embedding HTTP failed {StatusCode}: {Body}", (int)response.StatusCode, HttpLogSanitizer.Truncate(body) ?? "");
                    throw new HttpRequestException($"Embedding HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {HttpLogSanitizer.Truncate(body)}");
                }

                using var json = JsonDocument.Parse(body);
                if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException("Embedding response missing data array.");
                }

                var vectorsByIndex = new SortedDictionary<int, float[]>();
                foreach (var item in data.EnumerateArray())
                {
                    var index = item.TryGetProperty("index", out var indexElement) ? indexElement.GetInt32() : vectorsByIndex.Count;
                    var embedding = item.GetProperty("embedding").EnumerateArray().Select(value => value.GetSingle()).ToArray();
                    if (cfg.Dimension > 0 && embedding.Length != cfg.Dimension)
                    {
                        throw new InvalidOperationException($"Embedding dimension mismatch. Expected {cfg.Dimension}, got {embedding.Length}.");
                    }

                    vectorsByIndex[index] = embedding;
                }

                var batchArray = batch.ToArray();
                foreach (var pair in vectorsByIndex)
                {
                    if (pair.Key >= 0 && pair.Key < batchArray.Length)
                    {
                        result.Add(new EmbeddingVector(batchArray[pair.Key], pair.Value));
                    }
                }
            }

            sw.Stop();
            RecordEmbedding(texts.Count, sw.ElapsedMilliseconds, success: true);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            RecordEmbedding(texts.Count, sw.ElapsedMilliseconds, success: false, ex.GetType().Name);
            throw;
        }
    }

    private static void RecordEmbedding(int batchSize, long latencyMs, bool success, string? errorType = null)
    {
        try
        {
            BehaviorEventManager.Instance.Record(
                BehaviorEventIds.ModelCall,
                BehaviorEventTypes.Action,
                BehaviorEventIds.ModelCall,
                new Dictionary<string, object?>
                {
                    ["purpose"] = "Embedding",
                    ["batch_size"] = batchSize,
                    ["latency_ms"] = latencyMs,
                    ["result"] = success ? "success" : "failure",
                    ["error_type"] = errorType
                });
        }
        catch
        {
            // ignore
        }
    }

    private async Task<string?> ResolveApiKeyAsync(CancellationToken cancellationToken)
    {
        var apiKey = await credentialStore.GetSecretAsync(KnowledgeEmbeddingSettings.ApiKeySecretName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = Environment.GetEnvironmentVariable("ATHLON_KNOWLEDGE_EMBEDDING_API_KEY")
                ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        }

        return string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }
}
