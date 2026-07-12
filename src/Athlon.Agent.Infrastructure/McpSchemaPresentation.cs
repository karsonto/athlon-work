using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Athlon.Agent.Infrastructure;

internal sealed record McpSchemaPresentation(
    JsonElement InputSchema,
    string Fingerprint,
    bool RequiresDescribe,
    bool Truncated,
    string Guidance);

internal static class McpSchemaPresenter
{
    private const int MaxInlineSchemaChars = 2_000;
    private const int MaxInlineProperties = 12;
    private const int MaxInlineDepth = 3;

    public static McpSchemaPresentation Present(string inputSchemaJson)
    {
        var canonical = Canonicalize(inputSchemaJson);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        using var document = JsonDocument.Parse(canonical);
        var root = document.RootElement;
        var propertyCount = CountProperties(root);
        var depth = MeasureDepth(root);
        var complex = canonical.Length > MaxInlineSchemaChars
                      || propertyCount > MaxInlineProperties
                      || depth > MaxInlineDepth
                      || ContainsComplexComposition(root);
        if (!complex)
        {
            return new McpSchemaPresentation(
                root.Clone(),
                fingerprint,
                RequiresDescribe: false,
                Truncated: false,
                "Schema is complete; call mcp_call directly with native `arguments`.");
        }

        var summary = BuildSummary(root, propertyCount, depth);
        return new McpSchemaPresentation(
            JsonSerializer.SerializeToElement(summary),
            fingerprint,
            RequiresDescribe: true,
            Truncated: true,
            "Schema is complex or trimmed; call mcp_describe before mcp_call.");
    }

    private static string Canonicalize(string json)
    {
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return CanonicalizeNode(node)?.ToJsonString() ?? "{}";
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    private static JsonNode? CanonicalizeNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var canonical = new JsonObject();
            foreach (var property in obj.OrderBy(property => property.Key, StringComparer.Ordinal))
            {
                canonical[property.Key] = CanonicalizeNode(property.Value);
            }

            return canonical;
        }

        if (node is JsonArray array)
        {
            var canonical = new JsonArray();
            foreach (var item in array)
            {
                canonical.Add(CanonicalizeNode(item));
            }

            return canonical;
        }

        return node?.DeepClone();
    }

    private static object BuildSummary(JsonElement root, int propertyCount, int depth)
    {
        var required = root.TryGetProperty("required", out var requiredElement)
                       && requiredElement.ValueKind == JsonValueKind.Array
            ? requiredElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray()
            : Array.Empty<string?>();
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (root.TryGetProperty("properties", out var propertiesElement)
            && propertiesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in propertiesElement.EnumerateObject().Take(MaxInlineProperties))
            {
                properties[property.Name] = SummarizeProperty(property.Value);
            }
        }

        return new
        {
            type = root.TryGetProperty("type", out var type) ? type.GetString() : "object",
            required,
            properties,
            summary = new
            {
                propertyCount,
                depth,
                omittedProperties = Math.Max(0, propertyCount - properties.Count)
            }
        };
    }

    private static object SummarizeProperty(JsonElement schema)
    {
        var type = schema.TryGetProperty("type", out var typeElement)
            ? typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString() : typeElement.GetRawText()
            : null;
        var description = schema.TryGetProperty("description", out var descriptionElement)
                          && descriptionElement.ValueKind == JsonValueKind.String
            ? Truncate(descriptionElement.GetString(), 160)
            : null;
        var enumValues = schema.TryGetProperty("enum", out var enumElement)
            ? JsonSerializer.Deserialize<object>(enumElement.GetRawText())
            : null;
        return new { type, description, enumValues };
    }

    private static int CountProperties(JsonElement element)
    {
        var count = 0;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("properties") && property.Value.ValueKind == JsonValueKind.Object)
                {
                    count += property.Value.EnumerateObject().Count();
                }

                count += CountProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            count += element.EnumerateArray().Sum(CountProperties);
        }

        return count;
    }

    private static int MeasureDepth(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            return 1 + element.EnumerateObject().Select(property => MeasureDepth(property.Value)).DefaultIfEmpty(0).Max();
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            return 1 + element.EnumerateArray().Select(MeasureDepth).DefaultIfEmpty(0).Max();
        }

        return 0;
    }

    private static bool ContainsComplexComposition(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "$ref" or "oneOf" or "anyOf" or "allOf" or "not"
                    || ContainsComplexComposition(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(ContainsComplexComposition);
        }

        return false;
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength] + "…";
}
