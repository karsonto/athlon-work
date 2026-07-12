using System.Text.Json;
using System.Text.Json.Serialization;

namespace Athlon.Agent.Core;

public sealed class ToolJsonSchema
{
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly Dictionary<string, object?> _parametersObject;

    internal ToolJsonSchema(
        IReadOnlyDictionary<string, object?> properties,
        IReadOnlyList<string> required,
        bool additionalProperties)
    {
        _parametersObject = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required.Count == 0 ? Array.Empty<string>() : required.ToArray(),
            ["additionalProperties"] = additionalProperties
        };
    }

    public IReadOnlyDictionary<string, object?> ToOpenAiParameters() => _parametersObject;

    public string ToCanonicalJson() => JsonSerializer.Serialize(_parametersObject, CanonicalJsonOptions);

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(_parametersObject, CanonicalJsonOptions);
}
