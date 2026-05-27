using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Athlon.Agent.Mcp;

internal static class McpJsonRpc
{
    public const string ProtocolVersion = "2025-03-26";

    public static JsonObject CreateRequest(long id, string method, JsonNode? parameters = null)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method
        };
        if (parameters is not null)
        {
            request["params"] = parameters;
        }

        return request;
    }

    public static JsonElement ParseResultFromJsonBody(string body, long expectedId)
    {
        using var doc = JsonDocument.Parse(body);
        return ExtractResult(doc.RootElement, expectedId);
    }

    public static JsonElement ParseResultFromSseBody(string body, long expectedId)
    {
        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(data) || data == "[DONE]")
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id) && id == expectedId)
                {
                    return ExtractResult(doc.RootElement, expectedId);
                }
            }
            catch (JsonException)
            {
                // try next SSE event
            }
        }

        throw new InvalidOperationException($"No SSE data event matched JSON-RPC id {expectedId}.");
    }

    private static JsonElement ExtractResult(JsonElement root, long expectedId)
    {
        if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id) && id != expectedId)
        {
            throw new InvalidOperationException($"JSON-RPC id mismatch. Expected {expectedId}, got {id}.");
        }

        if (root.TryGetProperty("error", out var errEl))
        {
            throw new InvalidOperationException($"MCP error: {errEl.GetRawText()}");
        }

        if (root.TryGetProperty("result", out var resEl))
        {
            return resEl.Clone();
        }

        return root.Clone();
    }

    public static string BuildPostBody(JsonObject request) =>
        request.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    public static JsonObject CreateInitializeParams(string? clientName = null) =>
        new()
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = clientName ?? "Athlon.Agent",
                ["version"] = "0"
            }
        };

    public static JsonObject CreateNotification(string method, JsonNode? parameters = null)
    {
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method
        };
        if (parameters is not null)
        {
            notification["params"] = parameters;
        }

        return notification;
    }
}
