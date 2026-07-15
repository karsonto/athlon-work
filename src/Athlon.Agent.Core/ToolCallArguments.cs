using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Athlon.Agent.Core;

/// <summary>
/// Immutable, JSON-native tool-call arguments. Values are cloned when captured so they
/// never depend on the lifetime of the JsonDocument from which they were parsed.
/// </summary>
[JsonConverter(typeof(ToolCallArgumentsJsonConverter))]
public sealed class ToolCallArguments : IReadOnlyDictionary<string, JsonElement>
{
    private readonly IReadOnlyDictionary<string, JsonElement> _values;

    public static ToolCallArguments Empty { get; } = new(
        new Dictionary<string, JsonElement>(StringComparer.Ordinal));

    public ToolCallArguments(IEnumerable<KeyValuePair<string, JsonElement>> values)
    {
        _values = values
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value.Clone(),
                StringComparer.Ordinal);
    }

    public int Count => _values.Count;
    public IEnumerable<string> Keys => _values.Keys;
    public IEnumerable<JsonElement> Values => _values.Values;
    public JsonElement this[string key] => _values[key];

    public static ToolCallArguments Parse(string? json)
    {
        TryParse(json, out var arguments, out _);
        return arguments;
    }

    /// <summary>
    /// Parses tool-call arguments JSON. Empty/whitespace succeeds as <see cref="Empty"/>.
    /// Non-object roots and invalid JSON return false with an error description.
    /// </summary>
    public static bool TryParse(string? json, out ToolCallArguments arguments, out string? error)
    {
        arguments = Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = $"Root value must be a JSON object, got {document.RootElement.ValueKind}.";
                return false;
            }

            arguments = FromJsonElement(document.RootElement);
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static ToolCallArguments FromJsonElement(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? new ToolCallArguments(element.EnumerateObject().Select(
                property => new KeyValuePair<string, JsonElement>(property.Name, property.Value)))
            : Empty;

    public static ToolCallArguments FromStrings(IReadOnlyDictionary<string, string> values)
    {
        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            arguments[name] = JsonSerializer.SerializeToElement(value);
        }

        return new ToolCallArguments(arguments);
    }

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, out JsonElement value) => _values.TryGetValue(key, out value);

    public bool TryGetString(string key, out string value)
    {
        value = string.Empty;
        if (!_values.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    public string? GetString(string key, string? defaultValue = null) =>
        TryGetString(key, out var value) ? value : defaultValue;

    public bool TryGetInt32(string key, out int value)
    {
        value = default;
        if (!_values.TryGetValue(key, out var element))
        {
            return false;
        }

        return element.ValueKind == JsonValueKind.Number
            ? element.TryGetInt32(out value)
            : element.ValueKind == JsonValueKind.String
              && int.TryParse(element.GetString(), out value);
    }

    public int GetInt32(string key, int defaultValue = default) =>
        TryGetInt32(key, out var value) ? value : defaultValue;

    public bool TryGetBoolean(string key, out bool value)
    {
        value = default;
        if (!_values.TryGetValue(key, out var element))
        {
            return false;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }

        return element.ValueKind == JsonValueKind.String
            && bool.TryParse(element.GetString(), out value);
    }

    public bool GetBoolean(string key, bool defaultValue = default) =>
        TryGetBoolean(key, out var value) ? value : defaultValue;

    public bool TryGetObject(string key, out JsonElement value) =>
        TryGetKind(key, JsonValueKind.Object, out value);

    public bool TryGetArray(string key, out JsonElement value) =>
        TryGetKind(key, JsonValueKind.Array, out value);

    public bool IsNull(string key) =>
        _values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Null;

    public string? GetRawJson(string key, string? defaultValue = null) =>
        _values.TryGetValue(key, out var value) ? value.GetRawText() : defaultValue;

    public string ToJsonString(JsonSerializerOptions? options = null) =>
        JsonSerializer.Serialize(this, options);

    public ToolCallArguments WithString(string key, string value)
    {
        var copy = _values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        copy[key] = JsonSerializer.SerializeToElement(value);
        return new ToolCallArguments(copy);
    }

    public IEnumerator<KeyValuePair<string, JsonElement>> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private bool TryGetKind(string key, JsonValueKind kind, out JsonElement value)
    {
        if (_values.TryGetValue(key, out value) && value.ValueKind == kind)
        {
            return true;
        }

        value = default;
        return false;
    }
}

public sealed class ToolCallArgumentsJsonConverter : JsonConverter<ToolCallArguments>
{
    public override ToolCallArguments Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ToolCallArguments.FromJsonElement(document.RootElement);
    }

    public override void Write(
        Utf8JsonWriter writer,
        ToolCallArguments value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (name, element) in value)
        {
            writer.WritePropertyName(name);
            element.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}
