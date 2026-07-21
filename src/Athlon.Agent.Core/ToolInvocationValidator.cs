using System.Text.Json;
using System.Text.RegularExpressions;

namespace Athlon.Agent.Core;

public sealed record ToolInvocationError(
    string Code,
    string Path,
    string Expected,
    string Actual,
    string Remediation);

public static class ToolInvocationErrors
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static ToolResult Failure(string summary, ToolInvocationError error) =>
        ToolResult.Failure(summary, JsonSerializer.Serialize(error, Options));
}

public static class ToolInvocationValidator
{
    public static ToolInvocationError? Validate(ToolJsonSchema schema, ToolCallArguments arguments)
    {
        var value = JsonSerializer.SerializeToElement(arguments);
        return ValidateNode(schema.ToJsonElement(), value, "$");
    }

    private static ToolInvocationError? ValidateNode(JsonElement schema, JsonElement value, string path)
    {
        if (schema.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && !MatchesType(type.GetString(), value))
        {
            return Error(
                "schema.type_mismatch",
                path,
                type.GetString() ?? "declared JSON type",
                Describe(value),
                TypeMismatchRemediation(path, type.GetString()));
        }

        if (schema.TryGetProperty("enum", out var enumValues)
            && enumValues.ValueKind == JsonValueKind.Array
            && !enumValues.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
        {
            return Error(
                "schema.enum",
                path,
                enumValues.GetRawText(),
                value.GetRawText(),
                EnumRemediation(path, enumValues));
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var objectError = ValidateObject(schema, value, path);
            if (objectError is not null)
            {
                return objectError;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var arrayError = ValidateArray(schema, value, path);
            if (arrayError is not null)
            {
                return arrayError;
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var stringError = ValidateString(schema, value.GetString() ?? string.Empty, path);
            if (stringError is not null)
            {
                return stringError;
            }
        }
        else if (value.ValueKind == JsonValueKind.Number)
        {
            var numberError = ValidateNumber(schema, value, path);
            if (numberError is not null)
            {
                return numberError;
            }
        }

        return null;
    }

    private static ToolInvocationError? ValidateObject(JsonElement schema, JsonElement value, string path)
    {
        if (schema.TryGetProperty("required", out var required)
            && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                var name = item.GetString();
                if (!string.IsNullOrWhiteSpace(name) && !value.TryGetProperty(name, out _))
                {
                    return Error(
                        "schema.required",
                        PropertyPath(path, name),
                        "required property",
                        "missing",
                        $"Add the required `{name}` argument.");
                }
            }
        }

        var hasProperties = schema.TryGetProperty("properties", out var properties)
                            && properties.ValueKind == JsonValueKind.Object;
        var allowsAdditional = !schema.TryGetProperty("additionalProperties", out var additional)
                               || additional.ValueKind != JsonValueKind.False;

        foreach (var property in value.EnumerateObject())
        {
            if (hasProperties && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                var error = ValidateNode(propertySchema, property.Value, PropertyPath(path, property.Name));
                if (error is not null)
                {
                    return error;
                }
            }
            else if (!allowsAdditional)
            {
                return Error(
                    "schema.additional_property",
                    PropertyPath(path, property.Name),
                    "no additional properties",
                    property.Value.GetRawText(),
                    $"Remove the unsupported `{property.Name}` argument.");
            }
        }

        return null;
    }

    private static ToolInvocationError? ValidateArray(JsonElement schema, JsonElement value, string path)
    {
        var length = value.GetArrayLength();
        if (TryGetInt(schema, "minItems", out var minItems) && length < minItems)
        {
            return RangeError("schema.min_items", path, $"at least {minItems} item(s)", length);
        }

        if (TryGetInt(schema, "maxItems", out var maxItems) && length > maxItems)
        {
            return RangeError("schema.max_items", path, $"at most {maxItems} item(s)", length);
        }

        if (schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var error = ValidateNode(items, item, $"{path}[{index}]");
                if (error is not null)
                {
                    return error;
                }

                index++;
            }
        }

        return null;
    }

    private static ToolInvocationError? ValidateString(JsonElement schema, string value, string path)
    {
        if (TryGetInt(schema, "minLength", out var minLength) && value.Length < minLength)
        {
            return RangeError("schema.min_length", path, $"at least {minLength} character(s)", value.Length);
        }

        if (TryGetInt(schema, "maxLength", out var maxLength) && value.Length > maxLength)
        {
            return RangeError("schema.max_length", path, $"at most {maxLength} character(s)", value.Length);
        }

        if (schema.TryGetProperty("pattern", out var patternElement)
            && patternElement.ValueKind == JsonValueKind.String)
        {
            var pattern = patternElement.GetString() ?? string.Empty;
            try
            {
                if (!Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                {
                    return Error(
                        "schema.pattern",
                        path,
                        $"string matching /{pattern}/",
                        JsonSerializer.Serialize(value),
                        "Change the value so it matches the required pattern.");
                }
            }
            catch (ArgumentException)
            {
                return Error(
                    "schema.invalid_pattern",
                    path,
                    "valid schema regex",
                    pattern,
                    "Fix the tool schema pattern before invoking the tool.");
            }
        }

        return null;
    }

    private static ToolInvocationError? ValidateNumber(JsonElement schema, JsonElement value, string path)
    {
        if (!value.TryGetDecimal(out var number))
        {
            return null;
        }

        if (TryGetDecimal(schema, "minimum", out var minimum) && number < minimum)
        {
            return RangeError("schema.minimum", path, $">= {minimum}", number);
        }

        if (TryGetDecimal(schema, "maximum", out var maximum) && number > maximum)
        {
            return RangeError("schema.maximum", path, $"<= {maximum}", number);
        }

        return null;
    }

    private static bool MatchesType(string? type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number
                     && value.TryGetDecimal(out var number)
                     && decimal.Truncate(number) == number,
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true
    };

    private static bool TryGetInt(JsonElement schema, string name, out int value)
    {
        value = default;
        return schema.TryGetProperty(name, out var element) && element.TryGetInt32(out value);
    }

    private static bool TryGetDecimal(JsonElement schema, string name, out decimal value)
    {
        value = default;
        return schema.TryGetProperty(name, out var element) && element.TryGetDecimal(out value);
    }

    private static ToolInvocationError RangeError(string code, string path, string expected, object actual) =>
        Error(code, path, expected, Convert.ToString(actual, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            "Adjust the value to satisfy the documented range.");

    private static ToolInvocationError Error(
        string code,
        string path,
        string expected,
        string actual,
        string remediation) =>
        new(code, path, expected, actual, remediation);

    private static string PropertyPath(string path, string property) =>
        property.All(character => char.IsLetterOrDigit(character) || character == '_')
            ? $"{path}.{property}"
            : $"{path}[{JsonSerializer.Serialize(property)}]";

    private static string TypeMismatchRemediation(string path, string? expectedType)
    {
        var name = PropertyNameFromPath(path);
        return expectedType switch
        {
            "integer" => $"Pass an integer for {name}, e.g. \"{name}\": 1. Got a non-integer value instead.",
            "number" => $"Pass a number for {name}, e.g. \"{name}\": 1.5. Got a non-number value instead.",
            "boolean" => $"Pass a boolean for {name}, e.g. \"{name}\": true. Got a non-boolean value instead.",
            "string" => $"Pass a string for {name}, e.g. \"{name}\": \"src/foo.cs\". Got a non-string value instead.",
            "array" => $"Pass a JSON array for {name}, e.g. \"{name}\": []. Got a non-array value instead.",
            "object" => $"Pass a JSON object for {name}, e.g. \"{name}\": {{}}. Got a non-object value instead.",
            _ => $"Pass a value whose JSON type matches the schema for {name} (expected {expectedType ?? "declared type"})."
        };
    }

    private static string EnumRemediation(string path, JsonElement enumValues)
    {
        var name = PropertyNameFromPath(path);
        var options = enumValues.EnumerateArray()
            .Take(8)
            .Select(static item => item.GetRawText())
            .ToArray();
        var listed = string.Join(", ", options);
        var suffix = enumValues.GetArrayLength() > 8 ? ", ..." : string.Empty;
        return $"Use one of the allowed values for {name}: {listed}{suffix}.";
    }

    private static string PropertyNameFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "$")
        {
            return "argument";
        }

        var leaf = path;
        var dot = path.LastIndexOf('.');
        if (dot >= 0 && dot < path.Length - 1)
        {
            leaf = path[(dot + 1)..];
        }

        var bracket = leaf.IndexOf('[');
        if (bracket >= 0)
        {
            leaf = bracket == 0 ? "argument" : leaf[..bracket];
        }

        return string.IsNullOrWhiteSpace(leaf) ? "argument" : leaf;
    }

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Number => "number",
        JsonValueKind.String => "string",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.Null => "null",
        _ => value.ValueKind.ToString()
    };
}
