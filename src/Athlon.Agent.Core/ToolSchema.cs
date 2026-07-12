using System.Text.Json;

namespace Athlon.Agent.Core;

public sealed class ToolSchemaBuilder
{
    private readonly Dictionary<string, object?> _properties = new(StringComparer.Ordinal);
    private readonly List<string> _required = [];
    private bool _additionalProperties;

    internal ToolSchemaBuilder(bool additionalProperties) => _additionalProperties = additionalProperties;

    public ToolSchemaBuilder String(
        string name,
        string description,
        bool required = false,
        string? defaultValue = null,
        IReadOnlyList<string>? enumValues = null,
        string? pattern = null,
        int? minLength = null,
        int? maxLength = null)
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "string",
            ["description"] = description
        };
        AddConstraint(schema, "default", defaultValue);
        AddConstraint(schema, "enum", enumValues?.ToArray());
        AddConstraint(schema, "pattern", pattern);
        AddConstraint(schema, "minLength", minLength);
        AddConstraint(schema, "maxLength", maxLength);
        AddProperty(name, schema, required);
        return this;
    }

    public ToolSchemaBuilder Integer(
        string name,
        string description,
        bool required = false,
        int? defaultValue = null,
        int? minimum = null,
        int? maximum = null,
        IReadOnlyList<int>? enumValues = null)
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "integer",
            ["description"] = description
        };
        AddConstraint(schema, "default", defaultValue);
        AddConstraint(schema, "minimum", minimum);
        AddConstraint(schema, "maximum", maximum);
        AddConstraint(schema, "enum", enumValues?.ToArray());
        AddProperty(name, schema, required);
        return this;
    }

    public ToolSchemaBuilder Boolean(string name, string description, bool required = false, bool? defaultValue = null)
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "boolean",
            ["description"] = description
        };
        if (defaultValue.HasValue)
        {
            schema["default"] = defaultValue.Value;
        }

        AddProperty(name, schema, required);
        return this;
    }

    public ToolSchemaBuilder Object(
        string name,
        string description,
        ToolJsonSchema schema,
        bool required = false)
    {
        var property = new Dictionary<string, object?>(schema.ToOpenAiParameters(), StringComparer.Ordinal)
        {
            ["description"] = description
        };
        AddProperty(name, property, required);
        return this;
    }

    public ToolSchemaBuilder Array(
        string name,
        string description,
        bool required = false,
        ToolJsonSchema? items = null,
        int? minItems = null,
        int? maxItems = null)
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "array",
            ["description"] = description
        };
        AddConstraint(schema, "items", items?.ToOpenAiParameters());
        AddConstraint(schema, "minItems", minItems);
        AddConstraint(schema, "maxItems", maxItems);
        AddProperty(name, schema, required);
        return this;
    }

    public ToolSchemaBuilder AllowAdditionalProperties(bool allow = true)
    {
        _additionalProperties = allow;
        return this;
    }

    public ToolJsonSchema Build() =>
        new(_properties, _required, _additionalProperties);

    private void AddProperty(string name, Dictionary<string, object?> schema, bool required)
    {
        _properties[name] = schema;
        if (required)
        {
            _required.Add(name);
        }
    }

    private static void AddConstraint(Dictionary<string, object?> schema, string name, object? value)
    {
        if (value is not null)
        {
            schema[name] = value;
        }
    }
}

public static class ToolSchema
{
    public static ToolSchemaBuilder Object(bool additionalProperties = false) =>
        new(additionalProperties);

    public static ToolJsonSchema FromMcp(string inputSchemaJson)
    {
        if (string.IsNullOrWhiteSpace(inputSchemaJson))
        {
            return Object().Build();
        }

        using var document = JsonDocument.Parse(inputSchemaJson);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object
            && ((!root.TryGetProperty("type", out var typeElement)
                 && root.TryGetProperty("properties", out _))
                || string.Equals(typeElement.GetString(), "object", StringComparison.OrdinalIgnoreCase)))
        {
            return FromMcpObject(root);
        }

        return Object()
            .String("arguments", "JSON arguments for the MCP tool.", required: true)
            .Build();
    }

    public static ToolJsonSchema FromLegacyParameters(IReadOnlyDictionary<string, string> parameters)
    {
        var builder = Object();
        foreach (var (name, description) in parameters)
        {
            var isOptional = description.StartsWith("Optional", StringComparison.OrdinalIgnoreCase);
            builder.String(name, description, required: !isOptional);
        }

        return builder.Build();
    }

    public static ToolJsonSchema Parse(string json) => FromMcp(json);

    private static ToolJsonSchema FromMcpObject(JsonElement root)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (root.TryGetProperty("properties", out var propertiesElement)
            && propertiesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in propertiesElement.EnumerateObject())
            {
                properties[property.Name] = JsonSerializer.Deserialize<object>(property.Value.GetRawText());
            }
        }

        var required = new List<string>();
        if (root.TryGetProperty("required", out var requiredElement)
            && requiredElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in requiredElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var name = item.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        required.Add(name);
                    }
                }
            }
        }

        var additionalProperties = false;
        if (root.TryGetProperty("additionalProperties", out var additionalElement))
        {
            additionalProperties = additionalElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => false
            };
        }

        return new ToolJsonSchema(properties, required, additionalProperties);
    }
}
