using System.Text.Json;
using System.Text.Json.Serialization;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Infrastructure.Plan;

internal static class SubTaskJsonParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool TryParse(string json, out IReadOnlyList<SubTaskInput> subtasks, out string error)
    {
        subtasks = Array.Empty<SubTaskInput>();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "subtasks must be a non-empty JSON array.";
            return false;
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<SubTaskJsonDto>>(json, Options);
            if (items is null || items.Count == 0)
            {
                error = "subtasks must contain at least one subtask object.";
                return false;
            }

            var parsed = new List<SubTaskInput>();
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (string.IsNullOrWhiteSpace(item.Name))
                {
                    error = $"subtasks[{index}] requires a non-empty 'name'.";
                    return false;
                }

                parsed.Add(new SubTaskInput(
                    item.Name.Trim(),
                    item.Description?.Trim(),
                    item.ExpectedOutcome?.Trim() ?? item.Expected_Outcome?.Trim(),
                    NormalizeFiles(item.Files)));
            }

            subtasks = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Invalid subtasks JSON: {ex.Message}";
            return false;
        }
    }

    private static IReadOnlyList<string>? NormalizeFiles(IReadOnlyList<string>? files) =>
        files is null or { Count: 0 }
            ? null
            : files
                .Where(file => !string.IsNullOrWhiteSpace(file))
                .Select(file => file.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private sealed class SubTaskJsonDto
    {
        public string? Name { get; set; }
        public string? Description { get; set; }

        [JsonPropertyName("expected_outcome")]
        public string? ExpectedOutcome { get; set; }

        [JsonPropertyName("expectedOutcome")]
        public string? Expected_Outcome { get; set; }

        public List<string>? Files { get; set; }
    }
}
