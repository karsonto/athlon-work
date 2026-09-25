using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Audio;

namespace Athlon.Agent.Infrastructure.Audio;

/// <summary>
/// OpenAI-compatible TTS client. Requests raw 16-bit mono PCM over HTTP chunked transfer so the
/// caller can start the output device from the very first slice, without an intermediate
/// container (WAV/MP3) or SSE parsing.
/// </summary>
public sealed class OpenAiCompatibleTtsClient(
    HttpClient httpClient,
    AppSettings settings,
    ICredentialStore credentialStore,
    IAppLogger logger) : ITtsClient
{
    /// <summary>
    /// Read size for the response stream. 16 KB is roughly 0.34s of 24kHz/16-bit/mono audio: big
    /// enough to keep syscall overhead negligible, small enough that playback still starts quickly.
    /// </summary>
    private const int ReadBufferBytes = 16 * 1024;

    private const int DefaultSampleRate = 24_000;

    private readonly IAppLogger _logger = logger.ForContext("TtsGateway");

    public async IAsyncEnumerable<TtsAudioChunk> StreamAsync(
        TtsRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var cfg = settings.Tts;
        var endpoint = cfg.Endpoint.TrimEnd('/') + "/audio/speech?stream=true";
        var apiKey = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrWhiteSpace(cfg.ModelName) ? "qwen3-tts" : cfg.ModelName,
            ["input"] = request.Text,
            ["voice"] = request.Voice ?? cfg.Voice,
            ["speed"] = request.Speed ?? cfg.Speed,
            ["response_format"] = "pcm"
        };

        var instructions = request.Instructions ?? cfg.Instructions;
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            payload["instructions"] = instructions.Trim();
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            httpRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new TtsClientException($"无法连接语音合成服务（{cfg.Endpoint}）：{ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                throw new TtsClientException(DescribeFailure(response.StatusCode, body), null);
            }

            var sampleRate = ResolveSampleRate(response);

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            var buffer = new byte[ReadBufferBytes];
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (IOException ex)
                {
                    _logger.Warning("TTS 流读取中断：{Message}", ex.Message);
                    throw new TtsClientException($"语音合成流中断：{ex.Message}", ex);
                }

                if (read <= 0)
                {
                    break;
                }

                // Copy: the buffer is reused on the next read, so the chunk must own its bytes.
                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                yield return new TtsAudioChunk(chunk, sampleRate);
            }
        }
    }

    public async Task<TtsProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var cfg = settings.Tts;
        var endpoint = cfg.Endpoint.TrimEnd('/') + "/health";
        var apiKey = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new TtsClientException($"无法连接语音合成服务（{cfg.Endpoint}）：{ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new TtsClientException(DescribeFailure(response.StatusCode, body), null);
            }

            try
            {
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                var model = root.TryGetProperty("model", out var modelElement)
                    ? modelElement.GetString()
                    : null;
                var sampleRate = root.TryGetProperty("sample_rate", out var srElement)
                    && srElement.TryGetInt32(out var sr)
                        ? sr
                        : DefaultSampleRate;
                var ffmpeg = root.TryGetProperty("ffmpeg", out var ffmpegElement)
                    && ffmpegElement.ValueKind == JsonValueKind.True;

                return new TtsProbeResult(
                    model,
                    sampleRate,
                    ffmpeg,
                    ReadStringArray(root, "voices"),
                    ReadStringArray(root, "available_formats"));
            }
            catch (JsonException ex)
            {
                throw new TtsClientException($"语音合成服务返回了无法解析的响应：{ex.Message}", ex);
            }
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return element.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(value => value.Length > 0)
            .ToArray();
    }

    private static int ResolveSampleRate(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-Sample-Rate", out var values))
        {
            foreach (var value in values)
            {
                if (int.TryParse(value, out var sampleRate) && sampleRate > 0)
                {
                    return sampleRate;
                }
            }
        }

        return DefaultSampleRate;
    }

    private static async Task<string?> SafeReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Turns a non-2xx response into a message worth showing in the chat UI.</summary>
    private static string DescribeFailure(HttpStatusCode statusCode, string? body)
    {
        var detail = ExtractErrorMessage(body);
        return statusCode switch
        {
            HttpStatusCode.Unauthorized => detail is null
                ? "语音合成服务鉴权失败（401）：请检查设置中的 API Key。"
                : $"语音合成服务鉴权失败（401）：{detail}",
            HttpStatusCode.NotFound => detail is null
                ? "语音合成服务未找到该接口（404）：请检查 Endpoint 是否为 .../v1。"
                : $"语音合成服务未找到该接口（404）：{detail}",
            _ when (int)statusCode >= 500 => detail is null
                ? $"语音合成服务出错（{(int)statusCode}）。"
                : $"语音合成服务出错（{(int)statusCode}）：{detail}",
            _ => detail is null
                ? $"语音合成请求失败（{(int)statusCode}）。"
                : $"语音合成请求失败（{(int)statusCode}）：{detail}"
        };
    }

    /// <summary>Reads the OpenAI-style <c>{"error":{"message":...}}</c> envelope when present.</summary>
    private static string? ExtractErrorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString();
                }

                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString();
                }
            }

            if (json.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String)
            {
                return detail.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON; fall through to a truncated raw body.
        }

        var trimmed = body.Trim();
        return trimmed.Length == 0 ? null : HttpLogSanitizer.Truncate(trimmed);
    }

    private async Task<string?> ResolveApiKeyAsync(CancellationToken cancellationToken)
    {
        var apiKey = await credentialStore
            .GetSecretAsync(TtsSettings.ApiKeySecretName, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = Environment.GetEnvironmentVariable("TTS_API_KEY");
        }

        return string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }
}
