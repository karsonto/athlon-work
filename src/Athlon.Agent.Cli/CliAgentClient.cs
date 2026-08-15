using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Cli;

namespace Athlon.Agent.Cli;

internal sealed class CliAgentClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _autoYes;

    public CliAgentClient(CliEndpointInfo endpoint, bool autoYes)
    {
        _autoYes = autoYes;
        _http = new HttpClient
        {
            BaseAddress = new Uri(endpoint.Url, UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
    }

    public async Task<bool> HealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync("v1/health", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<string?> RunTurnAsync(
        string cwd,
        string input,
        string? sessionId,
        bool newSession,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/turns")
        {
            Content = JsonContent.Create(
                new CliTurnRequest
                {
                    Cwd = cwd,
                    Input = input,
                    SessionId = sessionId,
                    NewSession = newSession
                },
                options: JsonFileStoreOptions.WebCompactRelaxed)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var message = TryReadError(body) ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
            Console.Error.WriteLine(message);
            return sessionId;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var currentSession = sessionId;
        await foreach (var frame in CliSseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            currentSession = await HandleFrameAsync(frame, currentSession, cancellationToken).ConfigureAwait(false);
        }

        if (!Console.IsOutputRedirected)
        {
            Console.WriteLine();
        }

        return currentSession;
    }

    public async Task CancelAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.PostAsync(
                    $"v1/turns/{Uri.EscapeDataString(sessionId)}/cancel",
                    content: null,
                    cancellationToken)
                .ConfigureAwait(false);
            response.Dispose();
        }
        catch (HttpRequestException)
        {
            // ignored — turn may already have finished
        }
        catch (TaskCanceledException)
        {
            // ignored
        }
    }

    private async Task<string?> HandleFrameAsync(CliParsedSse frame, string? sessionId, CancellationToken cancellationToken)
    {
        switch (frame.Event)
        {
            case CliSseEventNames.Session:
            case CliSseEventNames.Done:
                return TryReadSessionId(frame.Data) ?? sessionId;
            case CliSseEventNames.Text:
                Console.Write(TryReadString(frame.Data, "delta") ?? "");
                return sessionId;
            case CliSseEventNames.ToolStart:
                var toolName = TryReadString(frame.Data, "name") ?? "tool";
                Console.WriteLine();
                Console.WriteLine($"→ {toolName}");
                return sessionId;
            case CliSseEventNames.ToolOutput:
                Console.Write(TryReadString(frame.Data, "delta") ?? "");
                return sessionId;
            case CliSseEventNames.ToolEnd:
                return sessionId;
            case CliSseEventNames.ApprovalRequired:
                await HandleApprovalAsync(frame.Data, cancellationToken).ConfigureAwait(false);
                return sessionId;
            case CliSseEventNames.Error:
                var error = TryReadString(frame.Data, "message") ?? frame.Data;
                if (!string.Equals(error, "cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine(error);
                }

                return sessionId;
            default:
                return sessionId;
        }
    }

    private async Task HandleApprovalAsync(string data, CancellationToken cancellationToken)
    {
        var toolCallId = TryReadString(data, "toolCallId") ?? "";
        var toolName = TryReadString(data, "toolName") ?? "tool";
        var approved = _autoYes;
        if (!approved)
        {
            Console.Write($"Allow {toolName}? [y/n] ");
            var answer = Console.ReadLine();
            approved = !string.IsNullOrWhiteSpace(answer)
                       && (answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)
                           || answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));
        }

        using var response = await _http.PostAsJsonAsync(
                "v1/approvals",
                new CliApprovalRequest
                {
                    ToolCallId = toolCallId,
                    Decision = approved ? "approved" : "denied"
                },
                JsonFileStoreOptions.WebCompactRelaxed,
                cancellationToken)
            .ConfigureAwait(false);
        response.Dispose();
    }

    private static string? TryReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString();
            }
        }
        catch (JsonException)
        {
            // fall through
        }

        return string.IsNullOrWhiteSpace(body) ? null : body;
    }

    private static string? TryReadSessionId(string data) => TryReadString(data, "sessionId");

    private static string? TryReadString(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(property, out var value)
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
