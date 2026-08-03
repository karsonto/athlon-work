using System.IO;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;

namespace Athlon.Agent.Infrastructure.Browser;

internal static class BrowserToolHelper
{
    public static async Task<ToolResult> InvokeHostAsync(
        Func<CancellationToken, Task<ToolResult>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Failure("Browser automation failed", ex.Message);
        }
    }

    public static ToolResult FromAriaJson(string json, string defaultSummary)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            var error = root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String
                ? errEl.GetString()
                : null;
            if (!ok)
            {
                return ToolResult.Failure(
                    "ARIA operation failed",
                    string.IsNullOrWhiteSpace(error) ? json : error!);
            }

            string? content = null;
            if (root.TryGetProperty("data", out var dataEl))
            {
                content = dataEl.ValueKind == JsonValueKind.String
                    ? dataEl.GetString()
                    : dataEl.GetRawText();
                if (dataEl.ValueKind == JsonValueKind.Object
                    && dataEl.TryGetProperty("tree", out var treeEl)
                    && treeEl.ValueKind == JsonValueKind.String)
                {
                    content = treeEl.GetString();
                }
            }

            return ToolResult.Success(defaultSummary, content ?? json);
        }
        catch (JsonException)
        {
            return ToolResult.Failure("Invalid ARIA host response", json);
        }
    }

    public static bool HasAnyStringArg(ToolInvocation invocation, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!invocation.Arguments.TryGetValue(key, out var element)
                || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(element.GetString()))
            {
                return true;
            }
        }

        return false;
    }

    public static string BuildArgsJson(ToolInvocation invocation, params string[] keys)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var key in keys)
            {
                if (!invocation.Arguments.TryGetValue(key, out var element)
                    || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                {
                    continue;
                }

                writer.WritePropertyName(key);
                element.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
